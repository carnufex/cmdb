using System.Text.Json;
using Cmdb.Graph;

namespace Cmdb.Api.Features.Plans;

/// <summary>One conductor of the cable being split: its number and its terminals at the A and B ends.</summary>
public sealed record SplitConductor(int Number, long A, long B);

/// <summary>
/// A site inserted into an existing cable (#168): the cable becomes two, A end → site and site → B end, and every
/// conductor is spliced through in the site unless it is to be terminated there. Connections at the old ends move to the
/// new cables' outer ends, and circuits through the cable get the new ends with the splice between them, so every
/// service runs exactly as before. Worked out against the graph as it is when the operation applies, so it also moves
/// connections earlier operations in the plan made.
/// </summary>
/// <remarks>
/// Planned ids (see <see cref="Planned"/>): the A part is the operation's object, the B part <see cref="SecondCable"/>.
/// Conductor k (1-based position, not number) of the A part has ends <c>Terminal(op, 2k-1)</c> (A) and <c>Terminal(op, 2k)</c>
/// (at the site); of the B part <c>Terminal(op, 2N+2k-1)</c> (at the site) and <c>Terminal(op, 2N+2k)</c> (B). Conductors are
/// <c>Conductor(op, k)</c> and <c>Conductor(op, N+k)</c>.
/// </remarks>
public static class CableSplit
{
    /// <summary>Terminal and conductor ranges per operation limit the cable's size.</summary>
    public const int MaxConductors = 2_000;

    public static long SecondCable(long op) => Planned.Terminal(op, Planned.PerObject - 1);

    public static long OuterA(long op, int k) => Planned.Terminal(op, (2 * k) - 1);

    public static long InnerA(long op, int k) => Planned.Terminal(op, 2 * k);

    public static long InnerB(long op, int n, int k) => Planned.Terminal(op, (2 * n) + (2 * k) - 1);

    public static long OuterB(long op, int n, int k) => Planned.Terminal(op, (2 * n) + (2 * k));

    public static IReadOnlyList<SplitConductor> Conductors(JsonElement payload) =>
        [.. payload.GetProperty("conductors").EnumerateArray().Select(c => new SplitConductor(c[0].GetInt32(), c[1].GetInt64(), c[2].GetInt64()))];

    public static IReadOnlySet<int> Terminated(JsonElement payload) =>
        payload.TryGetProperty("terminate", out var t) ? t.EnumerateArray().Select(x => x.GetInt32()).ToHashSet() : [];

    /// <summary>
    /// What the split does to the graph <paramref name="g"/> (the view just before the operation): the two new cables,
    /// connections moved off the old ends, the splices in the site, and the circuits through the cable rewritten.
    /// </summary>
    public static IReadOnlyList<GraphChange> Changes(Cmdb.Graph.Graph g, PlanOp op)
    {
        var conductors = Conductors(op.Payload);
        var terminated = Terminated(op.Payload);
        var n = conductors.Count;
        var changes = new List<GraphChange>
        {
            new GraphNewCable(Planned.ObjectId(op.Id), [.. conductors.Select((_, i) =>
                new GraphNewConductor(Planned.Conductor(op.Id, i + 1), OuterA(op.Id, i + 1), InnerA(op.Id, i + 1)))]),
            new GraphNewCable(SecondCable(op.Id), [.. conductors.Select((_, i) =>
                new GraphNewConductor(Planned.Conductor(op.Id, n + i + 1), InnerB(op.Id, n, i + 1), OuterB(op.Id, n, i + 1)))]),
        };

        var map = new Dictionary<long, long>();
        for (var i = 0; i < n; i++)
        {
            var c = conductors[i];
            var k = i + 1;
            map[c.A] = OuterA(op.Id, k);
            map[c.B] = OuterB(op.Id, n, k);
            foreach (var (old, replacement) in new[] { (c.A, OuterA(op.Id, k)), (c.B, OuterB(op.Id, n, k)) })
            {
                if (!g.TryGetNode(old, out var node))
                {
                    continue;
                }
                var targets = g.Neighbours(node);
                var kinds = g.NeighbourKinds(node);
                for (var e = 0; e < targets.Length; e++)
                {
                    if (kinds[e] == EdgeKind.Conductor)
                    {
                        continue;
                    }
                    var other = g.TerminalId(targets[e]);
                    changes.Add(new GraphEdgeChange(old, other, kinds[e], Add: false));
                    changes.Add(new GraphEdgeChange(replacement, other, kinds[e], Add: true));
                }
            }
            if (!terminated.Contains(c.Number))
            {
                changes.Add(new GraphEdgeChange(InnerA(op.Id, k), InnerB(op.Id, n, k), EdgeKind.Splice, Add: true));
            }
        }

        // Circuits through the old ends, with the new terminals and the splice in between.
        var circuits = new HashSet<int>();
        foreach (var c in conductors)
        {
            foreach (var end in new[] { c.A, c.B })
            {
                if (g.TryGetNode(end, out var node))
                {
                    foreach (var circuit in g.CircuitsThrough(node))
                    {
                        circuits.Add(circuit);
                    }
                }
            }
        }
        if (circuits.Count > 0)
        {
            var inner = new Dictionary<(long, long), long[]>();
            for (var i = 0; i < n; i++)
            {
                var (c, k) = (conductors[i], i + 1);
                inner[(c.A, c.B)] = [InnerA(op.Id, k), InnerB(op.Id, n, k)];
                inner[(c.B, c.A)] = [InnerB(op.Id, n, k), InnerA(op.Id, k)];
            }
            changes.Add(new GraphCircuitsChange([.. circuits.Order().Select(circuit => new GraphCircuit(
                g.CircuitId(circuit),
                g.LayerOf(circuit),
                RewriteHops([.. g.HopsOf(circuit).ToArray().Select(g.TerminalId)], map, inner),
                [.. g.CarriersOf(circuit).ToArray().Select(g.CircuitId)],
                [.. g.ServicesOf(circuit).ToArray().Select(g.ServiceId)]))], []));
        }
        return changes;
    }

    /// <summary>
    /// A circuit's hops with the cable split: each old end becomes the new outer end, and where the circuit runs along a
    /// conductor (its two old ends next to each other) the two inner ends at the site go in between.
    /// </summary>
    public static IReadOnlyList<long> RewriteHops(IReadOnlyList<long> hops, IReadOnlyDictionary<long, long> outer,
        IReadOnlyDictionary<(long, long), long[]> inner)
    {
        var result = new List<long>(hops.Count + 2);
        for (var i = 0; i < hops.Count; i++)
        {
            result.Add(outer.GetValueOrDefault(hops[i], hops[i]));
            if (i + 1 < hops.Count && inner.TryGetValue((hops[i], hops[i + 1]), out var between))
            {
                result.AddRange(between);
            }
        }
        return result;
    }
}
