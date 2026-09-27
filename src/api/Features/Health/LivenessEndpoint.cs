using FastEndpoints;

namespace Cmdb.Api.Features.Health;

/// <summary>Process is up. Does not touch dependencies.</summary>
public sealed class LivenessEndpoint : EndpointWithoutRequest<HealthResponse>
{
    public override void Configure()
    {
        Get("/health");
        AllowAnonymous();
        RoutePrefixOverride(string.Empty); // Probes stay outside /api.
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(new HealthResponse("ok"), ct);
}
