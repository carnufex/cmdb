using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Plans;
using Cmdb.Catalog;
using Cmdb.Database.Provenance;
using Cmdb.Exchange;
using Npgsql;
using NpgsqlTypes;

namespace Cmdb.Api.Features.Reconciliation;

/// <summary>
/// Reconciliation (#216, ADR-0020): a source's data in the exchange format (#210) compared with cmdb, inside the caller's
/// scopes. Objects are matched on the source and its id, then on the catalog's matching rules. What the source owns by
/// the source priority and differs becomes plan operations, in a plan brought into production at once where the source
/// is trusted with the attribute and one for review otherwise; every other difference is a deviation in the report.
/// Nothing is written silently and nothing is removed: what the source no longer reports is marked.
/// </summary>
public sealed class Reconciler(RequestDb db, PlanApplication application)
{
    /// <summary>The object types reconciled; the others in the files are counted only, until they have plan operations.</summary>
    public static readonly string[] ObjectTypes = ["site", "equipment", "cable", "service"];

    /// <summary>Deviations kept in a run's report; the counts cover all of them.</summary>
    public const int MaxDeviations = 1000;

    private static readonly JsonSerializerOptions OmitNull = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private sealed record Incoming(int Row, string ExternalId, JsonObject Reported, object Source);

    private sealed record Diff(string Type, int Row, long Id, string Attribute, JsonNode? Source, JsonNode? Current);

    private sealed record Op(string Kind, string Payload, bool Auto, int Pass);

    public async Task<ReconciliationReport> RunAsync(ClaimsPrincipal user, UserScope scope, string source, string folder, bool dryRun,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var report = new Report(source, dryRun);
        var errors = new List<ImportError>();
        var data = ExchangeFormat.Read(folder, TypeCatalog.Current, errors);
        report.NotReconciled.AddRange(NotReconciled(data));
        if (errors.Count > 0)
        {
            report.Errors.AddRange(errors.Take(200).Select(e => e.ToString()));
            return await SaveAsync(user, report, sw, ct);
        }

        var incoming = new Dictionary<string, List<Incoming>>
        {
            ["site"] = [.. data.Sites.Select(s => new Incoming(s.Row, s.Id, Reported(new()
            {
                ["code"] = s.Code, ["name"] = s.Name, ["siteType"] = s.SiteType, ["lifecycle"] = s.Lifecycle,
                ["position"] = new JsonArray(s.X, s.Y),
            }, s.Attributes), s))],
            ["equipment"] = [.. data.Equipment.Select(e => new Incoming(e.Row, e.Id, Reported(new()
            {
                ["name"] = e.Name, ["type"] = e.Type, ["lifecycle"] = e.Lifecycle,
            }, e.Attributes), e))],
            ["cable"] = [.. data.Cables.Select(c => new Incoming(c.Row, c.Id, Reported(new()
            {
                ["code"] = c.Code, ["type"] = c.Type, ["lifecycle"] = c.Lifecycle,
            }, c.Attributes), c))],
            ["service"] = [.. data.Services.Select(s => new Incoming(s.Row, s.Id, Reported(new()
            {
                ["code"] = s.Code, ["name"] = s.Name, ["type"] = s.Type, ["lifecycle"] = s.Lifecycle,
            }, s.Attributes), s))],
        };
        var files = new Dictionary<string, string>
        {
            ["site"] = ExchangeFormat.Sites,
            ["equipment"] = ExchangeFormat.Equipment,
            ["cable"] = ExchangeFormat.Cables,
            ["service"] = ExchangeFormat.Services,
        };

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var matched = new Dictionary<string, Dictionary<int, long>>();
        var diffs = new List<Diff>();
        var newRows = new Dictionary<string, List<Incoming>>();
        foreach (var type in ObjectTypes)
        {
            if (!data.Files.Contains(files[type]))
            {
                continue;
            }
            var rows = incoming[type];
            var count = report.Count(type);
            count.Reported = rows.Count;
            await LoadAsync(conn, tx, type, rows, ct);
            var (byId, linked, ambiguous, outside) = await MatchAsync(conn, tx, type, source, scope, ct);
            matched[type] = byId;
            count.Matched = byId.Count;
            count.Linked = linked;
            count.OutsideScope = outside.Count;
            var byRow = rows.ToDictionary(r => r.Row);
            foreach (var (row, how) in ambiguous)
            {
                report.Deviate(type, null, byRow[row].ExternalId, how, null, null, "ambiguous");
            }
            newRows[type] = [.. rows.Where(r => !byId.ContainsKey(r.Row) && !ambiguous.ContainsKey(r.Row) && !outside.Contains(r.Row))];
            diffs.AddRange(await DiffAsync(conn, tx, type, scope, ct));
            count.Missing = await MissingAsync(conn, tx, type, source, scope, dryRun, report, ct);
            if (!dryRun)
            {
                await ConfirmAsync(conn, tx, type, source, ct);
            }
        }

        // What differs: an operation where the source owns the attribute and one exists for it, a deviation otherwise.
        var others = await OtherSourcesAsync(conn, tx, diffs, source, ct);
        var priority = SourcePriority.Current;
        var ops = new List<Op>();
        var externalIds = incoming.ToDictionary(i => i.Key, i => i.Value.ToDictionary(r => r.Row, r => r.ExternalId));
        foreach (var group in diffs.GroupBy(d => (d.Type, d.Id)))
        {
            var external = externalIds[group.Key.Type][group.First().Row];
            var attributes = new Dictionary<bool, JsonObject>();
            foreach (var d in group)
            {
                var rank = priority.Rank(d.Type, d.Attribute, source);
                var owner = rank is not null && others.GetValueOrDefault((d.Type, d.Id), [])
                    .All(o => priority.Rank(d.Type, d.Attribute, o) is not { } other || other >= rank);
                var auto = priority.AutoApplies(d.Type, d.Attribute, source);
                if (!owner)
                {
                    report.Deviate(d.Type, d.Id, external, d.Attribute, d.Source, d.Current, rank is null ? "not-allowed" : "owned-by-other");
                }
                else if (d.Attribute.StartsWith(ReportedValues.AttributePrefix, StringComparison.Ordinal))
                {
                    (attributes.TryGetValue(auto, out var a) ? a : attributes[auto] = [])[d.Attribute[ReportedValues.AttributePrefix.Length..]] =
                        d.Source?.DeepClone();
                }
                else if (d.Attribute == "lifecycle")
                {
                    ops.Add(new("set_lifecycle", JsonSerializer.Serialize(new { type = d.Type, id = d.Id, lifecycle = d.Source!.GetValue<string>() }), auto, 2));
                }
                else if (d.Attribute == "name" && d.Type != "cable")
                {
                    ops.Add(new("rename", JsonSerializer.Serialize(new { type = d.Type, id = d.Id, name = d.Source!.GetValue<string>() }), auto, 2));
                }
                else
                {
                    report.Deviate(d.Type, d.Id, external, d.Attribute, d.Source, d.Current, "no-operation");
                }
            }
            foreach (var (auto, values) in attributes)
            {
                ops.Add(new("set_attributes", JsonSerializer.Serialize(new { type = group.Key.Type, id = group.Key.Id, attributes = values }), auto, 2));
            }
        }
        foreach (var type in ObjectTypes)
        {
            var changed = diffs.Where(d => d.Type == type).Select(d => d.Id).Distinct().Count();
            if (matched.TryGetValue(type, out var m))
            {
                report.Count(type).Changed = changed;
                report.Count(type).Unchanged = m.Count - changed;
            }
        }

        // What the source has that cmdb does not: created in the plan for review, carrying the source and its id.
        var creations = await CreationsAsync(conn, tx, newRows, matched, data, source, scope, report, ct);

        if (!dryRun)
        {
            var actor = PlanSql.Actor(user);
            var client = user.FindFirstValue(CmdbClaims.Client);
            await CancelPreviousAsync(conn, tx, source, ct);
            var review = ops.Where(o => !o.Auto).ToList();
            if (review.Count + creations.Sites.Count + creations.Rest.Count > 0)
            {
                report.ReviewPlanId = await PlanAsync(conn, tx, $"Avstämning {source} {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}",
                    $"Skillnader mot källan {source} som behöver granskas (#216).", actor, client, creations, review, ct);
            }
            var auto = ops.Where(o => o.Auto).ToList();
            if (auto.Count > 0)
            {
                report.AppliedPlanId = await PlanAsync(conn, tx, $"Avstämning {source} {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} (betrodd)",
                    $"Ändringar som källan {source} är betrodd med och som förs in direkt (#216).", actor, client, Creations.Empty, auto, ct);
            }
            await tx.CommitAsync(ct);

            if (report.AppliedPlanId is { } applied)
            {
                var result = await application.ApplyAsync(user, scope, applied, null, ct);
                if (result.Failure != PlanWriteFailure.None)
                {
                    report.AutoApplyProblem = result.Error;
                }
            }
        }
        report.Operations = ops.Count + creations.Sites.Count + creations.Rest.Count;
        return await SaveAsync(user, report, sw, ct);
    }

    /// <summary>The source's values in the shape of a source record (ADR-0019): own attributes as attributes.&lt;key&gt;.</summary>
    private static JsonObject Reported(JsonObject values, string attributes)
    {
        foreach (var (key, value) in JsonNode.Parse(attributes)!.AsObject())
        {
            if (value is not null)
            {
                values[ReportedValues.AttributePrefix + key] = value.DeepClone();
            }
        }
        return values;
    }

    private static IEnumerable<string> NotReconciled(ExchangeData data) =>
        new (string File, int Rows)[]
        {
            (ExchangeFormat.Locations, data.Locations.Count), (ExchangeFormat.Ports, data.Ports.Count),
            (ExchangeFormat.Connections, data.Connections.Count), (ExchangeFormat.Circuits, data.Circuits.Count),
            (ExchangeFormat.Hops, data.Hops.Count), (ExchangeFormat.Dependencies, data.Dependencies.Count),
            (ExchangeFormat.ServiceCircuits, data.ServiceCircuits.Count),
        }.Where(f => data.Files.Contains(f.File)).Select(f => $"{f.File}: {f.Rows} rader stäms inte av ännu");

    private static async Task LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string type, List<Incoming> rows, CancellationToken ct)
    {
        await ExecAsync(conn, tx, $"""
            DROP TABLE IF EXISTS x_{type};
            CREATE TEMP TABLE x_{type} (n int PRIMARY KEY, ext text NOT NULL, reported jsonb NOT NULL) ON COMMIT DROP;
            DROP TABLE IF EXISTS m_{type};
            CREATE TEMP TABLE m_{type} (n int PRIMARY KEY, id bigint NOT NULL, how text NOT NULL) ON COMMIT DROP;
            """, ct);
        await using (var copy = await conn.BeginBinaryImportAsync($"COPY x_{type} (n, ext, reported) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var r in rows)
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(r.Row, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(r.ExternalId, NpgsqlDbType.Text, ct);
                await copy.WriteAsync(r.Reported.ToJsonString(), NpgsqlDbType.Jsonb, ct);
            }
            await copy.CompleteAsync(ct);
        }
        await ExecAsync(conn, tx, $"ANALYZE x_{type}", ct);
    }

    private static string Visible(string type, string alias, int param) => type switch
    {
        "site" => ScopeSql.Site($"{alias}.id", param),
        "equipment" => ScopeSql.Site($"{alias}.site_id", param),
        "cable" => ScopeSql.Cable($"{alias}.id", param),
        _ => ScopeSql.Service($"{alias}.id", param),
    };

    /// <summary>
    /// Matches the rows on the source and its id, then rule by rule on the catalog's keys. A row found outside the
    /// caller's scopes is only counted; a row or object found twice by a rule is a deviation.
    /// </summary>
    private static async Task<(Dictionary<int, long> Matched, int Linked, Dictionary<int, string> Ambiguous, HashSet<int> Outside)> MatchAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string type, string source, UserScope scope, CancellationToken ct)
    {
        var outside = new HashSet<int>();
        var ambiguous = new Dictionary<int, string>();
        await using (var cmd = new NpgsqlCommand($"""
            WITH found AS (
                SELECT x.n, t.id FROM x_{type} x JOIN {type} t ON t.source_system = $1 AND t.external_id = x.ext
                UNION
                SELECT x.n, r.object_id FROM x_{type} x
                JOIN source_record r ON r.object_type = '{type}' AND r.source_system = $1 AND r.external_id = x.ext)
            SELECT f.n, f.id, {Visible(type, "t", 2)} FROM found f JOIN {type} t ON t.id = f.id
            """, conn, tx))
        {
            cmd.Parameters.Add(new() { Value = source });
            cmd.Parameters.Add(scope.Parameter());
            var rows = new List<(int N, long Id)>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    if (reader.GetBoolean(2))
                    {
                        rows.Add((reader.GetInt32(0), reader.GetInt64(1)));
                    }
                    else
                    {
                        outside.Add(reader.GetInt32(0));
                    }
                }
            }
            await InsertMatchesAsync(conn, tx, type, rows.Where(r => !outside.Contains(r.N)).DistinctBy(r => r.N), "id", ct);
        }

        var linked = 0;
        foreach (var rule in SourceMatching.Current.For(type))
        {
            if (rule.Keys.Any(k => Hidden(scope, k)))
            {
                continue;
            }
            var how = string.Join("+", rule.Keys);
            var on = string.Join(" AND ", rule.Keys.Select(k => $"x.reported->'{k}' = {KeyExpr(type, k, "t")}"));
            var present = string.Join(" AND ", rule.Keys.Select(k => $"jsonb_typeof(x.reported->'{k}') NOT IN ('null')"));
            await using var cmd = new NpgsqlCommand($"""
                SELECT x.n, t.id FROM x_{type} x JOIN {type} t ON {on}
                WHERE {present} AND NOT EXISTS (SELECT 1 FROM m_{type} m WHERE m.n = x.n)
                  AND NOT EXISTS (SELECT 1 FROM m_{type} m WHERE m.id = t.id)
                  AND t.lifecycle <> 'removed' AND {Visible(type, "t", 2)}
                  AND t.source_system IS DISTINCT FROM $1
                  AND NOT EXISTS (SELECT 1 FROM source_record r WHERE r.object_type = '{type}' AND r.object_id = t.id AND r.source_system = $1)
                """, conn, tx);
            cmd.Parameters.Add(new() { Value = source });
            cmd.Parameters.Add(scope.Parameter());
            var found = new List<(int N, long Id)>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    found.Add((reader.GetInt32(0), reader.GetInt64(1)));
                }
            }
            // Exactly one object for a row, and one row for an object; anything else is reported, never guessed.
            var twice = found.GroupBy(f => f.N).Where(g => g.Count() > 1).Select(g => g.Key)
                .Concat(found.GroupBy(f => f.Id).Where(g => g.Count() > 1).SelectMany(g => g.Select(f => f.N))).ToHashSet();
            foreach (var n in twice.Where(n => !ambiguous.ContainsKey(n)))
            {
                ambiguous[n] = how;
            }
            var unique = found.Where(f => !twice.Contains(f.N) && !ambiguous.ContainsKey(f.N)).ToList();
            await InsertMatchesAsync(conn, tx, type, unique, how, ct);
            linked += unique.Count;
        }

        var matched = new Dictionary<int, long>();
        await using (var cmd = new NpgsqlCommand($"SELECT n, id FROM m_{type}", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                matched[reader.GetInt32(0)] = reader.GetInt64(1);
            }
        }
        foreach (var n in ambiguous.Keys.Where(matched.ContainsKey).ToList())
        {
            ambiguous.Remove(n);
        }
        return (matched, linked, ambiguous, outside);
    }

    private static async Task InsertMatchesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string type, IEnumerable<(int N, long Id)> rows,
        string how, CancellationToken ct)
    {
        var list = rows.ToList();
        if (list.Count == 0)
        {
            return;
        }
        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO m_{type} (n, id, how) SELECT n, id, $3 FROM unnest($1::int[], $2::bigint[]) AS u(n, id) ON CONFLICT DO NOTHING
            """, conn, tx);
        cmd.Parameters.Add(new() { Value = list.Select(r => r.N).ToArray() });
        cmd.Parameters.Add(new() { Value = list.Select(r => r.Id).ToArray() });
        cmd.Parameters.Add(new() { Value = how });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>A matching key on the object, as jsonb, cheap where the column is plain.</summary>
    private static string KeyExpr(string type, string key, string alias) =>
        key.StartsWith(ReportedValues.AttributePrefix, StringComparison.Ordinal)
            ? $"{alias}.attributes->'{key[ReportedValues.AttributePrefix.Length..]}'"
            : key is "code" or "name" ? $"to_jsonb({alias}.{key})"
            : $"({ReportedValues.Sql(type, alias)})->'{key}'";

    private static bool Hidden(UserScope scope, string attribute) =>
        (attribute.StartsWith(ReportedValues.AttributePrefix, StringComparison.Ordinal)
            && scope.HiddenAttributes.Contains(attribute[ReportedValues.AttributePrefix.Length..]))
        || (attribute == "position" && scope.HidesCoordinates);

    /// <summary>Each attribute the source reports with another value than the matched object has now.</summary>
    private static async Task<List<Diff>> DiffAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string type, UserScope scope, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"""
            SELECT m.n, m.id, d.key, d.value::text, (c.now->d.key)::text
            FROM m_{type} m JOIN x_{type} x ON x.n = m.n JOIN {type} t ON t.id = m.id,
                 LATERAL (SELECT {ReportedValues.Sql(type, "t")} AS now) c, jsonb_each(x.reported) d(key, value)
            WHERE c.now->d.key IS DISTINCT FROM d.value
            """, conn, tx);
        var diffs = new List<Diff>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var attribute = reader.GetString(2);
            if (!Hidden(scope, attribute))
            {
                diffs.Add(new(type, reader.GetInt32(0), reader.GetInt64(1), attribute, JsonNode.Parse(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : JsonNode.Parse(reader.GetString(4))));
            }
        }
        return diffs;
    }

    /// <summary>The other sources that report each object with differences, the one that created it included.</summary>
    private static async Task<Dictionary<(string Type, long Id), List<string>>> OtherSourcesAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        List<Diff> diffs, string source, CancellationToken ct)
    {
        var others = new Dictionary<(string, long), List<string>>();
        foreach (var type in diffs.Select(d => d.Type).Distinct())
        {
            await using var cmd = new NpgsqlCommand($"""
                SELECT r.object_id, r.source_system FROM source_record r
                WHERE r.object_type = '{type}' AND r.object_id = ANY($1) AND r.source_system <> $2 AND r.missing_since IS NULL
                UNION
                SELECT t.id, t.source_system FROM {type} t WHERE t.id = ANY($1) AND t.source_system <> $2
                """, conn, tx);
            cmd.Parameters.Add(new() { Value = diffs.Where(d => d.Type == type).Select(d => d.Id).Distinct().ToArray() });
            cmd.Parameters.Add(new() { Value = source });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = (type, reader.GetInt64(0));
                (others.TryGetValue(key, out var list) ? list : others[key] = []).Add(reader.GetString(1));
            }
        }
        return others;
    }

    /// <summary>Marks what the source no longer reports, and clears the mark on what it does; nothing is removed.</summary>
    private static async Task<int> MissingAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string type, string source, UserScope scope,
        bool dryRun, Report report, CancellationToken ct)
    {
        var gone = $"""
            r.object_type = '{type}' AND r.source_system = $1 AND {Visible(type, "t", 2)}
            AND NOT EXISTS (SELECT 1 FROM x_{type} x WHERE x.ext = r.external_id)
            """;
        await using var cmd = new NpgsqlCommand(dryRun
            ? $"SELECT r.object_id, r.external_id FROM source_record r JOIN {type} t ON t.id = r.object_id WHERE {gone}"
            : $"""
                UPDATE source_record r SET missing_since = coalesce(r.missing_since, now()) FROM {type} t
                WHERE t.id = r.object_id AND {gone}
                RETURNING r.object_id, r.external_id
                """, conn, tx);
        cmd.Parameters.Add(new() { Value = source });
        cmd.Parameters.Add(scope.Parameter());
        var missing = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            missing++;
            report.Deviate(type, reader.GetInt64(0), reader.GetString(1), "", null, null, "missing");
        }
        return missing;
    }

    /// <summary>The source confirms what it reports: its record of the values, and the object's own mark when it created it.</summary>
    private static async Task ConfirmAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string type, string source, CancellationToken ct)
    {
        await ExecAsync(conn, tx, $"""
            INSERT INTO source_record (object_type, object_id, source_system, external_id, confirmed_at, reported)
            SELECT '{type}', m.id, $1, x.ext, now(), x.reported FROM m_{type} m JOIN x_{type} x ON x.n = m.n
            ON CONFLICT (object_type, object_id, source_system) DO UPDATE
                SET external_id = excluded.external_id, confirmed_at = excluded.confirmed_at, reported = excluded.reported, missing_since = NULL
            """, ct, source);
        await ExecAsync(conn, tx, $"UPDATE {type} t SET last_confirmed_at = now() FROM m_{type} m WHERE t.id = m.id AND t.source_system = $1",
            ct, source);
    }

    private sealed record Creations(List<(Incoming Row, string Payload)> Sites, List<Op> Rest)
    {
        public static Creations Empty { get; } = new([], []);
    }

    /// <summary>
    /// Create operations for what the source has and cmdb does not, checked like a person's: a new code, a position in
    /// the caller's scopes, sites that exist or are created alongside. What cannot be created in a plan yet is reported.
    /// </summary>
    private static async Task<Creations> CreationsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Dictionary<string, List<Incoming>> rows,
        Dictionary<string, Dictionary<int, long>> matched, ExchangeData data, string source, UserScope scope, Report report, CancellationToken ct)
    {
        var sites = new List<(Incoming, string)>();
        var newSites = rows.GetValueOrDefault("site", []);
        var taken = await TakenCodesAsync(conn, tx, "site", [.. newSites.Select(r => ((XSite)r.Source).Code)], ct);
        var inside = await InsideScopeAsync(conn, tx, scope, [.. newSites.Select(r => (XSite)r.Source)], ct);
        var creatable = new List<Incoming>();
        foreach (var r in newSites)
        {
            var s = (XSite)r.Source;
            if (taken.Contains(s.Code))
            {
                report.Deviate("site", null, r.ExternalId, "code", JsonValue.Create(s.Code), null, "code-taken");
            }
            else if (!inside.Contains(r.Row))
            {
                report.Deviate("site", null, r.ExternalId, "position", null, null, "outside-scope");
            }
            else
            {
                creatable.Add(r);
                sites.Add((r, JsonSerializer.Serialize(new
                {
                    code = s.Code,
                    name = s.Name,
                    siteType = s.SiteType,
                    x = s.X,
                    y = s.Y,
                    attributes = JsonNode.Parse(s.Attributes) is JsonObject { Count: > 0 } a ? a : null,
                    lifecycle = s.Lifecycle,
                    source,
                    externalId = s.Id,
                }, OmitNull)));
            }
        }
        report.Count("site").New = creatable.Count;

        // Sites by the source's id: matched and known ones by their id in cmdb, new ones by "new:<id>" until the plan gives them one.
        var siteIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var sitesByRow = data.Sites.ToDictionary(s => s.Row, s => s.Id);
        foreach (var (row, id) in matched.GetValueOrDefault("site", []))
        {
            siteIds[sitesByRow[row]] = id;
        }
        foreach (var (ext, id) in await KnownSitesAsync(conn, tx, source, scope, ct))
        {
            siteIds.TryAdd(ext, id);
        }
        var creating = creatable.Select(r => r.ExternalId).ToHashSet(StringComparer.Ordinal);
        string? SiteRef(string ext) => siteIds.TryGetValue(ext, out var id) ? id.ToString(CultureInfo.InvariantCulture)
            : creating.Contains(ext) ? "new:" + ext : null;

        var rest = new List<Op>();
        var locations = data.Locations.ToDictionary(l => l.Id, StringComparer.Ordinal);
        var equipment = rows.GetValueOrDefault("equipment", []);
        foreach (var r in equipment)
        {
            var e = (XEquipment)r.Source;
            if (e.Parent is not null)
            {
                report.Deviate("equipment", null, r.ExternalId, "placement", null, null, "cannot-create");
                continue;
            }
            if (SiteRef(e.Site) is not { } site)
            {
                report.Deviate("equipment", null, r.ExternalId, "placement", JsonValue.Create(e.Site), null, "unknown-site");
                continue;
            }
            var rack = e.Location is { } l && locations.TryGetValue(l, out var loc) ? loc : null;
            var room = rack?.Parent is { } p && locations.TryGetValue(p, out var parent) ? parent : null;
            rest.Add(new("create_equipment", JsonSerializer.Serialize(new
            {
                site,
                typeKey = e.Type,
                name = e.Name,
                rack = rack?.Name,
                room = room?.Name,
                position = e.RackPosition,
                attributes = JsonNode.Parse(e.Attributes) is JsonObject { Count: > 0 } a ? a : null,
                lifecycle = e.Lifecycle,
                source,
                externalId = e.Id,
            }, OmitNull), false, 1));
        }
        report.Count("equipment").New = rest.Count;

        var cables = rows.GetValueOrDefault("cable", []);
        var takenCables = await TakenCodesAsync(conn, tx, "cable", [.. cables.Select(r => ((XCable)r.Source).Code)], ct);
        var newCables = 0;
        foreach (var r in cables)
        {
            var c = (XCable)r.Source;
            if (takenCables.Contains(c.Code))
            {
                report.Deviate("cable", null, r.ExternalId, "code", JsonValue.Create(c.Code), null, "code-taken");
                continue;
            }
            if (SiteRef(c.A) is not { } a || SiteRef(c.B) is not { } b)
            {
                report.Deviate("cable", null, r.ExternalId, "ends", null, null, "unknown-site");
                continue;
            }
            newCables++;
            rest.Add(new("create_cable", JsonSerializer.Serialize(new
            {
                a,
                b,
                typeKey = c.Type,
                code = c.Code,
                attributes = JsonNode.Parse(c.Attributes) is JsonObject { Count: > 0 } at ? at : null,
                lifecycle = c.Lifecycle,
                source,
                externalId = c.Id,
            }, OmitNull), false, 1));
        }
        report.Count("cable").New = newCables;

        foreach (var r in rows.GetValueOrDefault("service", []))
        {
            report.Deviate("service", null, r.ExternalId, "", null, null, "cannot-create");
        }
        return new(sites, rest);
    }

    private static async Task<HashSet<string>> TakenCodesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string table, string[] codes,
        CancellationToken ct)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        if (codes.Length == 0)
        {
            return taken;
        }
        await using var cmd = new NpgsqlCommand($"SELECT code FROM {table} WHERE code = ANY($1)", conn, tx);
        cmd.Parameters.Add(new() { Value = codes });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            taken.Add(reader.GetString(0));
        }
        return taken;
    }

    /// <summary>Rows of new sites whose position lies inside one of the caller's scopes (all, when unrestricted).</summary>
    private static async Task<HashSet<int>> InsideScopeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, UserScope scope, List<XSite> sites,
        CancellationToken ct)
    {
        if (scope.Unrestricted || sites.Count == 0)
        {
            return [.. sites.Select(s => s.Row)];
        }
        await using var cmd = new NpgsqlCommand("""
            SELECT u.n FROM unnest($2::float8[], $3::float8[], $4::text[], $5::int[]) AS u(x, y, t, n)
            WHERE EXISTS (
                SELECT 1 FROM access_scope a
                WHERE a.key = ANY($1)
                  AND (a.area IS NULL OR ST_Intersects(a.area, ST_SetSRID(ST_MakePoint(u.x, u.y), 3006)))
                  AND (cardinality(a.site_types) = 0 OR u.t = ANY(a.site_types)))
            """, conn, tx);
        cmd.Parameters.Add(new() { Value = scope.Keys });
        cmd.Parameters.Add(new() { Value = sites.Select(s => s.X).ToArray() });
        cmd.Parameters.Add(new() { Value = sites.Select(s => s.Y).ToArray() });
        cmd.Parameters.Add(new() { Value = sites.Select(s => s.SiteType).ToArray() });
        cmd.Parameters.Add(new() { Value = sites.Select(s => s.Row).ToArray() });
        var inside = new HashSet<int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            inside.Add(reader.GetInt32(0));
        }
        return inside;
    }

    /// <summary>Sites the source already knows by its id, that the caller can see, for equipment and cables it reports without them.</summary>
    private static async Task<Dictionary<string, long>> KnownSitesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string source, UserScope scope,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"""
            SELECT r.external_id, r.object_id FROM source_record r JOIN site t ON t.id = r.object_id
            WHERE r.object_type = 'site' AND r.source_system = $1 AND {Visible("site", "t", 2)}
            UNION
            SELECT t.external_id, t.id FROM site t WHERE t.source_system = $1 AND t.external_id IS NOT NULL AND {Visible("site", "t", 2)}
            """, conn, tx);
        cmd.Parameters.Add(new() { Value = source });
        cmd.Parameters.Add(scope.Parameter());
        var known = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            known.TryAdd(reader.GetString(0), reader.GetInt64(1));
        }
        return known;
    }

    /// <summary>A new run supersedes the previous run's plan for review while it is still a draft, so nothing is proposed twice.</summary>
    private static async Task CancelPreviousAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string source, CancellationToken ct)
    {
        await ExecAsync(conn, tx, """
            WITH previous AS (
                UPDATE plan p SET status = 'cancelled', cancelled_at = now(), version = version + 1, updated_at = now()
                FROM reconciliation r WHERE r.source_system = $1 AND r.review_plan_id = p.id AND p.status = 'draft'
                RETURNING p.id)
            UPDATE reservation SET released_at = now()
            WHERE holder_kind = 'plan' AND holder_id IN (SELECT id FROM previous) AND released_at IS NULL
            """, ct, source);
    }

    /// <summary>A plan with the operations, written in passes since planned ids come from operation ids: sites first.</summary>
    private static async Task<long> PlanAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string name, string description, string actor,
        string? client, Creations creations, List<Op> ops, CancellationToken ct)
    {
        long plan;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO plan (name, description, created_by, created_via, client) VALUES ($1, $2, $3, 'api', $4) RETURNING id
            """, conn, tx))
        {
            cmd.Parameters.Add(new() { Value = name });
            cmd.Parameters.Add(new() { Value = description });
            cmd.Parameters.Add(new() { Value = actor });
            cmd.Parameters.Add(new() { Value = (object?)client ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            plan = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var siteOps = await InsertAsync(conn, tx, plan, actor, [.. creations.Sites.Select(s => ("create_site", s.Payload))], ct);
        var planned = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < siteOps.Length; i++)
        {
            planned["new:" + creations.Sites[i].Row.ExternalId] = Planned.ObjectId(siteOps[i]);
        }
        // References to new sites become their planned ids; existing ones are numbers already.
        string Resolve(string payload)
        {
            var node = JsonNode.Parse(payload)!.AsObject();
            foreach (var key in new[] { "site", "a", "b" })
            {
                if (node[key] is JsonValue v && v.TryGetValue<string>(out var text))
                {
                    node[key] = planned.TryGetValue(text, out var id) ? id : long.Parse(text, CultureInfo.InvariantCulture);
                }
            }
            return node.ToJsonString();
        }
        await InsertAsync(conn, tx, plan, actor, [.. creations.Rest.Concat(ops).OrderBy(o => o.Pass).Select(o => (o.Kind, Resolve(o.Payload)))], ct);
        return plan;
    }

    private static async Task<long[]> InsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long plan, string actor,
        List<(string Kind, string Payload)> ops, CancellationToken ct)
    {
        if (ops.Count == 0)
        {
            return [];
        }
        await using var cmd = new NpgsqlCommand("""
            WITH base AS (SELECT coalesce(max(seq), 0) AS s FROM plan_operation WHERE plan_id = $1)
            INSERT INTO plan_operation (plan_id, seq, kind, payload, created_by)
            SELECT $1, base.s + u.n, u.k, u.p::jsonb, $4 FROM base, unnest($2::text[], $3::text[]) WITH ORDINALITY AS u(k, p, n)
            RETURNING id, seq
            """, conn, tx);
        cmd.Parameters.Add(new() { Value = plan });
        cmd.Parameters.Add(new() { Value = ops.Select(o => o.Kind).ToArray() });
        cmd.Parameters.Add(new() { Value = ops.Select(o => o.Payload).ToArray() });
        cmd.Parameters.Add(new() { Value = actor });
        var rows = new List<(long Id, int Seq)>(ops.Count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetInt64(0), reader.GetInt32(1)));
        }
        return [.. rows.OrderBy(r => r.Seq).Select(r => r.Id)];
    }

    private async Task<ReconciliationReport> SaveAsync(ClaimsPrincipal user, Report report, Stopwatch sw, CancellationToken ct)
    {
        var result = report.Build(Math.Round(sw.Elapsed.TotalMilliseconds, 1));
        await using var cmd = db.CreateCommand("""
            INSERT INTO reconciliation (source_system, run_by, dry_run, elapsed_ms, review_plan_id, applied_plan_id, report)
            VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb) RETURNING id
            """);
        cmd.Parameters.Add(new() { Value = result.Source });
        cmd.Parameters.Add(new() { Value = PlanSql.Actor(user) });
        cmd.Parameters.Add(new() { Value = result.DryRun });
        cmd.Parameters.Add(new() { Value = result.ElapsedMs });
        cmd.Parameters.Add(new() { Value = (object?)result.ReviewPlanId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new() { Value = (object?)result.AppliedPlanId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new() { Value = JsonSerializer.Serialize(result, Reconciliations.Json) });
        var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return result with { Id = id };
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] values)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var v in values)
        {
            cmd.Parameters.Add(new() { Value = v });
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The report as it is built during a run.</summary>
    private sealed class Report(string source, bool dryRun)
    {
        private readonly Dictionary<string, ReconciliationCount> _counts = [];
        private readonly List<ReconciliationDeviation> _deviations = [];
        private readonly Dictionary<string, int> _reasons = new(StringComparer.Ordinal);

        public List<string> Errors { get; } = [];
        public List<string> NotReconciled { get; } = [];
        public long? ReviewPlanId { get; set; }
        public long? AppliedPlanId { get; set; }
        public string? AutoApplyProblem { get; set; }
        public int Operations { get; set; }

        public ReconciliationCount Count(string type) =>
            _counts.TryGetValue(type, out var c) ? c : _counts[type] = new ReconciliationCount { ObjectType = type };

        public void Deviate(string type, long? id, string externalId, string attribute, JsonNode? sourceValue, JsonNode? current, string reason)
        {
            _reasons[reason] = _reasons.GetValueOrDefault(reason) + 1;
            Count(type).Deviations++;
            if (_deviations.Count < MaxDeviations)
            {
                _deviations.Add(new(type, id, externalId, attribute, sourceValue?.DeepClone(), current?.DeepClone(), reason));
            }
        }

        public ReconciliationReport Build(double elapsedMs) => new(0, source, dryRun, [.. ObjectTypes.Where(_counts.ContainsKey).Select(t => _counts[t])],
            Operations, _reasons, [.. _deviations], Errors, NotReconciled, ReviewPlanId, AppliedPlanId, AutoApplyProblem, elapsedMs);
    }
}
