using System.Collections.Frozen;
using System.Text.Json;

namespace Cmdb.Catalog;

/// <summary>
/// A site template (#26): a site type, the equipment it holds (in named racks) and the internal cabling between it.
/// Creating a site from a template in a plan gives the ordinary operations, so everything is checked as usual.
/// </summary>
/// <param name="Key">Also the file name: <c>catalog/site-templates/&lt;key&gt;.json</c>.</param>
public sealed record SiteTemplate(
    string Key,
    string Name,
    string SiteType,
    string Description,
    IReadOnlyList<TemplateEquipment> Equipment,
    IReadOnlyList<TemplateConnection> Connections);

/// <param name="Ref">The equipment's name within the template, used by connections.</param>
/// <param name="Name">The instance's name; <c>{code}</c> is replaced by the site's code.</param>
/// <param name="Rack">The rack it sits in; racks are created in a building on the site.</param>
public sealed record TemplateEquipment(string Ref, string TypeKey, string Name, string Rack);

/// <param name="Kind">patch, splice, termination or internal.</param>
public sealed record TemplateConnection(string From, string FromPort, string To, string ToPort, string Kind);

/// <summary>The site templates, loaded and checked against the type catalog.</summary>
public sealed class SiteTemplates
{
    public static readonly IReadOnlySet<string> SiteTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "hub", "aggregation", "radio", "cabinet", "splice",
    };

    private static readonly HashSet<string> ConnectionKinds = new(StringComparer.Ordinal)
    {
        "patch", "splice", "termination", "internal",
    };

    private static readonly Lazy<SiteTemplates> CurrentTemplates = new(() => Load(CatalogSource.Current, TypeCatalog.Current));

    private readonly FrozenDictionary<string, SiteTemplate> _templates;

    private SiteTemplates(IEnumerable<SiteTemplate> templates) =>
        _templates = templates.ToFrozenDictionary(t => t.Key, StringComparer.Ordinal);

    public static SiteTemplates Current => CurrentTemplates.Value;

    public IReadOnlyCollection<SiteTemplate> All => _templates.Values;

    public SiteTemplate? Find(string key) => _templates.GetValueOrDefault(key);

    /// <summary>Parses and checks template files. Throws with every problem found.</summary>
    public static SiteTemplates Parse(IEnumerable<(string File, string Json)> files, TypeCatalog catalog)
    {
        var templates = new List<SiteTemplate>();
        var errors = new List<string>();
        foreach (var (file, json) in files)
        {
            SiteTemplate? template;
            try
            {
                template = JsonSerializer.Deserialize<SiteTemplate>(json, TypeCatalog.Json);
            }
            catch (JsonException ex)
            {
                errors.Add($"{file}: {ex.Message}");
                continue;
            }
            if (template is null)
            {
                errors.Add($"{file}: empty");
                continue;
            }
            var before = errors.Count;
            Check(file, template, catalog, errors);
            if (errors.Count == before)
            {
                templates.Add(template);
            }
        }
        foreach (var duplicate in templates.GroupBy(t => t.Key).Where(g => g.Count() > 1))
        {
            errors.Add($"site template key '{duplicate.Key}' is used more than once");
        }
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid site templates:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }
        return new SiteTemplates(templates);
    }

    private static void Check(string file, SiteTemplate t, TypeCatalog catalog, List<string> errors)
    {
        if (Path.GetFileNameWithoutExtension(file) != t.Key)
        {
            errors.Add($"{file}: key '{t.Key}' must match the file name");
        }
        if (!SiteTypes.Contains(t.SiteType))
        {
            errors.Add($"{file}: unknown site type '{t.SiteType}'");
        }
        if (t.Equipment.Count == 0)
        {
            errors.Add($"{file}: a template needs equipment");
        }
        var ports = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var e in t.Equipment)
        {
            if (!ports.TryAdd(e.Ref, []))
            {
                errors.Add($"{file}: equipment ref '{e.Ref}' is used more than once");
                continue;
            }
            if (catalog.Find(e.TypeKey) is not { } type)
            {
                errors.Add($"{file}: '{e.Ref}' has unknown type '{e.TypeKey}'");
                continue;
            }
            if (type.Category == "card")
            {
                errors.Add($"{file}: '{e.Ref}' is a card; templates hold top-level equipment");
            }
            if (!e.Name.Contains("{code}", StringComparison.Ordinal))
            {
                errors.Add($"{file}: '{e.Ref}' name must contain {{code}} so names stay unique per site");
            }
            if (string.IsNullOrWhiteSpace(e.Rack))
            {
                errors.Add($"{file}: '{e.Ref}' needs a rack");
            }
            ports[e.Ref] = [.. PortExpansion.Expand(type).Select(p => p.Name)];
        }
        var used = new HashSet<(string, string, string)>();
        foreach (var c in t.Connections)
        {
            if (!ConnectionKinds.Contains(c.Kind))
            {
                errors.Add($"{file}: connection kind '{c.Kind}' is not patch, splice, termination or internal");
            }
            foreach (var (equipment, port) in new[] { (c.From, c.FromPort), (c.To, c.ToPort) })
            {
                if (!ports.TryGetValue(equipment, out var names))
                {
                    errors.Add($"{file}: connection refers to unknown equipment '{equipment}'");
                }
                else if (!names.Contains(port))
                {
                    errors.Add($"{file}: '{equipment}' has no port '{port}'");
                }
                else if (!used.Add((equipment, port, c.Kind)))
                {
                    errors.Add($"{file}: '{equipment}' port '{port}' has two {c.Kind} connections");
                }
            }
            if (c.From == c.To && c.FromPort == c.ToPort)
            {
                errors.Add($"{file}: a port cannot connect to itself ({c.From} {c.FromPort})");
            }
        }
    }

    /// <summary>Loads the site templates from <paramref name="source"/> and checks them against <paramref name="catalog"/>.</summary>
    public static SiteTemplates Load(CatalogSource source, TypeCatalog catalog)
    {
        try
        {
            return Parse(source.Files("site-templates"), catalog);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{ex.Message}{Environment.NewLine}(catalog: {source.Name})", ex);
        }
    }
}
