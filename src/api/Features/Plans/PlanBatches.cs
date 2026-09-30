using Cmdb.Api.Auth;
using Cmdb.Catalog;
using FastEndpoints;

namespace Cmdb.Api.Features.Plans;

public sealed class BatchRequest
{
    public long Id { get; set; }
    public List<AddOperationRequest> Operations { get; set; } = [];
}

public sealed record TemplateSummary(string Key, string Name, string SiteType, string Description, int Equipment, int Connections);

public sealed class TerminationRequest
{
    public long Id { get; set; }
    public long CableId { get; set; }
}

internal static class PlanBatchResults
{
    public static async Task SendAsync<TRequest>(Endpoint<TRequest, List<PlanOperationView>> endpoint, PlanWrite<List<PlanOperationView>> result,
        CancellationToken ct)
        where TRequest : notnull
    {
        switch (result.Failure)
        {
            case PlanWriteFailure.NotFound:
                await endpoint.HttpContext.Response.SendNotFoundAsync(ct);
                return;
            case PlanWriteFailure.Conflict:
                await PlanSql.ConflictAsync(endpoint.HttpContext, result.Error!, ct);
                return;
            case PlanWriteFailure.Invalid:
                await TypedResults.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(endpoint.HttpContext);
                return;
            default:
                await endpoint.HttpContext.Response.SendOkAsync(result.Value!, cancellation: ct);
                return;
        }
    }
}

/// <summary>Several operations at once, all or none (#26): the base for templates, patterns and spreadsheet edits.</summary>
public sealed class BatchEndpoint(PlanPatterns patterns) : Endpoint<BatchRequest, List<PlanOperationView>>
{
    public override void Configure()
    {
        Post("/plans/{id}/operations/batch");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(BatchRequest req, CancellationToken ct) =>
        await PlanBatchResults.SendAsync(this, await patterns.BatchAsync(User, HttpContext.Scope(), req.Id, req.Operations, ct), ct);
}

/// <summary>Site templates (#26) from the catalog.</summary>
public sealed class ListTemplatesEndpoint : EndpointWithoutRequest<List<TemplateSummary>>
{
    public override void Configure() => Get("/templates");

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync([.. SiteTemplates.Embedded.All.OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new TemplateSummary(t.Key, t.Name, t.SiteType, t.Description, t.Equipment.Count, t.Connections.Count))], ct);
}

/// <summary>A site from a template in the plan: site, equipment in racks and internal cabling.</summary>
public sealed class TemplateEndpoint(PlanPatterns patterns) : Endpoint<TemplateRequest, List<PlanOperationView>>
{
    public override void Configure()
    {
        Post("/plans/{id}/templates");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(TemplateRequest req, CancellationToken ct) =>
        await PlanBatchResults.SendAsync(this, await patterns.TemplateAsync(User, HttpContext.Scope(), req, ct), ct);
}

/// <summary>Pattern patching (#26): a port range to a conductor range, with offset and step.</summary>
public sealed class PatternEndpoint(PlanPatterns patterns) : Endpoint<PatternRequest, List<PlanOperationView>>
{
    public override void Configure()
    {
        Post("/plans/{id}/patterns");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(PatternRequest req, CancellationToken ct) =>
        await PlanBatchResults.SendAsync(this, await patterns.PatternAsync(User, HttpContext.Scope(), req, ct), ct);
}

/// <summary>A suggested termination for a cable in the plan (#26), ready to add as a batch.</summary>
public sealed class TerminationEndpoint(PlanPatterns patterns) : Endpoint<TerminationRequest, Termination>
{
    public override void Configure() => Get("/plans/{id}/cables/{cableId}/termination");

    public override async Task HandleAsync(TerminationRequest req, CancellationToken ct)
    {
        if (await patterns.TerminationAsync(HttpContext.Scope(), req.Id, req.CableId, ct) is not { } termination)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(termination, ct);
    }
}
