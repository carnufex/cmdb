namespace Cmdb.Graph;

/// <summary>A change a plan makes to the graph (#24, #107), applied in order.</summary>
public abstract record GraphChange;

/// <summary>A connection a plan adds or removes (#24), by external terminal ids.</summary>
/// <param name="Kind">The kind of the connection; ignored when removing, which removes every connection between the two.</param>
public sealed record GraphEdgeChange(long A, long B, EdgeKind Kind, bool Add) : GraphChange;

/// <summary>A site a plan creates (#107). Planned ids are negative, so they never meet production's.</summary>
public sealed record GraphNewSite(long Id) : GraphChange;

/// <summary>Equipment a plan creates at a site that exists or is planned, with its ports' terminal ids.</summary>
public sealed record GraphNewEquipment(long Id, long SiteId, IReadOnlyList<long> Ports) : GraphChange;

/// <summary>A cable a plan creates, with each conductor's id and the terminal ids of its two ends.</summary>
public sealed record GraphNewCable(long Id, IReadOnlyList<GraphNewConductor> Conductors) : GraphChange;

public sealed record GraphNewConductor(long Id, long EndA, long EndB);

/// <summary>Why a change had no effect on the view: the plan says something production no longer allows.</summary>
public enum GraphChangeProblem
{
    UnknownTerminal,
    AlreadyConnected,
    NotConnected,
    SameTerminal,

    /// <summary>
    /// A terminal takes one connection of each kind (#25): one patch at the front of a port and one splice or
    /// termination at the back. Adding a second of the same kind is a conflict.
    /// </summary>
    Occupied,

    /// <summary>A planned object's id is taken.</summary>
    InvalidObject,
}

public sealed record GraphChangeIssue(int Index, GraphChange Change, GraphChangeProblem Problem);

/// <summary>
/// What a plan view adds to the base: replacement adjacency for the nodes it touches, and planned terminals, equipment,
/// sites, conductors and cables (#107), indexed after the base's own. Every other node reads the base arrays.
/// </summary>
internal sealed class GraphOverlay
{
    public Dictionary<int, (int[] Targets, EdgeKind[] Kinds)> Edges { get; init; } = [];

    public List<long> TerminalIds { get; init; } = [];
    public List<TerminalKind> Kinds { get; init; } = [];
    public List<int> Owners { get; init; } = [];
    public Dictionary<long, int> NodeById { get; init; } = [];

    public List<long> EquipmentIds { get; init; } = [];
    public List<int> EquipmentSites { get; init; } = [];
    public List<int[]> EquipmentPorts { get; init; } = [];
    public Dictionary<long, int> EquipmentById { get; init; } = [];

    public List<long> SiteIds { get; init; } = [];
    public Dictionary<long, int> SiteById { get; init; } = [];

    /// <summary>All equipment at sites that got planned equipment, base and planned together.</summary>
    public Dictionary<int, int[]> SiteEquipment { get; init; } = [];

    public List<long> ConductorIds { get; init; } = [];
    public List<int> ConductorCables { get; init; } = [];

    public List<long> CableIds { get; init; } = [];
    public List<int[]> CableEnds { get; init; } = [];
    public Dictionary<long, int> CableById { get; init; } = [];

    public GraphOverlay Copy() => new()
    {
        Edges = new(Edges),
        TerminalIds = [.. TerminalIds],
        Kinds = [.. Kinds],
        Owners = [.. Owners],
        NodeById = new(NodeById),
        EquipmentIds = [.. EquipmentIds],
        EquipmentSites = [.. EquipmentSites],
        EquipmentPorts = [.. EquipmentPorts],
        EquipmentById = new(EquipmentById),
        SiteIds = [.. SiteIds],
        SiteById = new(SiteById),
        SiteEquipment = new(SiteEquipment),
        ConductorIds = [.. ConductorIds],
        ConductorCables = [.. ConductorCables],
        CableIds = [.. CableIds],
        CableEnds = [.. CableEnds],
        CableById = new(CableById),
    };
}

public sealed partial class Graph
{
    private GraphOverlay? _overlay;
    private Graph? _base;

    /// <summary>The production graph a plan view was made from, or this graph itself.</summary>
    public Graph Base => _base ?? this;

    /// <summary>Whether this is a plan view (base + delta).</summary>
    public bool IsOverlay => _overlay is not null;

    /// <summary>Nodes whose connections differ from the base, for diagnostics.</summary>
    public int OverlayNodes => _overlay?.Edges.Count ?? 0;

    /// <summary>
    /// A view of this graph with the plan's changes applied in order (ADR-0005, #24, #107): all arrays are shared, only
    /// the adjacency of the touched nodes is replaced, and planned objects get indexes after the base's own, so a plan
    /// costs memory in proportion to its size, not the network's. Changes that do not fit (unknown terminal, already
    /// connected, not connected, occupied) are skipped and reported. Circuits and services are unchanged: a plan does
    /// not reroute existing circuits.
    /// </summary>
    public (Graph View, IReadOnlyList<GraphChangeIssue> Issues) WithChanges(IReadOnlyList<GraphChange> changes)
    {
        var view = (Graph)MemberwiseClone();
        var overlay = _overlay?.Copy() ?? new GraphOverlay();
        view._overlay = overlay;
        view._base = Base;
        var working = new Dictionary<int, (List<int> Targets, List<EdgeKind> Kinds)>();
        var issues = new List<GraphChangeIssue>();

        (List<int> Targets, List<EdgeKind> Kinds) Edit(int node)
        {
            if (!working.TryGetValue(node, out var list))
            {
                list = ([.. view.Neighbours(node)], [.. view.NeighbourKinds(node)]);
                working[node] = list;
            }
            return list;
        }

        int AddNode(long id, TerminalKind kind, int owner)
        {
            overlay.NodeById[id] = overlay.TerminalIds.Count;
            overlay.TerminalIds.Add(id);
            overlay.Kinds.Add(kind);
            overlay.Owners.Add(owner);
            var node = TerminalIds.Length + overlay.TerminalIds.Count - 1;
            working[node] = ([], []);
            return node;
        }

        int SiteIndex(long siteId)
        {
            if (view.TryGetSite(siteId, out var site))
            {
                return site;
            }
            // A planned site, or one in production that has no equipment yet and so is not in the graph.
            overlay.SiteById[siteId] = overlay.SiteIds.Count;
            overlay.SiteIds.Add(siteId);
            return SiteIds.Length + overlay.SiteIds.Count - 1;
        }

        for (var i = 0; i < changes.Count; i++)
        {
            switch (changes[i])
            {
                case GraphNewSite site:
                    if (view.TryGetSite(site.Id, out _))
                    {
                        issues.Add(new(i, site, GraphChangeProblem.InvalidObject));
                        break;
                    }
                    SiteIndex(site.Id);
                    break;

                case GraphNewEquipment equipment:
                    {
                        if (view.TryGetEquipment(equipment.Id, out _) || equipment.Ports.Any(p => view.TryGetNode(p, out _)))
                        {
                            issues.Add(new(i, equipment, GraphChangeProblem.InvalidObject));
                            break;
                        }
                        var site = SiteIndex(equipment.SiteId);
                        var index = EquipmentIds.Length + overlay.EquipmentIds.Count;
                        var existing = view.EquipmentAt(site).ToArray();
                        overlay.EquipmentById[equipment.Id] = overlay.EquipmentIds.Count;
                        overlay.EquipmentIds.Add(equipment.Id);
                        overlay.EquipmentSites.Add(site);
                        overlay.EquipmentPorts.Add([.. equipment.Ports.Select(p => AddNode(p, TerminalKind.Port, index))]);
                        overlay.SiteEquipment[site] = [.. existing, index];
                        break;
                    }

                case GraphNewCable cable:
                    {
                        if (view.TryGetCable(cable.Id, out _)
                            || cable.Conductors.Any(c => view.TryGetNode(c.EndA, out _) || view.TryGetNode(c.EndB, out _)))
                        {
                            issues.Add(new(i, cable, GraphChangeProblem.InvalidObject));
                            break;
                        }
                        var index = CableIds.Length + overlay.CableIds.Count;
                        overlay.CableById[cable.Id] = overlay.CableIds.Count;
                        overlay.CableIds.Add(cable.Id);
                        var ends = new List<int>();
                        foreach (var conductor in cable.Conductors)
                        {
                            var owner = ConductorIds.Length + overlay.ConductorIds.Count;
                            overlay.ConductorIds.Add(conductor.Id);
                            overlay.ConductorCables.Add(index);
                            var a = AddNode(conductor.EndA, TerminalKind.ConductorEnd, owner);
                            var b = AddNode(conductor.EndB, TerminalKind.ConductorEnd, owner);
                            working[a].Targets.Add(b);
                            working[a].Kinds.Add(EdgeKind.Conductor);
                            working[b].Targets.Add(a);
                            working[b].Kinds.Add(EdgeKind.Conductor);
                            ends.Add(a);
                            ends.Add(b);
                        }
                        overlay.CableEnds.Add([.. ends]);
                        break;
                    }

                case GraphEdgeChange change:
                    {
                        if (!view.TryGetNode(change.A, out var a) || !view.TryGetNode(change.B, out var b))
                        {
                            issues.Add(new(i, change, GraphChangeProblem.UnknownTerminal));
                            break;
                        }
                        if (a == b)
                        {
                            issues.Add(new(i, change, GraphChangeProblem.SameTerminal));
                            break;
                        }
                        var ea = Edit(a);
                        var eb = Edit(b);
                        if (change.Add)
                        {
                            if (IndexOfConnection(ea, b) >= 0)
                            {
                                issues.Add(new(i, change, GraphChangeProblem.AlreadyConnected));
                                break;
                            }
                            if (ea.Kinds.Contains(change.Kind) || eb.Kinds.Contains(change.Kind))
                            {
                                issues.Add(new(i, change, GraphChangeProblem.Occupied));
                                break;
                            }
                            ea.Targets.Add(b);
                            ea.Kinds.Add(change.Kind);
                            eb.Targets.Add(a);
                            eb.Kinds.Add(change.Kind);
                        }
                        else
                        {
                            var removed = RemoveConnections(ea, b);
                            RemoveConnections(eb, a);
                            if (removed == 0)
                            {
                                issues.Add(new(i, change, GraphChangeProblem.NotConnected));
                            }
                        }
                        break;
                    }

                default:
                    throw new ArgumentException($"Unknown change {changes[i].GetType().Name}.", nameof(changes));
            }
        }

        foreach (var (node, list) in working)
        {
            overlay.Edges[node] = ([.. list.Targets], [.. list.Kinds]);
        }
        return (view, issues);
    }

    /// <summary>A connection, not the conductor joining a cable's two ends.</summary>
    private static int IndexOfConnection((List<int> Targets, List<EdgeKind> Kinds) edges, int target)
    {
        for (var i = 0; i < edges.Targets.Count; i++)
        {
            if (edges.Targets[i] == target && edges.Kinds[i] != EdgeKind.Conductor)
            {
                return i;
            }
        }
        return -1;
    }

    private static int RemoveConnections((List<int> Targets, List<EdgeKind> Kinds) edges, int target)
    {
        var removed = 0;
        for (var i = edges.Targets.Count - 1; i >= 0; i--)
        {
            if (edges.Targets[i] == target && edges.Kinds[i] != EdgeKind.Conductor)
            {
                edges.Targets.RemoveAt(i);
                edges.Kinds.RemoveAt(i);
                removed++;
            }
        }
        return removed;
    }
}
