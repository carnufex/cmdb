using System.Text.Json;
using System.Text.Json.Nodes;
using Cmdb.Catalog;
using Npgsql;

namespace Cmdb.Api.Features.Plans;

/// <summary>
/// Runs a plan's operations against production (#24, #107), in one transaction. Objects the plan creates get real ids;
/// later operations that refer to them are rewritten on the way, and so are the draft plans that build on this one.
/// </summary>
internal sealed class PlanApply(NpgsqlConnection conn, NpgsqlTransaction tx)
{
    /// <summary>Planned id → production id, for objects, conductors and terminals.</summary>
    public Dictionary<long, long> Ids { get; } = [];

    /// <summary>Runs one operation; false when it no longer fits production and nothing should be kept.</summary>
    public async Task<bool> RunAsync(PlanOp op, CancellationToken ct)
    {
        var p = Remap(op).Payload;
        switch (op.Kind)
        {
            case "create_site":
                Ids[Planned.ObjectId(op.Id)] = await ScalarAsync<long>("""
                    INSERT INTO site (code, name, site_type, geom, lifecycle)
                    VALUES ($1, $2, $3, ST_SetSRID(ST_MakePoint($4, $5), 3006), 'planned') RETURNING id
                    """, ct, p.GetProperty("code").GetString()!, p.GetProperty("name").GetString()!, p.GetProperty("siteType").GetString()!,
                    p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble());
                return true;

            case "create_equipment":
                await CreateEquipmentAsync(op, p, ct);
                return true;

            case "create_cable":
                await CreateCableAsync(op, p, ct);
                return true;

            case "connect":
                return await ExecuteAsync("""
                    INSERT INTO connection (a_terminal_id, b_terminal_id, kind, lifecycle)
                    VALUES (least($1, $2), greatest($1, $2), $3::connection_kind, 'in_service')
                    ON CONFLICT (a_terminal_id, b_terminal_id) WHERE valid_to IS NULL DO NOTHING
                    """, ct, p.GetProperty("a").GetInt64(), p.GetProperty("b").GetInt64(), p.GetProperty("kind").GetString()!) > 0;

            case "disconnect":
                return await ExecuteAsync("""
                    UPDATE connection SET valid_to = now(), lifecycle = 'removed'
                    WHERE a_terminal_id = least($1, $2) AND b_terminal_id = greatest($1, $2) AND valid_to IS NULL
                    """, ct, p.GetProperty("a").GetInt64(), p.GetProperty("b").GetInt64()) > 0;

            case "set_attributes":
                return await ExecuteAsync($"UPDATE {Table(p)} SET attributes = jsonb_strip_nulls(attributes || $2::jsonb) WHERE id = $1", ct,
                    p.GetProperty("id").GetInt64(), p.GetProperty("attributes").GetRawText()) > 0;

            case "set_lifecycle":
                return await ExecuteAsync($"UPDATE {Table(p)} SET lifecycle = $2::lifecycle_state WHERE id = $1", ct,
                    p.GetProperty("id").GetInt64(), p.GetProperty("lifecycle").GetString()!) > 0;

            default:
                return await ExecuteAsync($"UPDATE {Table(p)} SET name = $2 WHERE id = $1", ct,
                    p.GetProperty("id").GetInt64(), p.GetProperty("name").GetString()!) > 0;
        }
    }

    /// <summary>
    /// Rewrites the operations of draft plans building on the applied one that refer to objects it created, so they
    /// point at production's ids now.
    /// </summary>
    public async Task RewriteDependentsAsync(IReadOnlyCollection<long> dependents, CancellationToken ct)
    {
        if (Ids.Count == 0 || dependents.Count == 0)
        {
            return;
        }
        var changed = new List<(long Id, string Payload, long Plan)>();
        await using (var cmd = new NpgsqlCommand("SELECT id, plan_id, kind, payload::text FROM plan_operation WHERE plan_id = ANY($1)", conn, tx))
        {
            cmd.Parameters.Add(new() { Value = dependents.ToArray() });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var op = new PlanOp(reader.GetInt64(0), reader.GetInt64(1), 0, reader.GetString(2),
                    JsonDocument.Parse(reader.GetString(3)).RootElement.Clone(), "", default);
                var remapped = Remap(op);
                if (!ReferenceEquals(remapped, op))
                {
                    changed.Add((op.Id, remapped.Payload.GetRawText(), op.PlanId));
                }
            }
        }
        foreach (var (id, payload, _) in changed)
        {
            await ExecuteAsync("UPDATE plan_operation SET payload = $2::jsonb WHERE id = $1", ct, id, payload);
        }
        foreach (var plan in changed.Select(c => c.Plan).Distinct())
        {
            await ExecuteAsync("UPDATE plan SET version = version + 1, updated_at = now() WHERE id = $1", ct, plan);
        }
    }

    /// <summary>A graph change with planned ids replaced by the ones production gave them.</summary>
    public Cmdb.Graph.GraphChange Production(Cmdb.Graph.GraphChange change)
    {
        long Id(long id) => Ids.GetValueOrDefault(id, id);
        return change switch
        {
            Cmdb.Graph.GraphNewSite s => new Cmdb.Graph.GraphNewSite(Id(s.Id)),
            Cmdb.Graph.GraphNewEquipment e => new Cmdb.Graph.GraphNewEquipment(Id(e.Id), Id(e.SiteId), [.. e.Ports.Select(Id)]),
            Cmdb.Graph.GraphNewCable c => new Cmdb.Graph.GraphNewCable(Id(c.Id),
                [.. c.Conductors.Select(k => new Cmdb.Graph.GraphNewConductor(Id(k.Id), Id(k.EndA), Id(k.EndB)))]),
            Cmdb.Graph.GraphEdgeChange e => e with { A = Id(e.A), B = Id(e.B) },
            _ => change,
        };
    }

    /// <summary>The operation with planned ids it refers to replaced by production's, or itself when there are none.</summary>
    private PlanOp Remap(PlanOp op)
    {
        string[] fields = op.Kind switch
        {
            "connect" or "disconnect" or "create_cable" => ["a", "b"],
            "create_equipment" => ["site"],
            _ => [],
        };
        if (!fields.Any(f => op.Payload.GetProperty(f).GetInt64() is < 0 and var id && Ids.ContainsKey(id)))
        {
            return op;
        }
        var node = JsonNode.Parse(op.Payload.GetRawText())!.AsObject();
        foreach (var field in fields)
        {
            if (Ids.TryGetValue(node[field]!.GetValue<long>(), out var real))
            {
                node[field] = real;
            }
        }
        return op with { Payload = JsonDocument.Parse(node.ToJsonString()).RootElement.Clone() };
    }

    private async Task CreateEquipmentAsync(PlanOp op, JsonElement p, CancellationToken ct)
    {
        var site = p.GetProperty("site").GetInt64();
        var typeKey = p.GetProperty("typeKey").GetString()!;
        var type = TypeCatalog.Embedded.Find(typeKey) ?? throw new InvalidOperationException($"Unknown equipment type {typeKey}.");
        // Equipment sits in a rack: the named one (#26), created in a building on the site when missing; without a name,
        // the site's first rack, or a new "Rack 1".
        var rack = p.TryGetProperty("rack", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : null;
        var location = rack is null
            ? await ScalarAsync<long?>("SELECT id FROM location WHERE site_id = $1 ORDER BY (kind = 'rack') DESC, id LIMIT 1", ct, site)
            : await ScalarAsync<long?>("SELECT id FROM location WHERE site_id = $1 AND kind = 'rack' AND name = $2 ORDER BY id LIMIT 1", ct, site, rack);
        if (location is null)
        {
            var building = await ScalarAsync<long?>("SELECT id FROM location WHERE site_id = $1 AND kind = 'building' ORDER BY id LIMIT 1", ct, site)
                ?? await ScalarAsync<long>("INSERT INTO location (site_id, kind, name) VALUES ($1, 'building', 'Byggnad A') RETURNING id", ct, site);
            location = await ScalarAsync<long>("INSERT INTO location (site_id, parent_id, kind, name) VALUES ($1, $2, 'rack', $3) RETURNING id", ct,
                site, building, rack ?? "Rack 1");
        }
        var equipment = await ScalarAsync<long>("""
            INSERT INTO equipment (equipment_type_id, site_id, location_id, name)
            SELECT t.id, $1, $2, $3 FROM equipment_type t WHERE t.key = $4 RETURNING id
            """, ct, site, location, p.GetProperty("name").GetString()!, typeKey);
        Ids[Planned.ObjectId(op.Id)] = equipment;

        var ports = PortExpansion.Expand(type);
        var terminals = await NewTerminalsAsync("port", ports.Count, ct);
        await ExecuteAsync("""
            INSERT INTO port (terminal_id, kind, equipment_id, name, port_type, port_group, position)
            SELECT t, 'port', $1, n, pt, pg, pos FROM unnest($2::bigint[], $3::text[], $4::text[], $5::text[], $6::int[]) AS u(t, n, pt, pg, pos)
            """, ct, equipment, terminals, ports.Select(x => x.Name).ToArray(), ports.Select(x => x.Type).ToArray(),
            ports.Select(x => x.Group).ToArray(), ports.Select(x => x.Position).ToArray());
        for (var i = 0; i < ports.Count; i++)
        {
            Ids[Planned.Terminal(op.Id, ports[i].Position)] = terminals[i];
        }
    }

    private async Task CreateCableAsync(PlanOp op, JsonElement p, CancellationToken ct)
    {
        var typeKey = p.GetProperty("typeKey").GetString()!;
        var cable = await ScalarAsync<long>("""
            INSERT INTO cable (cable_type_id, code, a_site_id, b_site_id, geom)
            SELECT t.id, 'NY-' || gen_random_uuid(), a.id, b.id, g
            FROM cable_type t, site a, site b,
                 LATERAL (SELECT ST_MakeLine(ST_PointOnSurface(a.geom), ST_PointOnSurface(b.geom)) AS g) line
            WHERE t.key = $1 AND a.id = $2 AND b.id = $3
            RETURNING id
            """, ct, typeKey, p.GetProperty("a").GetInt64(), p.GetProperty("b").GetInt64());
        await ExecuteAsync("UPDATE cable SET code = 'KP-' || lpad(id::text, 6, '0') WHERE id = $1", ct, cable);
        Ids[Planned.ObjectId(op.Id)] = cable;

        var count = Planned.ConductorCount(typeKey);
        var conductors = new List<long>();
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO conductor (cable_id, number) SELECT $1, n FROM generate_series(1, $2) n RETURNING id", conn, tx))
        {
            cmd.Parameters.Add(new() { Value = cable });
            cmd.Parameters.Add(new() { Value = count });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                conductors.Add(reader.GetInt64(0));
            }
        }
        conductors.Sort();
        var ends = await NewTerminalsAsync("conductor_end", 2 * count, ct);
        await ExecuteAsync("""
            INSERT INTO conductor_end (terminal_id, conductor_id, side)
            SELECT t, c, s FROM unnest($1::bigint[], $2::bigint[], $3::text[]) AS u(t, c, s)
            """, ct, ends, conductors.SelectMany(c => new[] { c, c }).ToArray(),
            Enumerable.Range(0, 2 * count).Select(i => i % 2 == 0 ? "A" : "B").ToArray());
        for (var k = 1; k <= count; k++)
        {
            Ids[Planned.Conductor(op.Id, k)] = conductors[k - 1];
            Ids[Planned.Terminal(op.Id, (2 * k) - 1)] = ends[(2 * k) - 2];
            Ids[Planned.Terminal(op.Id, 2 * k)] = ends[(2 * k) - 1];
        }
    }

    /// <summary>New terminals in id order.</summary>
    private async Task<long[]> NewTerminalsAsync(string kind, int count, CancellationToken ct)
    {
        var ids = new List<long>(count);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO terminal (kind) SELECT $1::terminal_kind FROM generate_series(1, $2) RETURNING id", conn, tx);
        cmd.Parameters.Add(new() { Value = kind });
        cmd.Parameters.Add(new() { Value = count });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        ids.Sort();
        return [.. ids];
    }

    private static string Table(JsonElement p) => p.GetProperty("type").GetString() switch
    {
        "site" => "site",
        "equipment" => "equipment",
        "cable" => "cable",
        var t => throw new InvalidOperationException($"Unknown object type {t}."),
    };

    private async Task<int> ExecuteAsync(string sql, CancellationToken ct, params object[] values)
    {
        await using var cmd = Command(sql, values);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<T> ScalarAsync<T>(string sql, CancellationToken ct, params object[] values)
    {
        await using var cmd = Command(sql, values);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default! : (T)value;
    }

    private NpgsqlCommand Command(string sql, object[] values)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var value in values)
        {
            cmd.Parameters.Add(value is null ? new NpgsqlParameter { Value = DBNull.Value } : new NpgsqlParameter { Value = value });
        }
        return cmd;
    }
}
