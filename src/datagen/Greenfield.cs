using System.Globalization;
using System.Text;
using Npgsql;

namespace Cmdb.DataGen;

/// <summary>
/// A greenfield area for the demo (#296, ADR-0021): east of the station Lingonåsen (<see cref="DemoScenarios"/>), where no
/// site stands, service points to plan a new network for (houses in clusters, a few blocks of flats, businesses along a
/// road and a mast) and the project <c>Lingonåsen öst</c> around them, without stages. Deterministic per seed, synthetic,
/// and safe to run again: it replaces what it made before.
/// </summary>
public static class Greenfield
{
    public const string Project = "Lingonåsen öst";
    public const string CodePrefix = "AP-LO-";
    public const string Source = "demo-adresser";

    private static readonly string[] Streets =
    [
        "Lingonvägen", "Blåbärsstigen", "Tallbacken", "Myrvägen", "Granåsvägen", "Hjortronstigen", "Kvarnbacken", "Ängsvägen",
        "Skogsbrynet", "Vitmossvägen", "Ripstigen", "Björkhagen",
    ];

    private static readonly string[] Businesses =
    [
        "Lingonåsens Snickeri", "Östra Bygg & Plåt", "Åsens Bilverkstad", "Hagens Lantbruk", "Exempel Livs", "Skogens Förskola",
        "Åsdalens Vårdcentral", "Tallbackens Camping", "Myrens Åkeri", "Kvarnens Café",
    ];

    /// <summary>
    /// Lays out the area and writes it; returns how many service points it made, or 0 without the station. The layout follows
    /// from the station, which the generator's seed decides, so the same seed gives the same area.
    /// </summary>
    public static async Task<int> SeedAsync(NpgsqlConnection conn, TextWriter log, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Exec(conn, "SET LOCAL cmdb.actor = 'datagen'", ct);
        await Exec(conn, $"DELETE FROM project WHERE name = '{Project}'", ct);
        await Exec(conn, $"DELETE FROM service_point WHERE code LIKE '{CodePrefix}%'", ct);

        long station;
        double sx, sy;
        await using (var cmd = new NpgsqlCommand(
            $"SELECT id, ST_X(ST_PointOnSurface(geom)), ST_Y(ST_PointOnSurface(geom)) FROM site WHERE name = '{DemoScenarios.Station}' ORDER BY id LIMIT 1", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                log.WriteLine("  greenfield: no station, skipped");
                return 0;
            }
            (station, sx, sy) = (reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2));
        }
        var (cx, cy) = await EmptySpotAsync(conn, sx, sy, ct);
        var points = Layout((int)(station % int.MaxValue), cx, cy);
        await WriteAsync(conn, points, ct);
        await Exec(conn, $"""
            INSERT INTO project (name, description, owner, created_by, area)
            SELECT '{Project}', 'Nytt fibernät till byarna öster om Lingonåsen: villor, några flerbostadshus, företag och en mast. Inga etapper än.',
                   'Kim Lindqvist', 'datagen', ST_Buffer(ST_ConvexHull(ST_Collect(geom)), 300, 'quad_segs=2')
            FROM service_point WHERE code LIKE '{CodePrefix}%'
            """, ct);
        await tx.CommitAsync(ct);
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  greenfield  {Project}: {points.Count} service points, {points.Sum(p => p.Units)} units around ({cx:0}, {cy:0})"));
        return points.Count;
    }

    internal sealed record Point(string Code, string Type, int Units, string Address, string? Name, double X, double Y);

    /// <summary>The area's centre: east of the station, moved on until no site is within 3 km.</summary>
    private static async Task<(double X, double Y)> EmptySpotAsync(NpgsqlConnection conn, double sx, double sy, CancellationToken ct)
    {
        for (var step = 0; step < 20; step++)
        {
            var (x, y) = (sx + 6000 + (step * 2000), sy + (step % 2 == 0 ? 0 : 1500));
            await using var cmd = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM site WHERE ST_DWithin(geom, ST_SetSRID(ST_MakePoint($1, $2), 3006), 3000))", conn);
            cmd.Parameters.Add(new() { Value = x });
            cmd.Parameters.Add(new() { Value = y });
            if (await cmd.ExecuteScalarAsync(ct) is false)
            {
                return (x, y);
            }
        }
        return (sx + 6000, sy);
    }

    /// <summary>The service points, the same for the same seed and centre.</summary>
    internal static List<Point> Layout(int seed, double cx, double cy)
    {
        var rng = new Random(seed * 7919 + 296);
        var points = new List<Point>();
        var houses = new Dictionary<string, int>();
        string Address(string street) => $"{street} {houses[street] = houses.GetValueOrDefault(street) + (1 + rng.Next(2))}";
        string Code() => string.Create(CultureInfo.InvariantCulture, $"{CodePrefix}{points.Count + 1:0000}");

        // Villages: houses in a loose grid around a few cluster centres, each its own streets.
        var clusters = 4 + rng.Next(2);
        for (var c = 0; c < clusters; c++)
        {
            var angle = (2 * Math.PI * c / clusters) + (rng.NextDouble() * 0.6);
            var (kx, ky) = (cx + (Math.Cos(angle) * (900 + rng.Next(600))), cy + (Math.Sin(angle) * (700 + rng.Next(500))));
            var street = Streets[(c * 2) % Streets.Length];
            var cross = Streets[((c * 2) + 1) % Streets.Length];
            var count = 25 + rng.Next(30);
            for (var i = 0; i < count; i++)
            {
                var (gx, gy) = (i % 6, i / 6);
                var on = gx < 3 ? street : cross;
                points.Add(new Point(Code(), rng.Next(10) == 0 ? "radhus" : "villa", 1, Address(on), null,
                    kx + (gx * 35) + (rng.NextDouble() * 12), ky + (gy * 30) + (rng.NextDouble() * 12)));
            }
        }
        // A few blocks of flats near the middle.
        for (var i = 0; i < 4 + rng.Next(3); i++)
        {
            points.Add(new Point(Code(), "flerbostadshus", 8 + rng.Next(33), Address("Centrumvägen"), null,
                cx - 150 + (i * 70) + rng.Next(20), cy + 120 + rng.Next(40)));
        }
        // Businesses along the road through the area.
        for (var i = 0; i < 6 + rng.Next(4); i++)
        {
            points.Add(new Point(Code(), "foretag", 1 + rng.Next(3), Address("Industrivägen"), Businesses[i % Businesses.Length],
                cx - 1200 + (i * 260) + rng.Next(40), cy - 400 + rng.Next(30)));
        }
        // And a mast on the hill.
        points.Add(new Point(Code(), "mast", 1, "Åsen 1", "Mast Lingonåsen öst", cx + 600 + rng.Next(100), cy + 900 + rng.Next(100)));
        return points;
    }

    private static async Task WriteAsync(NpgsqlConnection conn, List<Point> points, CancellationToken ct)
    {
        await using var copy = await conn.BeginBinaryImportAsync(
            "COPY service_point (code, type_key, units, address, name, geom, source_system, external_id) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var p in points)
        {
            await copy.StartRowAsync(ct);
            await copy.WriteAsync(p.Code, NpgsqlTypes.NpgsqlDbType.Text, ct);
            await copy.WriteAsync(p.Type, NpgsqlTypes.NpgsqlDbType.Text, ct);
            await copy.WriteAsync(p.Units, NpgsqlTypes.NpgsqlDbType.Integer, ct);
            await copy.WriteAsync(p.Address, NpgsqlTypes.NpgsqlDbType.Text, ct);
            if (p.Name is null)
            {
                await copy.WriteNullAsync(ct);
            }
            else
            {
                await copy.WriteAsync(p.Name, NpgsqlTypes.NpgsqlDbType.Text, ct);
            }
            await copy.WriteAsync(Wkb(p.X, p.Y), NpgsqlTypes.NpgsqlDbType.Bytea, ct);
            await copy.WriteAsync(Source, NpgsqlTypes.NpgsqlDbType.Text, ct);
            await copy.WriteAsync(p.Code, NpgsqlTypes.NpgsqlDbType.Text, ct);
        }
        await copy.CompleteAsync(ct);
    }

    /// <summary>An EWKB point in SWEREF 99 TM, as the loader writes sites.</summary>
    private static byte[] Wkb(double x, double y)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII);
        w.Write((byte)1);
        w.Write(0x20000001u);
        w.Write(3006);
        w.Write(x);
        w.Write(y);
        return ms.ToArray();
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
