using Cmdb.Catalog;

namespace Cmdb.DataGen;

internal enum SiteKind
{
    Hub,
    Aggregation,
    Radio,
    Cabinet,
    Splice,

    /// <summary>A manhole where conduit corridors meet (ADR-0014, #235).</summary>
    Manhole,
}

internal enum ConnectionKind : byte
{
    Patch,
    Splice,
    Termination,
    Internal,
}

internal static class Lifecycle
{
    public const string Planned = "planned";
    public const string UnderConstruction = "under_construction";
    public const string InService = "in_service";
}

internal sealed class Site
{
    public required long Id { get; init; }

    /// <summary>Access sites get code and name once their kind is known.</summary>
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required SiteKind Kind { get; set; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public string Lifecycle { get; set; } = DataGen.Lifecycle.InService;

    /// <summary>Next site towards the aggregation (access) or the hub (aggregation ring position).</summary>
    public Site? Parent { get; set; }
    public List<Site> Children { get; } = [];
    public int Depth { get; set; }
    public Site? Aggregation { get; set; }
    public Site? Hub { get; set; }

    /// <summary>Cable towards <see cref="Parent"/> for access sites.</summary>
    public Cable? Uplink { get; set; }

    /// <summary>The rack all equipment on the site is mounted in (0 for splice points).</summary>
    public long RackId { get; set; }

    /// <summary>The catalog's site type for <see cref="Kind"/>, by role (#219).</summary>
    public string SiteType { get; set; } = "";

    public double DistanceTo(Site other) => Math.Sqrt(((X - other.X) * (X - other.X)) + ((Y - other.Y) * (Y - other.Y)));
}

internal sealed record LocationRow(long Id, long SiteId, long? ParentId, string Kind, string Name, short? RackUnits, string Lifecycle);

/// <summary>A piece of equipment. Its ports are the terminals FirstTerminal .. FirstTerminal + Ports.Count - 1.</summary>
internal sealed class Equipment
{
    public required long Id { get; init; }
    public required EquipmentType Type { get; init; }
    public required long SiteId { get; init; }
    public long? LocationId { get; init; }
    public long? ParentId { get; init; }
    public string? Slot { get; init; }
    public required string Name { get; init; }
    public required string Attributes { get; init; }
    public required string Lifecycle { get; init; }
    public required long FirstTerminal { get; init; }
    public required PortLayout Layout { get; init; }

    public IReadOnlyList<Port> Ports => Layout.Ports;

    public long Terminal(string portName) => FirstTerminal + Layout.IndexOf(portName);

    public long TerminalAt(int index) => FirstTerminal + index;
}

/// <summary>Expanded ports of a type in a slot, with a name index. Shared by all equipment of that type and slot.</summary>
internal sealed class PortLayout(IReadOnlyList<Port> ports)
{
    private readonly Dictionary<string, int> _index = ports.Select((p, i) => (p.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);

    public IReadOnlyList<Port> Ports { get; } = ports;

    public int IndexOf(string name) => _index.TryGetValue(name, out var i) ? i : throw new KeyNotFoundException($"No port '{name}'.");
}

/// <summary>
/// A cable between two sites. Conductor n (1-based) has id FirstConductor + n - 1 and end terminals
/// FirstEndTerminal + 2(n - 1) at A and + 1 at B.
/// </summary>
internal sealed class Cable
{
    public required long Id { get; init; }
    public required CableType Type { get; init; }
    public required string Code { get; init; }
    public required Site A { get; init; }
    public required Site B { get; init; }
    /// <summary>The route; a cable laid in conduit gets the route of its route segments (#235).</summary>
    public required double[] Coordinates { get; set; }
    public required string Lifecycle { get; init; }
    public required long FirstConductor { get; init; }
    public required long FirstEndTerminal { get; init; }

    /// <summary>ODF port terminal per conductor at each end, or null where the cable is not terminated.</summary>
    public long[]? OdfPortsA { get; set; }
    public long[]? OdfPortsB { get; set; }

    private int _nextFree = 1;

    public int Count => Type.ConductorCount;

    public long End(int n, Site at) => FirstEndTerminal + (2 * (n - 1)) + (at == A ? 0 : 1);

    /// <summary>The ODF port conductor n is spliced to at the given end.</summary>
    public long OdfPort(int n, Site at) =>
        (at == A ? OdfPortsA : OdfPortsB)?[n - 1]
        ?? throw new InvalidOperationException($"Cable {Code} is not terminated at {at.Code}.");

    public int Allocate()
    {
        if (_nextFree > Count)
        {
            throw new InvalidOperationException($"Cable {Code} is full.");
        }
        return _nextFree++;
    }

    /// <summary>Reserves a specific conductor, used where numbering must continue straight through a splice point.</summary>
    public void Reserve(int n) => _nextFree = Math.Max(_nextFree, n + 1);
}

internal readonly record struct Connection(long A, long B, ConnectionKind Kind, bool Planned);

internal readonly record struct ChannelRow(long Id, long TerminalId, string Kind, int Number);

internal sealed record CircuitRow(long Id, string Code, string Layer, long A, long B, string Lifecycle);

internal readonly record struct Hop(long CircuitId, int Seq, long TerminalId, long? ChannelId);

internal sealed record ServiceRow(long Id, string Code, string Name, string Type, string Attributes, string Lifecycle);

/// <summary>A stretch of trench or other conduit between two sites (ADR-0014); <see cref="Coordinates"/> run from A to B.</summary>
internal sealed record RouteSegmentRow(long Id, string Code, long A, long B, string Construction, string? Owner, double[] Coordinates, string Lifecycle);

/// <summary>A duct on one route segment.</summary>
internal sealed record DuctRow(long Id, string Code, string TypeKey, long Segment, string Lifecycle);

internal readonly record struct SubductRow(long Id, long Duct, int Number, string? Color, string Occupancy);

/// <summary>Subduct number <see cref="Seq"/> of a cable's way from its A end.</summary>
internal readonly record struct CablePathRow(long Cable, int Seq, long Subduct);

/// <summary>Everything the generator produced, in insertion order. Ids are final.</summary>
internal sealed class Network
{
    /// <summary>The catalog the network was built from; the loader syncs it into the database.</summary>
    public TypeCatalog Catalog { get; set; } = TypeCatalog.Current;

    public List<Site> Sites { get; } = [];
    public List<LocationRow> Locations { get; } = [];
    public List<Equipment> Equipment { get; } = [];
    public List<Cable> Cables { get; } = [];
    public List<Connection> Connections { get; } = [];
    public List<ChannelRow> Channels { get; } = [];
    public List<CircuitRow> Circuits { get; } = [];
    public List<Hop> Hops { get; } = [];
    public List<(long Circuit, long Carrier)> Dependencies { get; } = [];
    public List<ServiceRow> Services { get; } = [];
    public List<(long Service, long Circuit)> ServiceCircuits { get; } = [];
    public List<RouteSegmentRow> RouteSegments { get; } = [];
    public List<DuctRow> Ducts { get; } = [];
    public List<SubductRow> Subducts { get; } = [];
    public List<CablePathRow> CablePaths { get; } = [];

    public long Terminals { get; set; }

    public long Ports => Equipment.Sum(e => (long)e.Ports.Count);

    public long Conductors => Cables.Sum(c => (long)c.Count);

    public IEnumerable<(string Name, long Count)> Counts() =>
    [
        ("sites", Sites.Count),
        ("locations", Locations.Count),
        ("equipment", Equipment.Count),
        ("ports", Ports),
        ("cables", Cables.Count),
        ("conductors", Conductors),
        ("terminals", Terminals),
        ("connections", Connections.Count),
        ("channels", Channels.Count),
        ("circuits", Circuits.Count),
        ("circuit hops", Hops.Count),
        ("services", Services.Count),
        ("route segments", RouteSegments.Count),
        ("ducts", Ducts.Count),
        ("subducts", Subducts.Count),
        ("cable paths", CablePaths.Count),
    ];
}
