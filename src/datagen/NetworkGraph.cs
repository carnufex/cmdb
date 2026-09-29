using Cmdb.Graph;
using GraphLifecycle = Cmdb.Graph.Lifecycle;

namespace Cmdb.DataGen;

/// <summary>
/// The rows the graph's database loader reads, taken straight from a generated network. Lets tests prove the
/// loader, and benchmarks build a full-scale graph without a database.
/// </summary>
internal static class NetworkGraph
{
    /// <summary>The same rows the database loader reads, taken straight from the generator.</summary>
    public static GraphData From(Network net)
    {
        var d = new GraphData();
        foreach (var e in net.Equipment)
        {
            d.EquipmentIds.Add(e.Id);
            d.EquipmentSites.Add(e.SiteId);
            for (var i = 0; i < e.Ports.Count; i++)
            {
                d.PortTerminals.Add(e.TerminalAt(i));
                d.PortEquipment.Add(e.Id);
            }
        }
        foreach (var c in net.Cables)
        {
            d.CableIds.Add(c.Id);
            d.CableLifecycles.Add((byte)Life(c.Lifecycle));
            for (var n = 1; n <= c.Count; n++)
            {
                var conductor = c.FirstConductor + n - 1;
                d.ConductorIds.Add(conductor);
                d.ConductorCables.Add(c.Id);
                d.EndTerminals.Add(c.End(n, c.A));
                d.EndConductors.Add(conductor);
                d.EndTerminals.Add(c.End(n, c.B));
                d.EndConductors.Add(conductor);
            }
        }
        foreach (var c in net.Connections)
        {
            d.ConnectionA.Add(c.A);
            d.ConnectionB.Add(c.B);
            d.ConnectionKinds.Add((byte)c.Kind);
            d.ConnectionLifecycles.Add((byte)(c.Planned ? GraphLifecycle.Planned : GraphLifecycle.InService));
        }
        foreach (var c in net.Circuits)
        {
            d.CircuitIds.Add(c.Id);
            d.CircuitLayers.Add(c.Layer switch { "physical" => 0, "transmission" => 1, _ => 2 });
        }
        foreach (var h in net.Hops.OrderBy(h => h.CircuitId).ThenBy(h => h.Seq))
        {
            d.HopCircuits.Add(h.CircuitId);
            d.HopTerminals.Add(h.TerminalId);
        }
        foreach (var (circuit, carrier) in net.Dependencies)
        {
            d.DependencyCircuits.Add(circuit);
            d.DependencyCarriers.Add(carrier);
        }
        foreach (var (service, circuit) in net.ServiceCircuits)
        {
            d.ServiceCircuitServices.Add(service);
            d.ServiceCircuitCircuits.Add(circuit);
        }
        return d;
    }

    private static GraphLifecycle Life(string lifecycle) => lifecycle switch
    {
        Lifecycle.Planned => GraphLifecycle.Planned,
        Lifecycle.UnderConstruction => GraphLifecycle.UnderConstruction,
        _ => GraphLifecycle.InService,
    };

}
