using System.Text.Json;
using Cmdb.Catalog;
using FastEndpoints;
using FluentValidation;
using Npgsql;
using NpgsqlTypes;

namespace Cmdb.Api.Features.Equipment;

public sealed record CreateEquipmentRequest(
    string TypeKey,
    string Name,
    long? LocationId,
    long? ParentId,
    string? Slot,
    JsonElement? Attributes);

public sealed record CreatedPort(long TerminalId, string Name, int Position);

public sealed record CreateEquipmentResponse(long Id, long SiteId, IReadOnlyList<CreatedPort> Ports);

public sealed class CreateEquipmentValidator : Validator<CreateEquipmentRequest>
{
    public CreateEquipmentValidator()
    {
        RuleFor(r => r.TypeKey).NotEmpty();
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        RuleFor(r => r)
            .Must(r => (r.LocationId is not null) != (r.ParentId is not null))
            .WithName("placement")
            .WithMessage("Give either locationId or parentId and slot.");
        RuleFor(r => r.Slot).NotEmpty().When(r => r.ParentId is not null);
        RuleFor(r => r.Slot).Empty().When(r => r.ParentId is null);
    }
}

/// <summary>
/// Creates equipment in a location or in a slot of other equipment, and generates its ports from the
/// type's port template in the same transaction.
/// </summary>
public sealed class CreateEquipmentEndpoint(NpgsqlDataSource db, TypeCatalog catalog)
    : Endpoint<CreateEquipmentRequest, CreateEquipmentResponse>
{
    public override void Configure()
    {
        Post("/equipment");
        // Interim until access scopes (#22): only fully authorised users may write.
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(CreateEquipmentRequest req, CancellationToken ct)
    {
        var type = catalog.Find(req.TypeKey);
        if (type is null)
        {
            ThrowError(r => r.TypeKey, $"Unknown equipment type '{req.TypeKey}'.");
        }

        var attributes = req.Attributes is { ValueKind: not JsonValueKind.Null } a ? a : JsonDocument.Parse("{}").RootElement;
        foreach (var error in catalog.ValidateAttributes(type.Key, attributes))
        {
            AddError(r => r.Attributes, error);
        }
        ThrowIfAnyErrors();

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var siteId = req.LocationId is { } locationId
            ? await SiteOfLocationAsync(conn, tx, locationId, ct)
            : await SiteOfSlotAsync(conn, tx, req.ParentId!.Value, req.Slot!, type, ct);

        long id;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO equipment (equipment_type_id, site_id, location_id, parent_id, slot, name, attributes)
            SELECT et.id, $2, $3, $4, $5, $6, $7 FROM equipment_type et WHERE et.key = $1
            RETURNING id
            """, conn, tx))
        {
            insert.Parameters.Add(new() { Value = type.Key });
            insert.Parameters.Add(new() { Value = siteId });
            insert.Parameters.Add(new() { Value = (object?)req.LocationId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            insert.Parameters.Add(new() { Value = (object?)req.ParentId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            insert.Parameters.Add(new() { Value = (object?)req.Slot ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            insert.Parameters.Add(new() { Value = req.Name });
            insert.Parameters.Add(new() { Value = attributes.GetRawText(), NpgsqlDbType = NpgsqlDbType.Jsonb });
            try
            {
                id = (long)(await insert.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException($"Type '{type.Key}' is not in the database; run the catalog sync."));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await Send.ResultAsync(TypedResults.Conflict(new { message = $"Slot '{req.Slot}' is already occupied." }));
                return;
            }
        }

        var ports = PortExpansion.Expand(type, req.Slot);
        var created = await InsertPortsAsync(conn, tx, id, ports, ct);
        await tx.CommitAsync(ct);

        await Send.CreatedAtAsync($"/api/equipment/{id}", null, new CreateEquipmentResponse(id, siteId, created), generateAbsoluteUrl: false, cancellation: ct);
    }

    private async Task<long> SiteOfLocationAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long locationId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT site_id FROM location WHERE id = $1", conn, tx);
        cmd.Parameters.Add(new() { Value = locationId });
        if (await cmd.ExecuteScalarAsync(ct) is long siteId)
        {
            return siteId;
        }
        ThrowError(r => r.LocationId, $"Location {locationId} does not exist.");
        return 0;
    }

    private async Task<long> SiteOfSlotAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long parentId, string slot, EquipmentType type, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT e.site_id, et.key FROM equipment e JOIN equipment_type et ON et.id = e.equipment_type_id WHERE e.id = $1
            """, conn, tx);
        cmd.Parameters.Add(new() { Value = parentId });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            ThrowError(r => r.ParentId, $"Equipment {parentId} does not exist.");
        }
        var siteId = reader.GetInt64(0);
        var parentType = catalog.Find(reader.GetString(1));

        var slotTemplate = parentType?.SlotList.FirstOrDefault(s => s.Name == slot);
        if (slotTemplate is null)
        {
            ThrowError(r => r.Slot, $"Equipment {parentId} has no slot '{slot}'.");
        }
        if (!slotTemplate.Accepts.Contains(type.Category))
        {
            ThrowError(r => r.Slot, $"Slot '{slot}' accepts {string.Join(", ", slotTemplate.Accepts)}, not {type.Category}.");
        }
        return siteId;
    }

    private static async Task<IReadOnlyList<CreatedPort>> InsertPortsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long equipmentId, IReadOnlyList<Port> ports, CancellationToken ct)
    {
        if (ports.Count == 0)
        {
            return [];
        }

        // Terminal ids are drawn from the sequence up front so each port is paired with its own terminal
        // deterministically, in one round trip.
        await using var cmd = new NpgsqlCommand("""
            WITH p AS (
                SELECT nextval(pg_get_serial_sequence('terminal', 'id')) AS id, u.*
                FROM unnest($2::text[], $3::text[], $4::text[], $5::int[]) WITH ORDINALITY AS u(name, type, grp, position, ord)
            ), t AS (
                INSERT INTO terminal (id, kind) OVERRIDING SYSTEM VALUE SELECT id, 'port' FROM p
            )
            INSERT INTO port (terminal_id, equipment_id, name, port_type, port_group, position)
            SELECT id, $1, name, type, grp, position FROM p
            RETURNING terminal_id, name, position
            """, conn, tx);
        cmd.Parameters.Add(new() { Value = equipmentId });
        cmd.Parameters.Add(new() { Value = ports.Select(p => p.Name).ToArray() });
        cmd.Parameters.Add(new() { Value = ports.Select(p => p.Type).ToArray() });
        cmd.Parameters.Add(new() { Value = ports.Select(p => p.Group).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = ports.Select(p => p.Position).ToArray() });

        var created = new List<CreatedPort>(ports.Count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            created.Add(new CreatedPort(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
        }
        return [.. created.OrderBy(p => p.Position)];
    }
}
