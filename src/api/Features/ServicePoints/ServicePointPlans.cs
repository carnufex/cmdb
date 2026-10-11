using System.Globalization;
using System.Text.Json;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Plans;
using Cmdb.Catalog;
using Cmdb.Database.Model;
using Npgsql;

namespace Cmdb.Api.Features.ServicePoints;

/// <summary>
/// The plan operations for service points (#292, ADR-0021): <c>create_service_point</c>, and <c>update_service_point</c>
/// that moves one, changes its status or connects it to the site (and port) that serves it. They are checked like every
/// other operation, do not touch the graph, and are written by the apply.
/// </summary>
public static class ServicePointPlans
{
    public static readonly string[] Kinds = ["create_service_point", "update_service_point"];

    public const string Type = "service-point";

    private static readonly JsonSerializerOptions OmitNull = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Payload fields that may hold a planned id the apply replaces.</summary>
    public static string[] IdFields(string kind) => kind switch
    {
        "create_service_point" => ["site"],
        "update_service_point" => ["id", "site"],
        _ => [],
    };

    public static IEnumerable<long> References(PlanOp op) =>
        IdFields(op.Kind).Select(f => op.Payload.TryGetProperty(f, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0).Where(id => id < 0);

    /// <summary>Checks an operation against production, the plan's view and the caller's scopes; the payload, or why not.</summary>
    public static async Task<(string? Payload, string? Error)> CheckAsync(NpgsqlDataSource db, UserScope scope, PlanView view, AddOperationRequest req,
        CancellationToken ct)
    {
        var ops = view.Chain.Operations;
        bool PlannedSite(long id) => ops.Any(o => o.Kind == "create_site" && Planned.ObjectId(o.Id) == id);
        bool PlannedPoint(long id) => ops.Any(o => o.Kind == "create_service_point" && Planned.ObjectId(o.Id) == id);

        if (req.Status is { } status && !ServicePointStatus.All.Contains(status))
        {
            return (null, "status är planned, ordered eller connected.");
        }
        if (req.X is { } px && req.Y is { } py && !await InScopeAsync(db, scope, px, py, ct))
        {
            return (null, "Positionen ligger utanför ditt omfång.");
        }
        // The site and port that serve it: an existing site in the caller's scopes or one the plan creates, and a port on it.
        if (req.SiteId is { } site && (site < 0 ? !PlannedSite(site) : !await PlanSql.ObjectVisibleAsync(db, "site", site, scope, ct)))
        {
            return (null, $"Site {site} finns inte.");
        }
        if (req.TerminalId is { } terminal)
        {
            if (req.SiteId is not { } portSite || portSite < 0)
            {
                return (null, "En port anges tillsammans med sin befintliga site.");
            }
            await using var cmd = db.CreateCommand("SELECT e.site_id FROM port p JOIN equipment e ON e.id = p.equipment_id WHERE p.terminal_id = $1");
            cmd.Parameters.Add(new() { Value = terminal });
            if (await cmd.ExecuteScalarAsync(ct) is not long on || on != portSite)
            {
                return (null, $"Porten {terminal} sitter inte på site {portSite}.");
            }
        }

        if (req.Kind == "create_service_point")
        {
            var code = req.Code!.Trim();
            var type = TypeCatalog.Current.FindServicePointType(req.TypeKey!)!;
            if (ops.Any(o => o.Kind == "create_service_point" && o.Payload.GetProperty("code").GetString() == code) || await CodeTakenAsync(db, code, ct))
            {
                return (null, $"Koden {code} används redan.");
            }
            if ((req.Status ?? ServicePointStatus.Planned) == ServicePointStatus.Connected && req.SiteId is null)
            {
                return (null, "En ansluten anslutningspunkt pekar ut den site som betjänar den.");
            }
            return (JsonSerializer.Serialize(new
            {
                code,
                name = Blank(req.Name),
                address = Blank(req.Address),
                typeKey = type.Key,
                units = req.Units ?? type.Units,
                status = req.Status ?? ServicePointStatus.Planned,
                x = Math.Round(req.X!.Value, 1),
                y = Math.Round(req.Y!.Value, 1),
                externalId = Blank(req.ExternalId),
                site = req.SiteId,
                terminal = req.TerminalId,
            }, OmitNull), null);
        }

        // update_service_point: an existing point in the caller's scopes, or one the plan creates.
        var id = req.ObjectId!.Value;
        if (id < 0 ? !PlannedPoint(id) : !await VisibleAsync(db, scope, id, ct))
        {
            return (null, $"Anslutningspunkt {id} finns inte.");
        }
        if (req.Status == ServicePointStatus.Connected && req.SiteId is null && !req.Disconnect && !await HasSiteAsync(db, id, ops, ct))
        {
            return (null, "En ansluten anslutningspunkt pekar ut den site som betjänar den.");
        }
        return (JsonSerializer.Serialize(new
        {
            id,
            x = req.X is { } x ? Math.Round(x, 1) : (double?)null,
            y = req.Y is { } y ? Math.Round(y, 1) : (double?)null,
            status = req.Status,
            site = req.SiteId,
            terminal = req.TerminalId,
            disconnect = req.Disconnect ? true : (bool?)null,
        }, OmitNull), null);
    }

    /// <summary>The operation in words; <paramref name="name"/> gives a service point's or site's code.</summary>
    public static string Text(PlanOp op, Func<string, long, string> name)
    {
        var p = op.Payload;
        var parts = new List<string>();
        if (p.TryGetProperty("x", out _))
        {
            parts.Add("flytta");
        }
        if (p.TryGetProperty("status", out var s))
        {
            parts.Add($"status {StatusName(s.GetString())}");
        }
        if (p.TryGetProperty("site", out var site))
        {
            parts.Add($"anslut till {name("site", site.GetInt64())}" + (p.TryGetProperty("terminal", out _) ? " (port angiven)" : ""));
        }
        if (p.TryGetProperty("disconnect", out _))
        {
            parts.Add("koppla bort från siten");
        }
        return $"Anslutningspunkt {name(Type, p.GetProperty("id").GetInt64())}: {string.Join(", ", parts)}";
    }

    /// <summary>A create in words, with its type and units.</summary>
    public static string CreateText(JsonElement p)
    {
        var typeKey = p.GetProperty("typeKey").GetString()!;
        var typeName = TypeCatalog.Current.FindServicePointType(typeKey)?.Name ?? typeKey;
        var units = p.GetProperty("units").GetInt32();
        var address = p.TryGetProperty("address", out var a) ? $", {a.GetString()}" : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"Ny anslutningspunkt {p.GetProperty("code").GetString()} ({typeName}{(units > 1 ? $", {units} enheter" : "")}){address}");
    }

    public static string StatusName(string? status) => status switch
    {
        ServicePointStatus.Planned => "planerad",
        ServicePointStatus.Ordered => "beställd",
        ServicePointStatus.Connected => "ansluten",
        _ => status ?? "",
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<bool> InScopeAsync(NpgsqlDataSource db, UserScope scope, double x, double y, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"SELECT {ScopeSql.ServicePoint("ST_SetSRID(ST_MakePoint($1, $2), 3006)", 3)}");
        cmd.Parameters.Add(new() { Value = x });
        cmd.Parameters.Add(new() { Value = y });
        cmd.Parameters.Add(scope.Parameter());
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    internal static async Task<bool> VisibleAsync(NpgsqlDataSource db, UserScope scope, long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"SELECT EXISTS (SELECT 1 FROM service_point p WHERE p.id = $1 AND {ScopeSql.ServicePoint("p.geom", 2)})");
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(scope.Parameter());
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<bool> CodeTakenAsync(NpgsqlDataSource db, string code, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT EXISTS (SELECT 1 FROM service_point WHERE code = $1)");
        cmd.Parameters.Add(new() { Value = code });
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Whether the point has a site already: in production, or from its create or an earlier update in the plan.</summary>
    private static async Task<bool> HasSiteAsync(NpgsqlDataSource db, long id, IReadOnlyList<PlanOp> ops, CancellationToken ct)
    {
        var planned = ops.Where(o => (o.Kind == "create_service_point" && Planned.ObjectId(o.Id) == id)
            || (o.Kind == "update_service_point" && o.Payload.GetProperty("id").GetInt64() == id)).ToList();
        if (planned.Count > 0 && planned[^1].Payload.TryGetProperty("site", out _))
        {
            return true;
        }
        if (id < 0)
        {
            return false;
        }
        await using var cmd = db.CreateCommand("SELECT site_id IS NOT NULL FROM service_point WHERE id = $1");
        cmd.Parameters.Add(new() { Value = id });
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>Codes of service points the caller may see, for describing operations; others are hidden.</summary>
    public static async Task<Dictionary<long, string>> CodesAsync(NpgsqlDataSource db, UserScope scope, long[] ids, CancellationToken ct)
    {
        var codes = new Dictionary<long, string>();
        if (ids.Length == 0)
        {
            return codes;
        }
        await using var cmd = db.CreateCommand($"SELECT p.id, p.code FROM service_point p WHERE p.id = ANY($1) AND {ScopeSql.ServicePoint("p.geom", 2)}");
        cmd.Parameters.Add(new() { Value = ids });
        cmd.Parameters.Add(scope.Parameter());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            codes[reader.GetInt64(0)] = reader.GetString(1);
        }
        return codes;
    }
}
