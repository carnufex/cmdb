using System.Text.Json;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Auth;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Equipment;

public sealed record EquipmentRequest(long Id);

public sealed record EquipmentPort(long TerminalId, string Name, string Type, string? Group, int Position, int Row, int Column,
    IReadOnlyList<PortConnection> Connections, int Circuits);

public sealed record PortConnection(string Kind, string Lifecycle, TerminalRef Peer);

public sealed record EquipmentCard(string Slot, ObjectRef Card);

public sealed record EquipmentDetail(
    long Id,
    string Name,
    string Lifecycle,
    string TypeKey,
    string Manufacturer,
    string Model,
    string Category,
    JsonElement Panel,
    JsonElement Attributes,
    ObjectRef Site,
    string? LocationPath,
    ObjectRef? Parent,
    string? Slot,
    IReadOnlyList<string> FreeSlots,
    IReadOnlyList<EquipmentCard> Cards,
    IReadOnlyList<EquipmentPort> Ports);

/// <summary>Equipment with its ports and what each port is connected to; the front panel is drawn from this.</summary>
public sealed class GetEquipmentEndpoint(NpgsqlDataSource db) : Endpoint<EquipmentRequest, EquipmentDetail>
{
    public override void Configure() => Get("/equipment/{id}");

    public override async Task HandleAsync(EquipmentRequest req, CancellationToken ct)
    {
        var detail = await LoadAsync(db, req.Id, HttpContext.Scope(), ct);
        if (detail is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(detail, ct);
    }

    /// <summary>Also used by the MCP tools (#61), so agents see exactly what the UI shows.</summary>
    internal static async Task<EquipmentDetail?> LoadAsync(NpgsqlDataSource db, long id, UserScope scope, CancellationToken ct)
    {
        // Equipment is visible with its site (#22); what its ports connect to outside the scope becomes a placeholder.
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(conn)
        {
            BatchCommands =
            {
                new($"""
                    WITH RECURSIVE path AS (
                        SELECT l.id, l.parent_id, l.name::text AS path FROM location l
                        WHERE l.id = (SELECT location_id FROM equipment WHERE id = $1)
                        UNION ALL
                        SELECT l.id, l.parent_id, l.name || ' / ' || path.path FROM location l JOIN path ON l.id = path.parent_id
                    )
                    SELECT e.id, e.name, e.lifecycle::text, et.key, et.manufacturer, et.model, et.category, et.panel::text,
                           e.attributes::text, s.id, s.code, s.name, s.lifecycle::text,
                           (SELECT path FROM path WHERE parent_id IS NULL),
                           pe.id, pe.name, pe.lifecycle::text, e.slot, et.slot_template::text
                    FROM equipment e
                    JOIN equipment_type et ON et.id = e.equipment_type_id
                    JOIN site s ON s.id = e.site_id
                    LEFT JOIN equipment pe ON pe.id = e.parent_id
                    WHERE e.id = $1 AND {ScopeSql.Site("s.id", 2)}
                    """) { Parameters = { new() { Value = id }, new() { Value = scope.Keys } } },
                new("""
                    SELECT c.slot, c.id, c.name, c.lifecycle::text FROM equipment c WHERE c.parent_id = $1 ORDER BY c.slot
                    """) { Parameters = { new() { Value = id } } },
                new("""
                    SELECT p.terminal_id, p.name, p.port_type, p.port_group, p.position,
                           (SELECT count(*) FROM circuit_hop h WHERE h.terminal_id = p.terminal_id)::int
                    FROM port p WHERE p.equipment_id = $1 ORDER BY p.position
                    """) { Parameters = { new() { Value = id } } },
                new("""
                    SELECT p.terminal_id, CASE WHEN c.a_terminal_id = p.terminal_id THEN c.b_terminal_id ELSE c.a_terminal_id END,
                           c.kind::text, c.lifecycle::text
                    FROM port p JOIN connection c ON (c.a_terminal_id = p.terminal_id OR c.b_terminal_id = p.terminal_id) AND c.valid_to IS NULL
                    WHERE p.equipment_id = $1
                    """) { Parameters = { new() { Value = id } } },
            },
        };

        EquipmentDetail? detail = null;
        var ports = new List<(long Terminal, string Name, string Type, string? Group, int Position, int Circuits)>();
        var connections = new List<(long Port, long Peer, string Kind, string Lifecycle)>();
        var cards = new List<EquipmentCard>();
        string? slotTemplate = null;
        await using (var reader = await batch.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }
            detail = new EquipmentDetail(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), Terminals.Json(reader.GetString(7)), Terminals.Json(scope.MaskAttributes(reader.GetString(8))),
                new ObjectRef("site", reader.GetInt64(9), reader.GetString(10), reader.GetString(11), reader.GetString(12)),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : new ObjectRef("equipment", reader.GetInt64(14), reader.GetString(15), null, reader.GetString(16)),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                [], [], []);
            slotTemplate = reader.GetString(18);

            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                cards.Add(new EquipmentCard(reader.GetString(0), new ObjectRef("equipment", reader.GetInt64(1), reader.GetString(2), null, reader.GetString(3))));
            }
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ports.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt32(4), reader.GetInt32(5)));
            }
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                connections.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        var peers = await Terminals.DescribeAsync(conn, [.. connections.Select(c => c.Peer).Distinct()], scope, ct);
        var cells = PanelCells(detail.TypeKey, detail.Slot);
        var byPort = connections.ToLookup(c => c.Port);
        var usedSlots = cards.Select(c => c.Slot).ToHashSet();
        var freeSlots = JsonDocument.Parse(slotTemplate!).RootElement.EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()!).Where(s => !usedSlots.Contains(s)).ToList();

        return detail with
        {
            FreeSlots = freeSlots,
            Cards = cards,
            Ports = [.. ports.Select(p =>
            {
                var (row, column) = cells.GetValueOrDefault(p.Position);
                return new EquipmentPort(p.Terminal, p.Name, p.Type, p.Group, p.Position, row, column,
                    [.. byPort[p.Terminal].Where(c => peers.ContainsKey(c.Peer)).Select(c => new PortConnection(c.Kind, c.Lifecycle, peers[c.Peer]))],
                    p.Circuits);
            })],
        };
    }

    /// <summary>Front-panel cell per port position, from the type's template.</summary>
    private static Dictionary<int, (int Row, int Column)> PanelCells(string typeKey, string? slot)
    {
        var type = Cmdb.Catalog.TypeCatalog.Embedded.Find(typeKey);
        return type is null
            ? []
            : Cmdb.Catalog.PortExpansion.Expand(type, slot).ToDictionary(p => p.Position, p => (p.Row, p.Column));
    }
}
