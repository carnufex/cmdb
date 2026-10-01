namespace Cmdb.Graph;

/// <summary>
/// The whole network in memory (ADR-0002), immutable. Nodes are terminals with dense internal indexes; edges are
/// connections plus conductors, in CSR form. Objects around the terminals (equipment, sites, cables, circuits,
/// services) are parallel arrays indexed the same way. External ids are only used at the edges of an operation.
/// </summary>
public sealed partial class Graph
{
    // Terminals, sorted by external id; the index is the node.
    internal long[] TerminalIds { get; init; } = null!;
    internal TerminalKind[] TerminalKinds { get; init; } = null!;

    /// <summary>Equipment index for ports, conductor index for conductor ends.</summary>
    internal int[] TerminalOwners { get; init; } = null!;

    // CSR adjacency: the edges of node n are EdgeTargets[EdgeStart[n]..EdgeStart[n + 1]].
    internal int[] EdgeStart { get; init; } = null!;
    internal int[] EdgeTargets { get; init; } = null!;
    internal EdgeKind[] EdgeKinds { get; init; } = null!;
    internal Lifecycle[] EdgeLifecycles { get; init; } = null!;

    internal long[] EquipmentIds { get; init; } = null!;
    internal int[] EquipmentSites { get; init; } = null!;
    internal long[] SiteIds { get; init; } = null!;

    internal long[] ConductorIds { get; init; } = null!;
    internal int[] ConductorCables { get; init; } = null!;
    internal long[] CableIds { get; init; } = null!;
    internal Lifecycle[] CableLifecycles { get; init; } = null!;

    internal long[] CircuitIds { get; init; } = null!;
    internal CircuitLayer[] CircuitLayers { get; init; } = null!;

    // Circuit c's hops (nodes, in order) are HopNodes[HopStart[c]..HopStart[c + 1]].
    internal int[] HopStart { get; init; } = null!;
    internal int[] HopNodes { get; init; } = null!;

    // Circuits through node n: NodeCircuits[NodeCircuitStart[n]..].
    internal int[] NodeCircuitStart { get; init; } = null!;
    internal int[] NodeCircuits { get; init; } = null!;

    // Circuits riding on circuit c: Dependents[DependentStart[c]..].
    internal int[] DependentStart { get; init; } = null!;
    internal int[] Dependents { get; init; } = null!;

    // Services carried by circuit c: CircuitServices[CircuitServiceStart[c]..], as indexes into ServiceIds.
    internal int[] CircuitServiceStart { get; init; } = null!;
    internal int[] CircuitServices { get; init; } = null!;
    internal long[] ServiceIds { get; init; } = null!;

    // Circuits carrying service s: ServiceCircuitList[ServiceCircuitStart[s]..] (#9).
    internal int[] ServiceCircuitStart { get; init; } = null!;
    internal int[] ServiceCircuitList { get; init; } = null!;

    // Circuits that circuit c rides on: Carriers[CarrierStart[c]..] (#9).
    internal int[] CarrierStart { get; init; } = null!;
    internal int[] Carriers { get; init; } = null!;

    // Derived at load, not stored in the snapshot (#10): ports per equipment, conductor ends per cable, equipment per site.
    internal int[] EquipmentPortStart { get; private set; } = null!;
    internal int[] EquipmentPorts { get; private set; } = null!;
    internal int[] CableEndStart { get; private set; } = null!;
    internal int[] CableEnds { get; private set; } = null!;
    internal int[] SiteEquipmentStart { get; private set; } = null!;
    internal int[] SiteEquipment { get; private set; } = null!;

    /// <summary>The change stream position the graph reflects (see <see cref="IGraphChangeFeed"/>).</summary>
    public required string Version { get => _version; init => _version = value; }

    private string _version = null!;

    public int NodeCount => TerminalIds.Length + (_overlay?.TerminalIds.Count ?? 0);

    /// <summary>Undirected edges; each is stored once per direction.</summary>
    public int EdgeCount => EdgeTargets.Length / 2;

    public int CircuitCount => CircuitIds.Length + (CircuitDelta?.CircuitIds.Count ?? 0);

    public int SiteCount => SiteIds.Length + (_overlay?.SiteIds.Count ?? 0);

    public int CableCount => CableIds.Length + (_overlay?.CableIds.Count ?? 0);

    public int ServiceCount => ServiceIds.Length + (CircuitDelta?.ServiceIds.Count ?? 0);

    /// <summary>The site index of an equipment index.</summary>
    public int SiteIndexOfEquipment(int equipment) =>
        equipment < EquipmentIds.Length ? EquipmentSites[equipment] : _overlay!.EquipmentSites[equipment - EquipmentIds.Length];

    /// <summary>The site index of a port's equipment, or -1 for a conductor end.</summary>
    public int SiteIndexOfNode(int node) => KindOf(node) == TerminalKind.Port ? SiteIndexOfEquipment(OwnerOf(node)) : -1;

    /// <summary>The cable index of a conductor end, or -1 for a port.</summary>
    public int CableIndexOfNode(int node) => KindOf(node) == TerminalKind.ConductorEnd ? CableOfConductor(OwnerOf(node)) : -1;

    public bool TryGetNode(long terminalId, out int node)
    {
        node = Array.BinarySearch(TerminalIds, terminalId);
        if (node >= 0)
        {
            return true;
        }
        if (_overlay is not null && _overlay.NodeById.TryGetValue(terminalId, out var planned))
        {
            node = TerminalIds.Length + planned;
            return true;
        }
        return false;
    }

    public long TerminalId(int node) => node < TerminalIds.Length ? TerminalIds[node] : _overlay!.TerminalIds[node - TerminalIds.Length];

    /// <summary>Whether the node is a terminal planned in this view (#107), not in production.</summary>
    public bool IsPlanned(int node) => node >= TerminalIds.Length;

    internal int OwnerOf(int node) => node < TerminalIds.Length ? TerminalOwners[node] : _overlay!.Owners[node - TerminalIds.Length];

    /// <summary>A conductor's id, also for one a delta or plan added after the base's arrays (#163).</summary>
    internal long ConductorId(int conductor) =>
        conductor < ConductorIds.Length ? ConductorIds[conductor] : _overlay!.ConductorIds[conductor - ConductorIds.Length];

    private int CableOfConductor(int conductor) =>
        conductor < ConductorIds.Length ? ConductorCables[conductor] : _overlay!.ConductorCables[conductor - ConductorIds.Length];

    public bool TryGetCircuit(long circuitId, out int circuit)
    {
        circuit = Array.BinarySearch(CircuitIds, circuitId);
        if (CircuitDelta is not { } delta)
        {
            return circuit >= 0;
        }
        if (circuit < 0 && delta.CircuitById.TryGetValue(circuitId, out var added))
        {
            circuit = CircuitIds.Length + added;
        }
        return circuit >= 0 && !delta.Removed.Contains(circuit);
    }

    /// <summary>Services are in the graph through their circuits; one that has none left is gone.</summary>
    public bool TryGetService(long serviceId, out int service)
    {
        service = Array.BinarySearch(ServiceIds, serviceId);
        if (CircuitDelta is not { } delta)
        {
            return service >= 0;
        }
        if (service < 0 && delta.ServiceById.TryGetValue(serviceId, out var added))
        {
            service = ServiceIds.Length + added;
        }
        return service >= 0 && CircuitsOf(service).Length > 0;
    }

    public bool TryGetEquipment(long equipmentId, out int equipment)
    {
        equipment = Array.BinarySearch(EquipmentIds, equipmentId);
        if (equipment >= 0)
        {
            return true;
        }
        if (_overlay is not null && _overlay.EquipmentById.TryGetValue(equipmentId, out var planned))
        {
            equipment = EquipmentIds.Length + planned;
            return true;
        }
        return false;
    }

    public bool TryGetCable(long cableId, out int cable)
    {
        cable = Array.BinarySearch(CableIds, cableId);
        if (cable >= 0)
        {
            return true;
        }
        if (_overlay is not null && _overlay.CableById.TryGetValue(cableId, out var planned))
        {
            cable = CableIds.Length + planned;
            return true;
        }
        return false;
    }

    /// <summary>Only sites with equipment are in the graph.</summary>
    public bool TryGetSite(long siteId, out int site)
    {
        site = Array.BinarySearch(SiteIds, siteId);
        if (site >= 0)
        {
            return true;
        }
        if (_overlay is not null && _overlay.SiteById.TryGetValue(siteId, out var planned))
        {
            site = SiteIds.Length + planned;
            return true;
        }
        return false;
    }

    /// <summary>The equipment's ports, as nodes.</summary>
    public ReadOnlySpan<int> PortsOf(int equipment) => equipment < EquipmentIds.Length
        ? Slice(EquipmentPorts, EquipmentPortStart, equipment)
        : _overlay!.EquipmentPorts[equipment - EquipmentIds.Length];

    /// <summary>The conductor ends of the cable, as nodes.</summary>
    public ReadOnlySpan<int> EndsOf(int cable) => cable < CableIds.Length
        ? Slice(CableEnds, CableEndStart, cable)
        : _overlay!.CableEnds[cable - CableIds.Length];

    /// <summary>The equipment at the site, as equipment indexes; in a plan view with planned equipment added.</summary>
    public ReadOnlySpan<int> EquipmentAt(int site) =>
        _overlay is not null && _overlay.SiteEquipment.TryGetValue(site, out var all) ? all
        : site < SiteIds.Length ? Slice(SiteEquipment, SiteEquipmentStart, site)
        : [];

    /// <summary>Builds the derived ownership indexes; called once by the builder and the snapshot reader.</summary>
    internal Graph IndexOwners()
    {
        var portCount = 0;
        foreach (var kind in TerminalKinds)
        {
            portCount += kind == TerminalKind.Port ? 1 : 0;
        }
        var ports = new int[portCount];
        var portOwners = new int[portCount];
        var ends = new int[TerminalIds.Length - portCount];
        var endCables = new int[ends.Length];
        for (int node = 0, p = 0, c = 0; node < TerminalIds.Length; node++)
        {
            if (TerminalKinds[node] == TerminalKind.Port)
            {
                ports[p] = node;
                portOwners[p++] = TerminalOwners[node];
            }
            else
            {
                ends[c] = node;
                endCables[c++] = ConductorCables[TerminalOwners[node]];
            }
        }
        (EquipmentPortStart, EquipmentPorts) = GraphBuilder.GroupStable(EquipmentIds.Length, portOwners, ports);
        (CableEndStart, CableEnds) = GraphBuilder.GroupStable(CableIds.Length, endCables, ends);
        var equipment = new int[EquipmentIds.Length];
        for (var e = 0; e < equipment.Length; e++)
        {
            equipment[e] = e;
        }
        (SiteEquipmentStart, SiteEquipment) = GraphBuilder.GroupStable(SiteIds.Length, EquipmentSites, equipment);
        return this;
    }

    public long CircuitId(int circuit) => circuit < CircuitIds.Length ? CircuitIds[circuit] : CircuitDelta!.CircuitIds[circuit - CircuitIds.Length];

    public CircuitLayer LayerOf(int circuit) =>
        CircuitDelta is { } delta && delta.Layers.TryGetValue(circuit, out var layer) ? layer : CircuitLayers[circuit];

    public long ServiceId(int service) => service < ServiceIds.Length ? ServiceIds[service] : CircuitDelta!.ServiceIds[service - ServiceIds.Length];

    // The circuit lists read the change stream's delta first (#121); a circuit or service new in it has no slice.

    /// <summary>The circuit's path, in order.</summary>
    public ReadOnlySpan<int> HopsOf(int circuit) =>
        CircuitDelta is { } delta && delta.Hops.TryGetValue(circuit, out var hops) ? hops : Slice(HopNodes, HopStart, circuit);

    /// <summary>Circuits whose path passes the node. Planned terminals carry none.</summary>
    public ReadOnlySpan<int> CircuitsThrough(int node) =>
        CircuitDelta is { } delta && delta.NodeCircuits.TryGetValue(node, out var circuits) ? circuits
        : node < TerminalIds.Length ? Slice(NodeCircuits, NodeCircuitStart, node) : [];

    /// <summary>Circuits riding on this one (upwards).</summary>
    public ReadOnlySpan<int> DependentsOf(int circuit) =>
        CircuitDelta is { } delta && delta.Dependents.TryGetValue(circuit, out var dependents) ? dependents
        : circuit < CircuitIds.Length ? Slice(Dependents, DependentStart, circuit) : [];

    /// <summary>Circuits this one rides on (downwards).</summary>
    public ReadOnlySpan<int> CarriersOf(int circuit) =>
        CircuitDelta is { } delta && delta.Carriers.TryGetValue(circuit, out var carriers) ? carriers : Slice(Carriers, CarrierStart, circuit);

    /// <summary>Services carried by the circuit, as service indexes.</summary>
    public ReadOnlySpan<int> ServicesOf(int circuit) =>
        CircuitDelta is { } delta && delta.Services.TryGetValue(circuit, out var services) ? services : Slice(CircuitServices, CircuitServiceStart, circuit);

    /// <summary>Circuits carrying the service.</summary>
    public ReadOnlySpan<int> CircuitsOf(int service) =>
        CircuitDelta is { } delta && delta.ServiceCircuits.TryGetValue(service, out var circuits) ? circuits
        : Slice(ServiceCircuitList, ServiceCircuitStart, service);

    private static ReadOnlySpan<int> Slice(int[] values, int[] start, int key) => values.AsSpan(start[key], start[key + 1] - start[key]);

    public TerminalKind KindOf(int node) => node < TerminalIds.Length ? TerminalKinds[node] : _overlay!.Kinds[node - TerminalIds.Length];

    public ReadOnlySpan<int> Neighbours(int node) =>
        _overlay is not null && _overlay.Edges.TryGetValue(node, out var edges)
            ? edges.Targets
            : EdgeTargets.AsSpan(EdgeStart[node], EdgeStart[node + 1] - EdgeStart[node]);

    public ReadOnlySpan<EdgeKind> NeighbourKinds(int node) =>
        _overlay is not null && _overlay.Edges.TryGetValue(node, out var edges)
            ? edges.Kinds
            : EdgeKinds.AsSpan(EdgeStart[node], EdgeStart[node + 1] - EdgeStart[node]);

    /// <summary>Equipment id of a port, or null for a conductor end.</summary>
    public long? EquipmentOf(int node) => KindOf(node) == TerminalKind.Port ? EquipmentId(OwnerOf(node)) : null;

    /// <summary>Cable id of a conductor end, or null for a port.</summary>
    public long? CableOf(int node) => KindOf(node) == TerminalKind.ConductorEnd ? CableId(CableOfConductor(OwnerOf(node))) : null;

    /// <summary>Site of a port's equipment, or null for a conductor end.</summary>
    public long? SiteOf(int node) => KindOf(node) == TerminalKind.Port ? SiteId(SiteIndexOfEquipment(OwnerOf(node))) : null;

    public long EquipmentId(int equipment) =>
        equipment < EquipmentIds.Length ? EquipmentIds[equipment] : _overlay!.EquipmentIds[equipment - EquipmentIds.Length];

    public long CableId(int cable) => cable < CableIds.Length ? CableIds[cable] : _overlay!.CableIds[cable - CableIds.Length];

    public long SiteId(int site) => site < SiteIds.Length ? SiteIds[site] : _overlay!.SiteIds[site - SiteIds.Length];

    /// <summary>Rough size of the arrays in bytes, for diagnostics.</summary>
    public long ApproximateBytes =>
        (TerminalIds.LongLength * 8) + TerminalKinds.LongLength + (TerminalOwners.LongLength * 4)
        + (EdgeStart.LongLength * 4) + (EdgeTargets.LongLength * 4) + EdgeKinds.LongLength + EdgeLifecycles.LongLength
        + (EquipmentIds.LongLength * 8) + (EquipmentSites.LongLength * 4) + (SiteIds.LongLength * 8)
        + (ConductorIds.LongLength * 8) + (ConductorCables.LongLength * 4) + (CableIds.LongLength * 8) + CableLifecycles.LongLength
        + (CircuitIds.LongLength * 8) + CircuitLayers.LongLength + (HopStart.LongLength * 4) + (HopNodes.LongLength * 4)
        + (NodeCircuitStart.LongLength * 4) + (NodeCircuits.LongLength * 4) + (DependentStart.LongLength * 4) + (Dependents.LongLength * 4)
        + (CircuitServiceStart.LongLength * 4) + (CircuitServices.LongLength * 4) + (ServiceIds.LongLength * 8)
        + (ServiceCircuitStart.LongLength * 4) + (ServiceCircuitList.LongLength * 4) + (CarrierStart.LongLength * 4) + (Carriers.LongLength * 4)
        + ((EquipmentPortStart.LongLength + EquipmentPorts.LongLength + CableEndStart.LongLength + CableEnds.LongLength
            + SiteEquipmentStart.LongLength + SiteEquipment.LongLength) * 4);
}
