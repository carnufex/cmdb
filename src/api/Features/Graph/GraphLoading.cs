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
/// Follow: woken by NOTIFY, or by the poll interval at the latest, each batch of changes is applied and swapped in
/// atomically. Changes arriving meanwhile form the next batch. A batch that moves connections, touches objects without
/// changing their structure, adds equipment and cables, or changes circuits becomes a delta on the graph (#81, #119,
/// #121), in time proportional to the batch. A delta past <c>Graph:DeltaMaxNodes</c>, or older than the snapshot
/// interval, is folded into the arrays. Any other batch (removed or moved objects) rebuilds: the base's rows with every
/// batch since replaced, built once. A bulk load, another database, or rows that do not fit together give a full reload
/// instead.
/// </para>
/// </summary>
public sealed partial class GraphLoadingService(
    Cmdb.Api.Auth.SystemDb system, IGraphChangeFeed feed, GraphHolder holder, Cmdb.Api.Auth.ScopeRefreshService scopes, IConfiguration config,
    ILogger<GraphLoadingService> logger) : BackgroundService
{
    private DateTimeOffset _snapshotWritten = DateTimeOffset.MinValue;

    /// <summary>The graph without delta, and the batches applied on top of it since (#81).</summary>
    private Cmdb.Graph.Graph? _base;

    private DateTimeOffset _baseAt;

    private readonly List<GraphChangeBatch> _pending = [];

    private int DeltaMaxNodes => config.GetValue("Graph:DeltaMaxNodes", 50_000);

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
        SetBase(graph);
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
        SetBase(graph);
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

        _pending.Add(batch);
        var mode = "delta";
        Cmdb.Graph.Graph? next;
        try
        {
            next = GraphChanges.TryDelta(holder.Require(), batch);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            // A delta that trips over itself is a bug, but not a reason to stop the API (#163): rebuild from rows instead.
            PatchFailed(logger, ex);
            next = null;
        }
        if (next is null)
        {
            mode = "rebuild";
            next = Compact();
            if (next is null)
            {
                await FullLoadAsync("reload", ct);
                return;
            }
        }
        else if (next.OverlayNodes > DeltaMaxNodes)
        {
            next = Flatten(next);
            if (next is null)
            {
                await FullLoadAsync("reload", ct);
                return;
            }
        }
        holder.Apply(next, new GraphChangeInfo(batch.Changes, batch.Keys.Count, sw.Elapsed, DateTimeOffset.UtcNow, mode));
        // New cables, circuits or equipment may change what access scopes show (#22).
        if (batch.Keys.Cables.Count + batch.Keys.Circuits.Count + batch.Keys.Equipment.Count > 0)
        {
            scopes.Request();
        }
        Applied(logger, batch.Changes, batch.Keys.Count, mode, sw.Elapsed.TotalMilliseconds, next.OverlayNodes, next.Version);
        var now = DateTimeOffset.UtcNow;
        var snapshotDue = SnapshotPath is not null && now - _snapshotWritten >= SnapshotInterval;
        // A snapshot holds arrays only, so the delta is folded in first; without snapshots, as often.
        if (_pending.Count > 0 && (snapshotDue || now - _baseAt >= SnapshotInterval))
        {
            sw.Restart();
            next = Flatten(next);
            if (next is null)
            {
                await FullLoadAsync("reload", ct);
                return;
            }
            holder.Compacted(next, new GraphChangeInfo(0, 0, sw.Elapsed, DateTimeOffset.UtcNow, "compaction"));
            Compacted(logger, sw.Elapsed.TotalMilliseconds, next.Version);
        }
        if (snapshotDue)
        {
            WriteSnapshot(next);
        }
    }

    private void SetBase(Cmdb.Graph.Graph graph)
    {
        _base = graph;
        _baseAt = DateTimeOffset.UtcNow;
        _pending.Clear();
    }

    /// <summary>
    /// The delta folded into the arrays, which become the base; rebuilt from rows when new ids do not follow the
    /// arrays' own. Null when the rows do not fit together.
    /// </summary>
    private Cmdb.Graph.Graph? Flatten(Cmdb.Graph.Graph graph)
    {
        if (GraphChanges.Flatten(graph, _pending) is not { } flat)
        {
            return Compact();
        }
        SetBase(flat);
        return flat;
    }

    /// <summary>The base with the pending batches, rebuilt once; null when the rows do not fit together.</summary>
    private Cmdb.Graph.Graph? Compact()
    {
        try
        {
            var graph = GraphChanges.Compact(_base!, _pending);
            SetBase(graph);
            return graph;
        }
        catch (InvalidOperationException ex)
        {
            // A change that is committed but not yet below the horizon left the rows inconsistent; start over.
            PatchFailed(logger, ex);
            return null;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Graph changes applied: {Changes} changes, {Keys} keys as {Mode} in {Milliseconds:0} ms, delta {DeltaNodes} nodes, position {Version}")]
    private static partial void Applied(ILogger logger, int changes, int keys, string mode, double milliseconds, int deltaNodes, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Graph compacted in {Milliseconds:0} ms, position {Version}")]
    private static partial void Compacted(ILogger logger, double milliseconds, string version);

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
