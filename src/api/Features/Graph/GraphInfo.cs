using Cmdb.Graph;
using FastEndpoints;

namespace Cmdb.Api.Features.Graph;

public sealed record GraphInfoResponse(
    bool Ready,
    string? Version,
    int Terminals,
    int Edges,
    int Circuits,
    long ArrayBytes,
    long ManagedBytes,
    string? Source,
    double LoadSeconds,
    DateTimeOffset? LoadedAt);

/// <summary>What the in-memory graph holds and how it was loaded (#8).</summary>
public sealed class GraphInfoEndpoint(GraphHolder holder) : EndpointWithoutRequest<GraphInfoResponse>
{
    public override void Configure() => Get("/graph");

    public override Task HandleAsync(CancellationToken ct)
    {
        var graph = holder.Current;
        var info = holder.LoadInfo;
        return Send.OkAsync(new GraphInfoResponse(
            graph is not null,
            graph?.Version,
            graph?.NodeCount ?? 0,
            graph?.EdgeCount ?? 0,
            graph?.CircuitCount ?? 0,
            graph?.ApproximateBytes ?? 0,
            info?.ManagedBytes ?? 0,
            info?.Source,
            Math.Round(info?.Duration.TotalSeconds ?? 0, 2),
            info?.LoadedAt), ct);
    }
}
