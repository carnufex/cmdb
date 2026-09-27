namespace Cmdb.DataGen;

/// <summary>Network size. Full is the national scale the performance budget is defined against.</summary>
internal sealed record Scale(string Name, int Sites, int Hubs, int Aggregations)
{
    public static readonly Scale Small = new("small", 400, 4, 16);
    public static readonly Scale Medium = new("medium", 4_000, 12, 120);
    public static readonly Scale Full = new("full", 40_000, 48, 1_200);

    public int Access => Sites - Hubs - Aggregations;

    public static Scale Parse(string name) => name switch
    {
        "small" => Small,
        "medium" => Medium,
        "full" => Full,
        _ => throw new ArgumentException($"Unknown scale '{name}'. Use small, medium or full."),
    };
}
