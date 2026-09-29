using BenchmarkDotNet.Attributes;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.Graph;

namespace Cmdb.Graph.Benchmarks;

/// <summary>
/// The graph engine (#8) on generated networks, no database: building the arrays, a snapshot round trip, and the
/// primitives traversals are made of (id lookup and walking neighbours).
/// </summary>
[MemoryDiagnoser]
public class GraphBenchmarks
{
    private GraphData _data = null!;
    private Graph _graph = null!;
    private byte[] _snapshot = null!;
    private long[] _terminals = null!;

    [Params("medium", "full")]
    public string Scale { get; set; } = "medium";

    [GlobalSetup]
    public void Setup()
    {
        var network = NetworkBuilder.Build(1, Cmdb.DataGen.Scale.Parse(Scale), TypeCatalog.Embedded);
        _data = NetworkGraph.From(network);
        _graph = GraphBuilder.Build(_data, "bench");
        using var stream = new MemoryStream();
        GraphSnapshot.Write(_graph, stream);
        _snapshot = stream.ToArray();
        var rng = new Random(42);
        _terminals = Enumerable.Range(0, 1_000).Select(_ => _graph.TerminalId(rng.Next(_graph.NodeCount))).ToArray();
    }

    [Benchmark(Description = "Build graph from rows")]
    public Graph Build() => GraphBuilder.Build(_data, "bench");

    [Benchmark(Description = "Read snapshot")]
    public Graph? ReadSnapshot() => GraphSnapshot.Read(new MemoryStream(_snapshot));

    /// <summary>Resolve 1 000 external terminal ids to nodes.</summary>
    [Benchmark(Description = "Lookup x1000")]
    public int Lookup()
    {
        var found = 0;
        foreach (var id in _terminals)
        {
            if (_graph.TryGetNode(id, out _))
            {
                found++;
            }
        }
        return found;
    }

    /// <summary>
    /// Breadth-first walk from 1 000 terminals up to 12 hops, the shape of a trace: a lower bound for #9.
    /// </summary>
    [Benchmark(Description = "Walk 12 hops x1000")]
    public long Walk()
    {
        var visited = new HashSet<int>();
        var queue = new Queue<(int Node, int Depth)>();
        long total = 0;
        foreach (var id in _terminals)
        {
            _graph.TryGetNode(id, out var start);
            visited.Clear();
            queue.Enqueue((start, 0));
            visited.Add(start);
            while (queue.TryDequeue(out var item))
            {
                total++;
                if (item.Depth == 12)
                {
                    continue;
                }
                foreach (var next in _graph.Neighbours(item.Node))
                {
                    if (visited.Add(next))
                    {
                        queue.Enqueue((next, item.Depth + 1));
                    }
                }
            }
        }
        return total;
    }
}
