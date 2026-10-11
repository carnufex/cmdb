using System.Globalization;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Objects;
using Cmdb.Catalog;
using FastEndpoints;
using FluentValidation;
using Npgsql;

namespace Cmdb.Api.Features.ServicePoints;

/// <summary>A service point (#292) as the panel and agents see it. Position only when the caller's scopes show positions.</summary>
public sealed record ServicePointDetail(long Id, string Code, string? Name, string? Address, string TypeKey, string TypeName, int Units,
    string Status, double? X, double? Y, ObjectRef? Site, ServicePointPort? Port, string? SourceSystem, string? ExternalId, string Attributes,
    DateTimeOffset UpdatedAt);

/// <summary>The port that serves a service point: the equipment and the port's name.</summary>
public sealed record ServicePointPort(long TerminalId, ObjectRef Equipment, string Name);

public sealed record ServicePointRow(long Id, string Code, string? Address, string TypeKey, int Units, string Status, double? X, double? Y, long? SiteId);

public sealed record ServicePointList(IReadOnlyList<ServicePointRow> Items, int Total, IReadOnlyDictionary<string, int> ByStatus, int Units);

public sealed record ServicePointRequest(long Id);

public sealed class ServicePointListRequest
{
    /// <summary>"minx,miny,maxx,maxy" in SWEREF 99 TM.</summary>
    [QueryParam]
    public string? Bbox { get; set; }

    /// <summary>Points within this many metres of <see cref="Near"/>.</summary>
    [QueryParam]
    public double? Radius { get; set; }

    /// <summary>"x,y" in SWEREF 99 TM, with <see cref="Radius"/>.</summary>
    [QueryParam]
    public string? Near { get; set; }

    [QueryParam]
    public string? Status { get; set; }

    /// <summary>Only points served by this site.</summary>
    [QueryParam]
    public long? Site { get; set; }

    [QueryParam]
    public int Limit { get; set; } = 500;
}

public sealed class ServicePointListValidator : Validator<ServicePointListRequest>
{
    public ServicePointListValidator()
    {
        RuleFor(r => r.Limit).InclusiveBetween(1, 5000);
        RuleFor(r => r.Status).Must(s => s is null || Database.Model.ServicePointStatus.All.Contains(s)).WithMessage("status is planned, ordered or connected.");
        RuleFor(r => r.Bbox).Must(b => b is null || ServicePointQueries.Numbers(b, 4) is not null).WithMessage("bbox is minx,miny,maxx,maxy.");
        RuleFor(r => r.Near).Must(n => n is null || ServicePointQueries.Numbers(n, 2) is not null).WithMessage("near is x,y.");
        RuleFor(r => r).Must(r => (r.Near is null) == (r.Radius is null)).WithMessage("near and radius go together.");
        RuleFor(r => r.Radius).InclusiveBetween(1, 50_000).When(r => r.Radius is not null);
    }
}

/// <summary>GET a service point: its type, status, units and the site and port that serve it.</summary>
public sealed class GetServicePointEndpoint(RequestDb db) : Endpoint<ServicePointRequest, ServicePointDetail>
{
    public override void Configure() => Get("/service-points/{id}");

    public override async Task HandleAsync(ServicePointRequest req, CancellationToken ct)
    {
        if (await ServicePointQueries.LoadAsync(db, req.Id, HttpContext.Scope(), ct) is not { } point)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(point, ct);
    }
}

/// <summary>Service points in an area, near a point or served by a site, with counts per status and the units in all.</summary>
public sealed class ListServicePointsEndpoint(RequestDb db) : Endpoint<ServicePointListRequest, ServicePointList>
{
    public override void Configure() => Get("/service-points");

    public override async Task HandleAsync(ServicePointListRequest req, CancellationToken ct) =>
        await Send.OkAsync(await ServicePointQueries.ListAsync(db, req, HttpContext.Scope(), ct), ct);
}

internal static class ServicePointQueries
{
    public static double[]? Numbers(string value, int count)
    {
        var parts = value.Split(',');
        if (parts.Length != count)
        {
            return null;
        }
        var numbers = new double[count];
        for (var i = 0; i < count; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }
        return numbers;
    }

    /// <summary>Null when the point does not exist or is outside the caller's scopes.</summary>
    public static async Task<ServicePointDetail?> LoadAsync(NpgsqlDataSource db, long id, UserScope scope, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT p.code, p.name, p.address, p.type_key, p.units, p.status, ST_X(p.geom), ST_Y(p.geom),
                   s.id, s.code, s.name, s.lifecycle::text, {ScopeSql.Site("s.id", 2)},
                   p.terminal_id, e.id, e.name, e.lifecycle::text, pt.name,
                   p.source_system, p.external_id, p.attributes::text, p.updated_at
            FROM service_point p
            LEFT JOIN site s ON s.id = p.site_id
            LEFT JOIN port pt ON pt.terminal_id = p.terminal_id
            LEFT JOIN equipment e ON e.id = pt.equipment_id
            WHERE p.id = $1 AND {ScopeSql.ServicePoint("p.geom", 2)}
            """);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(scope.Parameter());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        var typeKey = reader.GetString(3);
        var siteVisible = !reader.IsDBNull(8) && reader.GetBoolean(12);
        var site = reader.IsDBNull(8) ? null
            : siteVisible ? new ObjectRef("site", reader.GetInt64(8), reader.GetString(9), reader.GetString(10), reader.GetString(11))
            : ObjectRef.Hidden("site");
        var port = reader.IsDBNull(13) || !siteVisible ? null
            : new ServicePointPort(reader.GetInt64(13), new ObjectRef("equipment", reader.GetInt64(14), reader.GetString(15), null, reader.GetString(16)), reader.GetString(17));
        var hide = scope.HidesCoordinates;
        return new ServicePointDetail(id, reader.GetString(0), Text(1), Text(2), typeKey,
            TypeCatalog.Current.FindServicePointType(typeKey)?.Name ?? typeKey, reader.GetInt32(4), reader.GetString(5),
            hide ? null : Math.Round(reader.GetDouble(6), 1), hide ? null : Math.Round(reader.GetDouble(7), 1), site, port, Text(18), Text(19),
            scope.MaskAttributes(reader.GetString(20)), reader.GetFieldValue<DateTimeOffset>(21));
    }

    public static async Task<ServicePointList> ListAsync(NpgsqlDataSource db, ServicePointListRequest req, UserScope scope, CancellationToken ct)
    {
        var bbox = req.Bbox is null ? null : Numbers(req.Bbox, 4);
        var near = req.Near is null ? null : Numbers(req.Near, 2);
        // Positions the caller may not see cannot be searched by either (#22).
        if (scope.HidesCoordinates && (bbox is not null || near is not null))
        {
            return new ServicePointList([], 0, new Dictionary<string, int>(), 0);
        }
        var filter = $"""
            FROM service_point p
            WHERE {ScopeSql.ServicePoint("p.geom", 1)}
              AND ($2::float8 IS NULL OR p.geom && ST_MakeEnvelope($2, $3, $4, $5, 3006))
              AND ($6::float8 IS NULL OR ST_DWithin(p.geom, ST_SetSRID(ST_MakePoint($6, $7), 3006), $8))
              AND ($9::text IS NULL OR p.status = $9)
              AND ($10::bigint IS NULL OR p.site_id = $10)
            """;
        void Parameters(NpgsqlCommand cmd)
        {
            cmd.Parameters.Add(scope.Parameter());
            foreach (var v in new double?[] { bbox?[0], bbox?[1], bbox?[2], bbox?[3], near?[0], near?[1], req.Radius })
            {
                cmd.Parameters.Add(new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double });
            }
            cmd.Parameters.Add(new() { Value = (object?)req.Status ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            cmd.Parameters.Add(new() { Value = (object?)req.Site ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
        }
        var items = new List<ServicePointRow>();
        await using (var cmd = db.CreateCommand($"SELECT p.id, p.code, p.address, p.type_key, p.units, p.status, ST_X(p.geom), ST_Y(p.geom), p.site_id {filter} ORDER BY p.code LIMIT $11"))
        {
            Parameters(cmd);
            cmd.Parameters.Add(new() { Value = req.Limit });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var hide = scope.HidesCoordinates;
            while (await reader.ReadAsync(ct))
            {
                items.Add(new ServicePointRow(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                    reader.GetInt32(4), reader.GetString(5), hide ? null : Math.Round(reader.GetDouble(6), 1), hide ? null : Math.Round(reader.GetDouble(7), 1),
                    reader.IsDBNull(8) ? null : reader.GetInt64(8)));
            }
        }
        var byStatus = new Dictionary<string, int>();
        var (total, units) = (0, 0);
        await using (var cmd = db.CreateCommand($"SELECT p.status, count(*)::int, sum(p.units)::int {filter} GROUP BY p.status"))
        {
            Parameters(cmd);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                byStatus[reader.GetString(0)] = reader.GetInt32(1);
                total += reader.GetInt32(1);
                units += reader.GetInt32(2);
            }
        }
        return new ServicePointList(items, total, byStatus, units);
    }
}
