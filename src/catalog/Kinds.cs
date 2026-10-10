namespace Cmdb.Catalog;

/// <summary>
/// The roles code asks for instead of type names (#208). A site type or equipment category is catalog data with a
/// display name and roles, so an organisation can call its sites what it likes and still get plans, risks and
/// route suggestions that find its hubs and aggregation nodes.
/// </summary>
public static class CatalogRoles
{
    /// <summary>A core node: shown at every zoom level, the last choice when pointing at a shared site.</summary>
    public const string Hub = "hub";

    /// <summary>An aggregation node: shown at every zoom level, where false redundancy usually hides.</summary>
    public const string Aggregation = "aggregation";

    /// <summary>A site that serves customers or radio (cabinets, radio sites).</summary>
    public const string Access = "access";

    /// <summary>A place where cables are only spliced.</summary>
    public const string SplicePoint = "splice-point";

    public static readonly IReadOnlySet<string> SiteRoles = new HashSet<string>(StringComparer.Ordinal) { Hub, Aggregation, Access, SplicePoint };

    /// <summary>Sits in a slot of other equipment, never on its own in a rack or a site template.</summary>
    public const string Card = "card";

    /// <summary>Terminates fibres from a cable (an ODF): where patterns and route suggestions land conductors.</summary>
    public const string Termination = "termination";

    /// <summary>Power supply (batteries, rectifiers): checked for age by the risk overview.</summary>
    public const string Power = "power";

    public static readonly IReadOnlySet<string> CategoryRoles = new HashSet<string>(StringComparer.Ordinal) { Card, Termination, Power };
}

/// <summary>A site type from <c>catalog/site-types.json</c>.</summary>
/// <param name="Attributes">Optional JSON Schema for the site's attributes (#211); without one they are free.</param>
/// <param name="Icon">One of <see cref="CatalogIcons.Names"/> (#249); without one, the role's.</param>
public sealed record CatalogSiteType(string Key, string Name, IReadOnlyList<string> Roles, System.Text.Json.JsonElement? Attributes = null, string? Icon = null)
{
    public bool Has(string role) => Roles.Contains(role, StringComparer.Ordinal);
}

/// <summary>A service type from <c>catalog/service-types.json</c> (#211). The file is optional; service types not in it are free.</summary>
/// <param name="Attributes">Optional JSON Schema for the service's attributes; without one they are free.</param>
public sealed record ServiceType(string Key, string Name, System.Text.Json.JsonElement? Attributes = null);

/// <summary>An equipment category from <c>catalog/equipment-categories.json</c>.</summary>
/// <param name="Icon">One of <see cref="CatalogIcons.Names"/> (#249); without one, the role's.</param>
public sealed record EquipmentCategory(string Key, string Name, IReadOnlyList<string> Roles, string? Icon = null)
{
    public bool Has(string role) => Roles.Contains(role, StringComparer.Ordinal);
}

internal static class KindRules
{
    /// <summary>Checks one kinds file: keys like other catalog keys, a name, known roles, no duplicates.</summary>
    public static void Check(string file, IReadOnlyList<(string Key, string Name, IReadOnlyList<string> Roles)> kinds, IReadOnlySet<string> roles, List<string> errors)
    {
        foreach (var (key, name, list) in kinds)
        {
            if (key is null || name is null || list is null)
            {
                errors.Add($"{file}: key, name and roles are required");
                continue;
            }
            if (key.Length is 0 or > 40 || key.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_')))
            {
                errors.Add($"{file}: key '{key}' is 1–40 characters: lower-case letters, digits, - and _");
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"{file}: '{key}' needs a name");
            }
            foreach (var role in list.Where(r => !roles.Contains(r)))
            {
                errors.Add($"{file}: '{key}' has unknown role '{role}' (roles: {string.Join(", ", roles)})");
            }
        }
        foreach (var duplicate in kinds.Where(k => k.Key is not null).GroupBy(k => k.Key).Where(g => g.Count() > 1))
        {
            errors.Add($"{file}: key '{duplicate.Key}' is used more than once");
        }
    }

    /// <summary>Checks the icons a kinds file names (#249): each one of <see cref="CatalogIcons.Names"/>.</summary>
    public static void CheckIcons(string file, IEnumerable<(string Key, string? Icon)> kinds, List<string> errors)
    {
        foreach (var (key, icon) in kinds.Where(k => k.Icon is not null && !CatalogIcons.Names.Contains(k.Icon)))
        {
            errors.Add($"{file}: '{key}' has unknown icon '{icon}' (icons: {string.Join(", ", CatalogIcons.Names)})");
        }
    }
}
