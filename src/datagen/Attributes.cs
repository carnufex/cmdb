using System.Globalization;
using System.Text.Json;

namespace Cmdb.DataGen;

/// <summary>Fictional instance attributes that satisfy each catalog type's JSON Schema.</summary>
internal static class Attributes
{
    private static readonly int[] Bands = [700, 800, 900, 1800, 2100, 2600, 3500];

    /// <summary>What a model draws, in watts. Passive equipment is not here. scripts/backfill-power.sql holds the same numbers.</summary>
    private static readonly Dictionary<string, int> PowerDraw = new(StringComparer.Ordinal)
    {
        ["acme-ar-10"] = 180,
        ["acme-cr-8"] = 1200,
        ["acme-ax-24"] = 30,
        ["acme-ax-48"] = 55,
        ["acme-ax-48p"] = 90,
        ["acme-cx-32"] = 320,
        ["acme-ix-8"] = 25,
        ["acme-bb-6"] = 140,
        ["acme-mw-1"] = 35,
        ["acme-rr-2"] = 280,
        ["acme-rr-4"] = 520,
        ["acme-lc-16e1"] = 18,
        ["acme-lc-24x"] = 60,
        ["acme-lc-4c"] = 110,
        ["acme-ot-40"] = 400,
        ["acme-ot-8"] = 150,
        ["acme-sdh-63"] = 120,
        ["acme-pdu-12"] = 10,
        ["acme-rect-48"] = 45,
    };

    public static string For(string typeKey, string category, long id, Site site, Random rng, int sector = 0, string? label = null)
    {
        var a = new Dictionary<string, object>(StringComparer.Ordinal);
        if (PowerDraw.TryGetValue(typeKey, out var draw))
        {
            // Varies a little per unit, without the random stream: the rest of the network must stay as it was.
            a["powerDrawW"] = draw + (id % 7) - 3;
        }
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
