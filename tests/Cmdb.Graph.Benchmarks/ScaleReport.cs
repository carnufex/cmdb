using System.Diagnostics;
using System.Globalization;
using Cmdb.Catalog;
using Cmdb.DataGen;

namespace Cmdb.Graph.Benchmarks;

/// <summary>
/// How the graph grows with the network (#81, #119, #123): build time, memory at rest, snapshot size and read time,
/// change batches as a delta (patches, and an installation: new equipment with 24 patched ports and a circuit over them
/// that rides on an existing one and carries a service), folding the installation into the arrays, and a rebuild from
/// rows, with how far the managed heap rises (garbage included) while folding and rebuilding. A second table does the
/// same for a removal and a move: equipment and a cable removed with their connections, and equipment moved to another
/// site, which renumbers the arrays when folded in.
/// <c>dotnet run -c Release --project tests/Cmdb.Graph.Benchmarks -- scale full 2x 4x</c>
/// </summary>
internal static class ScaleReport
{
    public static void Run(IReadOnlyList<string> scales)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("| Skala | Terminaler | Kanter | Bygga | Minne i vila | Snapshot | Läsa snapshot | Delta 2 terminaler | Delta 100 terminaler | Delta installation | Kompaktering av delta | Topp | Ombyggnad från rader | Topp |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        var structure = new List<string>();
        foreach (var scale in scales)
        {
            structure.Add(Measure(scale));
        }
        Console.WriteLine();
        Console.WriteLine("| Skala | Delta borttag och flytt | Kompaktering av delta | Topp | Ombyggnad från rader | Topp |");
        Console.WriteLine("|---|---|---|---|---|---|");
        structure.ForEach(Console.WriteLine);
    }

    private static string Measure(string scaleName)
    {
        var (data, free) = Generate(scaleName);
        var idle = Heap();
        var sw = Stopwatch.StartNew();
        var graph = GraphBuilder.Build(data, "0");
        var build = sw.Elapsed;
        data = null;
        var rest = Heap() - idle;

        var path = Path.GetTempFileName();
        GraphSnapshot.WriteFile(graph, path);
        var snapshotBytes = new FileInfo(path).Length;
        sw.Restart();
        var read = GraphSnapshot.ReadFile(path, expectedVersion: null);
        var readTime = sw.Elapsed;
        read = null;
        File.Delete(path);
        GC.KeepAlive(read);

        var small = Batch(free.Take(2).ToArray());
        var large = Batch(free.Take(100).ToArray());
        var deltaSmall = Median(() => GraphChanges.TryDelta(graph, small) ?? throw new InvalidOperationException("not a delta"));
        var deltaLarge = Median(() => GraphChanges.TryDelta(graph, large) ?? throw new InvalidOperationException("not a delta"));

        var installation = Installation(graph, free.Skip(100).Take(24).ToArray());
        var deltaInstallation = Median(() => GraphChanges.TryDelta(graph, installation) ?? throw new InvalidOperationException("not a delta"));

        var delta = GraphChanges.TryDelta(graph, installation)!;
        var (flatten, flattenPeak) = Peak(() => GraphChanges.Flatten(delta, [installation]));
        delta = null;
        var (rebuild, rebuildPeak) = Peak(() => GraphChanges.Compact(graph, [installation]));

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"| {scaleName} | {graph.NodeCount / 1e6:0.0} M | {graph.EdgeCount / 1e6:0.0} M | {build.TotalSeconds:0.0} s | {Mb(rest)} | {Mb(snapshotBytes)} | {readTime.TotalSeconds:0.0} s | {deltaSmall:0.00} ms | {deltaLarge:0.00} ms | {deltaInstallation:0.00} ms | {flatten.TotalSeconds:0.00} s | +{Mb(flattenPeak)} | {rebuild.TotalSeconds:0.0} s | +{Mb(rebuildPeak)} |"));

        var removal = RemovalAndMove(graph);
        var deltaRemoval = Median(() => GraphChanges.TryDelta(graph, removal) ?? throw new InvalidOperationException("not a delta"));
        var removed = GraphChanges.TryDelta(graph, removal)!;
        var (flattenRemoval, flattenRemovalPeak) = Peak(() => GraphChanges.Flatten(removed, [removal]));
        removed = null;
        var (rebuildRemoval, rebuildRemovalPeak) = Peak(() => GraphChanges.Compact(graph, [removal]));
        GC.KeepAlive(graph);
        GC.KeepAlive(removed);
        return string.Create(CultureInfo.InvariantCulture,
            $"| {scaleName} | {deltaRemoval:0.00} ms | {flattenRemoval.TotalSeconds:0.00} s | +{Mb(flattenRemovalPeak)} | {rebuildRemoval.TotalSeconds:0.0} s | +{Mb(rebuildRemovalPeak)} |");
    }

    /// <summary>
    /// Equipment and a cable that carry no circuits removed with their connections, and equipment moved to the first
    /// equipment's site, as the change feed would read it (#123).
    /// </summary>
    private static GraphChangeBatch RemovalAndMove(Graph graph)
    {
        bool Free(ReadOnlySpan<int> nodes)
        {
            foreach (var node in nodes)
            {
                if (graph.CircuitsThrough(node).Length > 0)
                {
                    return false;
                }
            }
            return nodes.Length > 0;
        }
        var site = graph.SiteIndexOfEquipment(0);
        var equipment = Enumerable.Range(0, graph.EquipmentIds.Length).Where(e => Free(graph.PortsOf(e))).Take(2).ToArray();
        var moved = Enumerable.Range(0, graph.EquipmentIds.Length).First(e => graph.SiteIndexOfEquipment(e) != site && !equipment.Contains(e));
        var cable = Enumerable.Range(0, graph.CableIds.Length).First(c => Free(graph.EndsOf(c)));

        var keys = new GraphKeys();
        var rows = new GraphData();
        keys.Equipment.UnionWith([graph.EquipmentId(equipment[0]), graph.EquipmentId(moved)]);
        keys.Cables.Add(graph.CableId(cable));
        rows.EquipmentIds.Add(graph.EquipmentId(moved));
        rows.EquipmentSites.Add(graph.SiteId(site));
        foreach (var port in graph.PortsOf(moved))
        {
            rows.PortTerminals.Add(graph.TerminalId(port));
            rows.PortEquipment.Add(graph.EquipmentId(moved));
        }
        // The removed terminals' connections go; their other ends keep the rest.
        var gone = new HashSet<int>([.. graph.PortsOf(equipment[0]), .. graph.EndsOf(cable)]);
        var others = new HashSet<int>();
        foreach (var node in gone)
        {
            keys.Terminals.Add(graph.TerminalId(node));
            foreach (var other in graph.Neighbours(node))
            {
                if (!gone.Contains(other))
                {
                    others.Add(other);
                }
            }
        }
        foreach (var node in others)
        {
            keys.Terminals.Add(graph.TerminalId(node));
            var targets = graph.Neighbours(node);
            var kinds = graph.NeighbourKinds(node);
            for (var i = 0; i < targets.Length; i++)
            {
                if (kinds[i] != EdgeKind.Conductor && !gone.Contains(targets[i]) && (node < targets[i] || !others.Contains(targets[i])))
                {
                    rows.ConnectionA.Add(graph.TerminalId(node));
                    rows.ConnectionB.Add(graph.TerminalId(targets[i]));
                    rows.ConnectionKinds.Add((byte)kinds[i]);
                    rows.ConnectionLifecycles.Add((byte)Lifecycle.InService);
                }
            }
        }
        return new GraphChangeBatch("1", false, keys, rows, keys.Count);
    }

    /// <summary>How long the work takes and how far the managed heap rose meanwhile, garbage included.</summary>
    private static (TimeSpan Time, long Peak) Peak(Func<Graph> work)
    {
        var start = Heap();
        var peak = start;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(() =>
        {
            while (!sampling.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
                Thread.Sleep(1);
            }
        });
        var sw = Stopwatch.StartNew();
        var result = work();
        var time = sw.Elapsed;
        peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
        sampling.Cancel();
        sampler.Wait();
        GC.KeepAlive(result);
        return (time, peak - start);
    }

    /// <summary>The network's rows, and ports without connections to patch in the change batches.</summary>
    private static (GraphData Data, long[] Free) Generate(string scaleName)
    {
        var network = NetworkBuilder.Build(1, Scale.Parse(scaleName), TypeCatalog.Embedded);
        var data = NetworkGraph.From(network);
        var connected = new HashSet<long>(data.ConnectionA);
        connected.UnionWith(data.ConnectionB);
        var free = data.PortTerminals.Where(p => !connected.Contains(p)).Take(124).ToArray();
        return (data, free);
    }

    /// <summary>
    /// New equipment at the first equipment's site, with 24 ports above every terminal id, each patched to one of
    /// <paramref name="free"/>, and a new circuit over the ports riding on the first circuit and carrying the first
    /// service, as the change feed would read it.
    /// </summary>
    private static GraphChangeBatch Installation(Graph graph, long[] free)
    {
        var keys = new GraphKeys();
        var rows = new GraphData();
        var equipment = graph.EquipmentIds[^1] + 1;
        keys.Equipment.Add(equipment);
        rows.EquipmentIds.Add(equipment);
        rows.EquipmentSites.Add(graph.SiteId(graph.SiteIndexOfEquipment(0)));
        for (var i = 0; i < free.Length; i++)
        {
            var port = graph.TerminalIds[^1] + 1 + i;
            rows.PortTerminals.Add(port);
            rows.PortEquipment.Add(equipment);
            keys.Terminals.UnionWith([port, free[i]]);
            rows.ConnectionA.Add(free[i]);
            rows.ConnectionB.Add(port);
            rows.ConnectionKinds.Add((byte)EdgeKind.Patch);
            rows.ConnectionLifecycles.Add((byte)Lifecycle.Planned);
        }
        var circuit = graph.CircuitIds[^1] + 1;
        keys.Circuits.Add(circuit);
        rows.CircuitIds.Add(circuit);
        rows.CircuitLayers.Add((byte)CircuitLayer.Transmission);
        rows.HopCircuits.AddRange(rows.PortTerminals.Select(_ => circuit));
        rows.HopTerminals.AddRange(rows.PortTerminals);
        rows.DependencyCircuits.Add(circuit);
        rows.DependencyCarriers.Add(graph.CircuitIds[0]);
        rows.ServiceCircuitServices.Add(graph.ServiceIds[0]);
        rows.ServiceCircuitCircuits.Add(circuit);
        return new GraphChangeBatch("1", false, keys, rows, keys.Count);
    }

    /// <summary>Patches between pairs of free ports, as the change feed would read them.</summary>
    private static GraphChangeBatch Batch(long[] ports)
    {
        var keys = new GraphKeys();
        var rows = new GraphData();
        for (var i = 0; i + 1 < ports.Length; i += 2)
        {
            keys.Terminals.UnionWith([ports[i], ports[i + 1]]);
            rows.ConnectionA.Add(ports[i]);
            rows.ConnectionB.Add(ports[i + 1]);
            rows.ConnectionKinds.Add((byte)EdgeKind.Patch);
            rows.ConnectionLifecycles.Add((byte)Lifecycle.InService);
        }
        return new GraphChangeBatch("1", false, keys, rows, keys.Terminals.Count);
    }

    private static double Median(Func<Graph> run)
    {
        run();
        var times = new List<double>();
        for (var i = 0; i < 21; i++)
        {
            var sw = Stopwatch.StartNew();
            run();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return times[times.Count / 2];
    }

    private static long Heap() => GC.GetTotalMemory(forceFullCollection: true);

    private static string Mb(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0} MB");
}
