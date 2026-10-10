using System.Text.Json;
using System.Text.Json.Nodes;
using Cmdb.Catalog;
using Npgsql;

namespace Cmdb.Api.Features.Plans;

/// <summary>
/// Runs a plan's operations against production (#24, #107), in one transaction. Objects the plan creates get real ids;
/// later operations that refer to them are rewritten on the way, and so are the draft plans that build on this one.
/// </summary>
internal sealed class PlanApply(NpgsqlConnection conn, NpgsqlTransaction tx, string actor = "plan")
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
                    INSERT INTO site (code, name, site_type, geom, lifecycle, attributes)
                    VALUES ($1, $2, $3, ST_SetSRID(ST_MakePoint($4, $5), 3006), 'planned', $6::jsonb) RETURNING id
                    """, ct, p.GetProperty("code").GetString()!, p.GetProperty("name").GetString()!, p.GetProperty("siteType").GetString()!,
                    p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble(), Attributes(p));
                await FromSourceAsync("site", Ids[Planned.ObjectId(op.Id)], p, ct);
                return true;

            case "create_equipment":
                await CreateEquipmentAsync(op, p, ct);
                return true;

            case "create_cable":
                await CreateCableAsync(op, p, ct);
                return true;

            case "split_cable":
                return await SplitCableAsync(op, p, ct);

            case "remove":
                return await RemoveAsync(p, ct);

            case "move":
                return await MoveAsync(p, ct);

            case "create_location":
                {
                    var parent = p.TryGetProperty("parent", out var pa) && pa.ValueKind == JsonValueKind.Number ? (object)pa.GetInt64() : DBNull.Value;
                    var units = p.TryGetProperty("rackUnits", out var ru) && ru.ValueKind == JsonValueKind.Number ? (object)ru.GetInt16() : DBNull.Value;
                    var location = await ScalarAsync<long>("""
                        INSERT INTO location (site_id, parent_id, kind, name, rack_units) VALUES ($1, $2, $3, $4, $5) RETURNING id
                        """, ct, p.GetProperty("site").GetInt64(), parent, p.GetProperty("kind").GetString()!, p.GetProperty("name").GetString()!, units);
                    Ids[Planned.ObjectId(op.Id)] = location;
                    await FromSourceAsync("location", location, p, ct);
                    return true;
                }

            case "create_service":
                {
                    var service = await ScalarAsync<long>("""
                        INSERT INTO service (code, name, service_type, attributes, lifecycle) VALUES ($1, $2, $3, $4::jsonb, 'planned') RETURNING id
                        """, ct, p.GetProperty("code").GetString()!, p.GetProperty("name").GetString()!, p.GetProperty("serviceType").GetString()!, Attributes(p));
                    Ids[Planned.ObjectId(op.Id)] = service;
                    await FromSourceAsync("service", service, p, ct);
                    return true;
                }

            case "create_circuit":
                {
                    var hops = NetworkLinks.Hops(p).Select(h => Ids.GetValueOrDefault(h, h)).ToArray();
                    var circuit = await ScalarAsync<long>("""
                        INSERT INTO circuit (code, layer, a_terminal_id, b_terminal_id, lifecycle) VALUES ($1, $2::circuit_layer, $3, $4, 'planned') RETURNING id
                        """, ct, p.GetProperty("code").GetString()!, p.GetProperty("layer").GetString()!, hops[0], hops[^1]);
                    Ids[Planned.ObjectId(op.Id)] = circuit;
                    await HopsAsync(circuit, hops, ct);
                    await FromSourceAsync("circuit", circuit, p, ct);
                    return true;
                }

            case "set_circuit_path":
                {
                    var circuit = p.GetProperty("id").GetInt64();
                    var hops = NetworkLinks.Hops(p).Select(h => Ids.GetValueOrDefault(h, h)).ToArray();
                    if (await ExecuteAsync("UPDATE circuit SET a_terminal_id = $2, b_terminal_id = $3 WHERE id = $1", ct, circuit, hops[0], hops[^1]) == 0)
                    {
                        return false;
                    }
                    await ExecuteAsync("DELETE FROM circuit_hop WHERE circuit_id = $1", ct, circuit);
                    await HopsAsync(circuit, hops, ct);
                    return true;
                }

            case "link_circuit":
                return p.TryGetProperty("remove", out var rc) && rc.ValueKind == JsonValueKind.True
                    ? await ExecuteAsync("DELETE FROM circuit_dependency WHERE circuit_id = $1 AND carrier_id = $2", ct,
                        p.GetProperty("circuit").GetInt64(), p.GetProperty("carrier").GetInt64()) > 0
                    : await ExecuteAsync("INSERT INTO circuit_dependency (circuit_id, carrier_id) VALUES ($1, $2) ON CONFLICT DO NOTHING", ct,
                        p.GetProperty("circuit").GetInt64(), p.GetProperty("carrier").GetInt64()) > 0;

            case "link_service":
                return p.TryGetProperty("remove", out var rs) && rs.ValueKind == JsonValueKind.True
                    ? await ExecuteAsync("DELETE FROM service_circuit WHERE service_id = $1 AND circuit_id = $2", ct,
                        p.GetProperty("service").GetInt64(), p.GetProperty("circuit").GetInt64()) > 0
                    : await ExecuteAsync("INSERT INTO service_circuit (service_id, circuit_id) VALUES ($1, $2) ON CONFLICT DO NOTHING", ct,
                        p.GetProperty("service").GetInt64(), p.GetProperty("circuit").GetInt64()) > 0;

            case "set_conductor_usage":
                {
                    // Stated usage (#238): dark, leased dark fibre or spare; lit stays derived.
                    var usage = p.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.String ? (object)u.GetString()! : DBNull.Value;
                    return await ExecuteAsync("UPDATE conductor SET usage = $3 WHERE cable_id = $1 AND number = ANY($2)", ct,
                        p.GetProperty("id").GetInt64(), p.GetProperty("conductors").EnumerateArray().Select(n => n.GetInt32()).ToArray(), usage) > 0;
                }

            case "set_classification":
                {
                    // Classifications are not in the graph yet (#177): the row is all there is to change.
                    var type = p.GetProperty("type").GetString()!;
                    var id = p.GetProperty("id").GetInt64();
                    if (await ScalarAsync<long?>($"SELECT id FROM {(type == "service" ? "service" : Table(p))} WHERE id = $1", ct, id) is null)
                    {
                        return false;
                    }
                    await Classifications.ClassificationStore.SetAsync(conn, tx, type, id, p.GetProperty("schema").GetString()!,
                        p.TryGetProperty("level", out var level) && level.ValueKind == JsonValueKind.Number ? level.GetInt32() : null, actor, ct);
                    return true;
                }

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
            Cmdb.Graph.GraphCircuitsChange c => c with
            {
                Set = [.. c.Set.Select(x => x with { Id = Id(x.Id), Hops = [.. x.Hops.Select(Id)], Carriers = [.. x.Carriers.Select(Id)], Services = [.. x.Services.Select(Id)] })],
            },
            _ => change,
        };
    }

    /// <summary>The operation with planned ids it refers to replaced by production's, or itself when there are none.</summary>
    private PlanOp Remap(PlanOp op)
    {
        string[] fields = op.Kind switch
        {
            "connect" or "disconnect" or "create_cable" => ["a", "b"],
            "create_equipment" => ["site", "parent"],
            "split_cable" or "move" => ["site"],
            _ when NetworkLinks.IdFields(op.Kind).Length > 0 => NetworkLinks.IdFields(op.Kind),
            "set_classification" => ["id"],
            _ => [],
        };
        bool Planned(string f) => op.Payload.TryGetProperty(f, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt64() is < 0 and var id && Ids.ContainsKey(id);
        var hops = op.Kind is "create_circuit" or "set_circuit_path" && NetworkLinks.Hops(op.Payload).Any(h => h < 0 && Ids.ContainsKey(h));
        if (!fields.Any(Planned) && !hops)
        {
            return op;
        }
        var node = JsonNode.Parse(op.Payload.GetRawText())!.AsObject();
        foreach (var field in fields.Where(Planned))
        {
            node[field] = Ids[node[field]!.GetValue<long>()];
        }
        if (hops)
        {
            node["hops"] = new JsonArray([.. NetworkLinks.Hops(op.Payload).Select(h => (JsonNode)Ids.GetValueOrDefault(h, h))]);
        }
        return op with { Payload = JsonDocument.Parse(node.ToJsonString()).RootElement.Clone() };
    }

    /// <summary>
    /// The rack equipment goes in: the named one on the site, or the site's first, or a new one (in the named room, in a
    /// building that is created when the site has none).
    /// </summary>
    private async Task<long> RackAsync(long site, string? rack, string? room, CancellationToken ct)
    {
        var location = rack is null
            ? await ScalarAsync<long?>("SELECT id FROM location WHERE site_id = $1 ORDER BY (kind = 'rack') DESC, id LIMIT 1", ct, site)
            : await ScalarAsync<long?>("SELECT id FROM location WHERE site_id = $1 AND kind = 'rack' AND name = $2 ORDER BY id LIMIT 1", ct, site, rack);
        if (location is null)
        {
            var building = await ScalarAsync<long?>("SELECT id FROM location WHERE site_id = $1 AND kind = 'building' ORDER BY id LIMIT 1", ct, site)
                ?? await ScalarAsync<long>("INSERT INTO location (site_id, kind, name) VALUES ($1, 'building', 'Byggnad A') RETURNING id", ct, site);
            // A named room (#173) holds the rack; it is created in the building when missing.
            var parent = room is null ? building
                : await ScalarAsync<long?>("SELECT id FROM location WHERE site_id = $1 AND kind = 'room' AND name = $2 ORDER BY id LIMIT 1", ct, site, room)
                    ?? await ScalarAsync<long>("INSERT INTO location (site_id, parent_id, kind, name) VALUES ($1, $2, 'room', $3) RETURNING id", ct,
                        site, building, room);
            location = await ScalarAsync<long>("INSERT INTO location (site_id, parent_id, kind, name, rack_units) VALUES ($1, $2, 'rack', $3, 42) RETURNING id", ct,
                site, parent, rack ?? "Rack 1");
        }
        return location.Value;
    }

    private async Task CreateEquipmentAsync(PlanOp op, JsonElement p, CancellationToken ct)
    {
        var site = p.GetProperty("site").GetInt64();
        var typeKey = p.GetProperty("typeKey").GetString()!;
        var type = TypeCatalog.Current.Find(typeKey) ?? throw new InvalidOperationException($"Unknown equipment type {typeKey}.");
        // Equipment sits in a rack: the named one (#26), created in a building on the site when missing; without a name,
        // the site's first rack, or a new "Rack 1".
        if (p.TryGetProperty("parent", out var parentId) && parentId.ValueKind == JsonValueKind.Number)
        {
            await CreateCardAsync(op, p, type, parentId.GetInt64(), ct);
            return;
        }
        var rack = p.TryGetProperty("rack", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : null;
        var room = p.TryGetProperty("room", out var rm) && rm.ValueKind == JsonValueKind.String ? rm.GetString()! : null;
        var location = await RackAsync(site, rack, room, ct);
        // Its lowest rack unit (#173): the planned one, or on top of what the rack holds.
        var position = p.TryGetProperty("position", out var at) && at.ValueKind == JsonValueKind.Number ? (object)at.GetInt16() : DBNull.Value;
        var equipment = await ScalarAsync<long>("""
            INSERT INTO equipment (equipment_type_id, site_id, location_id, name, rack_position)
            SELECT t.id, $1, $2, $3,
                   CASE WHEN t.rack_units IS NULL THEN NULL
                        ELSE coalesce($5::smallint, (SELECT coalesce(max(e.rack_position + coalesce(u.rack_units, 1)), 1)::smallint
                                                     FROM equipment e JOIN equipment_type u ON u.id = e.equipment_type_id
                                                     WHERE e.location_id = $2 AND e.rack_position IS NOT NULL AND e.lifecycle <> 'removed')) END
            FROM equipment_type t WHERE t.key = $4 RETURNING id
            """, ct, site, location, p.GetProperty("name").GetString()!, typeKey, position);
        Ids[Planned.ObjectId(op.Id)] = equipment;
        if (p.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object)
        {
            await ExecuteAsync("UPDATE equipment SET attributes = $2::jsonb WHERE id = $1", ct, equipment, attributes.GetRawText());
        }
        await FromSourceAsync("equipment", equipment, p, ct);

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

    /// <summary>A card (#230): in its parent's slot at the parent's site, with ports named for the slot.</summary>
    private async Task CreateCardAsync(PlanOp op, JsonElement p, EquipmentType type, long parent, CancellationToken ct)
    {
        var slot = p.GetProperty("slot").GetString()!;
        var equipment = await ScalarAsync<long>("""
            INSERT INTO equipment (equipment_type_id, site_id, parent_id, slot, name)
            SELECT t.id, e.site_id, e.id, $3, $4 FROM equipment_type t, equipment e WHERE t.key = $1 AND e.id = $2 RETURNING id
            """, ct, type.Key, parent, slot, p.GetProperty("name").GetString()!);
        Ids[Planned.ObjectId(op.Id)] = equipment;
        await FromSourceAsync("equipment", equipment, p, ct);
        var ports = PortExpansion.Expand(type, slot);
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

    /// <summary>A circuit's path (#230): its hops in order.</summary>
    private async Task HopsAsync(long circuit, long[] hops, CancellationToken ct) =>
        await ExecuteAsync("""
            INSERT INTO circuit_hop (circuit_id, seq, terminal_id) SELECT $1, n - 1, t FROM unnest($2::bigint[]) WITH ORDINALITY AS u(t, n)
            """, ct, circuit, hops);

    /// <summary>
    /// An object reconciliation (#216) creates keeps its source's lifecycle and id, so that the next run matches it on the
    /// source and id instead of creating it again. Objects planned by hand start as planned, with no source.
    /// </summary>
    private async Task FromSourceAsync(string table, long id, JsonElement p, CancellationToken ct)
    {
        if (p.TryGetProperty("lifecycle", out var lifecycle) && lifecycle.ValueKind == JsonValueKind.String)
        {
            await ExecuteAsync($"UPDATE {table} SET lifecycle = $2::lifecycle_state WHERE id = $1", ct, id, lifecycle.GetString()!);
        }
        if (p.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String
            && p.TryGetProperty("externalId", out var externalId) && externalId.ValueKind == JsonValueKind.String)
        {
            await ExecuteAsync($"UPDATE {table} SET source_system = $2, external_id = $3, last_confirmed_at = now() WHERE id = $1", ct,
                id, source.GetString()!, externalId.GetString()!);
        }
    }

    private async Task CreateCableAsync(PlanOp op, JsonElement p, CancellationToken ct)
    {
        var typeKey = p.GetProperty("typeKey").GetString()!;
        // A route given with the cable (an import, #170) is kept between the two sites; otherwise a straight line.
        var route = p.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Array ? line.EnumerateArray().ToList() : [];
        var cable = await ScalarAsync<long>("""
            INSERT INTO cable (cable_type_id, code, a_site_id, b_site_id, geom, attributes)
            SELECT t.id, 'NY-' || gen_random_uuid(), a.id, b.id, g, $6::jsonb
            FROM cable_type t, site a, site b,
                 LATERAL (SELECT CASE WHEN cardinality($4::float8[]) >= 2
                     THEN ST_MakeLine(ARRAY[ST_PointOnSurface(a.geom)]
                          || ARRAY(SELECT ST_SetSRID(ST_MakePoint(x, y), 3006) FROM unnest($4::float8[], $5::float8[]) WITH ORDINALITY AS u(x, y, n) ORDER BY n)
                          || ARRAY[ST_PointOnSurface(b.geom)])
                     ELSE ST_MakeLine(ST_PointOnSurface(a.geom), ST_PointOnSurface(b.geom)) END AS g) line
            WHERE t.key = $1 AND a.id = $2 AND b.id = $3
            RETURNING id
            """, ct, typeKey, p.GetProperty("a").GetInt64(), p.GetProperty("b").GetInt64(),
            route.Select(pt => pt[0].GetDouble()).ToArray(), route.Select(pt => pt[1].GetDouble()).ToArray(), Attributes(p));
        // The source's code when reconciliation (#216) creates it, otherwise one of cmdb's own.
        await ExecuteAsync("UPDATE cable SET code = coalesce($2, 'KP-' || lpad(id::text, 6, '0')) WHERE id = $1", ct, cable,
            p.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString()! : DBNull.Value);
        Ids[Planned.ObjectId(op.Id)] = cable;
        await FromSourceAsync("cable", cable, p, ct);

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

    /// <summary>
    /// A site inserted into a cable (#168): two new cables split at the site, with the old conductors' numbers and colours;
    /// connections at the old ends move to the new outer ends; conductors not terminated are spliced through in the site;
    /// circuit hops are rewritten as in <see cref="CableSplit.RewriteHops"/>; the old cable is removed. False when the
    /// cable is gone, so the plan no longer fits.
    /// </summary>
    private async Task<bool> SplitCableAsync(PlanOp op, JsonElement p, CancellationToken ct)
    {
        var old = p.GetProperty("cable").GetInt64();
        var site = p.GetProperty("site").GetInt64();
        var conductors = CableSplit.Conductors(p);
        var terminated = CableSplit.Terminated(p);
        var n = conductors.Count;
        if (await ScalarAsync<long?>("SELECT id FROM cable WHERE id = $1 AND lifecycle <> 'removed' FOR UPDATE", ct, old) is null)
        {
            return false;
        }

        async Task<long> PartAsync(string suffix, bool first) => await ScalarAsync<long>($"""
            WITH c AS (SELECT c.*, s.geom AS sg FROM cable c, site s WHERE c.id = $1 AND s.id = $2),
                 f AS (SELECT c.*, ST_PointOnSurface(c.sg) AS p, ST_LineLocatePoint(c.geom, ST_PointOnSurface(c.sg)) AS f FROM c)
            INSERT INTO cable (cable_type_id, code, a_site_id, b_site_id, geom, lifecycle, attributes)
            SELECT cable_type_id, code || '{suffix}', {(first ? "a_site_id, $2" : "$2, b_site_id")},
                   {(first ? "ST_MakeLine(ST_LineSubstring(geom, 0, f), p)" : "ST_MakeLine(p, ST_LineSubstring(geom, f, 1))")},
                   lifecycle, attributes
            FROM f RETURNING id
            """, ct, old, site);
        var a = await PartAsync("-A", first: true);
        var b = await PartAsync("-B", first: false);
        Ids[Planned.ObjectId(op.Id)] = a;
        Ids[CableSplit.SecondCable(op.Id)] = b;

        var (conductorsA, endsA) = await ConductorsAsync(a, old, ct);
        var (conductorsB, endsB) = await ConductorsAsync(b, old, ct);
        var outer = new Dictionary<long, long>();
        var inner = new Dictionary<(long, long), long[]>();
        for (var i = 0; i < n; i++)
        {
            var (c, k) = (conductors[i], i + 1);
            Ids[Planned.Conductor(op.Id, k)] = conductorsA[i];
            Ids[Planned.Conductor(op.Id, n + k)] = conductorsB[i];
            (long outerA, long innerA, long innerB, long outerB) = (endsA[2 * i], endsA[(2 * i) + 1], endsB[2 * i], endsB[(2 * i) + 1]);
            Ids[CableSplit.OuterA(op.Id, k)] = outerA;
            Ids[CableSplit.InnerA(op.Id, k)] = innerA;
            Ids[CableSplit.InnerB(op.Id, n, k)] = innerB;
            Ids[CableSplit.OuterB(op.Id, n, k)] = outerB;
            outer[c.A] = outerA;
            outer[c.B] = outerB;
            inner[(c.A, c.B)] = [innerA, innerB];
            inner[(c.B, c.A)] = [innerB, innerA];
            foreach (var (from, to) in new[] { (c.A, outerA), (c.B, outerB) })
            {
                await ExecuteAsync("""
                    WITH moved AS (
                        UPDATE connection SET valid_to = now(), lifecycle = 'removed'
                        WHERE (a_terminal_id = $1 OR b_terminal_id = $1) AND valid_to IS NULL
                        RETURNING CASE WHEN a_terminal_id = $1 THEN b_terminal_id ELSE a_terminal_id END AS other, kind, lifecycle)
                    INSERT INTO connection (a_terminal_id, b_terminal_id, kind, lifecycle)
                    SELECT least($2, other), greatest($2, other), kind, 'in_service' FROM moved
                    """, ct, from, to);
            }
            if (!terminated.Contains(c.Number))
            {
                await ExecuteAsync("""
                    INSERT INTO connection (a_terminal_id, b_terminal_id, kind, lifecycle)
                    VALUES (least($1, $2), greatest($1, $2), 'splice', 'in_service')
                    """, ct, innerA, innerB);
            }
        }

        // Circuits along the cable: the new ends, with the splice between them.
        var hops = new Dictionary<long, List<(long Terminal, long? Channel)>>();
        await using (var cmd = Command("""
            SELECT h.circuit_id, h.terminal_id, h.channel_id FROM circuit_hop h
            WHERE h.circuit_id IN (SELECT circuit_id FROM circuit_hop WHERE terminal_id = ANY($1))
            ORDER BY h.circuit_id, h.seq
            """, [outer.Keys.ToArray()]))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (!hops.TryGetValue(reader.GetInt64(0), out var list))
                {
                    hops[reader.GetInt64(0)] = list = [];
                }
                list.Add((reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
            }
        }
        foreach (var (circuit, list) in hops)
        {
            var rewritten = new List<(long Terminal, long? Channel)>();
            for (var i = 0; i < list.Count; i++)
            {
                rewritten.Add((outer.GetValueOrDefault(list[i].Terminal, list[i].Terminal), list[i].Channel));
                if (i + 1 < list.Count && inner.TryGetValue((list[i].Terminal, list[i + 1].Terminal), out var between))
                {
                    rewritten.AddRange(between.Select(t => (t, (long?)null)));
                }
            }
            await ExecuteAsync("DELETE FROM circuit_hop WHERE circuit_id = $1", ct, circuit);
            await ExecuteAsync("""
                INSERT INTO circuit_hop (circuit_id, seq, terminal_id, channel_id)
                SELECT $1, s - 1, t, c FROM unnest($2::bigint[], $3::bigint[]) WITH ORDINALITY AS u(t, c, s)
                """, ct, circuit, rewritten.Select(r => r.Terminal).ToArray(), rewritten.Select(r => r.Channel).ToArray());
        }

        await ExecuteAsync("UPDATE cable SET lifecycle = 'removed' WHERE id = $1", ct, old);
        return true;
    }

    /// <summary>
    /// A removal (#172): every connection on the terminals goes, and the object (for a site, with its equipment and the
    /// cables ending there) gets the lifecycle removed. False when it was removed already.
    /// </summary>
    private async Task<bool> RemoveAsync(JsonElement p, CancellationToken ct)
    {
        var (equipment, cables) = ObjectRemoval.Objects(p);
        var type = p.GetProperty("type").GetString()!;
        var id = p.GetProperty("id").GetInt64();
        if (await ExecuteAsync($"UPDATE {Table(p)} SET lifecycle = 'removed' WHERE id = $1 AND lifecycle <> 'removed'", ct, id) == 0)
        {
            return false;
        }
        await ExecuteAsync("""
            WITH t AS (SELECT terminal_id FROM port WHERE equipment_id = ANY($1)
                       UNION ALL SELECT e.terminal_id FROM conductor_end e JOIN conductor c ON c.id = e.conductor_id WHERE c.cable_id = ANY($2))
            UPDATE connection SET valid_to = now(), lifecycle = 'removed'
            WHERE valid_to IS NULL AND (a_terminal_id IN (SELECT terminal_id FROM t) OR b_terminal_id IN (SELECT terminal_id FROM t))
            """, ct, equipment, cables);
        if (type == "site")
        {
            await ExecuteAsync("UPDATE equipment SET lifecycle = 'removed' WHERE id = ANY($1)", ct, equipment);
            await ExecuteAsync("UPDATE cable SET lifecycle = 'removed' WHERE id = ANY($1)", ct, cables);
        }
        return true;
    }

    /// <summary>
    /// A move (#187): equipment to another rack or site, or a cable's end to another site. What changes site (equipment) or
    /// moves (a cable end) loses the connections on its terminals; a cable's geometry follows the end to the site's point.
    /// False when the object is removed already.
    /// </summary>
    private async Task<bool> MoveAsync(JsonElement p, CancellationToken ct)
    {
        var type = p.GetProperty("type").GetString()!;
        var id = p.GetProperty("id").GetInt64();
        var site = p.GetProperty("site").GetInt64();
        if (type == "equipment")
        {
            if (await ScalarAsync<long?>("SELECT site_id FROM equipment WHERE id = $1 AND lifecycle <> 'removed'", ct, id) is not { } current)
            {
                return false;
            }
            var rack = p.TryGetProperty("rack", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            var room = p.TryGetProperty("room", out var rm) && rm.ValueKind == JsonValueKind.String ? rm.GetString() : null;
            var location = current == site && rack is null
                ? await ScalarAsync<long>("SELECT location_id FROM equipment WHERE id = $1", ct, id)
                : await RackAsync(site, rack, room, ct);
            if (current != site)
            {
                await ExecuteAsync("""
                    UPDATE connection SET valid_to = now(), lifecycle = 'removed'
                    WHERE valid_to IS NULL AND (a_terminal_id IN (SELECT terminal_id FROM port WHERE equipment_id = $1)
                                             OR b_terminal_id IN (SELECT terminal_id FROM port WHERE equipment_id = $1))
                    """, ct, id);
            }
            var position = p.TryGetProperty("position", out var at) && at.ValueKind == JsonValueKind.Number ? (object)at.GetInt16() : DBNull.Value;
            await ExecuteAsync("""
                UPDATE equipment e SET site_id = $2, location_id = $3,
                       rack_position = CASE WHEN t.rack_units IS NULL THEN NULL
                            ELSE coalesce($4::smallint, (SELECT coalesce(max(o.rack_position + coalesce(u.rack_units, 1)), 1)::smallint
                                                         FROM equipment o JOIN equipment_type u ON u.id = o.equipment_type_id
                                                         WHERE o.location_id = $3 AND o.id <> e.id AND o.rack_position IS NOT NULL
                                                           AND o.lifecycle <> 'removed')) END
                FROM equipment_type t WHERE e.id = $1 AND t.id = e.equipment_type_id
                """, ct, id, site, location, position);
            return true;
        }

        var end = p.GetProperty("end").GetString()!;
        if (await ScalarAsync<long?>("SELECT id FROM cable WHERE id = $1 AND lifecycle <> 'removed'", ct, id) is null)
        {
            return false;
        }
        await ExecuteAsync("""
            WITH t AS (SELECT e.terminal_id FROM conductor_end e JOIN conductor c ON c.id = e.conductor_id WHERE c.cable_id = $1 AND e.side = $2)
            UPDATE connection SET valid_to = now(), lifecycle = 'removed'
            WHERE valid_to IS NULL AND (a_terminal_id IN (SELECT terminal_id FROM t) OR b_terminal_id IN (SELECT terminal_id FROM t))
            """, ct, id, end);
        await ExecuteAsync($"""
            UPDATE cable SET {(end == "A" ? "a_site_id" : "b_site_id")} = $2,
                   geom = ST_SetPoint(geom, {(end == "A" ? "0" : "ST_NPoints(geom) - 1")}, (SELECT ST_PointOnSurface(geom) FROM site WHERE id = $2))
            WHERE id = $1
            """, ct, id, site);
        return true;
    }

    /// <summary>The old cable's conductors (number and colour) on a new cable, with A and B ends: conductor ids, and ends in pairs.</summary>
    private async Task<(long[] Conductors, long[] Ends)> ConductorsAsync(long cable, long old, CancellationToken ct)
    {
        var conductors = new List<long>();
        await using (var cmd = Command("""
            INSERT INTO conductor (cable_id, number, color) SELECT $1, number, color FROM conductor WHERE cable_id = $2 ORDER BY number RETURNING id
            """, [cable, old]))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                conductors.Add(reader.GetInt64(0));
            }
        }
        // Ids follow the numbers: the insert assigns them in order.
        conductors.Sort();
        var ends = await NewTerminalsAsync("conductor_end", 2 * conductors.Count, ct);
        await ExecuteAsync("""
            INSERT INTO conductor_end (terminal_id, conductor_id, side)
            SELECT t, c, s FROM unnest($1::bigint[], $2::bigint[], $3::text[]) AS u(t, c, s)
            """, ct, ends, conductors.SelectMany(c => new[] { c, c }).ToArray(),
            Enumerable.Range(0, ends.Length).Select(i => i % 2 == 0 ? "A" : "B").ToArray());
        return ([.. conductors], ends);
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

    /// <summary>The attributes a create operation gives the new object (#211), or none.</summary>
    private static string Attributes(JsonElement p) =>
        p.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object ? a.GetRawText() : "{}";

    private static string Table(JsonElement p) => p.GetProperty("type").GetString() switch
    {
        "site" => "site",
        "equipment" => "equipment",
        "cable" => "cable",
        "service" => "service",
        "location" => "location",
        "circuit" => "circuit",
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
