using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Cmdb.Catalog;

/// <summary>Writes the catalog into <c>equipment_type</c>. Runs in the same step as the migrations.</summary>
public static class CatalogSync
{
    /// <summary>Inserts new types and updates changed ones. Returns the number of rows written.</summary>
    /// <remarks>
    /// Types missing from the catalog are left in place since equipment may reference them. Changing a port
    /// template does not regenerate ports on existing equipment; that is a data migration of its own.
    /// </remarks>
    public static async Task<int> SyncAsync(NpgsqlDataSource db, TypeCatalog catalog, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var written = 0;
        foreach (var type in catalog.Types.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO equipment_type (key, manufacturer, model, category, rack_units, panel, port_template, slot_template, attribute_schema)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                ON CONFLICT (key) DO UPDATE SET
                    manufacturer = EXCLUDED.manufacturer,
                    model = EXCLUDED.model,
                    category = EXCLUDED.category,
                    rack_units = EXCLUDED.rack_units,
                    panel = EXCLUDED.panel,
                    port_template = EXCLUDED.port_template,
                    slot_template = EXCLUDED.slot_template,
                    attribute_schema = EXCLUDED.attribute_schema
                WHERE (equipment_type.manufacturer, equipment_type.model, equipment_type.category, equipment_type.rack_units,
                       equipment_type.panel, equipment_type.port_template, equipment_type.slot_template, equipment_type.attribute_schema)
                      IS DISTINCT FROM
                      (EXCLUDED.manufacturer, EXCLUDED.model, EXCLUDED.category, EXCLUDED.rack_units,
                       EXCLUDED.panel, EXCLUDED.port_template, EXCLUDED.slot_template, EXCLUDED.attribute_schema)
                """, conn, tx);
            cmd.Parameters.Add(new() { Value = type.Key });
            cmd.Parameters.Add(new() { Value = type.Manufacturer });
            cmd.Parameters.Add(new() { Value = type.Model });
            cmd.Parameters.Add(new() { Value = type.Category });
            cmd.Parameters.Add(new() { Value = type.RackUnits.HasValue ? (short)type.RackUnits.Value : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Smallint });
            cmd.Parameters.Add(Jsonb(type.Panel));
            cmd.Parameters.Add(Jsonb(type.Ports));
            cmd.Parameters.Add(Jsonb(type.SlotList));
            cmd.Parameters.Add(new() { Value = type.Attributes.GetRawText(), NpgsqlDbType = NpgsqlDbType.Jsonb });
            written += await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return written;
    }

    private static NpgsqlParameter Jsonb<T>(T value) =>
        new() { Value = JsonSerializer.Serialize(value, TypeCatalog.Json), NpgsqlDbType = NpgsqlDbType.Jsonb };
}
