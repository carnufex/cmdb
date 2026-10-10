using System.Text.Json;
using System.Text.Json.Nodes;
using Cmdb.Database;
using Microsoft.EntityFrameworkCore;
using DbCableMedium = Cmdb.Database.Model.CableMedium;
using DbCableType = Cmdb.Database.Model.CableType;
using DbDuctType = Cmdb.Database.Model.DuctType;
using DbEquipmentType = Cmdb.Database.Model.EquipmentType;

namespace Cmdb.Catalog;

/// <summary>Writes the catalog into <c>equipment_type</c>, <c>cable_type</c> and <c>duct_type</c>. Runs in the same step as the migrations.</summary>
public static class CatalogSync
{
    /// <summary>Inserts new types and updates changed ones. Returns the number of rows written.</summary>
    /// <remarks>
    /// Types missing from the catalog are left in place since equipment may reference them. Changing a port
    /// template does not regenerate ports on existing equipment; that is a data migration of its own.
    /// </remarks>
    public static async Task<int> SyncAsync(CmdbDbContext db, TypeCatalog catalog, CancellationToken ct = default)
    {
        var equipmentTypes = await db.EquipmentTypes.ToDictionaryAsync(t => t.Key, StringComparer.Ordinal, ct);
        foreach (var type in catalog.Types.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (!equipmentTypes.TryGetValue(type.Key, out var row))
            {
                row = new DbEquipmentType { Key = type.Key, Manufacturer = type.Manufacturer, Model = type.Model, Category = type.Category };
                db.EquipmentTypes.Add(row);
            }
            row.Manufacturer = type.Manufacturer;
            row.Model = type.Model;
            row.Category = type.Category;
            row.RackUnits = (short?)type.RackUnits;
            row.Panel = Json(row.Panel, JsonSerializer.Serialize(type.Panel, TypeCatalog.Json));
            row.PortTemplate = Json(row.PortTemplate, JsonSerializer.Serialize(type.Ports, TypeCatalog.Json));
            row.SlotTemplate = Json(row.SlotTemplate, JsonSerializer.Serialize(type.SlotList, TypeCatalog.Json));
            row.AttributeSchema = Json(row.AttributeSchema, type.Attributes.GetRawText());
        }

        var cableTypes = await db.CableTypes.ToDictionaryAsync(t => t.Key, StringComparer.Ordinal, ct);
        foreach (var type in catalog.CableTypes.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (!cableTypes.TryGetValue(type.Key, out var row))
            {
                row = new DbCableType { Key = type.Key, Name = type.Name };
                db.CableTypes.Add(row);
            }
            row.Name = type.Name;
            row.Medium = Enum.Parse<DbCableMedium>(type.Medium, ignoreCase: true);
            row.ConductorCount = type.ConductorCount;
            row.ColorCode = type.ColorCode;
        }

        var ductTypes = await db.DuctTypes.ToDictionaryAsync(t => t.Key, StringComparer.Ordinal, ct);
        foreach (var type in catalog.DuctTypes.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (!ductTypes.TryGetValue(type.Key, out var row))
            {
                row = new DbDuctType { Key = type.Key, Name = type.Name, Manufacturer = type.Manufacturer, Model = type.Model };
                db.DuctTypes.Add(row);
            }
            row.Name = type.Name;
            row.Manufacturer = type.Manufacturer;
            row.Model = type.Model;
            row.OuterDiameterMm = type.OuterDiameterMm;
            row.SubductCount = type.Subducts.Count;
            row.SubductInnerDiameterMm = type.Subducts.InnerDiameterMm;
            row.ColorCode = type.Subducts.ColorCode;
        }

        return await db.SaveChangesAsync(ct);
    }

    // jsonb normalises spacing and key order, so compare meaning and keep the stored text when nothing changed.
    private static string Json(string current, string wanted) =>
        JsonNode.DeepEquals(JsonNode.Parse(current), JsonNode.Parse(wanted)) ? current : wanted;
}
