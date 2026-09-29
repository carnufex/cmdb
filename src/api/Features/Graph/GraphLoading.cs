using System.Diagnostics;
using Cmdb.Graph;
using Npgsql;

namespace Cmdb.Api.Features.Graph;

/// <summary>
/// Loads the network graph when the API starts (ADR-0002, #8): from the snapshot file when it matches the database,
/// otherwise from the database, after which a new snapshot is written. Readiness waits for it.
/// </summary>
public sealed partial class GraphLoadingService(
    NpgsqlDataSource db, GraphHolder holder, IConfiguration config, ILogger<GraphLoadingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var snapshotPath = config["Graph:SnapshotPath"];
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await LoadAsync(snapshotPath, stoppingToken);
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException && !stoppingToken.IsCancellationRequested)
            {
                LoadFailed(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task LoadAsync(string? snapshotPath, CancellationToken ct)
    {
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var sw = Stopwatch.StartNew();
        var version = await GraphLoader.DataVersionAsync(db, ct);
        var source = "snapshot";
        var graph = snapshotPath is null ? null : GraphSnapshot.ReadFile(snapshotPath, version);
        if (graph is null)
        {
            source = "database";
            graph = await GraphLoader.LoadAsync(db, ct);
        }
        var duration = sw.Elapsed;
        var bytes = GC.GetTotalMemory(forceFullCollection: true) - before;
        holder.Set(graph, new GraphLoadInfo(source, duration, bytes, DateTimeOffset.UtcNow));
        Loaded(logger, graph.NodeCount, graph.EdgeCount, graph.CircuitCount, source, duration.TotalSeconds, bytes / (1024 * 1024), graph.Version);

        if (source == "database" && snapshotPath is not null)
        {
            var write = Stopwatch.StartNew();
            GraphSnapshot.WriteFile(graph, snapshotPath);
            SnapshotWritten(logger, snapshotPath, write.Elapsed.TotalSeconds);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Graph loaded: {Nodes} terminals, {Edges} edges, {Circuits} circuits from {Source} in {Seconds:0.0} s, {Megabytes} MB, version {Version}")]
    private static partial void Loaded(ILogger logger, int nodes, int edges, int circuits, string source, double seconds, long megabytes, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Graph snapshot written to {Path} in {Seconds:0.0} s")]
    private static partial void SnapshotWritten(ILogger logger, string path, double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Loading the graph failed; retrying in 10 s")]
    private static partial void LoadFailed(ILogger logger, Exception exception);
}
