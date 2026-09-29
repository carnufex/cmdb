using System.ComponentModel;
using System.Globalization;
using Cmdb.Api.Agents;
using Cmdb.Api.Features.Cables;
using Cmdb.Api.Features.Circuits;
using Cmdb.Api.Features.Search;
using Cmdb.Api.Features.Services;
using Cmdb.Api.Features.Sites;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Npgsql;
using EquipmentSlice = Cmdb.Api.Features.Equipment.GetEquipmentEndpoint;

namespace Cmdb.Api.Features.Objects;

public sealed record AgentObject(string Ref, string Url, object Detail);

public sealed record AgentImpact(string Ref, string Url, int Circuits, int Services, IReadOnlyList<AgentHit> AffectedServices, bool Truncated);

[McpServerToolType]
public sealed class ObjectTools(NpgsqlDataSource db, AgentLinks links)
{
    private const int MaxServices = 50;
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
            "site" => await GetSiteEndpoint.LoadAsync(db, id, ct),
            "equipment" => await EquipmentSlice.LoadAsync(db, id, ct),
            "cable" => await GetCableEndpoint.LoadAsync(db, id, ct),
            "service" => await GetServiceEndpoint.LoadAsync(db, id, ct),
            _ => await GetCircuitEndpoint.LoadAsync(db, id, ct),
        };
        return detail is null
            ? throw new McpException($"No {type} with id {id}.")
            : new AgentObject(AgentLinks.Ref(type, id), links.For(type, id), detail);
    }

    [McpServerTool(Name = "impact", Title = "Påverkansanalys", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What a cut of a cable, or an outage of a site, would affect: the circuits through it, circuits riding on those, " +
        "and the services they carry.")]
    public async Task<AgentImpact> Impact(
        [Description("A cable or site: \"cable:42\", \"site:1268\" or an exact code such as \"K-000123\".")] string reference,
        CancellationToken ct = default)
    {
        var (type, id) = await ResolveAsync(reference, ct);
        if (type is not ("cable" or "site"))
        {
            throw new McpException("impact works on cables and sites.");
        }
        var impact = await ImpactEndpoint.RunAsync(db, type, id, ct);
        return new AgentImpact(
            AgentLinks.Ref(type, id),
            links.For(type, id),
            impact.Circuits,
            impact.Services.Count,
            [.. impact.Services.Take(MaxServices).Select(s => new AgentHit(AgentLinks.Ref("service", s.Id), "service", s.Id, s.Code, s.Name, null, s.Lifecycle ?? "", links.For("service", s.Id)))],
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
            var exact = (await SearchEndpoint.RunAsync(db, value, null, null, 5, ct))
                .FirstOrDefault(h => string.Equals(h.Code, value, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return (exact.Type, exact.Id);
            }
        }
        throw new McpException($"'{reference}' is neither a reference like \"site:1268\" nor an exact code. Use search to find it.");
    }
}
