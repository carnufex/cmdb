using Cmdb.Api.Auth;
using System.ComponentModel;
using Cmdb.Api.Agents;
using Cmdb.Graph;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Npgsql;

namespace Cmdb.Api.Features.Trace;

public sealed record AgentTrace(string Start, string Url, TraceResult Trace);

[McpServerToolType]
public sealed class TraceTools(GraphHolder holder, RequestDb db, AgentLinks links, ScopeMasks masks, IHttpContextAccessor http)
{
    [McpServerTool(Name = "trace", Title = "Spårning", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Trace a service or circuit down through the layers it rides on (logical → transmission → physical), each " +
        "circuit with its ordered hops; or trace the physical route from a terminal (port or conductor end) through patches, " +
        "splices and conductors to the equipment at both ends, with the services passing it. Returns sites and cables along " +
        "the route in order.")]
    public async Task<AgentTrace> Trace(
        [Description("What to trace: \"service:1360\", \"circuit:42\" or \"terminal:123456\" (terminal ids appear in get_object for ports).")] string reference,
        CancellationToken ct = default)
    {
        var colon = reference.IndexOf(':', StringComparison.Ordinal);
        if (colon < 1 || !long.TryParse(reference.AsSpan(colon + 1), out var id))
        {
            throw new McpException("Use \"service:id\", \"circuit:id\" or \"terminal:id\".");
        }
        var type = reference[..colon];
        if (!holder.IsReady)
        {
            throw new McpException("The network graph is still loading; try again in a few seconds.");
        }
        var graph = holder.Require();
        var mask = await masks.GetAsync(graph, http.HttpContext!.Scope(), ct);
        var result = type switch
        {
            "service" => await TraceEndpoint.RunAsync(graph, mask, db, null, id, null, ct),
            "circuit" => await TraceEndpoint.RunAsync(graph, mask, db, null, null, id, ct),
            "terminal" => await TraceEndpoint.RunAsync(graph, mask, db, id, null, null, ct),
            _ => throw new McpException($"Cannot trace a {type}; use service, circuit or terminal."),
        };
        return result is null
            ? throw new McpException($"No {type} with id {id}.")
            : new AgentTrace(reference, type == "terminal" ? links.Base : links.For(type, id), result);
    }
}
