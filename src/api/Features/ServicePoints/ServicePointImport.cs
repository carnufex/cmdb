using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Plans;
using Cmdb.Catalog;
using Cmdb.Database.Model;
using Cmdb.Graph;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.ServicePoints;

/// <param name="Units">Homes and premises behind the imported points.</param>
/// <param name="Skipped">Points the plan has already, by code: an import can run again.</param>
public sealed record ServicePointImportResult(bool DryRun, int ServicePoints, int Units, int Skipped, IReadOnlyList<ImportProblem> Errors, double ElapsedMs);

/// <summary>
/// Service points into a plan (#292), from CSV or GeoJSON, all or none, as <c>create_service_point</c> operations written in
/// one go. CSV: a header row with <c>code</c>, <c>type</c> and <c>x</c>/<c>y</c> (SWEREF 99 TM) or <c>lat</c>/<c>lon</c>
/// (WGS 84), and optionally <c>name</c>, <c>address</c>, <c>units</c>, <c>status</c> and <c>externalId</c>; comma or semicolon
/// separated. GeoJSON: point features with the same properties; coordinates in SWEREF 99 TM, or WGS 84 when they look like it.
/// </summary>
public sealed class ServicePointImport(RequestDb db, GraphHolder holder, PlanViews views)
{
    public const int MaxRows = 20_000;

    private static readonly JsonSerializerOptions Payload = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private sealed record Row(int Line, string Code, string? Name, string? Address, string TypeKey, int? Units, string Status, double X, double Y,
        bool Wgs84, string? ExternalId);

    public async Task<PlanWrite<ServicePointImportResult>> ImportAsync(ClaimsPrincipal user, UserScope scope, ImportRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (await views.GetAsync(holder.Require(), req.Id, scope, ct) is not { } view)
        {
            return PlanWrite.Fail<ServicePointImportResult>(PlanWriteFailure.NotFound, $"Plan {req.Id} finns inte.");
        }
        if (view.Chain.Plan.Status != "draft")
        {
            return PlanWrite.Fail<ServicePointImportResult>(PlanWriteFailure.Conflict, "Bara utkast kan ändras.");
        }
        var errors = new List<ImportProblem>();
        var rows = req.Format.Equals("geojson", StringComparison.OrdinalIgnoreCase) ? ParseGeoJson(req.Content, errors) : ParseCsv(req.Content, errors);
        if (rows.Count > MaxRows)
        {
            return PlanWrite.Fail<ServicePointImportResult>(PlanWriteFailure.Invalid, $"Högst {MaxRows} anslutningspunkter per import.");
        }

        await using var conn = await db.OpenConnectionAsync(ct);
        rows = await TransformAsync(conn, rows, ct);
        foreach (var r in rows.Where(r => r.X is < Map.TileGrid.MinX or > Map.TileGrid.MaxX || r.Y is < Map.TileGrid.MinY or > Map.TileGrid.MaxY))
        {
            errors.Add(new(r.Line, $"{r.Code}: positionen ligger utanför kartan."));
        }
        foreach (var r in await OutsideScopeAsync(conn, scope, rows, ct))
        {
            errors.Add(new(r.Line, $"{r.Code}: positionen ligger utanför ditt omfång."));
        }
        foreach (var dup in rows.GroupBy(r => r.Code, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add(new(dup.ElementAt(1).Line, $"Koden {dup.Key} finns flera gånger i filen."));
        }
        var taken = await TakenCodesAsync(conn, [.. rows.Select(r => r.Code)], ct);
        foreach (var r in rows.Where(r => taken.Contains(r.Code)))
        {
            errors.Add(new(r.Line, $"Koden {r.Code} används redan."));
        }
        var inPlan = view.Chain.Operations.Where(o => o.Kind == "create_service_point").Select(o => o.Payload.GetProperty("code").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var skipped = rows.Count(r => inPlan.Contains(r.Code));
        rows = [.. rows.Where(r => !inPlan.Contains(r.Code))];
        var units = rows.Sum(r => r.Units ?? TypeCatalog.Current.FindServicePointType(r.TypeKey)?.Units ?? 1);
        if (errors.Count > 0 || req.DryRun || rows.Count == 0)
        {
            return new(new ServicePointImportResult(req.DryRun, rows.Count, units, skipped, [.. errors.OrderBy(e => e.Row).Take(200)],
                Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        await Operations.Operations.BeginAsync(conn, tx, user, "import_service_points", new { plan = req.Id, servicePoints = rows.Count }, null, ct);
        await PlanImport.InsertAsync(conn, tx, req.Id, PlanSql.Actor(user), [.. rows.Select(r => ("create_service_point", JsonSerializer.Serialize(new
        {
            code = r.Code,
            name = r.Name,
            address = r.Address,
            typeKey = r.TypeKey,
            units = r.Units ?? TypeCatalog.Current.FindServicePointType(r.TypeKey)!.Units,
            status = r.Status,
            x = Math.Round(r.X, 1),
            y = Math.Round(r.Y, 1),
            externalId = r.ExternalId,
        }, Payload)))], ct);
        await PlanSql.TouchAsync(conn, tx, req.Id, ct);
        await tx.CommitAsync(ct);
        return new(new ServicePointImportResult(false, rows.Count, units, skipped, [], Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
    }

    private static Row? Check(int line, string? code, string? name, string? address, string? type, string? units, string? status, double? x, double? y,
        bool wgs84, string? externalId, List<ImportProblem> errors)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 50)
        {
            problems.Add("code saknas eller är längre än 50 tecken");
        }
        if (type is null || TypeCatalog.Current.FindServicePointType(type.Trim()) is null)
        {
            problems.Add($"type är en av {string.Join(", ", TypeCatalog.Current.ServicePointTypes.Select(t => t.Key))}");
        }
        int? count = null;
        if (!string.IsNullOrWhiteSpace(units))
        {
            if (int.TryParse(units, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 10_000)
            {
                count = n;
            }
            else
            {
                problems.Add("units är ett heltal 1–10 000");
            }
        }
        var state = string.IsNullOrWhiteSpace(status) ? ServicePointStatus.Planned : status.Trim();
        if (state is not (ServicePointStatus.Planned or ServicePointStatus.Ordered))
        {
            problems.Add("status är planned eller ordered; en ansluten punkt kopplas till sin site i planen");
        }
        if (x is null || y is null)
        {
            problems.Add("x/y eller lat/lon saknas");
        }
        if ((address?.Length ?? 0) > 300 || (name?.Length ?? 0) > 200 || (externalId?.Length ?? 0) > 100)
        {
            problems.Add("address, name eller externalId är för lång");
        }
        if (problems.Count > 0)
        {
            errors.Add(new(line, $"{code}: {string.Join("; ", problems)}."));
            return null;
        }
        static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        return new Row(line, code!.Trim(), Blank(name), Blank(address), type!.Trim(), count, state, x!.Value, y!.Value, wgs84, Blank(externalId));
    }

    private static List<Row> ParseCsv(string content, List<ImportProblem> errors)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var headerIndex = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (headerIndex < 0)
        {
            errors.Add(new(0, "Filen är tom."));
            return [];
        }
        var separator = lines[headerIndex].Contains(';', StringComparison.Ordinal) ? ';' : ',';
        var header = lines[headerIndex].Split(separator).Select(h => h.Trim().ToLowerInvariant()).ToArray();
        int Column(string name) => Array.IndexOf(header, name.ToLowerInvariant());
        var rows = new List<Row>();
        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
            {
                continue;
            }
            var cells = lines[i].Split(separator);
            string? Cell(string name) => Column(name) is var c and >= 0 && c < cells.Length ? cells[c].Trim().Trim('"') : null;
            double? Number(string name) => double.TryParse(Cell(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            // Per row: lat/lon when they are given, otherwise x/y.
            var wgs84 = Number("lat") is not null && Number("lon") is not null;
            var (x, y) = wgs84 ? (Number("lon"), Number("lat")) : (Number("x"), Number("y"));
            if (Check(i + 1, Cell("code"), Cell("name"), Cell("address"), Cell("type"), Cell("units"), Cell("status"), x, y, wgs84, Cell("externalId"), errors)
                is { } row)
            {
                rows.Add(row);
            }
        }
        return rows;
    }

    private static List<Row> ParseGeoJson(string content, List<ImportProblem> errors)
    {
        var rows = new List<Row>();
        try
        {
            using var doc = JsonDocument.Parse(content);
            var features = doc.RootElement.TryGetProperty("features", out var f) ? f.EnumerateArray().ToList() : [doc.RootElement];
            for (var i = 0; i < features.Count; i++)
            {
                var feature = features[i];
                if (!feature.TryGetProperty("geometry", out var g) || g.GetProperty("type").GetString() != "Point")
                {
                    errors.Add(new(i + 1, "Bara punkter kan vara anslutningspunkter."));
                    continue;
                }
                var c = g.GetProperty("coordinates");
                var (x, y) = (c[0].GetDouble(), c[1].GetDouble());
                var p = feature.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object ? props : default;
                string? Prop(string name) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v)
                    ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText() : null;
                if (Check(i + 1, Prop("code"), Prop("name"), Prop("address"), Prop("type"), Prop("units"), Prop("status"), x, y,
                    Math.Abs(x) <= 180 && Math.Abs(y) <= 90, Prop("externalId"), errors) is { } row)
                {
                    rows.Add(row);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        {
            errors.Add(new(0, "Filen är inte giltig GeoJSON."));
        }
        return rows;
    }

    /// <summary>WGS 84 positions to SWEREF 99 TM, in one round trip.</summary>
    private static async Task<List<Row>> TransformAsync(NpgsqlConnection conn, List<Row> rows, CancellationToken ct)
    {
        var wgs = rows.Where(r => r.Wgs84).ToList();
        if (wgs.Count == 0)
        {
            return rows;
        }
        await using var cmd = new NpgsqlCommand("""
            SELECT ST_X(t), ST_Y(t) FROM unnest($1::float8[], $2::float8[]) WITH ORDINALITY AS u(lon, lat, n),
                 LATERAL ST_Transform(ST_SetSRID(ST_MakePoint(lon, lat), 4326), 3006) AS t ORDER BY n
            """, conn);
        cmd.Parameters.Add(new() { Value = wgs.Select(r => r.X).ToArray() });
        cmd.Parameters.Add(new() { Value = wgs.Select(r => r.Y).ToArray() });
        var transformed = new List<(double, double)>(wgs.Count);
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                transformed.Add((reader.GetDouble(0), reader.GetDouble(1)));
            }
        }
        var map = wgs.Select((r, i) => (r, transformed[i])).ToDictionary(t => t.r, t => t.Item2);
        return [.. rows.Select(r => map.TryGetValue(r, out var p) ? r with { X = p.Item1, Y = p.Item2, Wgs84 = false } : r)];
    }

    private static async Task<List<Row>> OutsideScopeAsync(NpgsqlConnection conn, UserScope scope, List<Row> rows, CancellationToken ct)
    {
        if (scope.Unrestricted || rows.Count == 0)
        {
            return [];
        }
        await using var cmd = new NpgsqlCommand($"""
            SELECT n FROM unnest($1::float8[], $2::float8[]) WITH ORDINALITY AS u(x, y, n)
            WHERE NOT {ScopeSql.ServicePoint("ST_SetSRID(ST_MakePoint(u.x, u.y), 3006)", 3)}
            """, conn);
        cmd.Parameters.Add(new() { Value = rows.Select(r => r.X).ToArray() });
        cmd.Parameters.Add(new() { Value = rows.Select(r => r.Y).ToArray() });
        cmd.Parameters.Add(scope.Parameter());
        var outside = new List<Row>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            outside.Add(rows[(int)reader.GetInt64(0) - 1]);
        }
        return outside;
    }

    private static async Task<HashSet<string>> TakenCodesAsync(NpgsqlConnection conn, string[] codes, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT code FROM service_point WHERE code = ANY($1)", conn);
        cmd.Parameters.Add(new() { Value = codes });
        var taken = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            taken.Add(reader.GetString(0));
        }
        return taken;
    }
}

/// <summary>POST service points into a plan (#292): <c>dryRun</c> checks and counts without writing.</summary>
public sealed class ServicePointImportEndpoint(ServicePointImport import) : Endpoint<ImportRequest, ServicePointImportResult>
{
    public override void Configure()
    {
        Post("/plans/{id}/service-points/import");
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
                await Send.ResultAsync(TypedResults.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest));
                return;
            default:
                await Send.OkAsync(result.Value!, ct);
                return;
        }
    }
}
