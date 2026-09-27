using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;

namespace Cmdb.Catalog;

/// <summary>The type catalog (equipment and cable types), loaded and validated from the embedded catalog files.</summary>
public sealed class TypeCatalog
{
    public static readonly IReadOnlySet<string> Categories = new HashSet<string>(StringComparer.Ordinal)
    {
        "switch", "router", "card", "radio", "antenna", "transmission", "odf", "patch", "power",
    };

    public static readonly IReadOnlySet<string> CableMedia = new HashSet<string>(StringComparer.Ordinal)
    {
        "fiber", "copper", "coax", "power",
    };

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private static readonly Lazy<TypeCatalog> EmbeddedCatalog = new(() => Load(typeof(TypeCatalog).Assembly));

    private readonly FrozenDictionary<string, EquipmentType> _types;
    private readonly FrozenDictionary<string, JsonSchema> _schemas;
    private readonly FrozenDictionary<string, CableType> _cableTypes;

    private TypeCatalog(IEnumerable<EquipmentType> types, IDictionary<string, JsonSchema> schemas, IEnumerable<CableType> cableTypes)
    {
        _types = types.ToFrozenDictionary(t => t.Key, StringComparer.Ordinal);
        _schemas = schemas.ToFrozenDictionary(StringComparer.Ordinal);
        _cableTypes = cableTypes.ToFrozenDictionary(t => t.Key, StringComparer.Ordinal);
    }

    /// <summary>The catalog shipped with this build.</summary>
    public static TypeCatalog Embedded => EmbeddedCatalog.Value;

    public IReadOnlyCollection<EquipmentType> Types => _types.Values;

    public EquipmentType? Find(string key) => _types.GetValueOrDefault(key);

    public IReadOnlyCollection<CableType> CableTypes => _cableTypes.Values;

    public CableType? FindCable(string key) => _cableTypes.GetValueOrDefault(key);

    /// <summary>Validates instance attributes against the type's JSON Schema. Returns error messages, empty when valid.</summary>
    public IReadOnlyList<string> ValidateAttributes(string key, JsonElement attributes)
    {
        var result = _schemas[key].Evaluate(attributes, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid)
        {
            return [];
        }
        return [.. (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
            .Distinct()];
    }

    /// <summary>Parses and validates catalog files. Throws with every problem found, not just the first.</summary>
    public static TypeCatalog Parse(IEnumerable<(string File, string Json)> files, string cableTypesJson = "[]")
    {
        var types = new List<EquipmentType>();
        var schemas = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var (file, json) in files)
        {
            EquipmentType? type;
            try
            {
                type = JsonSerializer.Deserialize<EquipmentType>(json, Json);
            }
            catch (JsonException ex)
            {
                errors.Add($"{file}: {ex.Message}");
                continue;
            }
            if (type is null)
            {
                errors.Add($"{file}: empty");
                continue;
            }

            var before = errors.Count;
            CatalogRules.Check(file, type, errors);
            if (errors.Count == before)
            {
                try
                {
                    schemas[type.Key] = JsonSchema.FromText(type.Attributes.GetRawText());
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    errors.Add($"{file}: attributes is not a valid JSON Schema: {ex.Message}");
                }
            }
            if (errors.Count == before)
            {
                types.Add(type);
            }
        }
        CatalogRules.CheckAcrossTypes(types, errors);

        var cableTypes = new List<CableType>();
        try
        {
            cableTypes.AddRange(JsonSerializer.Deserialize<List<CableType>>(cableTypesJson, Json) ?? []);
        }
        catch (JsonException ex)
        {
            errors.Add($"cable-types.json: {ex.Message}");
        }
        CatalogRules.CheckCableTypes(cableTypes, errors);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid type catalog:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }
        return new TypeCatalog(types, schemas, cableTypes);
    }

    private static TypeCatalog Load(Assembly assembly)
    {
        const string prefix = "equipment-types/";
        string Read(string name)
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            return reader.ReadToEnd();
        }
        return Parse(
            assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .Select(n => (n[prefix.Length..], Read(n))),
            Read("cable-types.json"));
    }
}
