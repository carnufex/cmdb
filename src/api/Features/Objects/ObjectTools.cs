using Cmdb.Api.Auth;
using System.ComponentModel;
using System.Globalization;
using Cmdb.Api.Agents;
using Cmdb.Api.Features.Cables;
using Cmdb.Api.Features.Circuits;
using Cmdb.Api.Features.Search;
using Cmdb.Api.Features.Services;
using Cmdb.Api.Features.Sites;
using Cmdb.Graph;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Npgsql;
using EquipmentSlice = Cmdb.Api.Features.Equipment.GetEquipmentEndpoint;

namespace Cmdb.Api.Features.Objects;

public sealed record AgentObject(string Ref, string Url, object Detail);

/// <param name="Via">Circuits from the service's own down to the one passing the object, as "code (layer)".</param>
public sealed record AgentImpactedService(string Ref, string Code, string? Name, string Lifecycle, string Url, IReadOnlyList<string> Via);

public sealed record AgentImpact(string Ref, string Url, int Circuits, int DirectCircuits, int Services, IReadOnlyList<AgentImpactedService> AffectedServices, bool Truncated);

[McpServerToolType]
public sealed class ObjectTools(NpgsqlDataSource db, GraphHolder holder, AgentLinks links, ScopeMasks masks, IHttpContextAccessor http)
{
    private const int MaxServices = 50;

    /// <summary>The calling agent's scopes (#22).</summary>
    private UserScope Scope => http.HttpContext!.Scope();
    private static readonly string[] Types = ["site", "equipment", "cable", "service", "circuit"];

    [McpServerTool(Name = "get_object", Title = "Hämta objekt", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Everything the UI shows for one object: a site with locations, equipment and cables; equipment with ports and " +
        "connections; a cable with conductors; a service with its circuits; a circuit with its hops.")]
    public async Task<AgentObject> GetObject(
        [Description("A reference \"type:id\" (site, equipment, cable, service, circuit), e.g. \"site:1268\", or an exact code such as \"RAD-000007\".")] string reference,
        CancellationToken ct = default)
    {
        var (type, id) = await ResolveAsync(reference, ct);
        object? detail = type switch
        {
            "site" => await GetSiteEndpoint.LoadAsync(db, id, Scope, ct),
            "equipment" => await EquipmentSlice.LoadAsync(db, id, Scope, ct),
            "cable" => await GetCableEndpoint.LoadAsync(db, id, Scope, ct),
            "service" => await GetServiceEndpoint.LoadAsync(db, id, Scope, ct),
            _ => await GetCircuitEndpoint.LoadAsync(db, id, Scope, ct),
        };
        return detail is null
            ? throw new McpException($"No {type} with id {id}.")
            : new AgentObject(AgentLinks.Ref(type, id), links.For(type, id), detail);
    }

    [McpServerTool(Name = "impact", Title = "Påverkansanalys", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What a cut of a cable, or an outage of equipment or a site, would affect: the circuits through it, circuits " +
        "riding on those, and the services they carry, each with the chain of circuits that reaches it.")]
    public async Task<AgentImpact> Impact(
        [Description("A cable, equipment or site: \"cable:42\", \"equipment:9001\", \"site:1268\" or an exact code such as \"K-000123\".")] string reference,
        CancellationToken ct = default)
    {
        var (type, id) = await ResolveAsync(reference, ct);
        if (type is not ("cable" or "equipment" or "site"))
        {
            throw new McpException("impact works on cables, equipment and sites.");
        }
        if (holder.Current is not { } graph)
        {
            throw new McpException("The network graph is still loading; try again in a few seconds.");
        }
        var impact = await ImpactEndpoint.RunAsync(graph, await masks.GetAsync(graph, Scope, ct), db, type, id, ct);
        return new AgentImpact(
            AgentLinks.Ref(type, id),
            links.For(type, id),
            impact.Circuits,
            impact.Direct,
            impact.Services.Count,
            [.. impact.Services.Take(MaxServices).Select(s => new AgentImpactedService(
                AgentLinks.Ref("service", s.Service.Id), s.Service.Code, s.Service.Name, s.Service.Lifecycle ?? "", links.For("service", s.Service.Id),
                [.. s.Path.Select(c => $"{c.Circuit.Code} ({c.Layer})")]))],
            impact.Services.Count > MaxServices);
    }

    /// <summary>Accepts "type:id" or an exact code, which agents often have from text.</summary>
    private async Task<(string Type, long Id)> ResolveAsync(string reference, CancellationToken ct)
    {
        var value = reference.Trim();
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && long.TryParse(value.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            var type = value[..colon];
            return Types.Contains(type) ? (type, id) : throw new McpException($"Unknown type '{type}'. Use one of {string.Join(", ", Types)}.");
        }
        if (value.Length >= 3)
        {
            var exact = (await SearchEndpoint.RunAsync(db, value, null, null, 5, Scope, ct))
                .FirstOrDefault(h => string.Equals(h.Code, value, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return (exact.Type, exact.Id);
            }
        }
        throw new McpException($"'{reference}' is neither a reference like \"site:1268\" nor an exact code. Use search to find it.");
    }
}
