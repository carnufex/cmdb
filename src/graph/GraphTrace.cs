namespace Cmdb.Graph;

/// <summary>Why a physical walk stopped where it did, at one end.</summary>
public enum TraceEnd : byte
{
    /// <summary>No further connection: active equipment, or an unterminated conductor.</summary>
    Endpoint,

    /// <summary>More than one way on; the walk does not guess.</summary>
    Branch,

    /// <summary>Came back to a terminal already on the path.</summary>
    Loop,

    /// <summary>Hit the hop limit.</summary>
    Limit,

    /// <summary>The next terminal is outside the caller's access scope (#22).</summary>
    Boundary,
}

/// <param name="Nodes">The terminals in order from one end to the other; the start is somewhere in between.</param>
/// <param name="EdgesBefore">The edge kind into each node from the one before; the first entry is meaningless.</param>
public sealed record PhysicalPath(int[] Nodes, EdgeKind[] EdgesBefore, int StartIndex, TraceEnd FirstEnd, TraceEnd LastEnd)
{
    public bool Complete => FirstEnd == TraceEnd.Endpoint && LastEnd == TraceEnd.Endpoint;
}

/// <param name="Parent">The circuit this one carries, or -1 at the top.</param>
public sealed record CircuitStep(int Circuit, int Depth, int Parent);

/// <summary>Traversals for tracing (#9). Pure functions over an immutable graph.</summary>
public static class GraphTrace
{
    public const int MaxHops = 2_000;

    /// <summary>
    /// Follows the signal from a terminal in both directions through patches, splices, internal connections and
    /// conductors until each side ends at active equipment, splits, loops or hits the limit.
    /// </summary>
    /// <param name="visible">Terminals the caller may see; the walk stops before any other (#22). Null means all.</param>
    public static PhysicalPath Physical(Graph g, int start, Func<int, bool>? visible = null)
    {
        var neighbours = g.Neighbours(start);
        var kinds = g.NeighbourKinds(start);
        var onPath = new HashSet<int> { start };
        // From an end the path reads away from the start; from the middle it reads end → start → end.
        var (first, firstKinds, firstEnd) = neighbours.Length switch
        {
            2 => Walk(g, start, neighbours[0], kinds[0], onPath, visible),
            > 2 => ([], [], TraceEnd.Branch),
            _ => ([], [], TraceEnd.Endpoint),
        };
        var (last, lastKinds, lastEnd) = neighbours.Length switch
        {
            0 => ([], [], TraceEnd.Endpoint),
            > 2 => ([], [], TraceEnd.Branch),
            _ => Walk(g, start, neighbours[^1], kinds[^1], onPath, visible),
        };

        // first runs away from the start: reverse it so the path reads end → start → end.
        var nodes = new int[first.Count + 1 + last.Count];
        var edges = new EdgeKind[nodes.Length];
        for (var i = 0; i < first.Count; i++)
        {
            nodes[first.Count - 1 - i] = first[i];
        }
        // The edge into node k (from k-1) on the reversed side is the edge that led out of node k during the walk.
        for (var i = 1; i < first.Count; i++)
        {
            edges[first.Count - i] = firstKinds[i];
        }
        nodes[first.Count] = start;
        if (first.Count > 0)
        {
            edges[first.Count] = firstKinds[0];
        }
        for (var i = 0; i < last.Count; i++)
        {
            nodes[first.Count + 1 + i] = last[i];
            edges[first.Count + 1 + i] = lastKinds[i];
        }
        return new PhysicalPath(nodes, edges, first.Count, firstEnd, lastEnd);
    }

    /// <summary>Walks away from <paramref name="from"/> starting at <paramref name="next"/>; returns the nodes and the edge into each.</summary>
    private static (List<int> Nodes, List<EdgeKind> Kinds, TraceEnd End) Walk(
        Graph g, int from, int next, EdgeKind kind, HashSet<int> onPath, Func<int, bool>? visible)
    {
        var nodes = new List<int>();
        var kinds = new List<EdgeKind>();
        var prev = from;
        var cur = next;
        while (true)
        {
            if (visible is not null && !visible(cur))
            {
                return (nodes, kinds, TraceEnd.Boundary);
            }
            if (!onPath.Add(cur))
            {
                return (nodes, kinds, TraceEnd.Loop);
            }
            nodes.Add(cur);
            kinds.Add(kind);
            if (nodes.Count >= MaxHops)
            {
                return (nodes, kinds, TraceEnd.Limit);
            }

            var neighbours = g.Neighbours(cur);
            var neighbourKinds = g.NeighbourKinds(cur);
            var way = -1;
            var ways = 0;
            var skippedBack = false;
            for (var i = 0; i < neighbours.Length; i++)
            {
                if (!skippedBack && neighbours[i] == prev)
                {
                    skippedBack = true;
                    continue;
                }
                way = i;
                ways++;
            }
            if (ways == 0)
            {
                return (nodes, kinds, TraceEnd.Endpoint);
            }
            if (ways > 1)
            {
                return (nodes, kinds, TraceEnd.Branch);
            }
            prev = cur;
            cur = neighbours[way];
            kind = neighbourKinds[way];
        }
    }

    /// <summary>
    /// The circuits carrying a service and, depth first, the circuits each rides on: logical over transmission over
    /// physical. Each circuit appears once.
    /// </summary>
    /// <param name="visible">Circuits the caller may see; others are left out with what they ride on (#22).</param>
    public static List<CircuitStep> Service(Graph g, int service, Func<int, bool>? visible = null)
    {
        var steps = new List<CircuitStep>();
        var seen = new HashSet<int>();
        foreach (var circuit in g.CircuitsOf(service))
        {
            Down(g, circuit, 0, -1, steps, seen, visible);
        }
        return steps;
    }

    /// <summary>A circuit and, depth first, what it rides on.</summary>
    public static List<CircuitStep> Circuit(Graph g, int circuit, Func<int, bool>? visible = null)
    {
        var steps = new List<CircuitStep>();
        Down(g, circuit, 0, -1, steps, [], visible);
        return steps;
    }

    private static void Down(Graph g, int circuit, int depth, int parent, List<CircuitStep> steps, HashSet<int> seen, Func<int, bool>? visible)
    {
        if (!seen.Add(circuit) || (visible is not null && !visible(circuit)))
        {
            return;
        }
        steps.Add(new CircuitStep(circuit, depth, parent));
        foreach (var carrier in g.CarriersOf(circuit))
        {
            Down(g, carrier, depth + 1, circuit, steps, seen, visible);
        }
    }

    /// <summary>Services whose circuits pass the node, directly or through circuits riding on those.</summary>
    public static List<int> ServicesThrough(Graph g, int node)
    {
        var circuits = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (var c in g.CircuitsThrough(node))
        {
            if (circuits.Add(c))
            {
                queue.Enqueue(c);
            }
        }
        while (queue.TryDequeue(out var c))
        {
            foreach (var up in g.DependentsOf(c))
            {
                if (circuits.Add(up))
                {
                    queue.Enqueue(up);
                }
            }
        }
        var services = new SortedSet<int>();
        foreach (var c in circuits)
        {
            foreach (var s in g.ServicesOf(c))
            {
                services.Add(s);
            }
        }
        return [.. services];
    }
}
