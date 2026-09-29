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

    /// <summary>The data version the graph was built from (see <see cref="GraphLoader.DataVersionAsync"/>).</summary>
    public required string Version { get; init; }

    public int NodeCount => TerminalIds.Length;

    /// <summary>Undirected edges; each is stored once per direction.</summary>
    public int EdgeCount => EdgeTargets.Length / 2;

    public int CircuitCount => CircuitIds.Length;

    public bool TryGetNode(long terminalId, out int node)
    {
        node = Array.BinarySearch(TerminalIds, terminalId);
        return node >= 0;
    }

    public long TerminalId(int node) => TerminalIds[node];

    public TerminalKind KindOf(int node) => TerminalKinds[node];

    public ReadOnlySpan<int> Neighbours(int node) => EdgeTargets.AsSpan(EdgeStart[node], EdgeStart[node + 1] - EdgeStart[node]);

    public ReadOnlySpan<EdgeKind> NeighbourKinds(int node) => EdgeKinds.AsSpan(EdgeStart[node], EdgeStart[node + 1] - EdgeStart[node]);

    /// <summary>Equipment id of a port, or null for a conductor end.</summary>
    public long? EquipmentOf(int node) => TerminalKinds[node] == TerminalKind.Port ? EquipmentIds[TerminalOwners[node]] : null;

    /// <summary>Cable id of a conductor end, or null for a port.</summary>
    public long? CableOf(int node) => TerminalKinds[node] == TerminalKind.ConductorEnd ? CableIds[ConductorCables[TerminalOwners[node]]] : null;

    /// <summary>Site of a port's equipment, or null for a conductor end.</summary>
    public long? SiteOf(int node) => TerminalKinds[node] == TerminalKind.Port ? SiteIds[EquipmentSites[TerminalOwners[node]]] : null;

    /// <summary>Rough size of the arrays in bytes, for diagnostics.</summary>
    public long ApproximateBytes =>
        (TerminalIds.LongLength * 8) + TerminalKinds.LongLength + (TerminalOwners.LongLength * 4)
        + (EdgeStart.LongLength * 4) + (EdgeTargets.LongLength * 4) + EdgeKinds.LongLength + EdgeLifecycles.LongLength
        + (EquipmentIds.LongLength * 8) + (EquipmentSites.LongLength * 4) + (SiteIds.LongLength * 8)
        + (ConductorIds.LongLength * 8) + (ConductorCables.LongLength * 4) + (CableIds.LongLength * 8) + CableLifecycles.LongLength
        + (CircuitIds.LongLength * 8) + CircuitLayers.LongLength + (HopStart.LongLength * 4) + (HopNodes.LongLength * 4)
        + (NodeCircuitStart.LongLength * 4) + (NodeCircuits.LongLength * 4) + (DependentStart.LongLength * 4) + (Dependents.LongLength * 4)
        + (CircuitServiceStart.LongLength * 4) + (CircuitServices.LongLength * 4) + (ServiceIds.LongLength * 8);
}
