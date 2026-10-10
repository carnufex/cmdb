namespace Cmdb.Catalog;

/// <summary>
/// The icons a site type or equipment category may name (#249), the same list as the web app's
/// (src/web/src/app/shell/icons.ts). Without one, the type gets its role's icon.
/// </summary>
public static class CatalogIcons
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "network", "area", "site", "hub", "aggregation", "cabinet", "tower", "splice", "manhole", "building", "room", "rack", "position",
        "equipment", "switch", "router", "card", "radio", "antenna", "transmission", "odf", "patch", "power", "port", "cable", "conductor",
        "route-segment", "duct", "circuit", "service", "plan", "action",
    };

    /// <summary>A site type's icon: its own, or its most prominent role's.</summary>
    public static string For(CatalogSiteType type) => type.Icon ?? (
        type.Has(CatalogRoles.Hub) ? "hub"
        : type.Has(CatalogRoles.Aggregation) ? "aggregation"
        : type.Has(CatalogRoles.Access) ? "cabinet"
        : type.Has(CatalogRoles.SplicePoint) ? "splice"
        : "site");

    /// <summary>An equipment category's icon: its own, or its role's.</summary>
    public static string For(EquipmentCategory category) => category.Icon ?? (
        category.Has(CatalogRoles.Card) ? "card"
        : category.Has(CatalogRoles.Termination) ? "odf"
        : category.Has(CatalogRoles.Power) ? "power"
        : "equipment");
}
