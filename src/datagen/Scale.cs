namespace Cmdb.DataGen;

/// <summary>Network size. Full is the national scale the performance budget is defined against.</summary>
internal sealed record Scale(string Name, int Sites, int Hubs, int Aggregations)
{
    public static readonly Scale Small = new("small", 400, 4, 16);
    public static readonly Scale Medium = new("medium", 4_000, 12, 120);
    public static readonly Scale Full = new("full", 40_000, 48, 1_200);

    // Growth (#81): at 25-50 % a year, twice the network is 2-3 years out and four times 3-6 years.
    public static readonly Scale Double = new("2x", 80_000, 96, 2_400);
    public static readonly Scale Quadruple = new("4x", 160_000, 192, 4_800);

    public int Access => Sites - Hubs - Aggregations;

    public static Scale Parse(string name) => name switch
    {
        "small" => Small,
        "medium" => Medium,
        "full" => Full,
        "2x" => Double,
        "4x" => Quadruple,
        _ => throw new ArgumentException($"Unknown scale '{name}'. Use small, medium, full, 2x or 4x."),
    };
}
