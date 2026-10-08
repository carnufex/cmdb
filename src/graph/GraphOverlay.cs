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

// Structure the change stream removes, moves or rebuilds (#123). Internal: a plan removes and moves objects through
// their connections (#172, #187) and keeps them in its view until it is applied.

/// <summary>Equipment removed with its ports and their connections.</summary>
internal sealed record GraphRemoveEquipment(long Id) : GraphChange;

/// <summary>A cable removed with its conductors, their ends and the connections on them.</summary>
internal sealed record GraphRemoveCable(long Id) : GraphChange;

/// <summary>A port removed from its equipment or an end from its conductor, with its connections.</summary>
internal sealed record GraphRemoveTerminal(long Terminal) : GraphChange;

/// <summary>A conductor removed from its cable, with its ends and the connections on them.</summary>
internal sealed record GraphRemoveConductor(long Id) : GraphChange;

/// <summary>Equipment at another site, ports and connections kept.</summary>
internal sealed record GraphMoveEquipment(long Id, long SiteId) : GraphChange;

/// <summary>New ports on equipment that is in the graph.</summary>
internal sealed record GraphAddPorts(long EquipmentId, IReadOnlyList<long> Ports) : GraphChange;

/// <summary>A new conductor in a cable that is in the graph.</summary>
internal sealed record GraphAddConductor(long CableId, GraphNewConductor Conductor) : GraphChange;

/// <summary>New ends on a conductor that is in the graph, joined to the end it kept.</summary>
internal sealed record GraphAddEnds(long ConductorId, IReadOnlyList<long> Ends) : GraphChange;

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

    /// <summary>A circuit rides on one that does not exist, or a removed one still carries others (#121).</summary>
    UnknownCircuit,

    /// <summary>Something to be removed still carries a circuit (#172): the services would break.</summary>
    CarriesCircuits,
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
    public Dictionary<long, int> ConductorById { get; init; } = [];

    public List<long> CableIds { get; init; } = [];
    public List<int[]> CableEnds { get; init; } = [];
    public Dictionary<long, int> CableById { get; init; } = [];

    // What the change stream removed, moved or rebuilt (#123), by index: the arrays' own and the delta's alike. Removed
    // objects keep their index, so the index spaces only grow until the delta is folded in.
    public HashSet<int> RemovedNodes { get; init; } = [];
    public HashSet<int> RemovedEquipment { get; init; } = [];
    public HashSet<int> RemovedConductors { get; init; } = [];
    public HashSet<int> RemovedCables { get; init; } = [];

    /// <summary>Sites whose last equipment was removed or moved away: not in the graph, as after a rebuild.</summary>
    public HashSet<int> RemovedSites { get; init; } = [];

    /// <summary>The site of equipment of the arrays that moved.</summary>
    public Dictionary<int, int> MovedEquipment { get; init; } = [];

    /// <summary>Ports of equipment of the arrays whose ports changed, and ends of such cables.</summary>
    public Dictionary<int, int[]> Ports { get; init; } = [];
    public Dictionary<int, int[]> Ends { get; init; } = [];

    /// <summary>Circuits changed by the change stream (#121); null until one is.</summary>
    public CircuitOverlay? Circuits { get; set; }

    public GraphOverlay Copy() => new()
    {
        Circuits = Circuits?.Copy(),
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
        ConductorById = new(ConductorById),
        RemovedNodes = [.. RemovedNodes],
        RemovedEquipment = [.. RemovedEquipment],
        RemovedConductors = [.. RemovedConductors],
        RemovedCables = [.. RemovedCables],
        RemovedSites = [.. RemovedSites],
        MovedEquipment = new(MovedEquipment),
        Ports = new(Ports),
        Ends = new(Ends),
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

    /// <summary>Whether this graph is its arrays plus a delta: a plan view, or production between compactions (#81).</summary>
    public bool IsOverlay => _overlay is not null;

    /// <summary>Nodes whose connections differ from the base, for diagnostics.</summary>
    public int OverlayNodes => _overlay?.Edges.Count ?? 0;

    /// <summary>
    /// This view as production (#81): changes from the change stream applied as a delta, at a new position. It is its
    /// own <see cref="Base"/>, so plan views are made on top of it. New equipment and cables (#119) are production
    /// objects here, with indexes after the arrays' own, and a plan's planned objects come after those.
    /// </summary>
    internal Graph AsProduction(string version)
    {
        var graph = (Graph)MemberwiseClone();
        graph._base = null;
        graph._version = version;
        return graph;
    }

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
            var site = view.SiteIndex(siteId);
            if (site >= 0)
            {
                return site;
            }
            // A planned site, or one in production that has no equipment yet and so is not in the graph.
            overlay.SiteById[siteId] = overlay.SiteIds.Count;
            overlay.SiteIds.Add(siteId);
            return SiteIds.Length + overlay.SiteIds.Count - 1;
        }

        void SetSiteEquipment(int site, int[] equipment)
        {
            overlay.SiteEquipment[site] = equipment;
            if (equipment.Length == 0)
            {
                overlay.RemovedSites.Add(site);
            }
            else
            {
                overlay.RemovedSites.Remove(site);
            }
        }

        void SetPorts(int equipment, int[] ports)
        {
            if (equipment < EquipmentIds.Length)
            {
                overlay.Ports[equipment] = ports;
            }
            else
            {
                overlay.EquipmentPorts[equipment - EquipmentIds.Length] = ports;
            }
        }

        void SetEnds(int cable, int[] ends)
        {
            if (cable < CableIds.Length)
            {
                overlay.Ends[cable] = ends;
            }
            else
            {
                overlay.CableEnds[cable - CableIds.Length] = ends;
            }
        }

        // A conductor and its two ends, joined.
        int[] AddConductor(int cable, GraphNewConductor conductor)
        {
            var owner = ConductorIds.Length + overlay.ConductorIds.Count;
            overlay.ConductorById[conductor.Id] = overlay.ConductorIds.Count;
            overlay.ConductorIds.Add(conductor.Id);
            overlay.ConductorCables.Add(cable);
            var a = AddNode(conductor.EndA, TerminalKind.ConductorEnd, owner);
            var b = AddNode(conductor.EndB, TerminalKind.ConductorEnd, owner);
            working[a].Targets.Add(b);
            working[a].Kinds.Add(EdgeKind.Conductor);
            working[b].Targets.Add(a);
            working[b].Kinds.Add(EdgeKind.Conductor);
            return [a, b];
        }

        // A terminal gone (#123): every edge on it goes from both sides, conductors too.
        void RemoveNode(int node)
        {
            var edges = Edit(node);
            foreach (var other in edges.Targets.Distinct().ToArray())
            {
                var back = Edit(other);
                for (var e = back.Targets.Count - 1; e >= 0; e--)
                {
                    if (back.Targets[e] == node)
                    {
                        back.Targets.RemoveAt(e);
                        back.Kinds.RemoveAt(e);
                    }
                }
            }
            working[node] = ([], []);
            overlay.RemovedNodes.Add(node);
            if (node >= TerminalIds.Length)
            {
                overlay.NodeById.Remove(view.TerminalId(node));
            }
        }

        void RemoveConductor(int conductor)
        {
            var cable = view.CableOfConductor(conductor);
            var ends = view.EndsOf(cable).ToArray();
            foreach (var end in ends.Where(e => view.OwnerOf(e) == conductor))
            {
                RemoveNode(end);
            }
            SetEnds(cable, [.. ends.Where(e => view.OwnerOf(e) != conductor)]);
            overlay.RemovedConductors.Add(conductor);
            if (conductor >= ConductorIds.Length)
            {
                overlay.ConductorById.Remove(view.ConductorId(conductor));
            }
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
                        SetSiteEquipment(site, [.. existing, index]);
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
                            ends.AddRange(AddConductor(index, conductor));
                        }
                        overlay.CableEnds.Add([.. ends]);
                        break;
                    }

                case GraphRemoveEquipment remove:
                    {
                        if (!view.TryGetEquipment(remove.Id, out var equipment))
                        {
                            issues.Add(new(i, remove, GraphChangeProblem.InvalidObject));
                            break;
                        }
                        foreach (var port in view.PortsOf(equipment).ToArray())
                        {
                            RemoveNode(port);
                        }
                        SetPorts(equipment, []);
                        var site = view.SiteIndexOfEquipment(equipment);
                        SetSiteEquipment(site, [.. view.EquipmentAt(site).ToArray().Where(e => e != equipment)]);
                        overlay.RemovedEquipment.Add(equipment);
                        if (equipment >= EquipmentIds.Length)
                        {
                            overlay.EquipmentById.Remove(remove.Id);
                        }
                        break;
                    }

                case GraphRemoveCable remove:
                    {
                        if (!view.TryGetCable(remove.Id, out var cable))
                        {
                            issues.Add(new(i, remove, GraphChangeProblem.InvalidObject));
                            break;
                        }
                        foreach (var conductor in view.EndsOf(cable).ToArray().Select(view.OwnerOf).Distinct().ToArray())
                        {
                            RemoveConductor(conductor);
                        }
                        overlay.RemovedCables.Add(cable);
                        if (cable >= CableIds.Length)
                        {
                            overlay.CableById.Remove(remove.Id);
                        }
                        break;
                    }

                case GraphRemoveTerminal remove:
                    {
                        if (!view.TryGetNode(remove.Terminal, out var node))
                        {
                            issues.Add(new(i, remove, GraphChangeProblem.UnknownTerminal));
                            break;
                        }
                        var owner = view.OwnerOf(node);
                        RemoveNode(node);
                        if (view.KindOf(node) == TerminalKind.Port)
                        {
                            SetPorts(owner, [.. view.PortsOf(owner).ToArray().Where(p => p != node)]);
                        }
                        else
                        {
                            var cable = view.CableOfConductor(owner);
                            SetEnds(cable, [.. view.EndsOf(cable).ToArray().Where(e => e != node)]);
                        }
                        break;
                    }

                case GraphRemoveConductor remove:
                    if (!view.TryGetConductor(remove.Id, out var removedConductor))
                    {
                        issues.Add(new(i, remove, GraphChangeProblem.InvalidObject));
                        break;
                    }
                    RemoveConductor(removedConductor);
                    break;

                case GraphMoveEquipment move:
                    {
                        if (!view.TryGetEquipment(move.Id, out var equipment))
                        {
                            issues.Add(new(i, move, GraphChangeProblem.InvalidObject));
                            break;
                        }
                        var from = view.SiteIndexOfEquipment(equipment);
                        var to = SiteIndex(move.SiteId);
                        if (from == to)
                        {
                            break;
                        }
                        SetSiteEquipment(from, [.. view.EquipmentAt(from).ToArray().Where(e => e != equipment)]);
                        SetSiteEquipment(to, [.. view.EquipmentAt(to).ToArray().Append(equipment).Order()]);
                        if (equipment < EquipmentIds.Length)
                        {
                            overlay.MovedEquipment[equipment] = to;
                        }
                        else
                        {
                            overlay.EquipmentSites[equipment - EquipmentIds.Length] = to;
                        }
                        break;
                    }

                case GraphAddPorts add:
                    {
                        if (!view.TryGetEquipment(add.EquipmentId, out var equipment) || add.Ports.Any(p => view.TryGetNode(p, out _)))
                        {
                            issues.Add(new(i, add, GraphChangeProblem.InvalidObject));
                            break;
                        }
                        SetPorts(equipment, [.. view.PortsOf(equipment).ToArray(), .. add.Ports.Select(p => AddNode(p, TerminalKind.Port, equipment))]);
                        break;
                    }

                case GraphAddConductor add:
                    {
                        if (!view.TryGetCable(add.CableId, out var cable) || view.TryGetConductor(add.Conductor.Id, out _)
                            || view.TryGetNode(add.Conductor.EndA, out _) || view.TryGetNode(add.Conductor.EndB, out _))
                        {
                            issues.Add(new(i, add, GraphChangeProblem.InvalidObject));
                            break;
                        }
                        SetEnds(cable, [.. view.EndsOf(cable).ToArray(), .. AddConductor(cable, add.Conductor)]);
                        break;
                    }

                case GraphAddEnds add:
                    {
                        if (!view.TryGetConductor(add.ConductorId, out var conductor) || add.Ends.Any(e => view.TryGetNode(e, out _)))
                        {
                            issues.Add(new(i, add, GraphChangeProblem.InvalidObject));
                            break;
                        }
                        var cable = view.CableOfConductor(conductor);
                        var ends = view.EndsOf(cable).ToArray();
                        // A conductor has two ends (the delta takes no others), so each new end joins the one there.
                        var joined = ends.Where(e => view.OwnerOf(e) == conductor).ToList();
                        var added = new List<int>();
                        foreach (var end in add.Ends)
                        {
                            var node = AddNode(end, TerminalKind.ConductorEnd, conductor);
                            foreach (var other in joined)
                            {
                                Edit(node).Targets.Add(other);
                                Edit(node).Kinds.Add(EdgeKind.Conductor);
                                Edit(other).Targets.Add(node);
                                Edit(other).Kinds.Add(EdgeKind.Conductor);
                            }
                            joined.Add(node);
                            added.Add(node);
                        }
                        SetEnds(cable, [.. ends, .. added]);
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

                case GraphCircuitsChange circuits:
                    view.ApplyCircuits(overlay, circuits, i, issues);
                    break;

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

    /// <summary>Whether a circuit still passes a terminal the delta removed (#123): its rows do not fit together yet.</summary>
    internal bool CarriesCircuitsOnRemovedNodes()
    {
        if (_overlay is null)
        {
            return false;
        }
        foreach (var node in _overlay.RemovedNodes)
        {
            if (CircuitsThrough(node).Length > 0)
            {
                return true;
            }
        }
        return false;
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
