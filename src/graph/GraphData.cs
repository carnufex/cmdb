namespace Cmdb.Graph;

public enum TerminalKind : byte
{
    Port = 0,
    ConductorEnd = 1,
}

/// <summary>Edge kinds: the four connection kinds, plus the conductor that joins its two ends.</summary>
public enum EdgeKind : byte
{
    Patch = 0,
    Splice = 1,
    Termination = 2,
    Internal = 3,
    Conductor = 4,
}

/// <summary>Lifecycle in the order of the Postgres enum, so it can be compared (planned &lt; in_service).</summary>
public enum Lifecycle : byte
{
    Planned = 0,
    UnderConstruction = 1,
    InService = 2,
    Decommissioning = 3,
    Removed = 4,
}

public enum CircuitLayer : byte
{
    Physical = 0,
    Transmission = 1,
    Logical = 2,
}

/// <summary>
/// The rows a graph is built from, column by column, with external ids as stored in Postgres. Filled by the database
/// loader, or directly from a generated network in tests and benchmarks.
/// </summary>
public sealed class GraphData
{
    public List<long> PortTerminals { get; } = [];
    public List<long> PortEquipment { get; } = [];

    public List<long> EndTerminals { get; } = [];
    public List<long> EndConductors { get; } = [];

    public List<long> EquipmentIds { get; } = [];
    public List<long> EquipmentSites { get; } = [];

    public List<long> ConductorIds { get; } = [];
    public List<long> ConductorCables { get; } = [];

    public List<long> CableIds { get; } = [];
    public List<byte> CableLifecycles { get; } = [];

    public List<long> ConnectionA { get; } = [];
    public List<long> ConnectionB { get; } = [];
    public List<byte> ConnectionKinds { get; } = [];
    public List<byte> ConnectionLifecycles { get; } = [];

    public List<long> CircuitIds { get; } = [];
    public List<byte> CircuitLayers { get; } = [];

    /// <summary>Hops ordered by circuit, then sequence.</summary>
    public List<long> HopCircuits { get; } = [];
    public List<long> HopTerminals { get; } = [];

    public List<long> DependencyCircuits { get; } = [];
    public List<long> DependencyCarriers { get; } = [];

    public List<long> ServiceCircuitServices { get; } = [];
    public List<long> ServiceCircuitCircuits { get; } = [];
}
