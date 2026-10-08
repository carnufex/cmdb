using System.Globalization;
using Cmdb.Graph;

namespace Cmdb.Api.Tests.Graph;

/// <summary>
/// Removed, moved and rebuilt equipment and cables as a delta (#123): the graph answers like one rebuilt from rows, and
/// folding the delta in gives that graph byte for byte.
/// </summary>
public sealed class GraphStructureDeltaTests
{
    private static readonly EdgeKind[] Kinds = [EdgeKind.Patch, EdgeKind.Splice, EdgeKind.Termination];

    /// <summary>The network as the database holds it, from which batches and rebuilds are read.</summary>
    private sealed class Network
    {
        public SortedDictionary<long, long> Equipment { get; } = [];
        public SortedDictionary<long, long> Ports { get; } = [];
        public SortedDictionary<long, Lifecycle> Cables { get; } = [];
        public SortedDictionary<long, long> Conductors { get; } = [];
        public SortedDictionary<long, long> Ends { get; } = [];
        public Dictionary<(long A, long B), EdgeKind> Connections { get; } = [];
        public SortedDictionary<long, long[]> Circuits { get; } = [];

        public long NextId { get; set; } = 1000;

        public GraphData Rows(GraphKeys? keys = null)
        {
            var d = new GraphData();
            foreach (var (id, site) in Equipment.Where(e => keys?.Equipment.Contains(e.Key) != false))
            {
                d.EquipmentIds.Add(id);
                d.EquipmentSites.Add(site);
            }
            foreach (var (terminal, equipment) in Ports.Where(p => keys?.Equipment.Contains(p.Value) != false))
            {
                d.PortTerminals.Add(terminal);
                d.PortEquipment.Add(equipment);
            }
            foreach (var (id, lifecycle) in Cables.Where(c => keys?.Cables.Contains(c.Key) != false))
            {
                d.CableIds.Add(id);
                d.CableLifecycles.Add((byte)lifecycle);
            }
            foreach (var (id, cable) in Conductors.Where(c => keys?.Cables.Contains(c.Value) != false))
            {
                d.ConductorIds.Add(id);
                d.ConductorCables.Add(cable);
            }
            foreach (var (terminal, conductor) in Ends.Where(e => keys?.Cables.Contains(Conductors[e.Value]) != false))
            {
                d.EndTerminals.Add(terminal);
                d.EndConductors.Add(conductor);
            }
            foreach (var ((a, b), kind) in Connections.Where(c => keys is null || keys.Terminals.Contains(c.Key.A) || keys.Terminals.Contains(c.Key.B)))
            {
                d.ConnectionA.Add(a);
                d.ConnectionB.Add(b);
                d.ConnectionKinds.Add((byte)kind);
                d.ConnectionLifecycles.Add((byte)Lifecycle.InService);
            }
            foreach (var (id, hops) in Circuits.Where(c => keys?.Circuits.Contains(c.Key) != false))
            {
                d.CircuitIds.Add(id);
                d.CircuitLayers.Add((byte)CircuitLayer.Physical);
                foreach (var hop in hops)
                {
                    d.HopCircuits.Add(id);
                    d.HopTerminals.Add(hop);
                }
                d.ServiceCircuitServices.Add(id + 1_000_000);
                d.ServiceCircuitCircuits.Add(id);
            }
            return d;
        }

        public bool Exists(long terminal) => Ports.ContainsKey(terminal) || Ends.ContainsKey(terminal);

        /// <summary>Drops the connections on a terminal, as the database does when it goes; the other ends change too.</summary>
        public void Disconnect(long terminal, GraphKeys keys)
        {
            foreach (var pair in Connections.Keys.Where(p => p.A == terminal || p.B == terminal).ToArray())
            {
                Connections.Remove(pair);
                keys.Terminals.UnionWith([pair.A, pair.B]);
            }
            keys.Terminals.Add(terminal);
        }

        public bool CarriesCircuits(long terminal) => Circuits.Values.Any(h => h.Contains(terminal));

        public long[] EndsOfConductor(long conductor) => [.. Ends.Where(e => e.Value == conductor).Select(e => e.Key)];
    }

    /// <summary>Ten pieces of equipment at four sites, eight cables, connections, and circuits through some of it.</summary>
    private static Network Start(Random random)
    {
        var n = new Network();
        for (var e = 0; e < 10; e++)
        {
            n.Equipment[100 + e] = 10 + (e % 4);
            for (var p = 0; p < 6; p++)
            {
                n.Ports[(1000 * (e + 1)) + p] = 100 + e;
            }
        }
        for (var c = 0; c < 8; c++)
        {
            n.Cables[500 + c] = Lifecycle.InService;
            for (var k = 0; k < 3; k++)
            {
                var conductor = 600 + (10 * c) + k;
                n.Conductors[conductor] = 500 + c;
                n.Ends[50_000 + (2 * (conductor - 600))] = conductor;
                n.Ends[50_001 + (2 * (conductor - 600))] = conductor;
            }
        }
        n.NextId = 100_000;
        for (var i = 0; i < 60; i++)
        {
            Connect(random, n, new GraphKeys());
        }
        return n;
    }

    private static void Connect(Random random, Network n, GraphKeys keys)
    {
        var all = n.Ports.Keys.Concat(n.Ends.Keys).ToArray();
        var a = all[random.Next(all.Length)];
        var b = all[random.Next(all.Length)];
        var kind = Kinds[random.Next(Kinds.Length)];
        var pair = (Math.Min(a, b), Math.Max(a, b));
        if (a == b || n.Connections.ContainsKey(pair)
            || n.Connections.Any(c => (c.Key.A == a || c.Key.B == a || c.Key.A == b || c.Key.B == b) && c.Value == kind))
        {
            return;
        }
        n.Connections[pair] = kind;
        keys.Terminals.UnionWith([a, b]);
    }

    /// <summary>One random change to the network, with the keys the change stream would report for it.</summary>
    private static void Change(Random random, Network n, GraphKeys keys)
    {
        switch (random.Next(11))
        {
            case 9:
                {
                    // A new cable, its id and its ends' ids between the others' or above them.
                    var id = random.Next(2) == 0 ? n.NextId++ : random.Next(400, 599);
                    if (n.Cables.ContainsKey(id))
                    {
                        return;
                    }
                    n.Cables[id] = (Lifecycle)random.Next(4);
                    for (var k = random.Next(1, 4); k > 0; k--)
                    {
                        var conductor = n.NextId++;
                        n.Conductors[conductor] = id;
                        foreach (var end in new[] { random.Next(1, 999), random.Next(1, 999) })
                        {
                            n.Ends[n.Exists(end) ? n.NextId++ : end] = conductor;
                        }
                    }
                    keys.Cables.Add(id);
                    return;
                }
            case 8:
                {
                    // A cable changes lifecycle: its conductors' edges follow when the delta is folded in.
                    if (n.Cables.Count == 0)
                    {
                        return;
                    }
                    var id = n.Cables.Keys.ElementAt(random.Next(n.Cables.Count));
                    n.Cables[id] = (Lifecycle)random.Next(4);
                    keys.Cables.Add(id);
                    return;
                }
            case 0:
                {
                    // Remove equipment that carries no circuits.
                    var candidates = n.Equipment.Keys.Where(e => !n.Ports.Any(p => p.Value == e && n.CarriesCircuits(p.Key))).ToArray();
                    if (candidates.Length == 0 || n.Equipment.Count < 6)
                    {
                        return;
                    }
                    var id = candidates[random.Next(candidates.Length)];
                    foreach (var port in n.Ports.Where(p => p.Value == id).Select(p => p.Key).ToArray())
                    {
                        n.Disconnect(port, keys);
                        n.Ports.Remove(port);
                    }
                    n.Equipment.Remove(id);
                    keys.Equipment.Add(id);
                    return;
                }
            case 1:
                {
                    // Remove a cable that carries no circuits.
                    var candidates = n.Cables.Keys.Where(c => !n.Ends.Any(e => n.Conductors[e.Value] == c && n.CarriesCircuits(e.Key))).ToArray();
                    if (candidates.Length == 0 || n.Cables.Count < 5)
                    {
                        return;
                    }
                    var id = candidates[random.Next(candidates.Length)];
                    foreach (var conductor in n.Conductors.Where(c => c.Value == id).Select(c => c.Key).ToArray())
                    {
                        foreach (var end in n.EndsOfConductor(conductor))
                        {
                            n.Disconnect(end, keys);
                            n.Ends.Remove(end);
                        }
                        n.Conductors.Remove(conductor);
                    }
                    n.Cables.Remove(id);
                    keys.Cables.Add(id);
                    return;
                }
            case 2:
                {
                    // Move equipment, to a site in the graph or a new one, older or newer than the others.
                    if (n.Equipment.Count == 0)
                    {
                        return;
                    }
                    var id = n.Equipment.Keys.ElementAt(random.Next(n.Equipment.Count));
                    n.Equipment[id] = random.Next(3) == 0 ? random.Next(1, 30) : 10 + random.Next(5);
                    keys.Equipment.Add(id);
                    return;
                }
            case 3:
                {
                    // Rebuild equipment: a port without circuits goes, a new one comes.
                    if (n.Equipment.Count == 0)
                    {
                        return;
                    }
                    var id = n.Equipment.Keys.ElementAt(random.Next(n.Equipment.Count));
                    var ports = n.Ports.Where(p => p.Value == id && !n.CarriesCircuits(p.Key)).Select(p => p.Key).ToArray();
                    if (ports.Length > 0 && random.Next(2) == 0)
                    {
                        var port = ports[random.Next(ports.Length)];
                        n.Disconnect(port, keys);
                        n.Ports.Remove(port);
                    }
                    // A new port, or one taken from other equipment: its connections are read again with it.
                    var added = random.Next(2) == 0 ? n.NextId++ : random.Next(1, 999);
                    if (n.Ends.ContainsKey(added) || n.CarriesCircuits(added))
                    {
                        return;
                    }
                    if (n.Ports.TryGetValue(added, out var owner))
                    {
                        keys.Equipment.Add(owner);
                        keys.Terminals.Add(added);
                    }
                    n.Ports[added] = id;
                    keys.Equipment.Add(id);
                    return;
                }
            case 4:
                {
                    // Rebuild a cable: a conductor without circuits gets new ends, or a new conductor comes.
                    if (n.Cables.Count == 0)
                    {
                        return;
                    }
                    var id = n.Cables.Keys.ElementAt(random.Next(n.Cables.Count));
                    var conductors = n.Conductors.Where(c => c.Value == id && !n.EndsOfConductor(c.Key).Any(n.CarriesCircuits))
                        .Select(c => c.Key).ToArray();
                    if (conductors.Length > 0 && random.Next(2) == 0)
                    {
                        var conductor = conductors[random.Next(conductors.Length)];
                        var end = n.EndsOfConductor(conductor)[random.Next(2)];
                        n.Disconnect(end, keys);
                        n.Ends.Remove(end);
                        n.Ends[n.NextId++] = conductor;
                    }
                    else
                    {
                        var conductor = n.NextId++;
                        var end = random.Next(1, 999);
                        n.Conductors[conductor] = id;
                        n.Ends[n.NextId++] = conductor;
                        n.Ends[n.Exists(end) ? n.NextId++ : end] = conductor;
                    }
                    keys.Cables.Add(id);
                    return;
                }
            case 5:
                {
                    // New equipment at a random site, with ports between the others' ids or above them.
                    var id = random.Next(2) == 0 ? n.NextId++ : random.Next(1, 99);
                    if (n.Equipment.ContainsKey(id))
                    {
                        return;
                    }
                    n.Equipment[id] = random.Next(1, 30);
                    for (var p = 0; p < 3; p++)
                    {
                        var terminal = random.Next(2) == 0 ? n.NextId++ : random.Next(1, 999);
                        if (!n.Exists(terminal))
                        {
                            n.Ports[terminal] = id;
                        }
                    }
                    keys.Equipment.Add(id);
                    return;
                }
            case 6:
                {
                    // A circuit over a few connected terminals, or one removed.
                    if (n.Circuits.Count > 0 && random.Next(2) == 0)
                    {
                        var id = n.Circuits.Keys.ElementAt(random.Next(n.Circuits.Count));
                        n.Circuits.Remove(id);
                        keys.Circuits.Add(id);
                        return;
                    }
                    if (n.Connections.Count == 0)
                    {
                        return;
                    }
                    var (a, b) = n.Connections.Keys.ElementAt(random.Next(n.Connections.Count));
                    var circuit = n.NextId++;
                    n.Circuits[circuit] = [a, b];
                    keys.Circuits.Add(circuit);
                    return;
                }
            default:
                {
                    // Rewiring.
                    if (n.Connections.Count > 0 && random.Next(3) == 0)
                    {
                        var pair = n.Connections.Keys.ElementAt(random.Next(n.Connections.Count));
                        if (!n.Circuits.Values.Any(h => h.Contains(pair.A) && h.Contains(pair.B)))
                        {
                            n.Connections.Remove(pair);
                            keys.Terminals.UnionWith([pair.A, pair.B]);
                        }
                        return;
                    }
                    Connect(random, n, keys);
                    return;
                }
        }
    }

    /// <summary>Everything a reader can ask about the ids in the network so far, by external id.</summary>
    private static string Describe(Cmdb.Graph.Graph g, IEnumerable<long> terminals, IEnumerable<long> equipment, IEnumerable<long> cables,
        IEnumerable<long> sites, IEnumerable<long> circuits)
    {
        var lines = new List<string>();
        foreach (var id in terminals.Distinct().Order())
        {
            if (!g.TryGetNode(id, out var node))
            {
                lines.Add($"t{id} -");
                continue;
            }
            var targets = g.Neighbours(node);
            var kinds = g.NeighbourKinds(node);
            var edges = new List<string>();
            for (var i = 0; i < targets.Length; i++)
            {
                edges.Add($"{g.TerminalId(targets[i])}/{kinds[i]}");
            }
            edges.Sort(StringComparer.Ordinal);
            var circuitsThrough = g.CircuitsThrough(node).ToArray().Select(g.CircuitId).Order();
            lines.Add($"t{id} {g.KindOf(node)} e{g.EquipmentOf(node)} c{g.CableOf(node)} s{g.SiteOf(node)}: {string.Join(' ', edges)} | {string.Join(' ', circuitsThrough)}");
        }
        foreach (var id in equipment.Distinct().Order())
        {
            lines.Add(g.TryGetEquipment(id, out var e)
                ? $"e{id} s{g.SiteId(g.SiteIndexOfEquipment(e))}: {string.Join(' ', g.PortsOf(e).ToArray().Select(g.TerminalId).Order())}"
                : $"e{id} -");
        }
        foreach (var id in cables.Distinct().Order())
        {
            lines.Add(g.TryGetCable(id, out var c) ? $"c{id}: {string.Join(' ', g.EndsOf(c).ToArray().Select(g.TerminalId).Order())}" : $"c{id} -");
        }
        foreach (var id in sites.Distinct().Order())
        {
            lines.Add(g.TryGetSite(id, out var s)
                ? $"s{id}: {string.Join(' ', g.EquipmentAt(s).ToArray().Select(g.EquipmentId).Order())}"
                : $"s{id} -");
        }
        foreach (var id in circuits.Distinct().Order())
        {
            lines.Add(g.TryGetCircuit(id, out var c) ? $"k{id}: {string.Join(' ', g.HopsOf(c).ToArray().Select(g.TerminalId))}" : $"k{id} -");
        }
        return string.Join('\n', lines);
    }

    private static byte[] Bytes(Cmdb.Graph.Graph g)
    {
        using var stream = new MemoryStream();
        GraphSnapshot.Write(g, stream);
        return stream.ToArray();
    }

    [Theory]
    [InlineData(123)]
    [InlineData(172)]
    [InlineData(187)]
    public void Random_removals_moves_and_rebuilds_as_deltas_give_the_graph_a_rebuild_gives(int seed)
    {
        var random = new Random(seed);
        var n = Start(random);
        var production = GraphBuilder.Build(n.Rows(), "0");
        var graph = production;
        var baseGraph = production;
        var batches = new List<GraphChangeBatch>();
        HashSet<long> terminals = [.. n.Ports.Keys, .. n.Ends.Keys], equipment = [.. n.Equipment.Keys], cables = [.. n.Cables.Keys];
        HashSet<long> sites = [.. n.Equipment.Values], circuits = [];

        for (var round = 1; round <= 120; round++)
        {
            var keys = new GraphKeys();
            for (var step = random.Next(1, 4); step > 0; step--)
            {
                Change(random, n, keys);
            }
            var watermark = round.ToString(CultureInfo.InvariantCulture);
            var batch = new GraphChangeBatch(watermark, false, keys, n.Rows(keys), keys.Count);
            batches.Add(batch);
            terminals.UnionWith([.. n.Ports.Keys, .. n.Ends.Keys, .. keys.Terminals]);
            equipment.UnionWith(keys.Equipment);
            cables.UnionWith(keys.Cables);
            sites.UnionWith(n.Equipment.Values);
            circuits.UnionWith(keys.Circuits);

            graph = GraphChanges.TryDelta(graph, batch).ShouldNotBeNull($"round {round}");
            graph.Version.ShouldBe(watermark);

            var rebuilt = GraphBuilder.Build(n.Rows(), watermark);
            Describe(graph, terminals, equipment, cables, sites, circuits)
                .ShouldBe(Describe(rebuilt, terminals, equipment, cables, sites, circuits), $"round {round}");

            if (round % 30 == 0)
            {
                // Folding in, as production does now and then, and carrying on from the folded graph.
                var flat = GraphChanges.Flatten(graph, batches);
                flat.IsOverlay.ShouldBeFalse();
                Bytes(flat).ShouldBe(Bytes(rebuilt), $"round {round}");
                Bytes(GraphChanges.Compact(baseGraph, batches)).ShouldBe(Bytes(rebuilt), $"round {round}");
                (graph, baseGraph) = (flat, flat);
                batches.Clear();
            }
        }
    }

    /// <summary>Equipment 100 (ports 1–3) and 101 (port 4) at sites 1 and 2, cable 500 (ends 11, 12), all patched in a line.</summary>
    private static Network Small()
    {
        var n = new Network();
        n.Equipment[100] = 1;
        n.Equipment[101] = 2;
        n.Ports[1] = 100;
        n.Ports[2] = 100;
        n.Ports[3] = 100;
        n.Ports[4] = 101;
        n.Cables[500] = Lifecycle.InService;
        n.Conductors[600] = 500;
        n.Ends[11] = 600;
        n.Ends[12] = 600;
        n.Connections[(1, 11)] = EdgeKind.Splice;
        n.Connections[(4, 12)] = EdgeKind.Splice;
        n.Connections[(2, 4)] = EdgeKind.Patch;
        return n;
    }

    [Fact]
    public void Removed_equipment_and_cables_leave_the_graph_and_a_site_with_them()
    {
        var n = Small();
        var production = GraphBuilder.Build(n.Rows(), "1");
        var keys = new GraphKeys();
        n.Disconnect(4, keys);
        n.Ports.Remove(4);
        n.Equipment.Remove(101);
        keys.Equipment.Add(101);
        n.Disconnect(11, keys);
        n.Disconnect(12, keys);
        n.Ends.Remove(11);
        n.Ends.Remove(12);
        n.Conductors.Remove(600);
        n.Cables.Remove(500);
        keys.Cables.Add(500);
        var batch = new GraphChangeBatch("2", false, keys, n.Rows(keys), keys.Count);

        var delta = GraphChanges.TryDelta(production, batch).ShouldNotBeNull();

        delta.TryGetEquipment(101, out _).ShouldBeFalse();
        delta.TryGetCable(500, out _).ShouldBeFalse();
        delta.TryGetNode(4, out _).ShouldBeFalse();
        delta.TryGetNode(11, out _).ShouldBeFalse();
        delta.TryGetSite(2, out _).ShouldBeFalse();
        delta.TryGetNode(1, out var port).ShouldBeTrue();
        delta.Neighbours(port).Length.ShouldBe(0);
        delta.TryGetNode(2, out port);
        GraphTrace.Physical(delta, port).Nodes.Select(delta.TerminalId).ShouldBe([2]);
        // Production is untouched, and plans made on the delta do not see what it removed.
        production.TryGetEquipment(101, out _).ShouldBeTrue();
        var (_, issues) = delta.WithChanges([new GraphEdgeChange(3, 4, EdgeKind.Patch, Add: true)]);
        issues.Single().Problem.ShouldBe(GraphChangeProblem.UnknownTerminal);

        var flat = GraphChanges.Flatten(delta, [batch]);
        Bytes(flat).ShouldBe(Bytes(GraphBuilder.Build(n.Rows(), "2")));
        flat.SiteCount.ShouldBe(1);
        flat.CableCount.ShouldBe(0);
    }

    [Fact]
    public void Moved_equipment_keeps_its_ports_and_connections_and_changes_site()
    {
        var n = Small();
        var production = GraphBuilder.Build(n.Rows(), "1");
        n.Equipment[101] = 3;
        var keys = new GraphKeys();
        keys.Equipment.Add(101);
        var batch = new GraphChangeBatch("2", false, keys, n.Rows(keys), 1);

        var delta = GraphChanges.TryDelta(production, batch).ShouldNotBeNull();

        delta.TryGetEquipment(101, out var equipment).ShouldBeTrue();
        delta.SiteId(delta.SiteIndexOfEquipment(equipment)).ShouldBe(3);
        delta.TryGetNode(4, out var port).ShouldBeTrue();
        delta.SiteOf(port).ShouldBe(3);
        delta.TryGetSite(2, out _).ShouldBeFalse();
        delta.TryGetSite(3, out var site).ShouldBeTrue();
        delta.EquipmentAt(site).ToArray().ShouldBe([equipment]);
        delta.TryGetNode(1, out port);
        GraphTrace.Physical(delta, port).Nodes.Select(delta.TerminalId).ShouldBe([1, 11, 12, 4, 2]);

        // A plan made on top sees the move; one that moves it back fits.
        var (plan, issues) = delta.WithChanges([new GraphNewEquipment(-1, 2, [-11])]);
        issues.ShouldBeEmpty();
        plan.TryGetSite(2, out site).ShouldBeTrue();
        plan.EquipmentAt(site).ToArray().Select(plan.EquipmentId).ShouldBe([-1]);

        Bytes(GraphChanges.Flatten(delta, [batch])).ShouldBe(Bytes(GraphBuilder.Build(n.Rows(), "2")));
    }

    [Fact]
    public void A_removed_terminal_a_circuit_still_passes_waits_for_a_rebuild()
    {
        var n = Small();
        n.Circuits[7000] = [2, 4];
        var production = GraphBuilder.Build(n.Rows(), "1");
        var keys = new GraphKeys();
        n.Disconnect(4, keys);
        n.Ports.Remove(4);
        keys.Equipment.Add(101);

        GraphChanges.TryDelta(production, new GraphChangeBatch("2", false, keys, n.Rows(keys), keys.Count)).ShouldBeNull();

        // With the circuit removed in the same batch, it fits.
        n.Circuits.Remove(7000);
        keys.Circuits.Add(7000);
        var batch = new GraphChangeBatch("2", false, keys, n.Rows(keys), keys.Count);
        var delta = GraphChanges.TryDelta(production, batch).ShouldNotBeNull();
        delta.TryGetCircuit(7000, out _).ShouldBeFalse();
        Bytes(GraphChanges.Flatten(delta, [batch])).ShouldBe(Bytes(GraphBuilder.Build(n.Rows(), "2")));
    }

    [Fact]
    public void A_port_moved_to_other_equipment_keeps_its_connections_only_when_the_batch_read_them()
    {
        var n = Small();
        var production = GraphBuilder.Build(n.Rows(), "1");
        n.Ports[2] = 101;
        var keys = new GraphKeys();
        keys.Equipment.UnionWith([100, 101]);

        // Port 2 is patched to 4; without its connections in the batch the delta cannot know them.
        GraphChanges.TryDelta(production, new GraphChangeBatch("2", false, keys, n.Rows(keys), 2)).ShouldBeNull();

        keys.Terminals.Add(2);
        var batch = new GraphChangeBatch("2", false, keys, n.Rows(keys), 3);
        var delta = GraphChanges.TryDelta(production, batch).ShouldNotBeNull();
        delta.TryGetNode(2, out var port).ShouldBeTrue();
        delta.EquipmentOf(port).ShouldBe(101);
        delta.Neighbours(port).ToArray().Select(delta.TerminalId).ShouldBe([4]);
        Bytes(GraphChanges.Flatten(delta, [batch])).ShouldBe(Bytes(GraphBuilder.Build(n.Rows(), "2")));
    }
}
