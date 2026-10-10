using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cmdb.Catalog;
using Cmdb.DataGen.Exchange;

namespace Cmdb.DataGen.CatalogGeneration;

/// <param name="Generated">Keys written to <c>equipment-types/</c>.</param>
/// <param name="Skipped">Models that were not written, and why.</param>
/// <param name="Warnings">Models that were written with a guess, and what was guessed.</param>
/// <param name="AddedCategories">Categories added to <c>equipment-categories.json</c>, without roles.</param>
/// <param name="Validation">Errors from loading the whole folder afterwards; empty when it loads, null when it lacks its base files.</param>
internal sealed record GenerationReport(IReadOnlyList<string> Generated, IReadOnlyList<(string Model, string Reason)> Skipped,
    IReadOnlyList<(string Model, string Warning)> Warnings, IReadOnlyList<string> AddedCategories, string? Validation);

/// <summary>
/// Catalog entries made from data (#209): an organisation with hundreds of models has them only implicitly, as equipment
/// with a model name and ports with names. Reads an export in the exchange format's <c>equipment.csv</c> and
/// <c>ports.csv</c> (with a model name instead of a catalog key) and writes one equipment type per model: its ports
/// grouped into numbered templates, a front panel they fit on, slots where the export puts cards, and a free attribute
/// schema. Every type is validated like a hand-written one before it is written.
/// </summary>
internal static partial class CatalogGenerator
{
    /// <summary>The widest panel row; longer runs of ports wrap onto the next row.</summary>
    public const int MaxColumns = 48;

    private const string DefaultCategory = "ovrigt";
    private const string DefaultCardCategory = "kort";
    private const string UnknownPortType = "unknown";

    private sealed record Instance(string Id, string Manufacturer, string Model, string? Category, int? RackUnits, string? Parent, string? Slot);

    private sealed record ExportPort(string Name, string? Type, string? Group);

    private sealed record Template(string Name, string Type, string? Group, int? First, int? Last)
    {
        public int Width => First is null ? 1 : Last!.Value - First.Value + 1;
    }

    /// <summary>A trailing number in a port name, with what comes before and after it.</summary>
    [GeneratedRegex(@"^(?<prefix>.*?)(?<n>\d+)(?<suffix>\D*)$")]
    private static partial Regex Numbered();

    public static GenerationReport Generate(string from, string output, bool force)
    {
        var equipmentFile = Path.Combine(from, "equipment.csv");
        if (!File.Exists(equipmentFile))
        {
            throw new InvalidOperationException($"{equipmentFile} finns inte.");
        }
        var equipment = CsvTable.Parse("equipment.csv", File.ReadAllText(equipmentFile));
        var portsFile = Path.Combine(from, "ports.csv");
        var ports = File.Exists(portsFile) ? CsvTable.Parse("ports.csv", File.ReadAllText(portsFile)) : null;

        var skipped = new List<(string, string)>();
        var warnings = new List<(string, string)>();
        var instances = new Dictionary<string, Instance>(StringComparer.Ordinal);
        foreach (var (row, cells) in equipment.Rows)
        {
            var id = equipment.Cell(cells, "id");
            var model = equipment.Has("model") ? equipment.Cell(cells, "model") : equipment.Cell(cells, "type");
            if (id.Length == 0 || model.Length == 0)
            {
                skipped.Add(($"equipment.csv rad {row}", "id och modell (kolumnen model eller type) krävs"));
                continue;
            }
            var units = int.TryParse(equipment.Cell(cells, "rackUnits"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) && u > 0 ? u : (int?)null;
            instances[id] = new Instance(id, Blank(equipment.Cell(cells, "manufacturer")) ?? "Okänd", model, Blank(equipment.Cell(cells, "category")), units,
                Blank(equipment.Cell(cells, "parent")), Blank(equipment.Cell(cells, "slot")));
        }
        var portsOf = new Dictionary<string, List<ExportPort>>(StringComparer.Ordinal);
        foreach (var (_, cells) in ports?.Rows ?? [])
        {
            var owner = ports!.Cell(cells, "equipment");
            var name = ports.Cell(cells, "name");
            if (instances.ContainsKey(owner) && name.Length > 0)
            {
                if (!portsOf.TryGetValue(owner, out var list))
                {
                    portsOf[owner] = list = [];
                }
                list.Add(new ExportPort(name, Blank(ports.Cell(cells, "type")), Blank(ports.Cell(cells, "group"))));
            }
        }

        // Categories: the folder's own, with what the export adds appended without roles.
        var categoriesFile = Path.Combine(output, "equipment-categories.json");
        var categories = File.Exists(categoriesFile)
            ? JsonNode.Parse(File.ReadAllText(categoriesFile))!.AsArray()
            : [];
        string[] Known() => [.. categories.Select(c => c!["key"]!.GetValue<string>())];
        string? cardCategory = categories.FirstOrDefault(c => c!["roles"]!.AsArray().Any(r => r!.GetValue<string>() == CatalogRoles.Card))?["key"]!.GetValue<string>();
        var added = new List<string>();
        string Category(string key, bool card)
        {
            if (!Known().Contains(key, StringComparer.Ordinal))
            {
                categories.Add(new JsonObject
                {
                    ["key"] = key,
                    ["name"] = key == DefaultCategory ? "Övrigt" : key == DefaultCardCategory ? "Kort" : key,
                    ["roles"] = card ? new JsonArray(CatalogRoles.Card) : new JsonArray(),
                });
                added.Add(key);
            }
            return key;
        }

        var types = new List<(string Key, string Json, string Model)>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var typesFolder = Path.Combine(output, "equipment-types");
        foreach (var group in instances.Values.GroupBy(i => (i.Manufacturer, i.Model)).OrderBy(g => g.Key.Manufacturer, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Model, StringComparer.Ordinal))
        {
            var label = group.Key.Manufacturer == "Okänd" ? group.Key.Model : $"{group.Key.Manufacturer} {group.Key.Model}";
            var key = Slug(group.Key.Manufacturer == "Okänd" ? group.Key.Model : $"{group.Key.Manufacturer}-{group.Key.Model}");
            if (key.Length == 0)
            {
                skipped.Add((label, "modellnamnet ger ingen nyckel (bara tecken utanför a–z och 0–9)"));
                continue;
            }
            for (var n = 2; !keys.Add(key); n++)
            {
                key = $"{key}-{n}";
            }
            if (!force && File.Exists(Path.Combine(typesFolder, $"{key}.json")))
            {
                skipped.Add((label, $"{key}.json finns redan (kör med --force för att skriva över)"));
                continue;
            }

            var isCard = group.Any(i => i.Parent is not null && i.Slot is not null);
            var categoryKeys = group.Select(i => i.Category).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            if (categoryKeys.Count > 1)
            {
                warnings.Add((label, $"flera kategorier i exporten ({string.Join(", ", categoryKeys)}), {categoryKeys[0]} används"));
            }
            var category = Category(categoryKeys.FirstOrDefault() ?? (isCard ? cardCategory ??= DefaultCardCategory : DefaultCategory), isCard && categoryKeys.Count == 0);

            // The port names of every unit of the model; a card's slot number becomes {slot}.
            var names = new Dictionary<string, (Dictionary<string, int> Types, string? Group, int Order)>(StringComparer.Ordinal);
            var order = 0;
            foreach (var unit in group)
            {
                foreach (var port in portsOf.GetValueOrDefault(unit.Id) ?? [])
                {
                    var name = isCard && unit.Slot is { } slot ? SlotToken(slot).Replace(port.Name, "{slot}", 1) : port.Name;
                    if (!names.TryGetValue(name, out var seen))
                    {
                        names[name] = seen = (new Dictionary<string, int>(StringComparer.Ordinal), port.Group, order++);
                    }
                    var type = port.Type ?? UnknownPortType;
                    seen.Types[type] = seen.Types.GetValueOrDefault(type) + 1;
                }
            }
            if (names.Values.Any(v => v.Types.Count > 1))
            {
                warnings.Add((label, "samma port med olika typ i exporten, den vanligaste används"));
            }
            if (names.Values.Count(v => v.Types.ContainsKey(UnknownPortType)) is > 0 and var untyped)
            {
                warnings.Add((label, $"porttyp saknas för {untyped} portar i exporten, satt till {UnknownPortType}"));
            }
            var templates = Templates([.. names.Select(n => (n.Key, n.Value.Types.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).First().Key, n.Value.Group, n.Value.Order))]);

            // Slots: where the export puts cards in units of this model.
            var slots = instances.Values.Where(c => c.Parent is not null && c.Slot is not null && group.Any(p => p.Id == c.Parent))
                .GroupBy(c => c.Slot!, StringComparer.Ordinal).OrderBy(s => s.Key, NaturalComparer.Instance)
                .Select(s => (s.Key, Accepts: s.Select(c => c.Category ?? cardCategory ?? DefaultCardCategory).Distinct(StringComparer.Ordinal).ToArray()))
                .ToList();
            foreach (var accepted in slots.SelectMany(s => s.Accepts).Distinct(StringComparer.Ordinal))
            {
                Category(accepted, card: true);
            }

            var json = TypeJson(key, group.Key.Manufacturer, group.Key.Model, category, isCard ? null : group.Max(i => i.RackUnits), templates, slots);
            types.Add((key, json, label));
        }

        // Each type is checked as the catalog checks it, against the categories as they will be written.
        var categoriesJson = Compact(categories.ToJsonString(Indented));
        var generated = new List<string>();
        Directory.CreateDirectory(typesFolder);
        foreach (var (key, json, model) in types)
        {
            try
            {
                TypeCatalog.Parse([($"{key}.json", json)], categoriesJson: categoriesJson);
            }
            catch (InvalidOperationException ex)
            {
                skipped.Add((model, string.Join("; ", ex.Message.Split('\n').Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0))));
                continue;
            }
            File.WriteAllText(Path.Combine(typesFolder, $"{key}.json"), json + "\n", new UTF8Encoding(false));
            generated.Add(key);
        }
        if (added.Count > 0)
        {
            File.WriteAllText(categoriesFile, categoriesJson + "\n", new UTF8Encoding(false));
        }

        // The whole folder, as the API would load it.
        string? validation = null;
        if (File.Exists(Path.Combine(output, "cable-types.json")) && File.Exists(Path.Combine(output, "site-types.json")))
        {
            try
            {
                TypeCatalog.Load(CatalogSource.FromDirectory(output));
                validation = "";
            }
            catch (InvalidOperationException ex)
            {
                validation = ex.Message;
            }
        }
        return new GenerationReport(generated, skipped, warnings, added, validation);
    }

    /// <summary>
    /// Port names to templates: names that differ only in a trailing number, with the same type and group, become one
    /// template per consecutive run (<c>ge-0/0/{n}</c>, 1–48); the rest stay single ports. In order of first appearance.
    /// </summary>
    private static List<Template> Templates(List<(string Name, string Type, string? Group, int Order)> ports)
    {
        var singles = new List<(Template Template, int Order)>();
        var numbered = new Dictionary<(string Prefix, string Suffix, string Type, string? Group), List<(int N, int Order)>>();
        foreach (var (name, type, group, order) in ports)
        {
            var match = Numbered().Match(name);
            var digits = match.Groups["n"].Value;
            // Zero-padded numbers would lose their padding in {n}: kept as they are.
            if (!match.Success || (digits.Length > 1 && digits[0] == '0') || digits.Length > 6 || name.Contains("{n}", StringComparison.Ordinal))
            {
                singles.Add((new Template(name, type, group, null, null), order));
                continue;
            }
            var key = (match.Groups["prefix"].Value, match.Groups["suffix"].Value, type, group);
            if (!numbered.TryGetValue(key, out var list))
            {
                numbered[key] = list = [];
            }
            list.Add((int.Parse(digits, CultureInfo.InvariantCulture), order));
        }
        var all = new List<(Template Template, int Order)>(singles);
        foreach (var ((prefix, suffix, type, group), list) in numbered)
        {
            var sorted = list.OrderBy(x => x.N).ToList();
            var start = 0;
            for (var i = 1; i <= sorted.Count; i++)
            {
                if (i < sorted.Count && sorted[i].N == sorted[i - 1].N + 1)
                {
                    continue;
                }
                var run = sorted[start..i];
                var firstOrder = run.Min(r => r.Order);
                all.Add(run.Count == 1
                    ? (new Template($"{prefix}{run[0].N.ToString(CultureInfo.InvariantCulture)}{suffix}", type, group, null, null), firstOrder)
                    : (new Template($"{prefix}{{n}}{suffix}", type, group, run[0].N, run[^1].N), firstOrder));
                start = i;
            }
        }
        return [.. all.OrderBy(t => t.Order).Select(t => t.Template)];
    }

    /// <summary>
    /// The type as catalog JSON: templates laid out left to right on rows as wide as the widest run (at most
    /// <see cref="MaxColumns"/>, longer runs split), so every port has its own cell.
    /// </summary>
    private static string TypeJson(string key, string manufacturer, string model, string category, int? rackUnits, List<Template> templates,
        List<(string Name, string[] Accepts)> slots)
    {
        var columns = Math.Clamp(Math.Max(templates.Count == 0 ? 1 : templates.Max(t => t.Width), Math.Min(templates.Sum(t => t.Width), 24)), 1, MaxColumns);
        var placed = new JsonArray();
        var (row, column) = (0, 0);
        foreach (var template in templates)
        {
            List<(int? First, int? Last)> chunks = template.First is null ? [(null, null)]
                : [.. Enumerable.Range(0, (template.Width + columns - 1) / columns)
                    .Select(i => ((int?)(template.First + (i * columns)), (int?)Math.Min(template.Last!.Value, template.First!.Value + ((i + 1) * columns) - 1)))];
            foreach (var (first, last) in chunks)
            {
                var width = first is null ? 1 : last!.Value - first.Value + 1;
                if (column + width > columns)
                {
                    (row, column) = (row + 1, 0);
                }
                var port = new JsonObject { ["name"] = template.Name };
                if (first is not null)
                {
                    port["range"] = new JsonArray(first.Value, last!.Value);
                }
                port["type"] = template.Type;
                if (template.Group is not null)
                {
                    port["group"] = template.Group;
                }
                port["at"] = new JsonArray(row, column);
                placed.Add(port);
                column += width;
            }
        }
        var type = new JsonObject
        {
            ["key"] = key,
            ["manufacturer"] = manufacturer,
            ["model"] = model,
            ["category"] = category,
        };
        if (rackUnits is not null)
        {
            type["rackUnits"] = rackUnits.Value;
        }
        type["panel"] = new JsonObject { ["rows"] = templates.Count == 0 ? 1 : row + 1, ["columns"] = columns };
        type["ports"] = placed;
        if (slots.Count > 0)
        {
            type["slots"] = new JsonArray([.. slots.Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s.Name,
                ["accepts"] = new JsonArray([.. s.Accepts.Select(a => (JsonNode)a)]),
            })]);
        }
        // No attribute schema is generated: attributes are free until someone writes one.
        type["attributes"] = new JsonObject { ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["type"] = "object" };
        // Short arrays of numbers or strings on one line, as in the hand-written files.
        return Compact(type.ToJsonString(Indented));
    }

    private static string Compact(string json) =>
        ShortArray().Replace(json, m => "[" + string.Join(", ", m.Groups[1].Value.Split(',').Select(v => v.Trim())) + "]");

    /// <summary>Indented, and port types like "SFP+" written as they are.</summary>
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex(@"\[\s*((?:""[^"",\[\]]*""|-?\d+)(?:\s*,\s*(?:""[^"",\[\]]*""|-?\d+))*)\s*\]")]
    private static partial Regex ShortArray();

    /// <summary>A catalog key: lower-case letters, digits and single dashes, at most 60 characters.</summary>
    internal static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            // Letters with marks by hand: the process may run without ICU, where normalisation does not decompose them.
            var plain = c switch
            {
                'å' or 'ä' or 'á' or 'à' or 'â' or 'ã' => "a",
                'ö' or 'ø' or 'ó' or 'ò' or 'ô' or 'õ' => "o",
                'é' or 'è' or 'ê' or 'ë' => "e",
                'ü' or 'ú' or 'ù' or 'û' => "u",
                'í' or 'ì' or 'î' or 'ï' => "i",
                'æ' => "ae",
                'ç' => "c",
                'ñ' => "n",
                _ when char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) => c.ToString(),
                _ => null,
            };
            if (plain is not null)
            {
                sb.Append(plain);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }

    /// <summary>The slot number as a whole token in a port name ("8" in "et-8/0/1", not in "18").</summary>
    private static Regex SlotToken(string slot) => new($"(?<![0-9A-Za-z]){Regex.Escape(slot)}(?![0-9A-Za-z])");

    private static string? Blank(string value) => value.Length == 0 ? null : value;

    /// <summary>Orders "2" before "10".</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y) =>
            int.TryParse(x, CultureInfo.InvariantCulture, out var a) && int.TryParse(y, CultureInfo.InvariantCulture, out var b) ? a.CompareTo(b)
            : string.CompareOrdinal(x, y);
    }
}
