namespace Cmdb.Graph;

/// <summary>
/// The current graph. Readers take <see cref="Current"/> once per operation and keep using that instance, so a
/// replacement (a reload, or the change stream in #11) never changes a graph under a running traversal.
/// </summary>
public sealed class GraphHolder
{
    private volatile Graph? _current;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Graph? Current => _current;

    public bool IsReady => _current is not null;

    /// <summary>Completes when the first graph is in place.</summary>
    public Task Ready => _ready.Task;

    /// <summary>How the current graph was loaded, for diagnostics.</summary>
    public GraphLoadInfo? LoadInfo { get; private set; }

    public void Set(Graph graph, GraphLoadInfo info)
    {
        LoadInfo = info;
        _current = graph;
        _ready.TrySetResult();
    }

    /// <summary>The current graph, or an exception that tells the caller to retry shortly.</summary>
    public Graph Require() => _current ?? throw new GraphNotReadyException();
}

/// <param name="Source">"database" or "snapshot".</param>
public sealed record GraphLoadInfo(string Source, TimeSpan Duration, long ManagedBytes, DateTimeOffset LoadedAt);

public sealed class GraphNotReadyException() : InvalidOperationException("The network graph is still loading.");
