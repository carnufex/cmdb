namespace Cmdb.Graph;

/// <summary>A connection a plan adds or removes (#24), by external terminal ids.</summary>
/// <param name="Kind">The kind of the connection; ignored when removing, which removes every connection between the two.</param>
public sealed record GraphEdgeChange(long A, long B, EdgeKind Kind, bool Add);

/// <summary>Why a change had no effect on the view: the plan says something production no longer allows.</summary>
public enum GraphChangeProblem
{
    UnknownTerminal,
    AlreadyConnected,
    NotConnected,
    SameTerminal,

    /// <summary>
    /// A terminal takes one connection of each kind (#25): one patch at the front of a port and one splice or
    /// termination at the back. Adding a second of the same kind is a conflict.
    /// </summary>
    Occupied,
}

public sealed record GraphChangeIssue(int Index, GraphEdgeChange Change, GraphChangeProblem Problem);

/// <summary>Replacement adjacency for the nodes a plan touches; every other node reads the base arrays.</summary>
internal sealed class GraphOverlay(Dictionary<int, (int[] Targets, EdgeKind[] Kinds)> edges)
{
    public Dictionary<int, (int[] Targets, EdgeKind[] Kinds)> Edges { get; } = edges;
}

public sealed partial class Graph
{
    private GraphOverlay? _overlay;
    private Graph? _base;

    /// <summary>The production graph a plan view was made from, or this graph itself.</summary>
    public Graph Base => _base ?? this;

    /// <summary>Whether this is a plan view (base + delta).</summary>
    public bool IsOverlay => _overlay is not null;

    /// <summary>Nodes whose connections differ from the base, for diagnostics.</summary>
    public int OverlayNodes => _overlay?.Edges.Count ?? 0;

    /// <summary>
    /// A view of this graph with connections added and removed (ADR-0005, #24): all arrays are shared, and only the
    /// adjacency of the touched nodes is replaced, so a plan costs memory in proportion to its size, not the network's.
    /// Changes are applied in order; ones that do not fit (unknown terminal, already connected, not connected) are
    /// skipped and reported. Circuits and services are unchanged: a plan does not reroute existing circuits.
    /// </summary>
    public (Graph View, IReadOnlyList<GraphChangeIssue> Issues) WithChanges(IReadOnlyList<GraphEdgeChange> changes)
    {
        var edges = _overlay is null ? [] : new Dictionary<int, (int[] Targets, EdgeKind[] Kinds)>(_overlay.Edges);
        var working = new Dictionary<int, (List<int> Targets, List<EdgeKind> Kinds)>();
        var issues = new List<GraphChangeIssue>();

        (List<int> Targets, List<EdgeKind> Kinds) Edit(int node)
        {
            if (!working.TryGetValue(node, out var list))
            {
                list = ([.. Neighbours(node)], [.. NeighbourKinds(node)]);
                working[node] = list;
            }
            return list;
        }

        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            if (!TryGetNode(change.A, out var a) || !TryGetNode(change.B, out var b))
            {
                issues.Add(new(i, change, GraphChangeProblem.UnknownTerminal));
                continue;
            }
            if (a == b)
            {
                issues.Add(new(i, change, GraphChangeProblem.SameTerminal));
                continue;
            }
            var ea = Edit(a);
            var eb = Edit(b);
            if (change.Add)
            {
                if (IndexOfConnection(ea, b) >= 0)
                {
                    issues.Add(new(i, change, GraphChangeProblem.AlreadyConnected));
                    continue;
                }
                if (ea.Kinds.Contains(change.Kind) || eb.Kinds.Contains(change.Kind))
                {
                    issues.Add(new(i, change, GraphChangeProblem.Occupied));
                    continue;
                }
                ea.Targets.Add(b);
                ea.Kinds.Add(change.Kind);
                eb.Targets.Add(a);
                eb.Kinds.Add(change.Kind);
            }
            else
            {
                var removed = RemoveConnections(ea, b);
                RemoveConnections(eb, a);
                if (removed == 0)
                {
                    issues.Add(new(i, change, GraphChangeProblem.NotConnected));
                }
            }
        }

        foreach (var (node, list) in working)
        {
            edges[node] = ([.. list.Targets], [.. list.Kinds]);
        }
        var view = (Graph)MemberwiseClone();
        view._overlay = new GraphOverlay(edges);
        view._base = Base;
        return (view, issues);
    }

    /// <summary>A connection, not the conductor joining a cable's two ends.</summary>
    private static int IndexOfConnection((List<int> Targets, List<EdgeKind> Kinds) edges, int target)
    {
        for (var i = 0; i < edges.Targets.Count; i++)
        {
            if (edges.Targets[i] == target && edges.Kinds[i] != EdgeKind.Conductor)
            {
                return i;
            }
        }
        return -1;
    }

    private static int RemoveConnections((List<int> Targets, List<EdgeKind> Kinds) edges, int target)
    {
        var removed = 0;
        for (var i = edges.Targets.Count - 1; i >= 0; i--)
        {
            if (edges.Targets[i] == target && edges.Kinds[i] != EdgeKind.Conductor)
            {
                edges.Targets.RemoveAt(i);
                edges.Kinds.RemoveAt(i);
                removed++;
            }
        }
        return removed;
    }
}
