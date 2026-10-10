using Cmdb.Api.Features.Query;
using Cmdb.Catalog;
using FastEndpoints;

namespace Cmdb.Api.Features.Catalog;

/// <summary>The catalog's site types and equipment categories with names, roles (#208) and icons (#249), and the attribute
/// fields of site, cable and service types (#211).</summary>
public sealed record CatalogKinds(IReadOnlyList<KindField> SiteTypes, IReadOnlyList<KindField> Categories,
    IReadOnlyList<KindField> CableTypes, IReadOnlyList<KindField> ServiceTypes);

/// <summary>
/// Site types and categories for the UI: labels, map and graph styling by role, and pickers. The catalog is fixed for
/// the life of the process, so this reads no database.
/// </summary>
public sealed class GetCatalogKindsEndpoint(TypeCatalog catalog) : EndpointWithoutRequest<CatalogKinds>
{
    public override void Configure() => Get("/catalog/kinds");

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(new CatalogKinds(QueryFieldsEndpoint.SiteTypeFields(catalog),
            [.. catalog.Categories.Select(c => new KindField(c.Key, c.Name, c.Roles, Icon: CatalogIcons.For(c)))],
            [.. catalog.CableTypes.Select(t => new KindField(t.Key, t.Name, [], QueryFieldsEndpoint.Fields(t.Attributes)))],
            QueryFieldsEndpoint.ServiceTypeFields(catalog)), ct);
}
