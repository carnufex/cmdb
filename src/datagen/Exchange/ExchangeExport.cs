using System.Globalization;
using System.Text;

namespace Cmdb.DataGen.Exchange;

/// <summary>
/// Writes a generated network in the exchange format (#210), as a source system would export it. Used for the
/// examples in docs/exempel/import, for tests that import a known network and for measuring the import at full scale.
/// </summary>
internal static class ExchangeExport
{
    public static void Write(Network net, string folder)
    {
        Directory.CreateDirectory(folder);
        var sites = net.Sites.ToDictionary(s => s.Id);
        var equipment = net.Equipment.ToDictionary(e => e.Id);
        var cables = net.Cables.ToDictionary(c => c.Id);
        var channels = net.Channels.ToDictionary(c => c.Id);
        var circuits = net.Circuits.ToDictionary(c => c.Id);
        var services = net.Services.ToDictionary(s => s.Id);

        // Which equipment port or conductor end each terminal is.
        var owners = new Dictionary<long, (Equipment? Equipment, int Index, Cable? Cable)>();
        foreach (var e in net.Equipment)
        {
            for (var i = 0; i < e.Ports.Count; i++)
            {
                owners[e.TerminalAt(i)] = (e, i, null);
            }
        }
        foreach (var c in net.Cables)
        {
            for (var n = 0; n < 2 * c.Count; n++)
            {
                owners[c.FirstEndTerminal + n] = (null, n, c);
            }
        }
        object?[] Terminal(long id)
        {
            var (e, index, c) = owners[id];
            return e is not null
                ? [EquipmentId(e), e.Ports[index].Name, null, null, null]
                : [null, null, c!.Code, (index / 2) + 1, index % 2 == 0 ? "A" : "B"];
        }

        Csv(folder, ExchangeFormat.Sites, ["id", "code", "name", "siteType", "x", "y", "lifecycle"], w =>
        {
            foreach (var s in net.Sites)
            {
                w.Row(s.Code, s.Code, s.Name, s.SiteType, s.X, s.Y, s.Lifecycle);
            }
        });
        Csv(folder, ExchangeFormat.Locations, ["id", "site", "parent", "kind", "name", "rackUnits"], w =>
        {
            foreach (var l in net.Locations)
            {
                w.Row(LocationId(l.Id), sites[l.SiteId].Code, l.ParentId is { } p ? LocationId(p) : null, l.Kind, l.Name, l.RackUnits);
            }
        });
        Csv(folder, ExchangeFormat.Equipment, ["id", "site", "location", "parent", "slot", "name", "type", "lifecycle", "attributes"], w =>
        {
            foreach (var e in net.Equipment)
            {
                w.Row(EquipmentId(e), sites[e.SiteId].Code, e.LocationId is { } l ? LocationId(l) : null,
                    e.ParentId is { } p ? EquipmentId(equipment[p]) : null, e.Slot, e.Name, e.Type.Key, e.Lifecycle, e.Attributes);
            }
        });
        Csv(folder, ExchangeFormat.Ports, ["equipment", "name"], w =>
        {
            foreach (var e in net.Equipment)
            {
                foreach (var p in e.Ports)
                {
                    w.Row(EquipmentId(e), p.Name);
                }
            }
        });
        Csv(folder, ExchangeFormat.Cables, ["id", "code", "cableType", "a", "b", "lifecycle", "route"], w =>
        {
            foreach (var c in net.Cables)
            {
                w.Row(c.Code, c.Code, c.Type.Key, c.A.Code, c.B.Code, c.Lifecycle, Wkt(c.Coordinates));
            }
        });
        Csv(folder, ExchangeFormat.Connections,
            ["aEquipment", "aPort", "aCable", "aConductor", "aSide", "bEquipment", "bPort", "bCable", "bConductor", "bSide", "kind", "lifecycle"], w =>
            {
                foreach (var c in net.Connections)
                {
                    var kind = c.Kind switch
                    {
                        ConnectionKind.Patch => "patch",
                        ConnectionKind.Splice => "splice",
                        ConnectionKind.Termination => "termination",
                        _ => "internal",
                    };
                    w.Row([.. Terminal(c.A), .. Terminal(c.B), kind, c.Planned ? Lifecycle.Planned : Lifecycle.InService]);
                }
            });
        Csv(folder, ExchangeFormat.Circuits, ["id", "code", "layer", "lifecycle"], w =>
        {
            foreach (var c in net.Circuits)
            {
                w.Row(c.Code, c.Code, c.Layer, c.Lifecycle);
            }
        });
        Csv(folder, ExchangeFormat.Hops, ["circuit", "seq", "equipment", "port", "cable", "conductor", "side", "channel"], w =>
        {
            foreach (var h in net.Hops)
            {
                var channel = h.ChannelId is { } id ? $"{channels[id].Kind}:{channels[id].Number.ToString(CultureInfo.InvariantCulture)}" : null;
                w.Row([circuits[h.CircuitId].Code, h.Seq, .. Terminal(h.TerminalId), channel]);
            }
        });
        Csv(folder, ExchangeFormat.Dependencies, ["circuit", "carrier"], w =>
        {
            foreach (var (circuit, carrier) in net.Dependencies)
            {
                w.Row(circuits[circuit].Code, circuits[carrier].Code);
            }
        });
        Csv(folder, ExchangeFormat.Services, ["id", "code", "name", "serviceType", "lifecycle", "attributes"], w =>
        {
            foreach (var s in net.Services)
            {
                w.Row(s.Code, s.Code, s.Name, s.Type, s.Lifecycle, s.Attributes);
            }
        });
        Csv(folder, ExchangeFormat.ServiceCircuits, ["service", "circuit"], w =>
        {
            foreach (var (service, circuit) in net.ServiceCircuits)
            {
                w.Row(services[service].Code, circuits[circuit].Code);
            }
        });
    }

    private static string LocationId(long id) => $"L{id.ToString(CultureInfo.InvariantCulture)}";

    private static string EquipmentId(Equipment e) => $"E{e.Id.ToString(CultureInfo.InvariantCulture)}";

    private static string Wkt(double[] coordinates)
    {
        var sb = new StringBuilder("LINESTRING(");
        for (var i = 0; i < coordinates.Length; i += 2)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{(i > 0 ? ", " : "")}{coordinates[i]:R} {coordinates[i + 1]:R}");
        }
        return sb.Append(')').ToString();
    }

    private static void Csv(string folder, string file, string[] header, Action<CsvWriter> write)
    {
        using var stream = new StreamWriter(Path.Combine(folder, file), false, new UTF8Encoding(false), 1 << 16);
        var writer = new CsvWriter(stream, header);
        write(writer);
        writer.Flush();
    }
}
