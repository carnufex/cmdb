using System.Diagnostics;
using System.Globalization;
using System.Text;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Grid;
using Cmdb.Api.Features.Objects;
using Cmdb.Graph;
using FastEndpoints;
using FluentValidation;

namespace Cmdb.Api.Features.Selection;

/// <summary>A selection (#253): sites and cables picked with a lasso, advanced search or the tree.</summary>
public sealed class SelectionRequest
{
    public long[] SiteIds { get; set; } = [];
    public long[] CableIds { get; set; } = [];
}

public sealed class SelectionValidator : Validator<SelectionRequest>
{
    public SelectionValidator()
    {
        RuleFor(r => r.SiteIds.Length + r.CableIds.Length).InclusiveBetween(1, 2 * GridEndpoint.MaxSites)
            .WithMessage($"Give 1–{GridEndpoint.MaxSites} sites and at most {GridEndpoint.MaxSites} cables.");
        RuleFor(r => r.SiteIds.Length).LessThanOrEqualTo(GridEndpoint.MaxSites);
        RuleFor(r => r.CableIds.Length).LessThanOrEqualTo(GridEndpoint.MaxSites);
    }
}

/// <summary>
/// What fails if everything in the selection fails at once (#253): every circuit through a port at its sites or an end
/// of its cables, what rides on those, and the services, as for a single object. What the caller cannot see affects
/// nothing as far as they can tell (#22).
/// </summary>
public sealed class SelectionImpactEndpoint(GraphHolder holder, RequestDb db, ScopeMasks masks) : Endpoint<SelectionRequest, Impact>
{
    public override void Configure() => Post("/selection/impact");

    public override async Task HandleAsync(SelectionRequest req, CancellationToken ct)
    {
        if (holder.Current is not { } g)
        {
            await Send.ResultAsync(TypedResults.Problem("The network graph is still loading; try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable));
            return;
        }
        var sw = Stopwatch.StartNew();
        var mask = await masks.GetAsync(g, HttpContext.Scope(), ct);
        var nodes = new List<int>();
        foreach (var id in req.SiteIds.Distinct())
        {
            if (g.TryGetSite(id, out var site) && mask.SiteVisible(site))
            {
                foreach (var equipment in g.EquipmentAt(site))
                {
                    foreach (var port in g.PortsOf(equipment))
                    {
                        nodes.Add(port);
                    }
                }
            }
        }
        foreach (var id in req.CableIds.Distinct())
        {
            if (g.TryGetCable(id, out var cable) && mask.CableVisible(cable))
            {
                foreach (var end in g.EndsOf(cable))
                {
                    nodes.Add(end);
                }
            }
        }
        await Send.OkAsync(await ImpactEndpoint.FromResultAsync(g, mask, db, GraphImpact.OfNodes(g, [.. nodes]), sw, ct), ct);
    }
}

/// <summary>
/// The selection as CSV for Excel (#253): one row per site and cable, separated by semicolons with a byte order mark.
/// Only what the caller's scopes show: objects outside them are left out, hidden attributes and, when positions are
/// hidden, the coordinates too.
/// </summary>
public sealed class SelectionExportEndpoint(RequestDb db) : Endpoint<SelectionRequest>
{
    public override void Configure() => Post("/selection/export");

    public override async Task HandleAsync(SelectionRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        var csv = new StringBuilder();
        csv.Append('﻿').AppendLine("Typ;Kod;Namn;Sort;Livscykel;X;Y;Attribut");
        await using (var cmd = db.CreateCommand($"""
            SELECT s.code, s.name, s.site_type, s.lifecycle::text, ST_X(ST_PointOnSurface(s.geom)), ST_Y(ST_PointOnSurface(s.geom)), s.attributes::text
            FROM site s WHERE s.id = ANY($1) AND {ScopeSql.Site("s.id", 2)} ORDER BY s.code
            """))
        {
            cmd.Parameters.Add(new() { Value = req.SiteIds });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                Row(csv, "site", reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    scope.HidesCoordinates ? null : reader.GetDouble(4), scope.HidesCoordinates ? null : reader.GetDouble(5),
                    scope.MaskAttributes(reader.GetString(6)));
            }
        }
        await using (var cmd = db.CreateCommand($"""
            SELECT c.code, coalesce(a.code, '') || ' – ' || coalesce(b.code, ''), ct.key, c.lifecycle::text, c.attributes::text
            FROM cable c JOIN cable_type ct ON ct.id = c.cable_type_id
            LEFT JOIN site a ON a.id = c.a_site_id AND {ScopeSql.Site("a.id", 2)}
            LEFT JOIN site b ON b.id = c.b_site_id AND {ScopeSql.Site("b.id", 2)}
            WHERE c.id = ANY($1) AND {ScopeSql.Cable("c.id", 2)} ORDER BY c.code
            """))
        {
            cmd.Parameters.Add(new() { Value = req.CableIds });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                Row(csv, "kabel", reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), null, null,
                    scope.MaskAttributes(reader.GetString(4)));
            }
        }
        HttpContext.Response.Headers.ContentDisposition = "attachment; filename=\"urval.csv\"";
        await Send.BytesAsync(Encoding.UTF8.GetBytes(csv.ToString()), contentType: "text/csv; charset=utf-8", cancellation: ct);
    }

    private static void Row(StringBuilder csv, string type, string code, string name, string kind, string lifecycle, double? x, double? y,
        string attributes)
    {
        string Field(string value) => value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
        string Number(double? value) => value is { } v ? Math.Round(v, 1).ToString(CultureInfo.InvariantCulture) : "";
        csv.AppendJoin(';', [Field(type == "site" ? "Site" : "Kabel"), Field(code), Field(name), Field(kind), Field(lifecycle), Number(x), Number(y),
            Field(attributes == "{}" ? "" : attributes)]).AppendLine();
    }
}
