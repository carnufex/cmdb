using System.Reflection;
using ModelContextProtocol.Server;

namespace Cmdb.Api.Agents;

/// <summary>
/// The MCP server (ADR-0011): Streamable HTTP on <c>/mcp</c>, stateless, behind the same JWT authentication as the
/// API. Tools live in the feature slices next to the endpoints they share code with.
/// </summary>
public static class McpSetup
{
    public const string Path = "/mcp";

    public static IServiceCollection AddCmdbMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<AgentLinks>();
        services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "cmdb", Title = "CMDB (syntetisk data)", Version = "1.0" };
                o.ServerInstructions = Instructions;
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithToolsFromAssembly(Assembly.GetExecutingAssembly())
            .WithResourcesFromAssembly(Assembly.GetExecutingAssembly())
            .WithPromptsFromAssembly(Assembly.GetExecutingAssembly());
        return services;
    }

    public static IEndpointConventionBuilder MapCmdbMcp(this IEndpointRouteBuilder app) =>
        app.MapMcp(Path).RequireAuthorization();

    /// <summary>Sent to the agent when it connects: what the data is and how to work with it.</summary>
    private const string Instructions = """
        CMDB for a nationwide telecom network. ALL DATA IS SYNTHETIC: the network, the organisation and every name are made up.

        Model: sites (hub, aggregation, radio, cabinet, splice) hold equipment in locations; equipment has ports. Cables run
        between sites and hold conductors (fibres, pairs). Ports and conductor ends are terminals, joined by connections
        (patch, splice, internal). Circuits are ordered paths of terminals in a layer (physical, transmission, logical) and
        may ride on lower circuits; services are carried by circuits.

        Every object has a stable reference "type:id" (e.g. "site:1268") and a code (e.g. "RAD-000007"). Every answer has a
        `url` that opens the same object in the web UI; give it to the human when you refer to an object.

        Start with `search` for names and codes, `find_sites` for structured questions (sites with equipment of a model,
        attribute values, services passing through), `get_object` for details, `impact` for what a cable or site cut
        affects and `neighbourhood` for nearby sites. `describe_catalog` lists equipment models, categories and attributes.
        The tools are read-only; changes will be proposed as plans that a human applies.
        """;
}
