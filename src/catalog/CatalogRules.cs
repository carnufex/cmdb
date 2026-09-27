namespace Cmdb.Catalog;

/// <summary>Consistency rules for catalog entries, so a broken file fails at load rather than at use.</summary>
internal static class CatalogRules
{
    public static void Check(string file, EquipmentType type, List<string> errors)
    {
        void Error(string message) => errors.Add($"{file}: {message}");

        // System.Text.Json leaves missing members null even for non-nullable record parameters.
        if (type.Key is null || type.Manufacturer is null || type.Model is null || type.Category is null || type.Panel is null || type.Ports is null)
        {
            Error("key, manufacturer, model, category, panel and ports are required");
            return;
        }

        if (file != $"{type.Key}.json")
        {
            Error($"file name must be '{type.Key}.json'");
        }
        if (!TypeCatalog.Categories.Contains(type.Category))
        {
            Error($"unknown category '{type.Category}'");
        }
        if (type.Panel.Rows < 1 || type.Panel.Columns < 1)
        {
            Error("panel must have at least one row and column");
        }
        if (type.Attributes.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            Error("attributes must be a JSON Schema object");
        }

        // Expansion needs well-formed templates; report shape errors and stop before expanding.
        var shapeValid = true;
        foreach (var template in type.Ports)
        {
            if (template.At is not { Count: 2 } || template.At[0] < 0 || template.At[1] < 0)
            {
                Error($"port '{template.Name}': 'at' must be [row, column]");
                shapeValid = false;
            }
            if (template.Range is not null && (template.Range.Count != 2 || template.Range[0] > template.Range[1] || template.Range[0] < 0))
            {
                Error($"port '{template.Name}': 'range' must be [first, last]");
                shapeValid = false;
            }
            if (template.Range is null && template.Name.Contains("{n}", StringComparison.Ordinal))
            {
                Error($"port '{template.Name}': '{{n}}' needs a range");
            }
            if (template.Range is not null && !template.Name.Contains("{n}", StringComparison.Ordinal))
            {
                Error($"port '{template.Name}': a range needs '{{n}}' in the name");
            }
        }
        if (!shapeValid)
        {
            return;
        }

        var ports = PortExpansion.Expand(type);
        foreach (var duplicate in ports.GroupBy(p => p.Name).Where(g => g.Count() > 1))
        {
            Error($"port name '{duplicate.Key}' is used more than once");
        }
        foreach (var outside in ports.Where(p => p.Row >= type.Panel.Rows || p.Column >= type.Panel.Columns))
        {
            Error($"port '{outside.Name}' at [{outside.Row}, {outside.Column}] is outside the {type.Panel.Rows}x{type.Panel.Columns} panel");
        }
        foreach (var overlap in ports.GroupBy(p => (p.Row, p.Column)).Where(g => g.Count() > 1))
        {
            Error($"ports {string.Join(", ", overlap.Select(p => p.Name))} share cell [{overlap.Key.Row}, {overlap.Key.Column}]");
        }

        foreach (var duplicate in type.SlotList.GroupBy(s => s.Name).Where(g => g.Count() > 1))
        {
            Error($"slot '{duplicate.Key}' is defined more than once");
        }
        foreach (var slot in type.SlotList)
        {
            foreach (var category in slot.Accepts.Where(c => !TypeCatalog.Categories.Contains(c)))
            {
                Error($"slot '{slot.Name}' accepts unknown category '{category}'");
            }
        }
        if (type.Category != "card" && type.Ports.Any(p => p.Name.Contains("{slot}", StringComparison.Ordinal)))
        {
            Error("only cards may use '{slot}' in port names");
        }
    }

    public static void CheckAcrossTypes(List<EquipmentType> types, List<string> errors)
    {
        foreach (var duplicate in types.GroupBy(t => t.Key).Where(g => g.Count() > 1))
        {
            errors.Add($"key '{duplicate.Key}' is used by more than one file");
        }
        foreach (var duplicate in types.GroupBy(t => (t.Manufacturer, t.Model)).Where(g => g.Count() > 1))
        {
            errors.Add($"{duplicate.Key.Manufacturer} {duplicate.Key.Model} is defined more than once");
        }
    }

    public static void CheckCableTypes(List<CableType> cableTypes, List<string> errors)
    {
        foreach (var type in cableTypes)
        {
            if (type.Key is null || type.Name is null || type.Medium is null)
            {
                errors.Add("cable-types.json: key, name and medium are required");
                continue;
            }
            if (!TypeCatalog.CableMedia.Contains(type.Medium))
            {
                errors.Add($"cable-types.json: '{type.Key}' has unknown medium '{type.Medium}'");
            }
            if (type.ConductorCount < 1)
            {
                errors.Add($"cable-types.json: '{type.Key}' must have at least one conductor");
            }
        }
        foreach (var duplicate in cableTypes.GroupBy(t => t.Key).Where(g => g.Count() > 1))
        {
            errors.Add($"cable-types.json: key '{duplicate.Key}' is used more than once");
        }
    }
}
