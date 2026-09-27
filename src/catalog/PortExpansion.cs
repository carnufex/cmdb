using System.Globalization;

namespace Cmdb.Catalog;

public static class PortExpansion
{
    /// <summary>
    /// Generates the ports of one piece of equipment. Positions are 1-based and follow template order,
    /// so the same type always yields the same numbering and the front panel can be drawn from it.
    /// </summary>
    public static IReadOnlyList<Port> Expand(EquipmentType type, string? slot = null)
    {
        var ports = new List<Port>();
        foreach (var template in type.Ports)
        {
            var (first, last) = template.Range is [var a, var b] ? (a, b) : (0, 0);
            for (var n = first; n <= last; n++)
            {
                var i = n - first;
                var (dr, dc) = template.Layout switch
                {
                    PortLayout.Row => (0, i),
                    PortLayout.Column => (i, 0),
                    PortLayout.Zigzag => (i % 2, i / 2),
                    _ => throw new InvalidOperationException($"Unknown layout {template.Layout}."),
                };
                var name = template.Name
                    .Replace("{n}", n.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{slot}", slot ?? "0", StringComparison.Ordinal);
                ports.Add(new Port(name, template.Type, template.Group, ports.Count + 1, template.At[0] + dr, template.At[1] + dc));
            }
        }
        return ports;
    }
}
