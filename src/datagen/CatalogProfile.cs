using Cmdb.Catalog;

namespace Cmdb.DataGen;

/// <summary>
/// What the generator takes from a catalog (#219): site types by role, cable types by medium and size, termination
/// (ODF), power and active models by category role and ports. A catalog that holds every model of the synthetic catalog
/// gets the full network (radio sectors, wavelengths, VLANs over routers and switches) and the same network as before for
/// a seed. Any other catalog gets the generic network: the same topology, cables and terminations, with its own active
/// models patched through the ODFs, physical circuits per link and one service per access site.
/// </summary>
internal sealed class CatalogProfile
{
    /// <summary>The models and cable types the full network is built from.</summary>
    private static readonly string[] SyntheticModels =
    [
        "acme-cr-8", "acme-lc-4c", "acme-lc-24x", "acme-cx-32", "acme-ax-24", "acme-ax-48", "acme-ax-48p", "acme-ar-10", "acme-sdh-63",
        "acme-bb-6", "acme-ix-8", "acme-rr-2", "acme-rr-4", "acme-ant-4p", "acme-mw-1", "acme-rect-48", "acme-bat-100", "acme-pdu-12",
        "acme-pp-24", "acme-odf-24", "acme-odf-48", "acme-odf-96", "acme-ot-40",
    ];

    private static readonly string[] SyntheticCables = ["fiber-12", "fiber-24", "fiber-48", "fiber-96", "fiber-144", "fiber-288", "copper-50"];

    private static readonly (SiteKind Kind, string Key, string Role)[] SyntheticSiteTypes =
    [
        (SiteKind.Hub, "hub", CatalogRoles.Hub), (SiteKind.Aggregation, "aggregation", CatalogRoles.Aggregation),
        (SiteKind.Radio, "radio", CatalogRoles.Access), (SiteKind.Cabinet, "cabinet", CatalogRoles.Access),
        (SiteKind.Splice, "splice", CatalogRoles.SplicePoint),
    ];

    private readonly Dictionary<SiteKind, string> _siteTypes = [];

    public CatalogProfile(TypeCatalog catalog)
    {
        Catalog = catalog;
        foreach (var (kind, key, role) in SyntheticSiteTypes)
        {
            // The synthetic key when the catalog has it with the role, otherwise the catalog's own type with that role
            // (radio sites take the first access type, cabinets the last).
            var withRole = catalog.SiteTypes.Where(t => t.Has(role)).Select(t => t.Key).ToList();
            _siteTypes[kind] = catalog.SiteTypeHas(key, role) ? key
                : withRole.Count == 0 ? throw new InvalidOperationException($"The catalog has no site type with the role '{role}'.")
                : kind == SiteKind.Cabinet ? withRole[^1] : withRole[0];
        }
        Synthetic = SyntheticModels.All(k => catalog.Find(k) is not null) && SyntheticCables.All(k => catalog.FindCable(k) is not null)
            && SyntheticSiteTypes.All(t => _siteTypes[t.Kind] == t.Key);

        Fibres = [.. catalog.CableTypes.Where(t => t.Medium == "fiber").OrderBy(t => t.ConductorCount).ThenBy(t => t.Key, StringComparer.Ordinal)];
        if (Fibres.Count == 0)
        {
            throw new InvalidOperationException("The catalog has no fibre cable type.");
        }
        Copper = catalog.CableTypes.Where(t => t.Medium == "copper").OrderBy(t => t.Key, StringComparer.Ordinal).FirstOrDefault();

        bool Racked(EquipmentType t) => !catalog.CategoryHas(t.Category, CatalogRoles.Card);
        Terminations = [.. catalog.Types.Where(t => Racked(t) && catalog.CategoryHas(t.Category, CatalogRoles.Termination) && Ports(t) > 0)
            .OrderBy(Ports).ThenBy(t => t.Key, StringComparer.Ordinal)];
        if (Terminations.Count == 0)
        {
            throw new InvalidOperationException("The catalog has no model with the role 'termination' (an ODF) to terminate cables on.");
        }
        Power = [.. catalog.Types.Where(t => Racked(t) && catalog.CategoryHas(t.Category, CatalogRoles.Power)).OrderBy(t => t.Key, StringComparer.Ordinal)];
        // Active models: racked, neither termination nor power, with at least two ports (an uplink and something to serve).
        Active = [.. catalog.Types.Where(t => Racked(t) && !catalog.CategoryHas(t.Category, CatalogRoles.Termination)
                && !catalog.CategoryHas(t.Category, CatalogRoles.Power) && Ports(t) >= 2)
            .OrderBy(Ports).ThenBy(t => t.Key, StringComparer.Ordinal)];
        if (!Synthetic && Active.Count == 0)
        {
            throw new InvalidOperationException("The catalog has no active model (a category without the roles card, termination or power, with two ports or more).");
        }
        ServiceType = catalog.ServiceTypes.Select(t => t.Key).FirstOrDefault() ?? "ethernet";
    }

    public TypeCatalog Catalog { get; }

    /// <summary>The catalog holds the synthetic models: the full network, as before.</summary>
    public bool Synthetic { get; }

    /// <summary>Fibre cable types, smallest first.</summary>
    public IReadOnlyList<CableType> Fibres { get; }

    public CableType? Copper { get; }

    /// <summary>Termination models (ODFs), fewest ports first.</summary>
    public IReadOnlyList<EquipmentType> Terminations { get; }

    public IReadOnlyList<EquipmentType> Power { get; }

    /// <summary>Active models, fewest ports first: access sites take the first, hubs and aggregation nodes the last.</summary>
    public IReadOnlyList<EquipmentType> Active { get; }

    /// <summary>The service type generic services get: the catalog's first, or "ethernet".</summary>
    public string ServiceType { get; }

    public string SiteType(SiteKind kind) => _siteTypes[kind];

    /// <summary>The smallest fibre cable with at least this many conductors (and at least 24 when there is one), else the largest.</summary>
    public CableType Fibre(int needed)
    {
        var floor = Math.Max(needed, Math.Min(24, Fibres[^1].ConductorCount));
        return Fibres.FirstOrDefault(t => t.ConductorCount >= floor) ?? Fibres[^1];
    }

    /// <summary>The termination model for what a site still needs: the smallest that holds it, else the largest.</summary>
    public EquipmentType Termination(int remaining) => Terminations.FirstOrDefault(t => Ports(t) >= remaining) ?? Terminations[^1];

    public static int Ports(EquipmentType type) => PortExpansion.Expand(type).Count;
}
