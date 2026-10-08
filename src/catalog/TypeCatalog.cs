using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;

namespace Cmdb.Catalog;

/// <summary>The type catalog (equipment and cable types), loaded and validated from the catalog files (<see cref="CatalogSource"/>).</summary>
public sealed class TypeCatalog
{
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

    private static readonly Lazy<TypeCatalog> CurrentCatalog = new(() => Load(CatalogSource.Current));

    private readonly FrozenDictionary<string, EquipmentType> _types;
    private readonly FrozenDictionary<string, JsonSchema> _schemas;
    private readonly FrozenDictionary<string, CableType> _cableTypes;
    private readonly IReadOnlyList<CatalogSiteType> _siteTypes;
    private readonly FrozenDictionary<string, CatalogSiteType> _siteTypesByKey;
    private readonly IReadOnlyList<EquipmentCategory> _categories;
    private readonly FrozenDictionary<string, EquipmentCategory> _categoriesByKey;

    private TypeCatalog(IEnumerable<EquipmentType> types, IDictionary<string, JsonSchema> schemas, IEnumerable<CableType> cableTypes,
        IReadOnlyList<CatalogSiteType> siteTypes, IReadOnlyList<EquipmentCategory> categories)
    {
        _types = types.ToFrozenDictionary(t => t.Key, StringComparer.Ordinal);
        _schemas = schemas.ToFrozenDictionary(StringComparer.Ordinal);
        _cableTypes = cableTypes.ToFrozenDictionary(t => t.Key, StringComparer.Ordinal);
        _siteTypes = siteTypes;
        _siteTypesByKey = siteTypes.ToFrozenDictionary(t => t.Key, StringComparer.Ordinal);
        _categories = categories;
        _categoriesByKey = categories.ToFrozenDictionary(c => c.Key, StringComparer.Ordinal);
    }

    /// <summary>The catalog this process uses (<see cref="CatalogSource.Current"/>).</summary>
    public static TypeCatalog Current => CurrentCatalog.Value;

    public IReadOnlyCollection<EquipmentType> Types => _types.Values;

    public EquipmentType? Find(string key) => _types.GetValueOrDefault(key);

    public IReadOnlyCollection<CableType> CableTypes => _cableTypes.Values;

    public CableType? FindCable(string key) => _cableTypes.GetValueOrDefault(key);

    /// <summary>The site types in catalog order (#208).</summary>
    public IReadOnlyList<CatalogSiteType> SiteTypes => _siteTypes;

    public CatalogSiteType? FindSiteType(string key) => _siteTypesByKey.GetValueOrDefault(key);

    /// <summary>The keys of the site types with any of <paramref name="roles"/>, in catalog order.</summary>
    public string[] SiteTypesWith(params string[] roles) => [.. _siteTypes.Where(t => roles.Any(t.Has)).Select(t => t.Key)];

    /// <summary>Whether <paramref name="siteType"/> has <paramref name="role"/>; an unknown type has none.</summary>
    public bool SiteTypeHas(string siteType, string role) => FindSiteType(siteType)?.Has(role) == true;

    /// <summary>The equipment categories in catalog order (#208).</summary>
    public IReadOnlyList<EquipmentCategory> Categories => _categories;

    public EquipmentCategory? FindCategory(string key) => _categoriesByKey.GetValueOrDefault(key);

    /// <summary>The keys of the categories with <paramref name="role"/>, in catalog order.</summary>
    public string[] CategoriesWith(string role) => [.. _categories.Where(c => c.Has(role)).Select(c => c.Key)];

    /// <summary>Whether <paramref name="category"/> has <paramref name="role"/>; an unknown category has none.</summary>
    public bool CategoryHas(string category, string role) => FindCategory(category)?.Has(role) == true;

    /// <summary>Whether the type with <paramref name="key"/> exists and its category has <paramref name="role"/>.</summary>
    public bool TypeHas(string key, string role) => Find(key) is { } type && CategoryHas(type.Category, role);

    /// <summary>Validates instance attributes against the type's JSON Schema. Returns error messages, empty when valid.</summary>
    public IReadOnlyList<string> ValidateAttributes(string key, JsonElement attributes)
    {
        // Formats (ipv4, date and so on) are part of the model, not annotations: they are checked too.
        var result = _schemas[key].Evaluate(attributes, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
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
    /// <param name="siteTypesJson">site-types.json; null takes the embedded one.</param>
    /// <param name="categoriesJson">equipment-categories.json; null takes the embedded one.</param>
    public static TypeCatalog Parse(IEnumerable<(string File, string Json)> files, string cableTypesJson = "[]",
        string? siteTypesJson = null, string? categoriesJson = null)
    {
        var types = new List<EquipmentType>();
        var schemas = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        var errors = new List<string>();

        var siteTypes = Kinds<CatalogSiteType>("site-types.json", siteTypesJson, errors);
        KindRules.Check("site-types.json", [.. siteTypes.Select(t => (t.Key, t.Name, t.Roles))], CatalogRoles.SiteRoles, errors);
        var categories = Kinds<EquipmentCategory>("equipment-categories.json", categoriesJson, errors);
        KindRules.Check("equipment-categories.json", [.. categories.Select(c => (c.Key, c.Name, c.Roles))], CatalogRoles.CategoryRoles, errors);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid type catalog:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }
        var categoryByKey = categories.ToDictionary(c => c.Key, StringComparer.Ordinal);
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
            CatalogRules.Check(file, type, categoryByKey, errors);
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
        return new TypeCatalog(types, schemas, cableTypes, siteTypes, categories);
    }

    /// <summary>Loads and validates the equipment and cable types from <paramref name="source"/>.</summary>
    public static TypeCatalog Load(CatalogSource source)
    {
        try
        {
            string Required(string file) =>
                source.Read(file) ?? throw new InvalidOperationException("Invalid type catalog:" + Environment.NewLine + $"{file}: missing");
            return Parse(source.Files("equipment-types"), Required("cable-types.json"), Required("site-types.json"), Required("equipment-categories.json"));
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{ex.Message}{Environment.NewLine}(catalog: {source.Name})", ex);
        }
    }

    private static List<T> Kinds<T>(string file, string? json, List<string> errors)
    {
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json ?? CatalogSource.Embedded.Read(file)!, Json) ?? [];
        }
        catch (JsonException ex)
        {
            errors.Add($"{file}: {ex.Message}");
            return [];
        }
    }
}
