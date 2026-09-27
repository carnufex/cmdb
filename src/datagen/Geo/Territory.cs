namespace Cmdb.DataGen.Geo;

/// <summary>
/// A deliberately coarse outline of Sweden, used only so synthetic sites land on land in the map.
/// It carries no information about real networks, roads or settlements.
/// </summary>
internal sealed class Territory
{
    // (latitude, longitude), clockwise from the southern tip. Roughly 30 km resolution.
    private static readonly (double Lat, double Lon)[] Outline =
    [
        (55.40, 13.35), (55.60, 13.05), (56.05, 12.75), (56.65, 12.90), (57.60, 12.00), (58.30, 11.60),
        (58.90, 11.30), (59.40, 11.80), (60.20, 12.50), (61.00, 12.40), (61.80, 12.30), (62.30, 12.20),
        (63.00, 12.10), (63.90, 13.50), (64.60, 14.10), (65.40, 14.60), (66.20, 15.30), (67.00, 16.10),
        (67.70, 17.20), (68.20, 18.40), (68.50, 19.90), (68.90, 20.60), (68.40, 21.80), (67.80, 23.30),
        (66.90, 23.70), (65.90, 23.90), (65.70, 22.20), (65.20, 21.60), (64.60, 21.20), (63.80, 20.40),
        (63.40, 19.40), (62.80, 18.10), (62.00, 17.50), (61.30, 17.20), (60.70, 17.40), (60.40, 18.50),
        (59.90, 18.90), (59.40, 18.60), (58.90, 17.60), (58.50, 16.80), (57.80, 16.60), (57.20, 16.50),
        (56.60, 16.30), (56.20, 15.80), (56.10, 14.70), (55.70, 14.30), (55.40, 14.10),
    ];

    private readonly (double X, double Y)[] _polygon;

    public Territory()
    {
        _polygon = [.. Outline.Select(p => SwerefTm.FromLatLon(p.Lat, p.Lon))];
        MinX = _polygon.Min(p => p.X);
        MaxX = _polygon.Max(p => p.X);
        MinY = _polygon.Min(p => p.Y);
        MaxY = _polygon.Max(p => p.Y);
        var area = 0.0;
        for (int i = 0, j = _polygon.Length - 1; i < _polygon.Length; j = i++)
        {
            area += (_polygon[j].X * _polygon[i].Y) - (_polygon[i].X * _polygon[j].Y);
        }
        Area = Math.Abs(area / 2);
    }

    public double MinX { get; }
    public double MaxX { get; }
    public double MinY { get; }
    public double MaxY { get; }

    /// <summary>Square metres.</summary>
    public double Area { get; }

    public bool Contains(double x, double y)
    {
        var inside = false;
        for (int i = 0, j = _polygon.Length - 1; i < _polygon.Length; j = i++)
        {
            var (xi, yi) = _polygon[i];
            var (xj, yj) = _polygon[j];
            if ((yi > y) != (yj > y) && x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    public (double X, double Y) RandomPoint(Random rng)
    {
        while (true)
        {
            var x = MinX + (rng.NextDouble() * (MaxX - MinX));
            var y = MinY + (rng.NextDouble() * (MaxY - MinY));
            if (Contains(x, y))
            {
                return (x, y);
            }
        }
    }

    /// <summary>A normally distributed point around a centre that stays inside the territory.</summary>
    public (double X, double Y) RandomPointNear(Random rng, double cx, double cy, double sigma)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var (gx, gy) = Gaussian(rng);
            var x = cx + (gx * sigma);
            var y = cy + (gy * sigma);
            if (Contains(x, y))
            {
                return (x, y);
            }
        }
        return (cx + rng.NextDouble(), cy + rng.NextDouble());
    }

    private static (double, double) Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        var r = Math.Sqrt(-2.0 * Math.Log(u1));
        return (r * Math.Cos(2 * Math.PI * u2), r * Math.Sin(2 * Math.PI * u2));
    }
}
