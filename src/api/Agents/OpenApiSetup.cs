using FastEndpoints.Swagger;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;

namespace Cmdb.Api.Agents;

/// <summary>
/// OpenAPI for agents and tools that do not speak MCP (ADR-0011, #63). Served without sign-in at
/// <c>/api/openapi.json</c>; the data behind it still needs a token.
/// </summary>
public static class OpenApiSetup
{
    public const string Path = "/api/openapi.json";

    public static IServiceCollection AddCmdbOpenApi(this IServiceCollection services) =>
        services.SwaggerDocument(o =>
        {
            o.ExcludeNonFastEndpoints = true;
            o.ShortSchemaNames = true;
            o.DocumentSettings = s =>
            {
                s.DocumentName = "cmdb";
                s.Title = "CMDB (syntetisk data)";
                s.Version = "v1";
                s.Description = """
                    CMDB for a nationwide telecom network. All data is synthetic. Every request needs a bearer token from the
                    homelab Authentik (see /llms.txt). Agents that speak MCP should use /mcp instead, which offers the same
                    reads as task-oriented tools.
                    """;
                s.OperationProcessors.Add(new Summaries());
            };
        });

    public static IApplicationBuilder UseCmdbOpenApi(this IApplicationBuilder app) =>
        app.UseOpenApi(c =>
        {
            c.Path = Path;
            c.DocumentName = "cmdb";
        });

    /// <summary>One-line summaries per operation, kept here so the endpoints stay lean.</summary>
    private sealed class Summaries : IOperationProcessor
    {
        private static readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal)
        {
            ["/api/search"] = "Quick search by code, name, id or attribute text over sites, equipment, cables, services and circuits.",
            ["/api/sites/{id}"] = "A site with its locations, equipment and cables.",
            ["/api/sites/{id}/neighbourhood"] = "Sites within 1–3 cable hops, nearest first.",
            ["/api/sites/{id}/impact"] = "Circuits and services affected if the site goes down.",
            ["/api/cables/{id}"] = "A cable with its conductors and where they are spliced.",
            ["/api/cables/{id}/impact"] = "Circuits and services affected if the cable is cut.",
            ["/api/equipment/{id}"] = "Equipment with its ports, connections and cards.",
            ["/api/equipment"] = "Create equipment in a location or slot; ports are generated from the type (cmdb-full only).",
            ["/api/services/{id}"] = "A service and the circuits carrying it.",
            ["/api/circuits/{id}"] = "A circuit with its ordered hops and the circuits it rides on.",
            ["/api/trace"] = "Trace a service or circuit down its layers, or the physical route from a terminal to the equipment at both ends.",
            ["/api/query/sites"] = "Advanced search: sites by type, lifecycle, equipment (category, model, attribute, count) and services.",
            ["/api/query/fields"] = "What advanced search can filter on, derived from the type catalog.",
            ["/api/tiles/{z}/{x}/{y}"] = "Map vector tile (MVT) of sites and cables in the SWEREF 99 TM tile grid.",
            ["/api/summary/{type}/{id}"] = "Key facts about an object, for hover cards.",
            ["/api/me"] = "The signed-in user, their groups and the client the token was issued to.",
            ["/api/me/preferences"] = "The user's saved preferences (theme).",
        };

        public bool Process(OperationProcessorContext context)
        {
            if (Texts.TryGetValue(context.OperationDescription.Path, out var text))
            {
                context.OperationDescription.Operation.Summary = text;
            }
            return true;
        }
    }
}
