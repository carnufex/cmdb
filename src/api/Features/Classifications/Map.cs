using Cmdb.Api.Auth;
using Cmdb.Catalog;
using Cmdb.Graph;
using FastEndpoints;

namespace Cmdb.Api.Features.Classifications;

public sealed record MapSite(long Id, string Code, string Name, double X, double Y, int Level);

public sealed record MapCable(long Id, string Code, int Level, double[][] Coordinates);

/// <param name="Sites">Sites at or above the level, highest first.</param>
/// <param name="Cables">Cables at or above the level, highest first, with their route simplified for drawing.</param>
/// <param name="Truncated">There were more than the map draws; the highest levels are kept.</param>
public sealed record ClassificationMap(string Schema, int Min, int CriticalFrom, IReadOnlyList<MapSite> Sites, IReadOnlyList<MapCable> Cables,
    bool Truncated, int ClassifiedServices, double ElapsedMs);

public sealed class ClassificationMapRequest
{
    public string Schema { get; set; } = "criticality";

    /// <summary>Only objects at or above this level.</summary>
    public int Min { get; set; } = 1;
}

/// <summary>
/// The classification layer of the map (#180, ADR-0017): the sites and cables whose level, counting what they contain and what runs
/// through them (#177), is at or above a chosen one. Worked out in bulk from the graph: every classified service marks the
/// sites and cables its circuits pass, down through the circuits they ride on, and equipment marks its site. Within the
/// caller's scopes, and nothing for a scope that hides positions.
/// </summary>
public sealed class ClassificationMapEndpoint(RequestDb db, GraphHolder holder, ScopeMasks masks) : Endpoint<ClassificationMapRequest, ClassificationMap>
{
    private const int MaxSites = 4_000;
    private const int MaxCables = 2_500;

    public override void Configure() => Get("/classifications/map");

    public override async Task HandleAsync(ClassificationMapRequest req, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var scope = HttpContext.Scope();
        if (ClassificationCatalog.Embedded.Find(req.Schema) is not { } schema || holder.Current is not { } graph)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var min = Math.Max(req.Min, 1);
        if (scope.HidesCoordinates)
        {
            await Send.OkAsync(new ClassificationMap(schema.Key, min, schema.CriticalFrom, [], [], false, 0, 0), ct);
            return;
        }
        var mask = await masks.GetAsync(graph, scope, ct);

        // What was set, at or above the level.
        var direct = new List<(string Type, long Id, int Level)>();
        await using (var cmd = db.Source.CreateCommand("SELECT object_type, object_id, level FROM classification WHERE schema_key = $1 AND level >= $2"))
        {
            cmd.Parameters.Add(new() { Value = schema.Key });
            cmd.Parameters.Add(new() { Value = min });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                direct.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2)));
            }
        }
        var siteLevel = new Dictionary<long, int>();
        var cableLevel = new Dictionary<long, int>();
        void Raise(Dictionary<long, int> levels, long id, int level)
        {
            if (!levels.TryGetValue(id, out var current) || level > current)
            {
                levels[id] = level;
            }
        }

        var equipment = direct.Where(d => d.Type == "equipment").ToList();
        if (equipment.Count > 0)
        {
            await using var cmd = db.Source.CreateCommand("SELECT id, site_id FROM equipment WHERE id = ANY($1)");
            cmd.Parameters.Add(new() { Value = equipment.Select(e => e.Id).ToArray() });
            var siteOf = new Dictionary<long, long>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    siteOf[reader.GetInt64(0)] = reader.GetInt64(1);
                }
            }
            foreach (var (_, id, level) in equipment.Where(e => siteOf.ContainsKey(e.Id)))
            {
                Raise(siteLevel, siteOf[id], level);
            }
        }
        foreach (var (type, id, level) in direct)
        {
            if (type == "site")
            {
                Raise(siteLevel, id, level);
            }
            else if (type == "cable")
            {
                Raise(cableLevel, id, level);
            }
        }

        // What classified services run through.
        var services = direct.Where(d => d.Type == "service" && graph.TryGetService(d.Id, out _)).ToList();
        foreach (var (_, id, level) in services)
        {
            if (!graph.TryGetService(id, out var service) || !mask.ServiceVisible(service))
            {
                continue;
            }
            var seen = new HashSet<int>();
            var stack = new Stack<int>(graph.CircuitsOf(service).ToArray());
            var sites = new HashSet<int>();
            var cables = new HashSet<int>();
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                if (!seen.Add(c))
                {
                    continue;
                }
                foreach (var node in graph.HopsOf(c))
                {
                    if (graph.SiteIndexOfNode(node) is >= 0 and var s)
                    {
                        sites.Add(s);
                    }
                    else if (graph.CableIndexOfNode(node) is >= 0 and var k)
                    {
                        cables.Add(k);
                    }
                }
                foreach (var carrier in graph.CarriersOf(c))
                {
                    stack.Push(carrier);
                }
            }
            foreach (var s in sites.Where(mask.SiteVisible))
            {
                Raise(siteLevel, graph.SiteId(s), level);
            }
            foreach (var k in cables.Where(mask.CableVisible))
            {
                Raise(cableLevel, graph.CableId(k), level);
            }
        }

        var siteIds = siteLevel.OrderByDescending(s => s.Value).ThenBy(s => s.Key).Take(MaxSites).ToList();
        var cableIds = cableLevel.OrderByDescending(c => c.Value).ThenBy(c => c.Key).Take(MaxCables).ToList();
        var outSites = new List<MapSite>(siteIds.Count);
        await using (var cmd = db.Source.CreateCommand($"""
            SELECT s.id, s.code, s.name, ST_X(ST_PointOnSurface(s.geom)), ST_Y(ST_PointOnSurface(s.geom)) FROM site s
            WHERE s.id = ANY($1) AND {ScopeSql.Site("s.id", 2)}
            """))
        {
            cmd.Parameters.Add(new() { Value = siteIds.Select(s => s.Key).ToArray() });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                outSites.Add(new MapSite(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3), reader.GetDouble(4), siteLevel[reader.GetInt64(0)]));
            }
        }
        var outCables = new List<MapCable>(cableIds.Count);
        await using (var cmd = db.Source.CreateCommand($"""
            SELECT c.id, c.code, ARRAY(SELECT ARRAY[round(ST_X(p.geom)), round(ST_Y(p.geom))]
                                       FROM ST_DumpPoints(ST_Simplify({ScopeSql.CableGeometry("c", 2, scope)}, 40)) p ORDER BY p.path)
            FROM cable c WHERE c.id = ANY($1) AND {ScopeSql.Cable("c.id", 2)}
            """))
        {
            cmd.Parameters.Add(new() { Value = cableIds.Select(c => c.Key).ToArray() });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.GetValue(2) is double[,] points && points.Length > 0)
                {
                    outCables.Add(new MapCable(reader.GetInt64(0), reader.GetString(1), cableLevel[reader.GetInt64(0)],
                        [.. Enumerable.Range(0, points.GetLength(0)).Select(i => new[] { points[i, 0], points[i, 1] })]));
                }
            }
        }
        await Send.OkAsync(new ClassificationMap(schema.Key, min, schema.CriticalFrom,
            [.. outSites.OrderByDescending(s => s.Level).ThenBy(s => s.Id)], [.. outCables.OrderByDescending(c => c.Level).ThenBy(c => c.Id)],
            siteLevel.Count > MaxSites || cableLevel.Count > MaxCables, services.Count,
            Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1)), ct);
    }
}
