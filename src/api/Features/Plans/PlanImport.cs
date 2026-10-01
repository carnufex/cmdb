using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Cmdb.Api.Auth;
using Cmdb.Catalog;
using Cmdb.Graph;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Plans;

public sealed class ImportRequest
{
    public long Id { get; set; }

    /// <summary>csv or geojson.</summary>
    public string Format { get; set; } = "csv";
    public string Content { get; set; } = "";

    /// <summary>Check and count without writing anything.</summary>
    public bool DryRun { get; set; }
}

public sealed record ImportProblem(int Row, string Message);

/// <param name="Skipped">Sites already in the plan with the same code: an import can run again.</param>
public sealed record ImportResult(bool DryRun, int Sites, int Equipment, int Cables, int Connections, int Skipped, IReadOnlyList<ImportProblem> Errors,
    int Problems, double ElapsedMs);

/// <summary>
/// Bulk import into a plan (#170): sites (with a template's equipment and cabling, or alone) and cables between them,
/// from CSV or GeoJSON. Everything is checked first and either all of it goes in or none; the operations are written in
/// bulk and the plan's view is built once, so 10 000 sites take seconds, not the hours one-at-a-time adding would.
/// </summary>
/// <remarks>
/// CSV: a header row; columns <c>kind</c> (site or cable), <c>code</c>, <c>name</c>, <c>siteType</c> or <c>template</c>,
/// <c>x</c>/<c>y</c> (SWEREF 99 TM) or <c>lat</c>/<c>lon</c> (WGS 84), and for cables <c>a</c>, <c>b</c> (site codes) and
/// <c>cableType</c>. Comma or semicolon separated. GeoJSON: points are sites, line strings cables (their line is the
/// cable's route), with the same names as properties; coordinates in SWEREF 99 TM, or WGS 84 when they look like it.
/// </remarks>
public sealed class PlanImport(RequestDb db, GraphHolder holder, PlanViews views)
{
    public const int MaxRows = 20_000;

    private sealed record SiteRow(int Row, string Code, string Name, string SiteType, SiteTemplate? Template, double X, double Y, bool Wgs84);

    private sealed record CableRow(int Row, string A, string B, string TypeKey, double[][]? Line, bool Wgs84);

    public async Task<PlanWrite<ImportResult>> ImportAsync(ClaimsPrincipal user, UserScope scope, ImportRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (await views.GetAsync(holder.Require(), req.Id, scope, ct) is not { } view)
        {
            return PlanWrite.Fail<ImportResult>(PlanWriteFailure.NotFound, $"Plan {req.Id} finns inte.");
        }
        if (view.Chain.Plan.Status != "draft")
        {
            return PlanWrite.Fail<ImportResult>(PlanWriteFailure.Conflict, "Bara utkast kan ändras.");
        }

        var errors = new List<ImportProblem>();
        var (sites, cables) = req.Format.Equals("geojson", StringComparison.OrdinalIgnoreCase)
            ? ParseGeoJson(req.Content, errors)
            : ParseCsv(req.Content, errors);
        if (sites.Count + cables.Count > MaxRows)
        {
            return PlanWrite.Fail<ImportResult>(PlanWriteFailure.Invalid, $"Högst {MaxRows} rader per import.");
        }

        await using var conn = await db.OpenConnectionAsync(ct);

        // Positions: WGS 84 to SWEREF 99 TM in one go, then inside the map and the caller's scopes.
        sites = await TransformAsync(conn, sites, ct);
        cables = await TransformLinesAsync(conn, cables, ct);
        foreach (var s in sites.Where(s => s.X is < Map.TileGrid.MinX or > Map.TileGrid.MaxX || s.Y is < Map.TileGrid.MinY or > Map.TileGrid.MaxY))
        {
            errors.Add(new(s.Row, $"{s.Code}: positionen ligger utanför kartan."));
        }
        foreach (var row in await OutsideScopeAsync(conn, scope, sites, ct))
        {
            errors.Add(new(row.Row, $"{row.Code}: positionen ligger utanför ditt omfång."));
        }

        // Codes: unique in the file, new in production; a site the plan has already is skipped, so an import can run again.
        foreach (var dup in sites.GroupBy(s => s.Code, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add(new(dup.ElementAt(1).Row, $"Koden {dup.Key} finns flera gånger i filen."));
        }
        var inProduction = await ExistingCodesAsync(conn, [.. sites.Select(s => s.Code)], ct);
        foreach (var s in sites.Where(s => inProduction.Contains(s.Code)))
        {
            errors.Add(new(s.Row, $"Koden {s.Code} används redan."));
        }
        var planned = view.Chain.Operations.Where(o => o.Kind == "create_site")
            .ToDictionary(o => o.Payload.GetProperty("code").GetString()!, o => Planned.ObjectId(o.Id), StringComparer.Ordinal);
        var skipped = sites.Count(s => planned.ContainsKey(s.Code));
        sites = [.. sites.Where(s => !planned.ContainsKey(s.Code))];

        // Cable ends: a site in the file, one the plan creates, or one in production the caller can see.
        var codes = cables.SelectMany(c => new[] { c.A, c.B }).Distinct(StringComparer.Ordinal)
            .Where(c => !planned.ContainsKey(c) && sites.All(s => s.Code != c)).ToArray();
        var existing = await VisibleSitesAsync(conn, scope, codes, ct);
        foreach (var c in cables)
        {
            foreach (var end in new[] { c.A, c.B })
            {
                if (!planned.ContainsKey(end) && sites.All(s => s.Code != end) && !existing.ContainsKey(end))
                {
                    errors.Add(new(c.Row, $"Kabeländen {end} finns inte."));
                }
            }
        }

        // A cable between the same two sites, of the same type, that the plan has already: skipped like a site.
        long? EndId(string code) => planned.TryGetValue(code, out var id) ? id : existing.TryGetValue(code, out var e) ? e : (long?)null;
        var plannedCables = view.Chain.Operations.Where(o => o.Kind == "create_cable")
            .Select(o => (o.Payload.GetProperty("a").GetInt64(), o.Payload.GetProperty("b").GetInt64(), o.Payload.GetProperty("typeKey").GetString()))
            .ToHashSet();
        bool InPlan(CableRow c) => EndId(c.A) is { } a && EndId(c.B) is { } b
            && (plannedCables.Contains((a, b, c.TypeKey)) || plannedCables.Contains((b, a, c.TypeKey)));
        skipped += cables.Count(InPlan);
        cables = [.. cables.Where(c => !InPlan(c))];

        var equipment = sites.Sum(s => s.Template?.Equipment.Count ?? 0);
        var connections = sites.Sum(s => s.Template?.Connections.Count ?? 0);
        if (errors.Count > 0 || req.DryRun)
        {
            return new(new ImportResult(req.DryRun, sites.Count, equipment, cables.Count, connections, skipped,
                [.. errors.OrderBy(e => e.Row).Take(200)], 0, Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
        }

        // Written in passes, since planned ids come from operation ids: sites, then their equipment, then the template
        // cabling and the cables.
        await using var tx = await conn.BeginTransactionAsync(ct);
        var actor = PlanSql.Actor(user);
        var siteOps = await InsertAsync(conn, tx, req.Id, actor, [.. sites.Select(s => ("create_site", JsonSerializer.Serialize(new
        {
            code = s.Code,
            name = s.Name,
            siteType = s.SiteType,
            x = Math.Round(s.X, 1),
            y = Math.Round(s.Y, 1),
        })))], ct);
        var siteIds = new Dictionary<string, long>(planned, StringComparer.Ordinal);
        for (var i = 0; i < sites.Count; i++)
        {
            siteIds[sites[i].Code] = Planned.ObjectId(siteOps[i]);
        }

        var templated = sites.Where(s => s.Template is not null).ToList();
        var equipmentRows = templated.SelectMany(s => s.Template!.Equipment.Select(e => (Site: s, Equipment: e))).ToList();
        var equipmentOps = await InsertAsync(conn, tx, req.Id, actor, [.. equipmentRows.Select(r => ("create_equipment", JsonSerializer.Serialize(new
        {
            site = siteIds[r.Site.Code],
            typeKey = r.Equipment.TypeKey,
            name = r.Equipment.Name.Replace("{code}", r.Site.Code, StringComparison.Ordinal),
            rack = r.Equipment.Rack,
        })))], ct);
        var equipmentOp = new Dictionary<(string Site, string Ref), long>();
        for (var i = 0; i < equipmentRows.Count; i++)
        {
            equipmentOp[(equipmentRows[i].Site.Code, equipmentRows[i].Equipment.Ref)] = equipmentOps[i];
        }

        long Port(string site, string equipmentRef, string port)
        {
            var e = templated.First(s => s.Code == site).Template!.Equipment.First(x => x.Ref == equipmentRef);
            var position = PortExpansion.Expand(TypeCatalog.Embedded.Find(e.TypeKey)!).Single(p => p.Name == port).Position;
            return Planned.Terminal(equipmentOp[(site, equipmentRef)], position);
        }
        long End(string code) => siteIds.TryGetValue(code, out var id) ? id : existing[code];
        var last = new List<(string, string)>();
        last.AddRange(templated.SelectMany(s => s.Template!.Connections.Select(c => ("connect", JsonSerializer.Serialize(new
        {
            a = Port(s.Code, c.From, c.FromPort),
            b = Port(s.Code, c.To, c.ToPort),
            kind = c.Kind,
        })))));
        last.AddRange(cables.Select(c => ("create_cable", c.Line is null
            ? JsonSerializer.Serialize(new { a = End(c.A), b = End(c.B), typeKey = c.TypeKey })
            : JsonSerializer.Serialize(new { a = End(c.A), b = End(c.B), typeKey = c.TypeKey, line = c.Line }))));
        await InsertAsync(conn, tx, req.Id, actor, last, ct);
        await PlanSql.TouchAsync(conn, tx, req.Id, ct);
        await tx.CommitAsync(ct);

        var after = await views.GetAsync(holder.Require(), req.Id, scope, ct);
        return new(new ImportResult(false, sites.Count, equipment, cables.Count, connections, skipped, [],
            after?.Problems.Count ?? 0, Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
    }

    /// <summary>Operations appended to the plan in order; their ids in the same order.</summary>
    private static async Task<long[]> InsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long plan, string actor,
        List<(string Kind, string Payload)> ops, CancellationToken ct)
    {
        if (ops.Count == 0)
        {
            return [];
        }
        await using var cmd = new NpgsqlCommand("""
            WITH base AS (SELECT coalesce(max(seq), 0) AS s FROM plan_operation WHERE plan_id = $1)
            INSERT INTO plan_operation (plan_id, seq, kind, payload, created_by)
            SELECT $1, base.s + u.n, u.k, u.p::jsonb, $4 FROM base, unnest($2::text[], $3::text[]) WITH ORDINALITY AS u(k, p, n)
            RETURNING id, seq
            """, conn, tx);
        cmd.Parameters.Add(new() { Value = plan });
        cmd.Parameters.Add(new() { Value = ops.Select(o => o.Kind).ToArray() });
        cmd.Parameters.Add(new() { Value = ops.Select(o => o.Payload).ToArray() });
        cmd.Parameters.Add(new() { Value = actor });
        var rows = new List<(long Id, int Seq)>(ops.Count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetInt64(0), reader.GetInt32(1)));
        }
        return [.. rows.OrderBy(r => r.Seq).Select(r => r.Id)];
    }

    private static (List<SiteRow> Sites, List<CableRow> Cables) ParseCsv(string content, List<ImportProblem> errors)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var header = lines.FirstOrDefault(l => l.Trim().Length > 0);
        if (header is null)
        {
            errors.Add(new(1, "Filen är tom."));
            return ([], []);
        }
        var separator = header.Count(c => c == ';') > header.Count(c => c == ',') ? ';' : ',';
        var columns = header.Split(separator).Select(c => c.Trim().Trim('"').ToLowerInvariant()).ToList();
        int Col(string name) => columns.IndexOf(name.ToLowerInvariant());
        var sites = new List<SiteRow>();
        var cables = new List<CableRow>();
        for (var i = Array.IndexOf(lines, header) + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
            {
                continue;
            }
            var cells = lines[i].Split(separator).Select(c => c.Trim().Trim('"')).ToArray();
            string Cell(string name) => Col(name) is var c and >= 0 && c < cells.Length ? cells[c] : "";
            Row(i + 1, Cell("kind"), Cell, sites, cables, errors);
        }
        return (sites, cables);
    }

    private static (List<SiteRow> Sites, List<CableRow> Cables) ParseGeoJson(string content, List<ImportProblem> errors)
    {
        var sites = new List<SiteRow>();
        var cables = new List<CableRow>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(content);
        }
        catch (JsonException e)
        {
            errors.Add(new(1, $"Ogiltig GeoJSON: {e.Message}"));
            return (sites, cables);
        }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new(1, "GeoJSON ska vara en FeatureCollection."));
                return (sites, cables);
            }
            var n = 0;
            foreach (var f in features.EnumerateArray())
            {
                n++;
                var props = f.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
                string Prop(string name) => props.ValueKind == JsonValueKind.Object && props.TryGetProperty(name, out var v)
                    ? v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText() : "";
                var geometry = f.TryGetProperty("geometry", out var g) ? g : default;
                var type = geometry.ValueKind == JsonValueKind.Object && geometry.TryGetProperty("type", out var t) ? t.GetString() : null;
                var coords = geometry.ValueKind == JsonValueKind.Object && geometry.TryGetProperty("coordinates", out var c) ? c : default;
                if (type == "Point")
                {
                    var (x, y) = (coords[0].GetDouble(), coords[1].GetDouble());
                    string Cell(string name) => name switch
                    {
                        "x" => x.ToString(CultureInfo.InvariantCulture),
                        "y" => y.ToString(CultureInfo.InvariantCulture),
                        _ => Prop(name),
                    };
                    Row(n, "site", Cell, sites, cables, errors);
                }
                else if (type == "LineString")
                {
                    var line = coords.EnumerateArray().Select(pt => new[] { pt[0].GetDouble(), pt[1].GetDouble() }).ToArray();
                    var before = cables.Count;
                    Row(n, "cable", Prop, sites, cables, errors);
                    if (cables.Count > before)
                    {
                        cables[^1] = cables[^1] with { Line = line, Wgs84 = LooksLikeWgs84(line[0][0], line[0][1]) };
                    }
                }
                else
                {
                    errors.Add(new(n, $"Geometrin {type ?? "saknas"} stöds inte: punkter blir siter och linjer kablar."));
                }
            }
        }
        return (sites, cables);
    }

    /// <summary>One row, from a CSV line or a feature's properties.</summary>
    private static void Row(int row, string kind, Func<string, string> cell, List<SiteRow> sites, List<CableRow> cables, List<ImportProblem> errors)
    {
        kind = kind.Trim().ToLowerInvariant();
        if (kind is "" or "site")
        {
            var code = cell("code").Trim();
            var name = cell("name").Trim();
            var templateKey = cell("template").Trim();
            var template = templateKey.Length > 0 ? SiteTemplates.Embedded.Find(templateKey) : null;
            var siteType = template?.SiteType ?? cell("siteType").Trim();
            if (code.Length is 0 or > 50 || name.Length is 0 or > 200)
            {
                errors.Add(new(row, "code (högst 50 tecken) och name (högst 200) krävs."));
                return;
            }
            if (templateKey.Length > 0 && template is null)
            {
                errors.Add(new(row, $"{code}: mallen {templateKey} finns inte."));
                return;
            }
            if (!PlanKinds.SiteTypes.Contains(siteType))
            {
                errors.Add(new(row, $"{code}: siteType är hub, aggregation, radio, cabinet eller splice (eller ange template)."));
                return;
            }
            var (x, y, wgs84) = Number(cell("x")) is { } sx && Number(cell("y")) is { } sy ? (sx, sy, LooksLikeWgs84(sx, sy))
                : Number(cell("lon")) is { } lon && Number(cell("lat")) is { } lat ? (lon, lat, true)
                : (double.NaN, double.NaN, false);
            if (double.IsNaN(x))
            {
                errors.Add(new(row, $"{code}: ange x och y (SWEREF 99 TM) eller lat och lon (WGS 84)."));
                return;
            }
            sites.Add(new SiteRow(row, code, name, siteType, template, x, y, wgs84));
        }
        else if (kind == "cable")
        {
            var (a, b, typeKey) = (cell("a").Trim(), cell("b").Trim(), cell("cableType").Trim());
            if (a.Length == 0 || b.Length == 0 || a == b)
            {
                errors.Add(new(row, "En kabel behöver a och b: två olika sitekoder."));
                return;
            }
            if (TypeCatalog.Embedded.CableTypes.All(t => t.Key != typeKey))
            {
                errors.Add(new(row, $"Kabeltypen {typeKey} finns inte: {string.Join(", ", TypeCatalog.Embedded.CableTypes.Select(t => t.Key))}."));
                return;
            }
            cables.Add(new CableRow(row, a, b, typeKey, null, false));
        }
        else
        {
            errors.Add(new(row, $"kind är site eller cable, inte {kind}."));
        }
    }

    private static double? Number(string text) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>SWEREF 99 TM is in metres, hundreds of thousands; WGS 84 in degrees.</summary>
    private static bool LooksLikeWgs84(double x, double y) => Math.Abs(x) <= 180 && Math.Abs(y) <= 90;

    private static async Task<List<SiteRow>> TransformAsync(NpgsqlConnection conn, List<SiteRow> sites, CancellationToken ct)
    {
        var wgs = sites.Where(s => s.Wgs84).ToList();
        if (wgs.Count == 0)
        {
            return sites;
        }
        await using var cmd = new NpgsqlCommand("""
            SELECT ST_X(p), ST_Y(p) FROM unnest($1::float8[], $2::float8[]) WITH ORDINALITY AS u(x, y, n),
                 LATERAL (SELECT ST_Transform(ST_SetSRID(ST_MakePoint(u.x, u.y), 4326), 3006) AS p) t ORDER BY u.n
            """, conn);
        cmd.Parameters.Add(new() { Value = wgs.Select(s => s.X).ToArray() });
        cmd.Parameters.Add(new() { Value = wgs.Select(s => s.Y).ToArray() });
        var moved = new Dictionary<SiteRow, SiteRow>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            var i = 0;
            while (await reader.ReadAsync(ct))
            {
                moved[wgs[i]] = wgs[i] with { X = reader.GetDouble(0), Y = reader.GetDouble(1), Wgs84 = false };
                i++;
            }
        }
        return [.. sites.Select(s => moved.GetValueOrDefault(s, s))];
    }

    private static async Task<List<CableRow>> TransformLinesAsync(NpgsqlConnection conn, List<CableRow> cables, CancellationToken ct)
    {
        var result = new List<CableRow>(cables.Count);
        foreach (var c in cables)
        {
            if (c.Line is null || !c.Wgs84)
            {
                result.Add(c);
                continue;
            }
            await using var cmd = new NpgsqlCommand("""
                SELECT ST_X(p), ST_Y(p) FROM unnest($1::float8[], $2::float8[]) WITH ORDINALITY AS u(x, y, n),
                     LATERAL (SELECT ST_Transform(ST_SetSRID(ST_MakePoint(u.x, u.y), 4326), 3006) AS p) t ORDER BY u.n
                """, conn);
            cmd.Parameters.Add(new() { Value = c.Line.Select(p => p[0]).ToArray() });
            cmd.Parameters.Add(new() { Value = c.Line.Select(p => p[1]).ToArray() });
            var line = new List<double[]>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    line.Add([Math.Round(reader.GetDouble(0), 1), Math.Round(reader.GetDouble(1), 1)]);
                }
            }
            result.Add(c with { Line = [.. line], Wgs84 = false });
        }
        return result;
    }

    private static async Task<List<SiteRow>> OutsideScopeAsync(NpgsqlConnection conn, UserScope scope, List<SiteRow> sites, CancellationToken ct)
    {
        if (scope.Unrestricted || sites.Count == 0)
        {
            return [];
        }
        await using var cmd = new NpgsqlCommand("""
            SELECT u.n FROM unnest($2::float8[], $3::float8[], $4::text[]) WITH ORDINALITY AS u(x, y, t, n)
            WHERE NOT EXISTS (
                SELECT 1 FROM access_scope a
                WHERE a.key = ANY($1)
                  AND (a.area IS NULL OR ST_Intersects(a.area, ST_SetSRID(ST_MakePoint(u.x, u.y), 3006)))
                  AND (cardinality(a.site_types) = 0 OR u.t = ANY(a.site_types)))
            """, conn);
        cmd.Parameters.Add(new() { Value = scope.Keys });
        cmd.Parameters.Add(new() { Value = sites.Select(s => s.X).ToArray() });
        cmd.Parameters.Add(new() { Value = sites.Select(s => s.Y).ToArray() });
        cmd.Parameters.Add(new() { Value = sites.Select(s => s.SiteType).ToArray() });
        var outside = new List<SiteRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            outside.Add(sites[(int)reader.GetInt64(0) - 1]);
        }
        return outside;
    }

    private static async Task<HashSet<string>> ExistingCodesAsync(NpgsqlConnection conn, string[] codes, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT code FROM site WHERE code = ANY($1)", conn);
        cmd.Parameters.Add(new() { Value = codes });
        var found = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            found.Add(reader.GetString(0));
        }
        return found;
    }

    private static async Task<Dictionary<string, long>> VisibleSitesAsync(NpgsqlConnection conn, UserScope scope, string[] codes, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT s.code, s.id FROM site s WHERE s.code = ANY($1) AND {ScopeSql.Site("s.id", 2)}", conn);
        cmd.Parameters.Add(new() { Value = codes });
        cmd.Parameters.Add(scope.Parameter());
        var found = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            found[reader.GetString(0)] = reader.GetInt64(1);
        }
        return found;
    }
}

/// <summary>Imports sites and cables from CSV or GeoJSON into a draft plan (#170); <c>dryRun</c> only checks.</summary>
public sealed class ImportEndpoint(PlanImport import) : Endpoint<ImportRequest, ImportResult>
{
    public override void Configure()
    {
        Post("/plans/{id}/import");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(ImportRequest req, CancellationToken ct)
    {
        var result = await import.ImportAsync(User, HttpContext.Scope(), req, ct);
        switch (result.Failure)
        {
            case PlanWriteFailure.NotFound:
                await Send.NotFoundAsync(ct);
                return;
            case PlanWriteFailure.Conflict:
                await PlanSql.ConflictAsync(HttpContext, result.Error!, ct);
                return;
            case PlanWriteFailure.Invalid:
                AddError(result.Error!);
                await Send.ErrorsAsync(cancellation: ct);
                return;
            default:
                // Row problems are part of the answer, not a failure: the person fixes the file and tries again.
                await Send.OkAsync(result.Value!, ct);
                return;
        }
    }
}
