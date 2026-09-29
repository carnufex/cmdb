using Cmdb.Graph;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Health;

/// <summary>Ready to serve traffic: the database answers and the network graph is loaded.</summary>
public sealed class ReadinessEndpoint(NpgsqlDataSource db, GraphHolder graph) : EndpointWithoutRequest<HealthResponse>
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
            await using var cmd = db.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(ct);
            if (!graph.IsReady)
            {
                await Send.ResponseAsync(new HealthResponse("graph loading"), StatusCodes.Status503ServiceUnavailable, ct);
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
