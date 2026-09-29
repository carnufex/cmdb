using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace Cmdb.Api.Agents;

/// <summary>
/// The project's own documentation as MCP resources, embedded at build time from <c>docs/</c>, so agents and humans
/// read the same text.
/// </summary>
[McpServerResourceType]
public sealed class McpDocs
{
    [McpServerResource(UriTemplate = "cmdb://docs/domanmodell", Name = "domanmodell", Title = "Domänmodell", MimeType = "text/markdown")]
    [Description("The domain model: sites, equipment, terminals, cables, connections, circuits across layers, services, plans and lifecycle.")]
    public static string DomainModel() => Read("domanmodell.md");

    [McpServerResource(UriTemplate = "cmdb://docs/plan", Name = "plan", Title = "Plan och prestandabudget", MimeType = "text/markdown")]
    [Description("Vision, scale and the performance budget (p95 targets per interaction).")]
    public static string Plan() => Read("plan.md");

    [McpServerResource(UriTemplate = "cmdb://docs/arkitektur", Name = "arkitektur", Title = "Arkitektur", MimeType = "text/markdown")]
    [Description("Architecture: graph engine, writes and traceability, access scopes, geography and operations.")]
    public static string Architecture() => Read("arkitektur.md");

    private static string Read(string file)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"docs/{file}")
            ?? throw new InvalidOperationException($"docs/{file} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>Ready-made tasks an agent client can offer its user.</summary>
[McpServerPromptType]
public sealed class McpPrompts
{
    [McpServerPrompt(Name = "cable_cut_impact", Title = "Vad händer om en kabel kapas?")]
    [Description("Analyse what a cut of a cable would affect and who should be told.")]
    public static string CableCutImpact([Description("Cable code or reference, e.g. \"K-000123\" or \"cable:42\".")] string cable) =>
        $"""
        Kabel {cable} kapas. Använd CMDB-verktygen:
        1. get_object på kabeln: vilka siter den går mellan, typ och längd.
        2. impact på kabeln: hur många kretsar som påverkas och vilka tjänster.
        3. För de fem första tjänsterna: get_object för att se vilka siter tjänsten börjar och slutar i.
        Sammanfatta på svenska: påverkade tjänster per typ, vilka siter som blir utan förbindelse och länkar (url) till
        kabeln och tjänsterna i CMDB:n. All data är syntetisk.
        """;

    [McpServerPrompt(Name = "find_sites_by_equipment", Title = "Hitta siter efter utrustning")]
    [Description("Turn a question about equipment into a structured site search.")]
    public static string FindSitesByEquipment([Description("The question, e.g. \"radiositer med 3500 MHz-radio\".")] string question) =>
        $"""
        Fråga: {question}
        1. Anropa describe_catalog för att se kategorier, modellnycklar och attribut med tillåtna värden.
        2. Översätt frågan till find_sites (sitetyper, livscykel, utrustningsvillkor, tjänstetyper).
        3. Svara med antal träffar, de första siterna med länkar och hur frågan tolkades, så att användaren kan justera.
        """;
}
