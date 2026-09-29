namespace Cmdb.Graph;

/// <param name="Circuits">Every affected circuit, breadth first: the ones through the object, then those riding on them.</param>
/// <param name="ReachedFrom">For each entry in <paramref name="Circuits"/>, the position of the circuit it rides on, or -1 when hit directly.</param>
/// <param name="Direct">How many of the first circuits are hit directly.</param>
/// <param name="Services">Affected services, as service indexes, sorted.</param>
/// <param name="Via">For each service, the position in <paramref name="Circuits"/> of the nearest circuit carrying it.</param>
public sealed record ImpactResult(int[] Circuits, int[] ReachedFrom, int Direct, int[] Services, int[] Via)
{
    /// <summary>The circuits from the service's circuit down to the circuit hit directly.</summary>
    public List<int> PathOf(int serviceEntry)
    {
        var path = new List<int>();
        for (var at = Via[serviceEntry]; at >= 0; at = ReachedFrom[at])
        {
            path.Add(Circuits[at]);
        }
        return path;
    }
}

/// <summary>Impact analysis (#10). Pure functions over an immutable graph.</summary>
public static class GraphImpact
{
    /// <summary>What a cut of the cable affects: circuits through any of its conductor ends.</summary>
    public static ImpactResult OfCable(Graph g, int cable) => OfNodes(g, g.EndsOf(cable));

    /// <summary>What an outage of the equipment affects: circuits through any of its ports.</summary>
    public static ImpactResult OfEquipment(Graph g, int equipment) => OfNodes(g, g.PortsOf(equipment));

    /// <summary>What an outage of the site affects: circuits through any port of its equipment.</summary>
    public static ImpactResult OfSite(Graph g, int site)
    {
        var nodes = new List<int>();
        foreach (var equipment in g.EquipmentAt(site))
        {
            foreach (var port in g.PortsOf(equipment))
            {
                nodes.Add(port);
            }
        }
        return OfNodes(g, [.. nodes]);
    }

    public static ImpactResult OfNodes(Graph g, ReadOnlySpan<int> nodes)
    {
        var direct = new SortedSet<int>();
        foreach (var node in nodes)
        {
            foreach (var circuit in g.CircuitsThrough(node))
            {
                direct.Add(circuit);
            }
        }

        // Breadth first upwards, remembering where each circuit was reached from: the shortest path to a service.
        var circuits = new List<int>(direct);
        var reachedFrom = new List<int>(circuits.Count);
        for (var i = 0; i < circuits.Count; i++)
        {
            reachedFrom.Add(-1);
        }
        var seen = new HashSet<int>(circuits);
        for (var at = 0; at < circuits.Count; at++)
        {
            foreach (var up in g.DependentsOf(circuits[at]))
            {
                if (seen.Add(up))
                {
                    circuits.Add(up);
                    reachedFrom.Add(at);
                }
            }
        }

        var via = new SortedDictionary<int, int>();
        for (var at = 0; at < circuits.Count; at++)
        {
            foreach (var service in g.ServicesOf(circuits[at]))
            {
                via.TryAdd(service, at);
            }
        }
        return new ImpactResult([.. circuits], [.. reachedFrom], direct.Count, [.. via.Keys], [.. via.Values]);
    }
}
