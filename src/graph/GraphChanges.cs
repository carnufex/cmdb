namespace Cmdb.Graph;

/// <summary>What changed since a watermark, as keys whose rows must be replaced (#11).</summary>
public sealed class GraphKeys
{
    public HashSet<long> Equipment { get; } = [];
    public HashSet<long> Cables { get; } = [];

    /// <summary>Terminals whose connections changed.</summary>
    public HashSet<long> Terminals { get; } = [];
    public HashSet<long> Circuits { get; } = [];

    public int Count => Equipment.Count + Cables.Count + Terminals.Count + Circuits.Count;
}

/// <param name="Watermark">The position after this batch; pass it to the next read. Opaque to callers.</param>
/// <param name="Reload">A bulk load happened: rebuild from scratch instead of patching.</param>
/// <param name="Keys">The changed keys.</param>
/// <param name="Rows">The current rows for <paramref name="Keys"/>: equipment and its ports, cables with conductors and ends,
/// connections touching the terminals, circuits with hops, dependencies and services.</param>
/// <param name="Changes">Outbox entries read, for diagnostics.</param>
public sealed record GraphChangeBatch(string Watermark, bool Reload, GraphKeys Keys, GraphData Rows, int Changes);

/// <summary>
/// The change stream as the graph engine sees it. Postgres (outbox + LISTEN/NOTIFY) today; a Kafka topic can take its
/// place later, with the watermark as the offset.
/// </summary>
public interface IGraphChangeFeed
{
    /// <summary>The current position: a full load started now reflects every change before it.</summary>
    Task<string> PositionAsync(CancellationToken ct);

    /// <summary>Changes committed since <paramref name="watermark"/>, with the current rows for their keys.</summary>
    Task<GraphChangeBatch> ReadAsync(string watermark, CancellationToken ct);

    /// <summary>Completes when there may be new changes, or after <paramref name="timeout"/> at the latest.</summary>
    Task WaitAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>Applying a batch: the graph's rows with the changed keys' rows replaced, rebuilt deterministically.</summary>
public static class GraphChanges
{
    /// <summary>
    /// The graph after the batch. Throws <see cref="InvalidOperationException"/> when the patched rows do not fit
    /// together (a later change is not visible yet); the caller then reloads in full.
    /// </summary>
    public static Graph Apply(Graph graph, GraphChangeBatch batch)
    {
        var data = GraphData.From(graph);
        data.Replace(batch.Keys, batch.Rows);
        return GraphBuilder.Build(data, batch.Watermark);
    }
}
