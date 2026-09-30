namespace Cmdb.Graph;

/// <summary>
/// The current graph. Readers take <see cref="Current"/> once per operation and keep using that instance, so a
/// replacement (a reload, or a batch from the change stream) never changes a graph under a running traversal.
/// </summary>
public sealed class GraphHolder
{
    private volatile Graph? _current;
    private volatile string? _position;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Graph? Current => _current;

    public bool IsReady => _current is not null;

    /// <summary>Completes when the first graph is in place.</summary>
    public Task Ready => _ready.Task;

    /// <summary>How the current base was loaded, for diagnostics.</summary>
    public GraphLoadInfo? LoadInfo { get; private set; }

    /// <summary>The latest batch from the change stream, for diagnostics.</summary>
    public GraphChangeInfo? LastChange { get; private set; }

    /// <summary>The change stream position the graph reflects; it can move ahead of the graph's version when a batch was empty.</summary>
    public string? Position => _position;

    public void Set(Graph graph, GraphLoadInfo info)
    {
        LoadInfo = info;
        _current = graph;
        _position = graph.Version;
        _ready.TrySetResult();
    }

    /// <summary>A graph with a batch of changes applied.</summary>
    public void Apply(Graph graph, GraphChangeInfo info)
    {
        LastChange = info;
        _current = graph;
        _position = graph.Version;
    }

    /// <summary>The same graph with its delta folded in (#81); the position stays, as it may be ahead of the graph.</summary>
    public void Compacted(Graph graph, GraphChangeInfo info)
    {
        LastChange = info;
        _current = graph;
    }

    /// <summary>A batch without changes to the graph: only the position moves.</summary>
    public void Advance(string position) => _position = position;

    /// <summary>The current graph, or an exception that tells the caller to retry shortly.</summary>
    public Graph Require() => _current ?? throw new GraphNotReadyException();
}

/// <param name="Source">"database", "snapshot" or "reload".</param>
public sealed record GraphLoadInfo(string Source, TimeSpan Duration, long ManagedBytes, DateTimeOffset LoadedAt);

/// <param name="Changes">Outbox entries in the batch.</param>
/// <param name="Keys">Distinct keys re-read.</param>
/// <param name="Duration">Reading, patching and rebuilding.</param>
/// <param name="Mode">"delta" (on top of the graph), "rebuild" (compacted with the batch) or "compaction" (#81).</param>
public sealed record GraphChangeInfo(int Changes, int Keys, TimeSpan Duration, DateTimeOffset AppliedAt, string Mode = "rebuild");

public sealed class GraphNotReadyException() : InvalidOperationException("The network graph is still loading.");
