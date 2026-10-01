using System.Text.Json;
using Cmdb.Graph;

namespace Cmdb.Api.Features.Plans;

/// <summary>
/// Removing a site, equipment or cable in a plan (#172). The object keeps its row with the lifecycle removed, and every
/// connection on its ports or conductor ends goes, so the graph changes by connections only. A site takes its equipment
/// and the cables that end at it along. Nothing that carries a circuit can be removed: the services would break without
/// anyone deciding it, so the circuits have to be moved first.
/// </summary>
public static class ObjectRemoval
{
    /// <summary>The equipment and cables the removal takes: the object itself, or for a site what the payload lists.</summary>
    public static (long[] Equipment, long[] Cables) Objects(JsonElement payload) => payload.GetProperty("type").GetString() switch
    {
        "equipment" => ([payload.GetProperty("id").GetInt64()], []),
        "cable" => ([], [payload.GetProperty("id").GetInt64()]),
        _ => ([.. payload.GetProperty("equipment").EnumerateArray().Select(e => e.GetInt64())],
              [.. payload.GetProperty("cables").EnumerateArray().Select(c => c.GetInt64())]),
    };

    /// <summary>The ports and conductor ends of what is removed, as graph nodes in <paramref name="g"/>.</summary>
    public static List<int> Nodes(Cmdb.Graph.Graph g, long[] equipment, long[] cables)
    {
        var nodes = new List<int>();
        foreach (var id in equipment)
        {
            if (g.TryGetEquipment(id, out var e))
            {
                nodes.AddRange(g.PortsOf(e).ToArray());
            }
        }
        foreach (var id in cables)
        {
            if (g.TryGetCable(id, out var c))
            {
                nodes.AddRange(g.EndsOf(c).ToArray());
            }
        }
        return nodes;
    }

    /// <summary>Circuits through any of the nodes.</summary>
    public static HashSet<int> Circuits(Cmdb.Graph.Graph g, IEnumerable<int> nodes)
    {
        var circuits = new HashSet<int>();
        foreach (var node in nodes)
        {
            foreach (var circuit in g.CircuitsThrough(node))
            {
                circuits.Add(circuit);
            }
        }
        return circuits;
    }

    /// <summary>Every connection on the removed object's terminals goes; a conductor's own two ends stay together.</summary>
    public static IReadOnlyList<GraphChange> Changes(Cmdb.Graph.Graph g, PlanOp op)
    {
        var (equipment, cables) = Objects(op.Payload);
        var nodes = Nodes(g, equipment, cables);
        var removing = nodes.ToHashSet();
        var changes = new List<GraphChange>();
        var seen = new HashSet<(int, int)>();
        foreach (var node in nodes)
        {
            var targets = g.Neighbours(node);
            var kinds = g.NeighbourKinds(node);
            for (var e = 0; e < targets.Length; e++)
            {
                var other = targets[e];
                if (kinds[e] == EdgeKind.Conductor || (removing.Contains(other) && !seen.Add((Math.Min(node, other), Math.Max(node, other)))))
                {
                    continue;
                }
                changes.Add(new GraphEdgeChange(g.TerminalId(node), g.TerminalId(other), kinds[e], Add: false));
            }
        }
        return changes;
    }
}
