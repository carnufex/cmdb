using System.Text.RegularExpressions;

namespace Cmdb.Catalog;

/// <summary>Consistency rules for catalog entries, so a broken file fails at load rather than at use.</summary>
internal static partial class CatalogRules
{
    /// <summary>A file in <c>equipment-images/</c>: a plain name, SVG or PNG, so it cannot point anywhere else.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*\\.(svg|png)$")]
    public static partial Regex ImageFile();

    /// <param name="imageExists">Whether an image file is in the catalog; null skips that check.</param>
    public static void Check(string file, EquipmentType type, IReadOnlyDictionary<string, EquipmentCategory> categories, List<string> errors,
        Func<string, bool>? imageExists = null)
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
        if (!categories.ContainsKey(type.Category))
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
            if (template.Image is { } image)
            {
                if (image.Side is not (PortImage.FrontSide or PortImage.BackSide))
                {
                    Error($"port '{template.Name}': image side must be 'front' or 'back'");
                    shapeValid = false;
                }
                if (image.At is not { Count: 2 } || image.At[0] < 0 || image.At[1] < 0)
                {
                    Error($"port '{template.Name}': image 'at' must be [x, y]");
                    shapeValid = false;
                }
                if (image.Size is not { Count: 2 } || image.Size[0] < 1 || image.Size[1] < 1)
                {
                    Error($"port '{template.Name}': image 'size' must be [width, height]");
                    shapeValid = false;
                }
                if (image.Step is not null && image.Step.Count != 2)
                {
                    Error($"port '{template.Name}': image 'step' must be [dx, dy]");
                    shapeValid = false;
                }
                if (image.Step is null && template.Range is [var first, var last] && last > first)
                {
                    Error($"port '{template.Name}': a range on the image needs 'step'");
                    shapeValid = false;
                }
            }
        }
        CheckImages(type, imageExists, Error);
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
        CheckBoxes(type, ports, Error);

        foreach (var duplicate in type.SlotList.GroupBy(s => s.Name).Where(g => g.Count() > 1))
        {
            Error($"slot '{duplicate.Key}' is defined more than once");
        }
        foreach (var slot in type.SlotList)
        {
            foreach (var category in slot.Accepts.Where(c => !categories.ContainsKey(c)))
            {
                Error($"slot '{slot.Name}' accepts unknown category '{category}'");
            }
        }
        if (categories.GetValueOrDefault(type.Category)?.Has(CatalogRoles.Card) != true && type.Ports.Any(p => p.Name.Contains("{slot}", StringComparison.Ordinal)))
        {
            Error("only cards (a category with role card) may use '{slot}' in port names");
        }
    }

    // The images themselves: named files that exist, with a size to place ports in.
    private static void CheckImages(EquipmentType type, Func<string, bool>? imageExists, Action<string> error)
    {
        if (type.Panel.Images is not { } images)
        {
            return;
        }
        if (images.Front is null && images.Back is null)
        {
            error("panel images needs 'front' or 'back'");
        }
        foreach (var (side, image) in new[] { (PortImage.FrontSide, images.Front), (PortImage.BackSide, images.Back) })
        {
            if (image is null)
            {
                continue;
            }
            if (image.File is null || !ImageFile().IsMatch(image.File))
            {
                error($"{side} image must be an .svg or .png file name in lower case, without folders");
            }
            else if (imageExists?.Invoke(image.File) == false)
            {
                error($"{side} image '{image.File}' is missing from equipment-images/");
            }
            if (image.Width < 1 || image.Height < 1)
            {
                error($"{side} image needs a width and height");
            }
        }
    }

    // With images every port is placed on one, inside it and clear of the others, so each is something to click.
    private static void CheckBoxes(EquipmentType type, IReadOnlyList<Port> ports, Action<string> error)
    {
        var images = type.Panel.Images;
        if (images is null)
        {
            foreach (var placed in ports.Where(p => p.Box is not null).Take(1))
            {
                error($"port '{placed.Name}' has an image position but the panel has no images");
            }
            return;
        }
        var boxed = new List<Port>();
        foreach (var port in ports)
        {
            if (port.Box is not { } box)
            {
                error($"port '{port.Name}' has no position on the panel images");
                continue;
            }
            if (images.Side(box.Side) is not { } image)
            {
                error($"port '{port.Name}' is on the {box.Side}, which has no image");
                continue;
            }
            if (box.X + box.Width > image.Width || box.Y + box.Height > image.Height)
            {
                error($"port '{port.Name}' at ({box.X}, {box.Y}) size {box.Width}x{box.Height} is outside the {image.Width}x{image.Height} {box.Side} image");
                continue;
            }
            boxed.Add(port);
        }
        for (var i = 0; i < boxed.Count; i++)
        {
            for (var j = i + 1; j < boxed.Count; j++)
            {
                if (boxed[i].Box!.Overlaps(boxed[j].Box!))
                {
                    error($"ports {boxed[i].Name} and {boxed[j].Name} overlap on the {boxed[i].Box!.Side} image");
                }
            }
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
