using System.Diagnostics;
using System.Globalization;
using Cmdb.Catalog;
using Cmdb.DataGen;

namespace Cmdb.Graph.Benchmarks;

/// <summary>
/// How the graph grows with the network (#81): build time, memory at rest, snapshot size and read time, a change
/// batch as a delta, folding the delta into the arrays, and a rebuild from rows, with the managed heap's peak (over the
/// graph at rest, garbage included) while folding and rebuilding.
/// <c>dotnet run -c Release --project tests/Cmdb.Graph.Benchmarks -- scale full 2x 4x</c>
/// </summary>
internal static class ScaleReport
{
    public static void Run(IReadOnlyList<string> scales)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("| Skala | Terminaler | Kanter | Bygga | Minne i vila | Snapshot | Läsa snapshot | Delta 2 terminaler | Delta 100 terminaler | Kompaktering av delta | Topp | Ombyggnad från rader | Topp |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var scale in scales)
        {
            Measure(scale);
        }
    }

    private static void Measure(string scaleName)
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

        var delta = GraphChanges.TryDelta(graph, large)!;
        var (flatten, flattenPeak) = Peak(() => GraphChanges.Flatten(delta, [large]));
        delta = null;
        var (rebuild, rebuildPeak) = Peak(() => GraphChanges.Compact(graph, [large]));
        GC.KeepAlive(graph);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"| {scaleName} | {graph.NodeCount / 1e6:0.0} M | {graph.EdgeCount / 1e6:0.0} M | {build.TotalSeconds:0.0} s | {Mb(rest)} | {Mb(snapshotBytes)} | {readTime.TotalSeconds:0.0} s | {deltaSmall:0.00} ms | {deltaLarge:0.00} ms | {flatten.TotalSeconds:0.00} s | +{Mb(flattenPeak)} | {rebuild.TotalSeconds:0.0} s | +{Mb(rebuildPeak)} |"));
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
        var free = data.PortTerminals.Where(p => !connected.Contains(p)).Take(100).ToArray();
        return (data, free);
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
