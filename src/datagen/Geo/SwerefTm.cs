namespace Cmdb.DataGen.Geo;

/// <summary>
/// WGS 84 / ETRS89 latitude and longitude to SWEREF 99 TM (EPSG:3006): transverse Mercator on GRS80,
/// central meridian 15°E, scale 0.9996, false easting 500 000 m. Gauss–Krüger series as published by
/// Lantmäteriet; accurate to well under a millimetre within Sweden.
/// </summary>
internal static class SwerefTm
{
    private const double A = 6378137.0;
    private const double F = 1 / 298.257222101;
    private const double CentralMeridian = 15.0;
    private const double Scale = 0.9996;
    private const double FalseEasting = 500000.0;

    private static readonly double E2 = F * (2 - F);
    private static readonly double N = F / (2 - F);
    private static readonly double ARoof = A / (1 + N) * (1 + N * N / 4 + N * N * N * N / 64);
    private static readonly double CA = E2;
    private static readonly double CB = (5 * E2 * E2 - E2 * E2 * E2) / 6;
    private static readonly double CC = (104 * Math.Pow(E2, 3) - 45 * Math.Pow(E2, 4)) / 120;
    private static readonly double CD = 1237 * Math.Pow(E2, 4) / 1260;
    private static readonly double B1 = N / 2 - 2 * N * N / 3 + 5 * Math.Pow(N, 3) / 16 + 41 * Math.Pow(N, 4) / 180;
    private static readonly double B2 = 13 * N * N / 48 - 3 * Math.Pow(N, 3) / 5 + 557 * Math.Pow(N, 4) / 1440;
    private static readonly double B3 = 61 * Math.Pow(N, 3) / 240 - 103 * Math.Pow(N, 4) / 140;
    private static readonly double B4 = 49561 * Math.Pow(N, 4) / 161280;

    /// <summary>Returns (easting, northing) in metres, i.e. PostGIS (x, y) order.</summary>
    public static (double X, double Y) FromLatLon(double latitude, double longitude)
    {
        var phi = latitude * Math.PI / 180;
        var dLambda = (longitude - CentralMeridian) * Math.PI / 180;
        var sin = Math.Sin(phi);
        var sin2 = sin * sin;
        var phiStar = phi - sin * Math.Cos(phi) * (CA + sin2 * (CB + sin2 * (CC + sin2 * CD)));
        var xi = Math.Atan(Math.Tan(phiStar) / Math.Cos(dLambda));
        var eta = Math.Atanh(Math.Cos(phiStar) * Math.Sin(dLambda));

        var northing = Scale * ARoof * (xi
            + B1 * Math.Sin(2 * xi) * Math.Cosh(2 * eta)
            + B2 * Math.Sin(4 * xi) * Math.Cosh(4 * eta)
            + B3 * Math.Sin(6 * xi) * Math.Cosh(6 * eta)
            + B4 * Math.Sin(8 * xi) * Math.Cosh(8 * eta));
        var easting = Scale * ARoof * (eta
            + B1 * Math.Cos(2 * xi) * Math.Sinh(2 * eta)
            + B2 * Math.Cos(4 * xi) * Math.Sinh(4 * eta)
            + B3 * Math.Cos(6 * xi) * Math.Sinh(6 * eta)
            + B4 * Math.Cos(8 * xi) * Math.Sinh(8 * eta)) + FalseEasting;
        return (easting, northing);
    }
}
