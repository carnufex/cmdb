using System.Diagnostics;
using Cmdb.Graph;
using Npgsql;

namespace Cmdb.Api.Features.Graph;

/// <summary>
/// Loads the network graph when the API starts (ADR-0002, #8) and then follows the change stream (#11).
/// <para>
/// Start: the snapshot file if there is one, caught up from its position; otherwise the database, after which a new
/// snapshot is written. Readiness waits for the first graph.
/// </para>
/// <para>
/// Follow: woken by NOTIFY, or by the poll interval at the latest, each batch of changes is applied by rebuilding the
/// graph from its own rows with the changed keys replaced, and swapped in atomically. Changes arriving meanwhile form
/// the next batch. A bulk load, another database, or rows that do not fit together give a full reload instead.
/// </para>
/// </summary>
public sealed partial class GraphLoadingService(
    Cmdb.Api.Auth.SystemDb system, IGraphChangeFeed feed, GraphHolder holder, Cmdb.Api.Auth.ScopeRefreshService scopes, IConfiguration config,
    ILogger<GraphLoadingService> logger) : BackgroundService
{
    private DateTimeOffset _snapshotWritten = DateTimeOffset.MinValue;

    private string? SnapshotPath => config["Graph:SnapshotPath"];

    private TimeSpan Poll => TimeSpan.FromSeconds(config.GetValue("Graph:PollSeconds", 5.0));

    /// <summary>How often a changed graph is written back to the snapshot file, so a restart has little to catch up.</summary>
    private TimeSpan SnapshotInterval => TimeSpan.FromMinutes(config.GetValue("Graph:SnapshotMinutes", 10.0));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await InitialLoadAsync(stoppingToken);
                break;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException && !stoppingToken.IsCancellationRequested)
            {
                LoadFailed(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await feed.WaitAsync(Poll, stoppingToken);
                await CatchUpAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException && !stoppingToken.IsCancellationRequested)
            {
                FollowFailed(logger, ex);
                await Task.Delay(Poll, stoppingToken);
            }
        }
    }

    private async Task InitialLoadAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var graph = SnapshotPath is null ? null : GraphSnapshot.ReadFile(SnapshotPath, expectedVersion: null);
        if (graph is null)
        {
            await FullLoadAsync("database", ct);
            return;
        }
        var bytes = GC.GetTotalMemory(forceFullCollection: true) - before;
        holder.Set(graph, new GraphLoadInfo("snapshot", sw.Elapsed, bytes, DateTimeOffset.UtcNow));
        Loaded(logger, graph.NodeCount, graph.EdgeCount, graph.CircuitCount, "snapshot", sw.Elapsed.TotalSeconds, bytes / (1024 * 1024), graph.Version);
        await CatchUpAsync(ct);
    }

    private async Task FullLoadAsync(string source, CancellationToken ct)
    {
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var sw = Stopwatch.StartNew();
        var graph = await GraphLoader.LoadAsync(system.Source, ct);
        var duration = sw.Elapsed;
        var bytes = GC.GetTotalMemory(forceFullCollection: true) - before;
        holder.Set(graph, new GraphLoadInfo(source, duration, bytes, DateTimeOffset.UtcNow));
        Loaded(logger, graph.NodeCount, graph.EdgeCount, graph.CircuitCount, source, duration.TotalSeconds, bytes / (1024 * 1024), graph.Version);
        WriteSnapshot(graph);
    }

    private async Task CatchUpAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var batch = await feed.ReadAsync(holder.Position!, ct);
        if (batch.Reload)
        {
            ReloadRequested(logger, batch.Changes);
            await FullLoadAsync("reload", ct);
            return;
        }
        if (batch.Keys.Count == 0)
        {
            holder.Advance(batch.Watermark);
            return;
        }

        Cmdb.Graph.Graph next;
        try
        {
            next = GraphChanges.Apply(holder.Require(), batch);
        }
        catch (InvalidOperationException ex)
        {
            // A change that is committed but not yet below the horizon left the rows inconsistent; start over.
            PatchFailed(logger, ex);
            await FullLoadAsync("reload", ct);
            return;
        }
        holder.Apply(next, new GraphChangeInfo(batch.Changes, batch.Keys.Count, sw.Elapsed, DateTimeOffset.UtcNow));
        // New cables, circuits or equipment may change what access scopes show (#22).
        if (batch.Keys.Cables.Count + batch.Keys.Circuits.Count + batch.Keys.Equipment.Count > 0)
        {
            scopes.Request();
        }
        Applied(logger, batch.Changes, batch.Keys.Count, sw.Elapsed.TotalMilliseconds, next.Version);
        if (DateTimeOffset.UtcNow - _snapshotWritten >= SnapshotInterval)
        {
            WriteSnapshot(next);
        }
    }

    private void WriteSnapshot(Cmdb.Graph.Graph graph)
    {
        if (SnapshotPath is null)
        {
            return;
        }
        var sw = Stopwatch.StartNew();
        GraphSnapshot.WriteFile(graph, SnapshotPath);
        _snapshotWritten = DateTimeOffset.UtcNow;
        SnapshotWritten(logger, SnapshotPath, sw.Elapsed.TotalSeconds);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Graph loaded: {Nodes} terminals, {Edges} edges, {Circuits} circuits from {Source} in {Seconds:0.0} s, {Megabytes} MB, position {Version}")]
    private static partial void Loaded(ILogger logger, int nodes, int edges, int circuits, string source, double seconds, long megabytes, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Graph changes applied: {Changes} changes, {Keys} keys in {Milliseconds:0} ms, position {Version}")]
    private static partial void Applied(ILogger logger, int changes, int keys, double milliseconds, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Graph reload requested by the change stream ({Changes} changes)")]
    private static partial void ReloadRequested(ILogger logger, int changes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Graph changes did not fit together; reloading in full")]
    private static partial void PatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Graph snapshot written to {Path} in {Seconds:0.0} s")]
    private static partial void SnapshotWritten(ILogger logger, string path, double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Loading the graph failed; retrying in 10 s")]
    private static partial void LoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Following the change stream failed; retrying")]
    private static partial void FollowFailed(ILogger logger, Exception exception);
}
