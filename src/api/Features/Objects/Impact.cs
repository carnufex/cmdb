using Cmdb.Api.Auth;
using System.Diagnostics;
using Cmdb.Api.Features.Trace;
using Cmdb.Graph;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Objects;

public sealed class ImpactRequest
{
    public long Id { get; set; }

    /// <summary>Impact in a plan's view (#24) instead of production.</summary>
    [QueryParam]
    public long? Plan { get; set; }
}

/// <param name="Path">How the outage reaches the service: the service's circuit first, down to the circuit hit directly.</param>
public sealed record ImpactedService(ObjectRef Service, IReadOnlyList<ImpactCircuit> Path);

public sealed record ImpactCircuit(ObjectRef Circuit, string Layer);

/// <summary>What would be affected: circuits through the object and, following dependencies upwards, services.</summary>
/// <param name="Circuits">All affected circuits.</param>
/// <param name="Direct">Circuits passing the object itself; the rest ride on those.</param>
/// <param name="Services">Affected services by code, each with the path that reaches it.</param>
/// <param name="HiddenServices">Affected services outside the caller's scope (#22): counted, not shown.</param>
/// <param name="Cables">For a route segment (#237): the cables in its ducts that the caller may see.</param>
/// <param name="HiddenCables">For a route segment: its cables outside the caller's scope, counted.</param>
public sealed record Impact(int Circuits, int Direct, IReadOnlyList<ImpactedService> Services, double ElapsedMs, int HiddenServices = 0,
    IReadOnlyList<ObjectRef>? Cables = null, int HiddenCables = 0);

/// <summary>
/// Impact of a cable (a span between two sites or splice points), equipment or a site (#10): every circuit whose path
/// touches one of its terminals, every circuit riding on those, and the services they carry, each with the shortest
/// path of circuits that reaches it. The walk is in memory (ADR-0002); only names come from the database.
/// Budget: 200 ms for a cable span (docs/plan.md).
/// </summary>
public sealed class ImpactEndpoint(GraphHolder holder, RequestDb db, ScopeMasks masks, Cmdb.Api.Features.Plans.PlanViews plans) : Endpoint<ImpactRequest, Impact>
{
    public override void Configure() => Get("/cables/{id}/impact", "/sites/{id}/impact", "/equipment/{id}/impact");

    public override async Task HandleAsync(ImpactRequest req, CancellationToken ct)
    {
        if (holder.Current is not { } graph)
        {
            await Send.ResultAsync(TypedResults.Problem("The network graph is still loading; try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable));
            return;
        }
        var path = HttpContext.Request.Path.Value!;
        var type = path.Contains("/sites/", StringComparison.Ordinal) ? "site"
            : path.Contains("/equipment/", StringComparison.Ordinal) ? "equipment"
            : "cable";
        if (req.Plan is { } planId)
        {
            if (await plans.GetAsync(graph, planId, HttpContext.Scope(), ct) is not { } view)
            {
                await Send.NotFoundAsync(ct);
                return;
            }
            graph = view.Graph;
        }
        var mask = await masks.GetAsync(graph, HttpContext.Scope(), ct);
        await Send.OkAsync(await RunAsync(graph, mask, db, type, req.Id, ct), ct);
    }

    /// <summary>
    /// Impact of a cable, equipment or site. Also used by the MCP tool <c>impact</c>. An object the graph does not know
    /// (a site without equipment, equipment added since the graph was loaded) affects nothing.
    /// </summary>
    /// <remarks>
    /// Access scopes (#22): an object outside the scope affects nothing as far as the caller can tell. Counts cover
    /// visible circuits only; services outside the scope are counted in <see cref="Impact.HiddenServices"/>, and
    /// circuits outside it on a visible service's path are placeholders.
    /// </remarks>
    internal static async Task<Impact> RunAsync(Cmdb.Graph.Graph g, GraphMask mask, NpgsqlDataSource db, string type, long id, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        ImpactResult? result = type switch
        {
            "site" when g.TryGetSite(id, out var site) && mask.SiteVisible(site) => GraphImpact.OfSite(g, site),
            "equipment" when g.TryGetEquipment(id, out var equipment) && mask.SiteVisible(g.SiteIndexOfEquipment(equipment)) => GraphImpact.OfEquipment(g, equipment),
            "cable" when g.TryGetCable(id, out var cable) && mask.CableVisible(cable) => GraphImpact.OfCable(g, cable),
            _ => null,
        };
        if (result is null)
        {
            return new Impact(0, 0, [], Math.Round(sw.Elapsed.TotalMilliseconds, 2));
        }
        return await FromResultAsync(g, mask, db, result, sw, ct);
    }

    /// <summary>The walk's result as an answer: what the caller may see named, the rest counted.</summary>
    internal static async Task<Impact> FromResultAsync(Cmdb.Graph.Graph g, GraphMask mask, NpgsqlDataSource db, ImpactResult result, Stopwatch sw,
        CancellationToken ct)
    {
        var shown = Enumerable.Range(0, result.Services.Length).Where(i => mask.ServiceVisible(result.Services[i])).ToList();
        var paths = shown.ToDictionary(i => i, result.PathOf);
        var pathCircuits = new HashSet<long>();
        foreach (var c in paths.Values.SelectMany(p => p).Where(mask.CircuitVisible))
        {
            pathCircuits.Add(g.CircuitId(c));
        }
        var names = await TraceNames.LoadAsync(db, [], [.. pathCircuits], [.. shown.Select(i => g.ServiceId(result.Services[i]))], ct);

        var services = shown
            .Select(i => new ImpactedService(
                names.Service(g.ServiceId(result.Services[i])),
                [.. paths[i].Select(c => new ImpactCircuit(
                    mask.CircuitVisible(c) ? names.Circuit(g.CircuitId(c)) : ObjectRef.Hidden("circuit"),
                    TraceEndpoint.Layer(g.LayerOf(c))))]))
            .OrderBy(s => s.Service.Code, StringComparer.Ordinal)
            .ToList();
        var circuits = result.Circuits.Count(mask.CircuitVisible);
        var direct = result.Circuits.Take(result.Direct).Count(mask.CircuitVisible);
        return new Impact(circuits, direct, services, Math.Round(sw.Elapsed.TotalMilliseconds, 2), result.Services.Length - shown.Count);
    }
}
