using Cmdb.Api.Auth;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Objects;

public sealed record SummaryRequest(string Type, long Id);

/// <summary>Key facts for a hover card. Facts are label/value pairs so the card needs no per-type layout.</summary>
public sealed record ObjectSummary(string Type, long Id, string Code, string? Name, string Lifecycle, IReadOnlyList<SummaryFact> Facts);

public sealed record SummaryFact(string Label, string Value);

public sealed class SummaryEndpoint(NpgsqlDataSource db) : Endpoint<SummaryRequest, ObjectSummary>
{
    private static readonly Dictionary<string, string> Queries = new(StringComparer.Ordinal)
    {
        ["site"] = $"""
            SELECT s.code, s.name, s.lifecycle::text,
                   ARRAY['Typ', 'Utrustning', 'Kablar'],
                   ARRAY[s.site_type, (SELECT count(*) FROM equipment e WHERE e.site_id = s.id)::text,
                         (SELECT count(*) FROM cable c WHERE c.a_site_id = s.id OR c.b_site_id = s.id)::text]
            FROM site s WHERE s.id = $1 AND {ScopeSql.Site("s.id", 2)}
            """,
        ["equipment"] = $"""
            SELECT e.name, NULL, e.lifecycle::text,
                   ARRAY['Modell', 'Site', 'Portar'],
                   ARRAY[et.manufacturer || ' ' || et.model, s.code, (SELECT count(*) FROM port p WHERE p.equipment_id = e.id)::text]
            FROM equipment e JOIN equipment_type et ON et.id = e.equipment_type_id JOIN site s ON s.id = e.site_id
            WHERE e.id = $1 AND {ScopeSql.Site("s.id", 2)}
            """,
        ["cable"] = $"""
            SELECT c.code, ct.name, c.lifecycle::text,
                   ARRAY['Mellan', 'Längd'],
                   ARRAY[CASE WHEN {ScopeSql.Site("a.id", 2)} THEN a.code ELSE 'Dold' END || ' – ' || CASE WHEN {ScopeSql.Site("b.id", 2)} THEN b.code ELSE 'Dold' END, round((c.length_m / 1000.0)::numeric, 1)::text || ' km']
            FROM cable c JOIN cable_type ct ON ct.id = c.cable_type_id JOIN site a ON a.id = c.a_site_id JOIN site b ON b.id = c.b_site_id
            WHERE c.id = $1 AND {ScopeSql.Cable("c.id", 2)}
            """,
        ["service"] = $"""
            SELECT v.code, v.name, v.lifecycle::text, ARRAY['Typ', 'Kretsar'],
                   ARRAY[v.service_type, (SELECT count(*) FROM service_circuit sc WHERE sc.service_id = v.id)::text]
            FROM service v WHERE v.id = $1 AND {ScopeSql.Service("v.id", 2)}
            """,
        ["circuit"] = $"""
            SELECT r.code, NULL, r.lifecycle::text, ARRAY['Lager', 'Hopp'],
                   ARRAY[r.layer::text, (SELECT count(*) FROM circuit_hop h WHERE h.circuit_id = r.id)::text]
            FROM circuit r WHERE r.id = $1 AND {ScopeSql.Circuit("r.id", 2)}
            """,
    };

    public override void Configure() => Get("/summary/{type}/{id}");

    public override async Task HandleAsync(SummaryRequest req, CancellationToken ct)
    {
        if (!Queries.TryGetValue(req.Type, out var sql))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter { Value = req.Id });
        cmd.Parameters.Add(new NpgsqlParameter { Value = HttpContext.Scope().Keys });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var labels = reader.GetFieldValue<string[]>(3);
        var values = reader.GetFieldValue<string[]>(4);
        await Send.OkAsync(new ObjectSummary(req.Type, req.Id, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2), [.. labels.Zip(values, (l, v) => new SummaryFact(l, v))]), ct);
    }
}
