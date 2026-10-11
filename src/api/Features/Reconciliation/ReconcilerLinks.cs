using System.Text.Json;
using System.Text.Json.Nodes;
using Cmdb.Api.Auth;
using Cmdb.Catalog;
using Cmdb.Exchange;
using Npgsql;
using NpgsqlTypes;

namespace Cmdb.Api.Features.Reconciliation;

/// <summary>
/// Reconciliation of what lies between the objects (#230): connections, circuit paths, dependencies and the circuits a
/// service runs on, and the locations equipment stands in. All of it set-based: the rows go into temporary tables with
/// <c>COPY</c>, the source's ids are translated to cmdb's through what was matched, and the comparison is SQL, so only
/// the differences leave the database. Terminals on objects created in the same run cannot be resolved yet; they are
/// reported and reconciled in the next run, once the plan creating them is in.
/// </summary>
public sealed partial class Reconciler
{
    /// <summary>The order operations go into a plan: what is referred to first, since planned ids come from operation ids.</summary>
    private static class Passes
    {
        public const int Sites = 0;
        public const int Locations = 10;
        public const int Equipment = 30;
        public const int Cards = 31;
        public const int Cables = 40;
        public const int Services = 45;
        public const int Circuits = 50;
        public const int Changes = 60;
        public const int Links = 70;

        /// <summary>Nesting levels kept apart in a plan, for locations in locations and cards in cards.</summary>
        public const int Depth = 8;
    }

    /// <summary>A reference to an object the plan creates, by its source id, until the plan gives it a planned id.</summary>
    private static string NewRef(string type, string ext) => $"new:{type}:{ext}";

    /// <summary>The payload fields that may hold a reference to an object the plan creates.</summary>
    private static readonly string[] RefFields = ["site", "a", "b", "parent", "service", "circuit", "carrier"];

    /// <summary>Source ids → cmdb ids per type: what this run matched, and what the source made or reported before, inside the scopes.</summary>
    private static async Task MapAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string type, bool loaded, string source, UserScope scope,
        CancellationToken ct)
    {
        await ExecAsync(conn, tx, $"""
            DROP TABLE IF EXISTS map_{type};
            CREATE TEMP TABLE map_{type} (ext text PRIMARY KEY, id bigint NOT NULL) ON COMMIT DROP;
            """, ct);
        if (loaded)
        {
            await ExecAsync(conn, tx, $"INSERT INTO map_{type} SELECT x.ext, m.id FROM m_{type} m JOIN x_{type} x ON x.n = m.n ON CONFLICT DO NOTHING", ct);
        }
        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO map_{type}
            SELECT r.external_id, r.object_id FROM source_record r JOIN {type} t ON t.id = r.object_id
            WHERE r.object_type = '{type}' AND r.source_system = $1 AND {Visible(type, "t", 2)}
            UNION ALL
            SELECT t.external_id, t.id FROM {type} t WHERE t.source_system = $1 AND t.external_id IS NOT NULL AND {Visible(type, "t", 2)}
            ON CONFLICT DO NOTHING
            """, conn, tx);
        cmd.Parameters.Add(new() { Value = source });
        cmd.Parameters.Add(scope.Parameter());
        await cmd.ExecuteNonQueryAsync(ct);
        await ExecAsync(conn, tx, $"ANALYZE map_{type}", ct);
    }

    private static async Task<Dictionary<string, long>> MapOfAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string type, CancellationToken ct)
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand($"SELECT ext, id FROM map_{type}", conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[reader.GetString(0)] = reader.GetInt64(1);
        }
        return map;
    }

    /// <summary>
    /// Locations match on the source and its id, then on site, kind and name, the site being the one the source's site id
    /// leads to. A location found twice is a deviation.
    /// </summary>
    private static async Task<(Dictionary<int, long> Matched, int Linked, Dictionary<int, string> Ambiguous, HashSet<int> Outside)> MatchLocationsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string source, UserScope scope, ExchangeData data, CancellationToken ct)
    {
        var (byId, linked, ambiguous, outside) = await MatchAsync(conn, tx, "location", source, scope, ct);
        await MapAsync(conn, tx, "site", data.Files.Contains(ExchangeFormat.Sites), source, scope, ct);
        await ExecAsync(conn, tx, """
            DROP TABLE IF EXISTS x_location_site;
            CREATE TEMP TABLE x_location_site (n int PRIMARY KEY, site_ext text NOT NULL) ON COMMIT DROP;
            """, ct);
        await using (var copy = await conn.BeginBinaryImportAsync("COPY x_location_site (n, site_ext) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var l in data.Locations)
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(l.Row, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(l.Site, NpgsqlDbType.Text, ct);
            }
            await copy.CompleteAsync(ct);
        }
        var found = new List<(int N, long Id)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT x.n, t.id FROM x_location x JOIN x_location_site xs ON xs.n = x.n JOIN map_site s ON s.ext = xs.site_ext
            JOIN location t ON t.site_id = s.id AND t.kind = x.reported->>'kind' AND t.name = x.reported->>'name'
            WHERE NOT EXISTS (SELECT 1 FROM m_location m WHERE m.n = x.n) AND NOT EXISTS (SELECT 1 FROM m_location m WHERE m.id = t.id)
              AND t.lifecycle <> 'removed'
              AND NOT EXISTS (SELECT 1 FROM source_record r WHERE r.object_type = 'location' AND r.object_id = t.id AND r.source_system = $1)
            """, conn, tx))
        {
            cmd.Parameters.Add(new() { Value = source });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                found.Add((reader.GetInt32(0), reader.GetInt64(1)));
            }
        }
        var twice = found.GroupBy(f => f.N).Where(g => g.Count() > 1).Select(g => g.Key)
            .Concat(found.GroupBy(f => f.Id).Where(g => g.Count() > 1).SelectMany(g => g.Select(f => f.N))).ToHashSet();
        foreach (var n in twice)
        {
            ambiguous.TryAdd(n, "site+kind+name");
        }
        var unique = found.Where(f => !twice.Contains(f.N)).ToList();
        await InsertMatchesAsync(conn, tx, "location", unique, "site+kind+name", ct);
        foreach (var (n, id) in unique)
        {
            byId[n] = id;
        }
        return (byId, linked + unique.Count, ambiguous, outside);
    }

    /// <summary>How deep an object sits under parents created in the same run, so a parent goes into the plan before its children.</summary>
    private static int Depth(string? parent, Func<string, string?> parentOf, HashSet<string> creating)
    {
        var depth = 0;
        for (var p = parent; p is not null && creating.Contains(p) && depth < Passes.Depth - 1; p = parentOf(p))
        {
            depth++;
        }
        return depth;
    }

    /// <summary>
    /// What the source says lies between the objects, against cmdb: connections, paths, dependencies and service circuits.
    /// Operations where the source owns it, deviations otherwise; circuits cmdb lacks are created with their path.
    /// </summary>
    private static async Task LinksAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ExchangeData data, string source, List<Incoming> newCircuits,
        Report report, List<Op> ops, CancellationToken ct)
    {
        var priority = SourcePriority.Current;
        bool Owns(string type, string attribute, out bool auto)
        {
            auto = priority.AutoApplies(type, attribute, source);
            return priority.Rank(type, attribute, source) is not null;
        }

        if (data.Files.Contains(ExchangeFormat.Connections))
        {
            await ConnectionsAsync(conn, tx, data, report, ops, Owns("connection", "kind", out var auto), auto, ct);
        }

        var paths = new Dictionary<string, long[]>(StringComparer.Ordinal);
        if (data.Files.Contains(ExchangeFormat.Hops))
        {
            paths = await PathsAsync(conn, tx, data, report, ops, Owns("circuit", "path", out var auto), auto, ct);
        }
        await NewCircuitsAsync(conn, tx, newCircuits, paths, source, report, ops, ct);

        if (data.Files.Contains(ExchangeFormat.Dependencies))
        {
            await LinkRowsAsync(conn, tx, "circuit", "circuit", "circuit_dependency", "circuit_id", "carrier_id", "link_circuit", "circuit", "carrier",
                "carriers", [.. data.Dependencies.Select(d => (d.Circuit, d.Carrier))], report, ops, Owns("circuit", "carriers", out var auto), auto, ct);
        }
        if (data.Files.Contains(ExchangeFormat.ServiceCircuits))
        {
            await LinkRowsAsync(conn, tx, "service", "circuit", "service_circuit", "service_id", "circuit_id", "link_service", "service", "circuit",
                "circuits", [.. data.ServiceCircuits.Select(s => (s.Service, s.Circuit))], report, ops, Owns("service", "circuits", out var auto), auto, ct);
        }
    }

    private const string TerminalColumns = "eq text, port text, cable text, cond int, side text";

    private static async Task WriteTerminalAsync(NpgsqlBinaryImporter copy, XTerminal t, CancellationToken ct)
    {
        await copy.WriteAsync((object?)t.Equipment ?? DBNull.Value, NpgsqlDbType.Text, ct);
        await copy.WriteAsync((object?)t.Port ?? DBNull.Value, NpgsqlDbType.Text, ct);
        await copy.WriteAsync((object?)t.Cable ?? DBNull.Value, NpgsqlDbType.Text, ct);
        await copy.WriteAsync(t.IsPort ? DBNull.Value : (object)t.Conductor, NpgsqlDbType.Integer, ct);
        await copy.WriteAsync(t.IsPort ? DBNull.Value : (object)t.Side.ToString(), NpgsqlDbType.Text, ct);
    }

    /// <summary>
    /// Joins resolving the terminal a row's columns with prefix <paramref name="p"/> name: a port of mapped equipment, or a
    /// conductor end of a mapped cable. The terminal is <c>coalesce({alias}_p.terminal_id, {alias}_e.terminal_id)</c>.
    /// </summary>
    private static string Resolve(string p, string alias) => $"""
        LEFT JOIN map_equipment {alias}_me ON {alias}_me.ext = x.{p}eq
        LEFT JOIN port {alias}_p ON {alias}_p.equipment_id = {alias}_me.id AND {alias}_p.name = x.{p}port
        LEFT JOIN map_cable {alias}_mc ON {alias}_mc.ext = x.{p}cable
        LEFT JOIN conductor {alias}_k ON {alias}_k.cable_id = {alias}_mc.id AND {alias}_k.number = x.{p}cond
        LEFT JOIN conductor_end {alias}_e ON {alias}_e.conductor_id = {alias}_k.id AND {alias}_e.side::text = x.{p}side
        """;

    private static async Task ConnectionsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ExchangeData data, Report report, List<Op> ops,
        bool owned, bool auto, CancellationToken ct)
    {
        var count = report.Count("connection");
        count.Reported = data.Connections.Count;
        await ExecAsync(conn, tx, """
            DROP TABLE IF EXISTS x_connection;
            CREATE TEMP TABLE x_connection (n int PRIMARY KEY, a_eq text, a_port text, a_cable text, a_cond int, a_side text,
                b_eq text, b_port text, b_cable text, b_cond int, b_side text, kind text NOT NULL) ON COMMIT DROP;
            """, ct);
        await using (var copy = await conn.BeginBinaryImportAsync(
            "COPY x_connection (n, a_eq, a_port, a_cable, a_cond, a_side, b_eq, b_port, b_cable, b_cond, b_side, kind) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var c in data.Connections)
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(c.Row, NpgsqlDbType.Integer, ct);
                await WriteTerminalAsync(copy, c.A, ct);
                await WriteTerminalAsync(copy, c.B, ct);
                await copy.WriteAsync(c.Kind, NpgsqlDbType.Text, ct);
            }
            await copy.CompleteAsync(ct);
        }
        await ExecAsync(conn, tx, $"""
            ANALYZE x_connection;
            DROP TABLE IF EXISTS r_connection;
            CREATE TEMP TABLE r_connection ON COMMIT DROP AS
            SELECT n, least(ta, tb) AS lo, greatest(ta, tb) AS hi, kind FROM (
                SELECT x.n, coalesce(a_p.terminal_id, a_e.terminal_id) AS ta, coalesce(b_p.terminal_id, b_e.terminal_id) AS tb, x.kind
                FROM x_connection x {Resolve("a_", "a")} {Resolve("b_", "b")}) r;
            CREATE INDEX ON r_connection (lo, hi);
            ANALYZE r_connection;
            DROP TABLE IF EXISTS s_terminal;
            CREATE TEMP TABLE s_terminal ON COMMIT DROP AS
            SELECT p.terminal_id AS t FROM map_equipment me JOIN port p ON p.equipment_id = me.id
            UNION SELECT e.terminal_id FROM map_cable mc JOIN conductor k ON k.cable_id = mc.id JOIN conductor_end e ON e.conductor_id = k.id;
            CREATE UNIQUE INDEX ON s_terminal (t);
            ANALYZE s_terminal;
            """, ct);

        // Rows naming a terminal cmdb cannot find (yet), new connections and changed kinds; the rest is as reported.
        var external = data.Connections.ToDictionary(c => c.Row);
        var added = new List<(long A, long B, string Kind, int N)>();
        var differs = 0;
        var unknown = 0;
        await using (var cmd = new NpgsqlCommand("""
            SELECT r.n, r.lo, r.hi, r.kind, c.kind::text
            FROM r_connection r
            LEFT JOIN connection c ON c.valid_to IS NULL AND c.a_terminal_id = r.lo AND c.b_terminal_id = r.hi
            WHERE r.lo IS NULL OR c.id IS NULL OR c.kind::text <> r.kind
            ORDER BY r.n
            """, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var c = external[reader.GetInt32(0)];
                var name = $"{c.A} – {c.B}";
                if (reader.IsDBNull(1) || reader.IsDBNull(2))
                {
                    unknown++;
                    report.Deviate("connection", null, name, "terminal", null, null, "unknown-terminal");
                }
                else if (reader.IsDBNull(4))
                {
                    added.Add((reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), c.Row));
                }
                else
                {
                    differs++;
                    report.Deviate("connection", null, name, "kind", JsonValue.Create(reader.GetString(3)), JsonValue.Create(reader.GetString(4)), "no-operation");
                }
            }
        }

        // Connections between the source's own objects that it no longer reports: a connections file is the whole truth.
        var gone = new List<(long A, long B)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT c.a_terminal_id, c.b_terminal_id FROM connection c
            JOIN s_terminal sa ON sa.t = c.a_terminal_id JOIN s_terminal sb ON sb.t = c.b_terminal_id
            WHERE c.valid_to IS NULL AND NOT EXISTS (SELECT 1 FROM r_connection r WHERE r.lo = c.a_terminal_id AND r.hi = c.b_terminal_id)
            """, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                gone.Add((reader.GetInt64(0), reader.GetInt64(1)));
            }
        }
        count.New = added.Count;
        count.Missing = gone.Count;
        count.Matched = count.Reported - added.Count - unknown;
        count.Changed = differs;
        count.Unchanged = count.Matched - differs;
        foreach (var (a, b, kind, n) in added)
        {
            if (owned)
            {
                ops.Add(new("connect", JsonSerializer.Serialize(new { a, b, kind }), auto, Passes.Changes));
            }
            else
            {
                var c = external[n];
                report.Deviate("connection", null, $"{c.A} – {c.B}", "kind", JsonValue.Create(kind), null, "not-allowed");
            }
        }
        foreach (var (a, b) in gone)
        {
            // What the source no longer reports is proposed for review, never taken away on trust.
            if (owned)
            {
                ops.Add(new("disconnect", JsonSerializer.Serialize(new { a, b }), false, Passes.Changes));
            }
            else
            {
                report.Deviate("connection", null, $"{a} – {b}", "", null, null, "missing");
            }
        }
    }

    /// <summary>
    /// Circuit paths: the source's hops resolved to terminals, against the circuits' paths. A changed path becomes
    /// set_circuit_path; the resolved paths of circuits cmdb does not have are returned for create_circuit.
    /// </summary>
    private static async Task<Dictionary<string, long[]>> PathsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ExchangeData data, Report report,
        List<Op> ops, bool owned, bool auto, CancellationToken ct)
    {
        await ExecAsync(conn, tx, $"""
            DROP TABLE IF EXISTS x_hop;
            CREATE TEMP TABLE x_hop (circuit text NOT NULL, seq int NOT NULL, {TerminalColumns}) ON COMMIT DROP;
            """, ct);
        await using (var copy = await conn.BeginBinaryImportAsync("COPY x_hop (circuit, seq, eq, port, cable, cond, side) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var h in data.Hops)
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(h.Circuit, NpgsqlDbType.Text, ct);
                await copy.WriteAsync(h.Seq, NpgsqlDbType.Integer, ct);
                await WriteTerminalAsync(copy, h.Terminal, ct);
            }
            await copy.CompleteAsync(ct);
        }
        await ExecAsync(conn, tx, "ANALYZE x_hop", ct);
        var paths = new Dictionary<string, long[]>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand($"""
            WITH rep AS (
                SELECT x.circuit, array_agg(coalesce(h_p.terminal_id, h_e.terminal_id) ORDER BY x.seq) AS hops,
                       bool_and(coalesce(h_p.terminal_id, h_e.terminal_id) IS NOT NULL) AS ok
                FROM x_hop x {Resolve("", "h")} GROUP BY x.circuit
            ), cur AS (
                SELECT h.circuit_id, array_agg(h.terminal_id ORDER BY h.seq) AS hops
                FROM circuit_hop h JOIN map_circuit mc ON mc.id = h.circuit_id GROUP BY h.circuit_id
            )
            SELECT rep.circuit, rep.ok, CASE WHEN rep.ok THEN rep.hops END, mc.id
            FROM rep LEFT JOIN map_circuit mc ON mc.ext = rep.circuit LEFT JOIN cur ON cur.circuit_id = mc.id
            WHERE NOT rep.ok OR mc.id IS NULL OR rep.hops IS DISTINCT FROM cur.hops
            """, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var ext = reader.GetString(0);
                long? id = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                if (!reader.GetBoolean(1))
                {
                    report.Deviate("circuit", id, ext, "path", null, null, "unknown-terminal");
                    continue;
                }
                var hops = reader.GetFieldValue<long[]>(2);
                if (id is null)
                {
                    paths[ext] = hops;
                }
                else if (owned)
                {
                    ops.Add(new("set_circuit_path", JsonSerializer.Serialize(new { type = "circuit", id, hops }), auto, Passes.Changes));
                }
                else
                {
                    report.Deviate("circuit", id, ext, "path", new JsonArray([.. hops.Select(h => (JsonNode)h)]), null, "not-allowed");
                }
            }
        }
        return paths;
    }

    /// <summary>Circuits the source has and cmdb does not, created with the path their hops resolve to; without one they wait.</summary>
    private static async Task NewCircuitsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, List<Incoming> rows, Dictionary<string, long[]> paths,
        string source, Report report, List<Op> ops, CancellationToken ct)
    {
        var taken = await TakenCodesAsync(conn, tx, "circuit", [.. rows.Select(r => ((XCircuit)r.Source).Code)], ct);
        var created = 0;
        foreach (var r in rows)
        {
            var c = (XCircuit)r.Source;
            if (taken.Contains(c.Code))
            {
                report.Deviate("circuit", null, r.ExternalId, "code", JsonValue.Create(c.Code), null, "code-taken");
            }
            else if (!paths.TryGetValue(c.Id, out var hops) || hops.Length < 2)
            {
                report.Deviate("circuit", null, r.ExternalId, "path", null, null, "no-path");
            }
            else
            {
                created++;
                ops.Add(Create("circuit", c.Id, "create_circuit", JsonSerializer.Serialize(new
                {
                    code = c.Code,
                    layer = c.Layer,
                    hops,
                    lifecycle = c.Lifecycle,
                    source,
                    externalId = c.Id,
                }), Passes.Circuits, report));
            }
        }
        if (rows.Count > 0)
        {
            report.Count("circuit").New = created;
        }
    }

    /// <summary>
    /// Links between two kinds of objects (dependencies, service circuits): the source's pairs against cmdb's among the
    /// objects it knows. A pair with an object created in the same run refers to it until the plan gives it an id.
    /// </summary>
    private static async Task LinkRowsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string fromType, string toType, string table,
        string fromColumn, string toColumn, string kind, string fromKey, string toKey, string attribute, List<(string From, string To)> pairs,
        Report report, List<Op> ops, bool owned, bool auto, CancellationToken ct)
    {
        var fromIds = await MapOfAsync(conn, tx, fromType, ct);
        var toIds = fromType == toType ? fromIds : await MapOfAsync(conn, tx, toType, ct);
        var existing = new HashSet<(long, long)>();
        await using (var cmd = new NpgsqlCommand($"""
            SELECT l.{fromColumn}, l.{toColumn} FROM {table} l JOIN map_{fromType} f ON f.id = l.{fromColumn} JOIN map_{toType} t ON t.id = l.{toColumn}
            """, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                existing.Add((reader.GetInt64(0), reader.GetInt64(1)));
            }
        }

        JsonNode? Ref(Dictionary<string, long> ids, string type, string ext) =>
            ids.TryGetValue(ext, out var id) ? JsonValue.Create(id) : report.Creating(type, ext) ? JsonValue.Create(NewRef(type, ext)) : null;
        var reported = new HashSet<(long, long)>();
        foreach (var (from, to) in pairs.Distinct())
        {
            var a = Ref(fromIds, fromType, from);
            var b = Ref(toIds, toType, to);
            if (a is null || b is null)
            {
                report.Deviate(fromType, null, from, attribute, JsonValue.Create(to), null, "unknown-object");
                continue;
            }
            if (fromIds.TryGetValue(from, out var x) && toIds.TryGetValue(to, out var y))
            {
                reported.Add((x, y));
                if (existing.Contains((x, y)))
                {
                    continue;
                }
            }
            if (owned)
            {
                ops.Add(new(kind, new JsonObject { ["type"] = fromType, [fromKey] = a, [toKey] = b }.ToJsonString(), auto, Passes.Links));
            }
            else
            {
                report.Deviate(fromType, fromIds.TryGetValue(from, out var f) ? f : null, from, attribute, JsonValue.Create(to), null, "not-allowed");
            }
        }
        foreach (var (f, t) in existing.Where(e => !reported.Contains(e)))
        {
            if (owned)
            {
                ops.Add(new(kind, new JsonObject { ["type"] = fromType, [fromKey] = f, [toKey] = t, ["remove"] = true }.ToJsonString(), false, Passes.Links));
            }
            else
            {
                report.Deviate(fromType, f, "", attribute, null, JsonValue.Create(t), "missing");
            }
        }
    }

    /// <summary>A create operation, noted in the report so that what refers to the new object can.</summary>
    private static Op Create(string type, string ext, string kind, string payload, int pass, Report report)
    {
        report.Create(type, ext);
        return new(kind, payload, false, pass, NewRef(type, ext));
    }
}
