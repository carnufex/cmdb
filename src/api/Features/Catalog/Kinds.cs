using Cmdb.Api.Features.Query;
using Cmdb.Catalog;
using FastEndpoints;

namespace Cmdb.Api.Features.Catalog;

/// <summary>The catalog's site types and equipment categories with names and roles (#208).</summary>
public sealed record CatalogKinds(IReadOnlyList<KindField> SiteTypes, IReadOnlyList<KindField> Categories);

/// <summary>
/// Site types and categories for the UI: labels, map and graph styling by role, and pickers. The catalog is fixed for
/// the life of the process, so this reads no database.
/// </summary>
public sealed class GetCatalogKindsEndpoint(TypeCatalog catalog) : EndpointWithoutRequest<CatalogKinds>
{
    public override void Configure() => Get("/catalog/kinds");

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(new CatalogKinds(QueryFieldsEndpoint.SiteTypeFields(catalog),
            [.. catalog.Categories.Select(c => new KindField(c.Key, c.Name, c.Roles))]), ct);
}
