using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cmdb.DataGen;

/// <summary>A hash over every generated row, used to prove that a seed always yields the same network.</summary>
internal static class Fingerprint
{
    public static string Of(Network net)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sb = new StringBuilder();
        void Row(FormattableString row)
        {
            sb.Clear().Append(row.ToString(CultureInfo.InvariantCulture)).Append('\n');
            hash.AppendData(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        foreach (var s in net.Sites)
        {
            Row($"S|{s.Id}|{s.Code}|{s.Name}|{s.SiteType}|{s.X:R}|{s.Y:R}|{s.Lifecycle}|{s.Parent?.Id}");
        }
        foreach (var l in net.Locations)
        {
            Row($"L|{l}");
        }
        foreach (var e in net.Equipment)
        {
            Row($"E|{e.Id}|{e.Type.Key}|{e.SiteId}|{e.LocationId}|{e.ParentId}|{e.Slot}|{e.Name}|{e.Attributes}|{e.Lifecycle}|{e.FirstTerminal}");
        }
        foreach (var c in net.Cables)
        {
            Row($"C|{c.Id}|{c.Type.Key}|{c.Code}|{c.A.Id}|{c.B.Id}|{string.Join(',', c.Coordinates.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))}|{c.FirstConductor}|{c.FirstEndTerminal}");
        }
        foreach (var c in net.Connections)
        {
            Row($"X|{c}");
        }
        foreach (var c in net.Channels)
        {
            Row($"H|{c}");
        }
        foreach (var c in net.Circuits)
        {
            Row($"R|{c}");
        }
        foreach (var h in net.Hops)
        {
            Row($"P|{h}");
        }
        foreach (var d in net.Dependencies)
        {
            Row($"D|{d.Circuit}|{d.Carrier}");
        }
        foreach (var s in net.Services)
        {
            Row($"V|{s}");
        }
        foreach (var s in net.ServiceCircuits)
        {
            Row($"W|{s.Service}|{s.Circuit}");
        }
        foreach (var r in net.RouteSegments)
        {
            Row($"T|{r.Id}|{r.Code}|{r.A}|{r.B}|{r.Construction}|{r.Owner}|{string.Join(',', r.Coordinates.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))}|{r.Lifecycle}");
        }
        foreach (var d in net.Ducts)
        {
            Row($"U|{d}");
        }
        foreach (var s in net.Subducts)
        {
            Row($"B|{s}");
        }
        foreach (var c in net.CablePaths)
        {
            Row($"Q|{c}");
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
