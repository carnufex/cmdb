using Cmdb.Graph;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Health;

/// <summary>Ready to serve traffic: the database answers, the network graph is loaded and access scopes are known.</summary>
public sealed class ReadinessEndpoint(Cmdb.Api.Auth.SystemDb system, GraphHolder graph, Cmdb.Api.Auth.ScopeRefreshService scopes) : EndpointWithoutRequest<HealthResponse>
{
    public override void Configure()
    {
        Get("/health/ready");
        AllowAnonymous();
        RoutePrefixOverride(string.Empty); // Probes stay outside /api.
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            await using var cmd = system.Source.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(ct);
            if (!graph.IsReady)
            {
                await Send.ResponseAsync(new HealthResponse("graph loading"), StatusCodes.Status503ServiceUnavailable, ct);
                return;
            }
            if (!scopes.Ready.IsCompleted)
            {
                await Send.ResponseAsync(new HealthResponse("scopes loading"), StatusCodes.Status503ServiceUnavailable, ct);
                return;
            }
            await Send.OkAsync(new HealthResponse("ok"), ct);
        }
        catch (NpgsqlException)
        {
            await Send.ResponseAsync(new HealthResponse("database unavailable"), StatusCodes.Status503ServiceUnavailable, ct);
        }
    }
}
