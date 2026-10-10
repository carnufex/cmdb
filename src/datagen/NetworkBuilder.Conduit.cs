namespace Cmdb.DataGen;

/// <summary>
/// Conduit (#235, ADR-0014): route segments, ducts, subducts and the cables' paths through them. Long cables (backbone and
/// rings) are laid along a grid of corridors with a manhole at each crossing, about every ten kilometres, so cables heading
/// the same way share trench: a hub's links and its rings leave it in the same corridors. An access cable gets a trench of
/// its own along its route, shared with any other cable between the same two sites. Runs last, on a random source of its
/// own, so everything generated before it keeps its ids and values; only the long cables' routes follow their corridors.
/// </summary>
internal sealed partial class NetworkBuilder
{
    private const double CorridorGrid = 10_000;
    private static readonly bool[] DiagonalFirst = [true, false];

    private sealed class Segment(long id, Site a, Site b, double[] coordinates, bool corridor)
    {
        public long Id { get; } = id;
        public Site A { get; } = a;
        public Site B { get; } = b;
        public double[] Coordinates { get; } = coordinates;
        public bool Corridor { get; } = corridor;
        public List<Cable> Cables { get; } = [];
        public List<long> Free { get; } = [];
    }

    private void BuildConduit(List<Cable> longCables)
    {
        var rng = new Random(_seed ^ 0x2C0D);
        var corridorDuct = _profile.CorridorDuct!;
        var accessDuct = _profile.AccessDuct!;
        var microDuct = _profile.MicroDuct!;
        var long_ = longCables.ToHashSet();
        var segments = new Dictionary<(long, long), Segment>();
        var nodes = new Dictionary<(int, int), Site>();

        Site Node((int I, int J) cell)
        {
            if (!nodes.TryGetValue(cell, out var node))
            {
                // A little off the grid, so corridors do not look ruled, and always on land.
                var (x, y) = (cell.I * CorridorGrid, cell.J * CorridorGrid);
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    var (jx, jy) = (x + ((rng.NextDouble() - 0.5) * 3_000), y + ((rng.NextDouble() - 0.5) * 3_000));
                    if (_territory.Contains(jx, jy))
                    {
                        (x, y) = (jx, jy);
                        break;
                    }
                }
                var n = NextCode("BR");
                node = NewSite(SiteKind.Manhole, x, y, $"BR-{n:000000}", $"Brunn {n}");
                nodes[cell] = node;
                _state[node.Id] = new SiteState();
            }
            return node;
        }
        (Segment Segment, bool Forward) Between(Site a, Site b, double[]? route, bool corridor)
        {
            var key = a.Id < b.Id ? (a.Id, b.Id) : (b.Id, a.Id);
            if (!segments.TryGetValue(key, out var segment))
            {
                segment = new Segment(segments.Count + 1, a, b, route ?? [a.X, a.Y, b.X, b.Y], corridor);
                segments[key] = segment;
            }
            return (segment, segment.A == a);
        }
        static (int, int) Cell(Site s) => ((int)Math.Round(s.X / CorridorGrid), (int)Math.Round(s.Y / CorridorGrid));

        // Each cable's way from its A end, as segments and the direction it runs them.
        var ways = new List<(Cable Cable, List<(Segment Segment, bool Forward)> Way)>();
        foreach (var cable in _net.Cables)
        {
            var way = new List<(Segment, bool)>();
            var (from, to) = (Cell(cable.A), Cell(cable.B));
            var cells = !long_.Contains(cable) || from == to || cable.A.DistanceTo(cable.B) < 1.5 * CorridorGrid ? null
                : DiagonalFirst.Select(diagonalFirst => Corridor(from, to, diagonalFirst))
                    .FirstOrDefault(path => path.All(c => _territory.Contains(c.Item1 * CorridorGrid, c.Item2 * CorridorGrid)));
            if (cells is null)
            {
                // Short, or a corridor would leave land: a trench of its own along the cable's route.
                way.Add(Between(cable.A, cable.B, cable.Coordinates, corridor: long_.Contains(cable)));
            }
            else
            {
                var at = Node(from);
                way.Add(Between(cable.A, at, null, corridor: true));
                foreach (var cell in cells.Skip(1))
                {
                    var next = Node(cell);
                    way.Add(Between(at, next, null, corridor: true));
                    at = next;
                }
                way.Add(Between(at, cable.B, null, corridor: true));
            }
            // A cable sharing a trench with another between the same sites takes the trench's route too.
            cable.Coordinates = Join(way);
            foreach (var (segment, _) in way)
            {
                segment.Cables.Add(cable);
            }
            ways.Add((cable, way));
        }

        // Ducts: corridors get multiducts with room to spare, a single cable a duct of its own, and some access trenches a
        // bundle of microducts with blown fibre.
        var nextDuct = 1L;
        var nextSubduct = 1L;
        void AddDuct(Segment segment, Cmdb.Catalog.DuctType type, string lifecycle, bool forCables, int blown = 0)
        {
            var duct = new DuctRow(nextDuct++, $"DK-{NextCode("DK"):000000}", type.Key, segment.Id, lifecycle);
            _net.Ducts.Add(duct);
            for (var n = 1; n <= type.Subducts.Count; n++)
            {
                var id = nextSubduct++;
                var color = type.Subducts.ColorCode is null ? null : ConductorColor(n);
                _net.Subducts.Add(new SubductRow(id, duct.Id, n, color, n <= blown ? "blown_fibre" : "empty"));
                if (forCables)
                {
                    segment.Free.Add(id);
                }
            }
        }
        var subductIndex = new Dictionary<long, int>();
        foreach (var segment in segments.Values.OrderBy(s => s.Id))
        {
            var lifecycle = Strongest(segment.Cables.Select(c => c.Lifecycle));
            var (construction, owner) = segment.Corridor
                ? (rng.NextDouble() < 0.3 ? "existing" : "trench", rng.NextDouble() < 0.2 ? "Kanalisation Exempel AB" : null)
                : (rng.NextDouble() switch { < 0.5 => "plough", < 0.9 => "trench", _ => "aerial" }, (string?)null);
            _net.RouteSegments.Add(new RouteSegmentRow(segment.Id, $"TR-{segment.Id:000000}", segment.A.Id, segment.B.Id, construction, owner,
                segment.Coordinates, lifecycle, Trunk: segment.Cables.Any(c => c.Count >= 96)));
            if (segment.Corridor)
            {
                var count = (int)Math.Ceiling(segment.Cables.Count / (double)corridorDuct.Subducts.Count) + (rng.NextDouble() < 0.3 ? 1 : 0);
                for (var d = 0; d < Math.Max(count, 1); d++)
                {
                    AddDuct(segment, corridorDuct, lifecycle, forCables: true);
                }
            }
            else
            {
                var type = rng.NextDouble() < 0.2 ? corridorDuct : accessDuct;
                for (var d = 0; d < Math.Max(1, (int)Math.Ceiling(segment.Cables.Count / (double)type.Subducts.Count)); d++)
                {
                    AddDuct(segment, type, lifecycle, forCables: true);
                }
                if (rng.NextDouble() < 0.1 && microDuct != type)
                {
                    AddDuct(segment, microDuct, lifecycle, forCables: false, blown: rng.Next(1, 5));
                }
            }
        }
        for (var i = 0; i < _net.Subducts.Count; i++)
        {
            subductIndex[_net.Subducts[i].Id] = i;
        }

        // Every cable takes the next free tube on each segment of its way.
        foreach (var (cable, way) in ways)
        {
            for (var seq = 0; seq < way.Count; seq++)
            {
                var segment = way[seq].Segment;
                var subduct = segment.Free[0];
                segment.Free.RemoveAt(0);
                _net.CablePaths.Add(new CablePathRow(cable.Id, seq, subduct));
                var i = subductIndex[subduct];
                _net.Subducts[i] = _net.Subducts[i] with { Occupancy = "cable" };
            }
        }

        // A manhole is as built as the strongest corridor through it.
        foreach (var node in nodes.Values)
        {
            node.Lifecycle = Strongest(segments.Values.Where(s => s.A == node || s.B == node).SelectMany(s => s.Cables).Select(c => c.Lifecycle));
        }
    }

    /// <summary>
    /// Grid cells from one to the other in steps of one cell, diagonally first and then straight, or straight first along
    /// the longer axis and then diagonally.
    /// </summary>
    private static List<(int, int)> Corridor((int, int) from, (int, int) to, bool diagonalFirst)
    {
        var cells = new List<(int, int)> { from };
        var cell = from;
        while (cell != to)
        {
            var (dx, dy) = (to.Item1 - cell.Item1, to.Item2 - cell.Item2);
            var straight = Math.Abs(dx) != Math.Abs(dy) && !diagonalFirst;
            cell = straight
                ? Math.Abs(dx) > Math.Abs(dy) ? (cell.Item1 + Math.Sign(dx), cell.Item2) : (cell.Item1, cell.Item2 + Math.Sign(dy))
                : (cell.Item1 + Math.Sign(dx), cell.Item2 + Math.Sign(dy));
            cells.Add(cell);
        }
        return cells;
    }

    /// <summary>The way's coordinates from its start, without repeating the point where two segments meet.</summary>
    private static double[] Join(List<(Segment Segment, bool Forward)> way)
    {
        var points = new List<double>();
        foreach (var (segment, forward) in way)
        {
            var c = segment.Coordinates;
            var n = c.Length / 2;
            for (var k = 0; k < n; k++)
            {
                var p = forward ? k : n - 1 - k;
                if (points.Count > 0 && k == 0)
                {
                    continue;
                }
                points.Add(c[2 * p]);
                points.Add(c[(2 * p) + 1]);
            }
        }
        return [.. points];
    }

    private static string Strongest(IEnumerable<string> lifecycles)
    {
        var all = lifecycles.ToList();
        return all.Contains(Lifecycle.InService) ? Lifecycle.InService
            : all.Contains(Lifecycle.UnderConstruction) ? Lifecycle.UnderConstruction
            : all.Count > 0 ? Lifecycle.Planned : Lifecycle.InService;
    }
}
