using System.Globalization;
using System.Text.Json;

namespace Cmdb.DataGen;

/// <summary>Fictional instance attributes that satisfy each catalog type's JSON Schema.</summary>
internal static class Attributes
{
    private static readonly int[] Bands = [700, 800, 900, 1800, 2100, 2600, 3500];

    public static string For(string typeKey, string category, long id, Site site, Random rng, int sector = 0, string? label = null)
    {
        var a = new Dictionary<string, object>(StringComparer.Ordinal);
        switch (category)
        {
            case "switch":
                a["serialNumber"] = Serial(rng);
                a["firmware"] = Firmware(rng);
                a["managementIp"] = Ip(10, id);
                if (typeKey == "acme-ax-48p")
                {
                    a["poeBudgetW"] = 740;
                }
                break;
            case "router":
                a["serialNumber"] = Serial(rng);
                a["firmware"] = Firmware(rng);
                a["loopbackIp"] = Ip(172, id);
                a["asNumber"] = 64512 + (int)((site.Hub?.Id ?? site.Id) % 1000);
                break;
            case "card":
                a["serialNumber"] = Serial(rng);
                break;
            case "radio":
                a["serialNumber"] = Serial(rng);
                if (typeKey is "acme-rr-2" or "acme-rr-4")
                {
                    a["bandMHz"] = Bands[rng.Next(Bands.Length)];
                    a["transmitPowerW"] = 20 * rng.Next(1, 5);
                }
                if (typeKey == "acme-mw-1")
                {
                    a["linkId"] = $"MW-{site.Code}";
                }
                break;
            case "antenna":
                a["azimuthDeg"] = ((sector * 120) + rng.Next(-10, 11) + 360) % 360;
                a["tiltDeg"] = Math.Round(-rng.NextDouble() * 6, 1);
                a["heightM"] = rng.Next(20, 61);
                break;
            case "transmission":
                a["serialNumber"] = Serial(rng);
                if (typeKey == "acme-sdh-63")
                {
                    a["channelPlan"] = "SDH STM-1";
                }
                else
                {
                    a["channelPlan"] = "C-band 100 GHz";
                    a["lineRateGbps"] = 100;
                }
                break;
            case "odf":
                a["connector"] = "LC";
                if (label is not null)
                {
                    a["label"] = label;
                }
                break;
            case "patch":
                a["label"] = label ?? "Kundanslutning";
                break;
            case "power":
                a["serialNumber"] = Serial(rng);
                a["nominalVoltage"] = -48;
                if (typeKey == "acme-bat-100")
                {
                    a["capacityAh"] = 50 * rng.Next(2, 5);
                }
                break;
            default:
                break;
        }
        return JsonSerializer.Serialize(a);
    }

    private static string Serial(Random rng) =>
        string.Create(CultureInfo.InvariantCulture, $"SN{rng.Next(0x1000000):X6}{rng.Next(0x10000):X4}");

    private static string Firmware(Random rng) =>
        string.Create(CultureInfo.InvariantCulture, $"{rng.Next(1, 4)}.{rng.Next(0, 10)}.{rng.Next(0, 20)}");

    private static string Ip(int first, long id) =>
        string.Create(CultureInfo.InvariantCulture, $"{first}.{(id >> 16) & 255}.{(id >> 8) & 255}.{id & 255}");
}
