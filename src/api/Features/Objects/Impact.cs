using System.Diagnostics;
using Cmdb.Api.Features.Trace;
using Cmdb.Graph;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Objects;

public sealed record ImpactRequest(long Id);

/// <param name="Path">How the outage reaches the service: the service's circuit first, down to the circuit hit directly.</param>
public sealed record ImpactedService(ObjectRef Service, IReadOnlyList<ImpactCircuit> Path);

public sealed record ImpactCircuit(ObjectRef Circuit, string Layer);

/// <summary>What would be affected: circuits through the object and, following dependencies upwards, services.</summary>
/// <param name="Circuits">All affected circuits.</param>
/// <param name="Direct">Circuits passing the object itself; the rest ride on those.</param>
/// <param name="Services">Affected services by code, each with the path that reaches it.</param>
public sealed record Impact(int Circuits, int Direct, IReadOnlyList<ImpactedService> Services, double ElapsedMs);

/// <summary>
/// Impact of a cable (a span between two sites or splice points), equipment or a site (#10): every circuit whose path
/// touches one of its terminals, every circuit riding on those, and the services they carry, each with the shortest
/// path of circuits that reaches it. The walk is in memory (ADR-0002); only names come from the database.
/// Budget: 200 ms for a cable span (docs/plan.md).
/// </summary>
public sealed class ImpactEndpoint(GraphHolder holder, NpgsqlDataSource db) : Endpoint<ImpactRequest, Impact>
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
        await Send.OkAsync(await RunAsync(graph, db, type, req.Id, ct), ct);
    }

    /// <summary>
    /// Impact of a cable, equipment or site. Also used by the MCP tool <c>impact</c>. An object the graph does not know
    /// (a site without equipment, equipment added since the graph was loaded) affects nothing.
    /// </summary>
    internal static async Task<Impact> RunAsync(Cmdb.Graph.Graph g, NpgsqlDataSource db, string type, long id, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // TODO(#22): only services within the caller's scope are listed; the rest are counted.
        ImpactResult? result = type switch
        {
            "site" when g.TryGetSite(id, out var site) => GraphImpact.OfSite(g, site),
            "equipment" when g.TryGetEquipment(id, out var equipment) => GraphImpact.OfEquipment(g, equipment),
            "cable" when g.TryGetCable(id, out var cable) => GraphImpact.OfCable(g, cable),
            _ => null,
        };
        if (result is null)
        {
            return new Impact(0, 0, [], Math.Round(sw.Elapsed.TotalMilliseconds, 2));
        }

        var paths = new List<int>[result.Services.Length];
        var pathCircuits = new HashSet<long>();
        for (var i = 0; i < paths.Length; i++)
        {
            paths[i] = result.PathOf(i);
            foreach (var c in paths[i])
            {
                pathCircuits.Add(g.CircuitId(c));
            }
        }
        var names = await TraceNames.LoadAsync(db, [], [.. pathCircuits], [.. result.Services.Select(g.ServiceId)], ct);

        var services = result.Services
            .Select((s, i) => new ImpactedService(
                names.Service(g.ServiceId(s)),
                [.. paths[i].Select(c => new ImpactCircuit(names.Circuit(g.CircuitId(c)), TraceEndpoint.Layer(g.LayerOf(c))))]))
            .OrderBy(s => s.Service.Code, StringComparer.Ordinal)
            .ToList();
        return new Impact(result.Circuits.Length, result.Direct, services, Math.Round(sw.Elapsed.TotalMilliseconds, 2));
    }
}
