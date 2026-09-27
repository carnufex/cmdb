using System.Globalization;
using System.Text.Json;
using Cmdb.Catalog;
using Cmdb.DataGen.Geo;

namespace Cmdb.DataGen;

/// <summary>
/// Builds a deterministic synthetic national network: a meshed backbone between hubs, aggregation rings per
/// hub and access branches around each aggregation site. The topology is random within a coarse outline of
/// the country and does not follow any real network. See issue #6.
/// </summary>
internal sealed class NetworkBuilder
{
    private const int MaxBranchDepth = 3;
    private static readonly int[] FiberSizes = [12, 24, 48, 96, 144, 288];
    private static readonly string[] FiberColors =
        ["blå", "orange", "grön", "brun", "grå", "vit", "röd", "svart", "gul", "violett", "rosa", "turkos"];

    private readonly Random _rng;
    private readonly Scale _scale;
    private readonly TypeCatalog _catalog;
    private readonly Territory _territory = new();
    private readonly Network _net = new();
    private readonly Dictionary<(string Type, string? Slot), PortLayout> _layouts = [];
    private readonly Dictionary<long, SiteState> _state = [];

    private long _nextLocation = 1;
    private long _nextEquipment = 1;
    private long _nextCable = 1;
    private long _nextConductor = 1;
    private long _nextChannel = 1;
    private long _nextCircuit = 1;
    private long _nextService = 1;
    private readonly Dictionary<string, int> _codeCounters = [];

    private NetworkBuilder(int seed, Scale scale, TypeCatalog catalog)
    {
        _rng = new Random(seed);
        _scale = scale;
        _catalog = catalog;
        _net.Terminals = 0;
    }

    public static Network Build(int seed, Scale scale, TypeCatalog catalog) => new NetworkBuilder(seed, scale, catalog).Run();

    /// <summary>Per-site working state that does not belong in the output.</summary>
    private sealed class SiteState
    {
        public List<long> Racks { get; } = [];
        public int NextRack { get; set; }
        public Dictionary<string, int> ModelCount { get; } = new(StringComparer.Ordinal);

        // Radio
        public Equipment? Baseband { get; set; }
        public Equipment? Ethernet { get; set; }

        // Cabinet
        public Equipment? Switch { get; set; }
        public Equipment? PatchPanel { get; set; }

        // Aggregation
        public Equipment? Router { get; set; }
        public List<Equipment> Switches { get; } = [];
        public int NextSwitchPort { get; set; }
        public int NextVlan { get; set; } = 100;
        public long RingCircuit { get; set; }
        public long HubPort { get; set; }

        // Cable termination
        public Equipment? Odf { get; set; }
        public int NextOdfPort { get; set; }
        public int OdfNeed { get; set; }

        // Hub
        public List<Equipment> Chassis { get; } = [];
        public List<long> FreeRouterPorts { get; } = [];
        public int NextCardSlot { get; set; }
    }

    private Network Run()
    {
        var hubs = PlaceHubs();
        var aggregations = PlaceAggregations(hubs);
        var access = PlaceAccess(aggregations);
        BuildBranches(aggregations, access);
        ClassifyAccess(access);
        var rings = BuildRings(hubs, aggregations);
        var backbone = BuildBackbone(hubs);

        foreach (var site in _net.Sites)
        {
            EquipSite(site);
        }

        // ODFs are sized for everything that terminates on the site, so cables share them.
        foreach (var (a, b) in backbone)
        {
            _state[a.Id].OdfNeed += 288;
            _state[b.Id].OdfNeed += 288;
        }
        foreach (var site in rings.SelectMany(r => r))
        {
            _state[site.Id].OdfNeed += 2 * 96;
        }
        foreach (var site in access)
        {
            var size = _catalog.FindCable(FiberCable(2 * Links(site)))!.ConductorCount;
            _state[site.Id].OdfNeed += site.Kind == SiteKind.Splice ? 0 : size;
            _state[site.Parent!.Id].OdfNeed += site.Parent.Kind == SiteKind.Splice ? 0 : size;
        }

        // Cables: backbone, rings, access branches (child to parent), then copper distribution.
        var backboneCables = backbone.Select(e => NewCable("fiber-288", e.A, e.B)).ToList();
        var ringCables = rings.Select(ring => Enumerable.Range(0, ring.Count)
            .Select(i => NewCable("fiber-96", ring[i], ring[(i + 1) % ring.Count]))
            .ToList()).ToList();
        foreach (var site in access)
        {
            site.Uplink = NewCable(FiberCable(2 * Links(site)), site, site.Parent!);
        }
        foreach (var site in access.Where(s => s.Kind == SiteKind.Cabinet && s.Depth >= 2))
        {
            if (_rng.NextDouble() < 0.1)
            {
                NewCable("copper-50", site, site.Parent!, terminate: false);
            }
        }
        foreach (var site in access.Where(s => s.Kind == SiteKind.Splice))
        {
            SpliceThrough(site);
        }

        for (var i = 0; i < backbone.Count; i++)
        {
            WireBackbone(backbone[i].A, backbone[i].B, backboneCables[i]);
        }
        for (var r = 0; r < rings.Count; r++)
        {
            WireRing(rings[r], ringCables[r]);
        }
        foreach (var site in access)
        {
            WireAccess(site);
        }
        return _net;
    }

    // ---------------------------------------------------------------- topology

    private List<Site> PlaceHubs()
    {
        var hubs = new List<Site>();
        var spacing = Math.Sqrt(_territory.Area / _scale.Hubs) * 0.55;
        while (hubs.Count < _scale.Hubs)
        {
            var placed = false;
            for (var attempt = 0; attempt < 2000 && !placed; attempt++)
            {
                var (x, y) = _territory.RandomPoint(_rng);
                if (hubs.All(h => Distance(h.X, h.Y, x, y) >= spacing))
                {
                    var n = hubs.Count + 1;
                    hubs.Add(NewSite(SiteKind.Hub, x, y, $"HUB-{n:000}", $"Nav {n}"));
                    placed = true;
                }
            }
            if (!placed)
            {
                spacing *= 0.9;
            }
        }
        foreach (var hub in hubs)
        {
            hub.Hub = hub;
        }
        return hubs;
    }

    private List<Site> PlaceAggregations(List<Site> hubs)
    {
        var aggregations = new List<Site>();
        for (var i = 1; i <= _scale.Aggregations; i++)
        {
            double x, y;
            if (_rng.NextDouble() < 0.7)
            {
                var centre = hubs[_rng.Next(hubs.Count)];
                (x, y) = _territory.RandomPointNear(_rng, centre.X, centre.Y, 35_000);
            }
            else
            {
                (x, y) = _territory.RandomPoint(_rng);
            }
            var site = NewSite(SiteKind.Aggregation, x, y, $"AGG-{i:0000}", $"Aggregering {i}");
            site.Hub = hubs.MinBy(h => Distance(h.X, h.Y, x, y));
            site.Aggregation = site;
            aggregations.Add(site);
        }
        return aggregations;
    }

    private List<Site> PlaceAccess(List<Site> aggregations)
    {
        var access = new List<Site>(_scale.Access);
        for (var i = 0; i < _scale.Access; i++)
        {
            var aggregation = aggregations[_rng.Next(aggregations.Count)];
            var (x, y) = _territory.RandomPointNear(_rng, aggregation.X, aggregation.Y, 4_000);
            var site = NewSite(SiteKind.Cabinet, x, y, "", "");
            site.Aggregation = aggregation;
            site.Hub = aggregation.Hub;
            access.Add(site);
        }
        return access;
    }

    /// <summary>Each access site hangs off the nearest already connected site of its aggregation, at most three hops out.</summary>
    private static void BuildBranches(List<Site> aggregations, List<Site> access)
    {
        var byAggregation = access.GroupBy(s => s.Aggregation!.Id).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var aggregation in aggregations)
        {
            if (!byAggregation.TryGetValue(aggregation.Id, out var members))
            {
                continue;
            }
            var connected = new List<Site> { aggregation };
            foreach (var site in members.OrderBy(s => s.DistanceTo(aggregation)).ThenBy(s => s.Id))
            {
                var parent = connected.Where(c => c.Depth < MaxBranchDepth).MinBy(site.DistanceTo)!;
                site.Parent = parent;
                site.Depth = parent.Depth + 1;
                parent.Children.Add(site);
                connected.Add(site);
            }
        }
    }

    private void ClassifyAccess(List<Site> access)
    {
        foreach (var site in access)
        {
            site.Kind = site.Children.Count == 1 && _rng.NextDouble() < 0.25 ? SiteKind.Splice
                : _rng.NextDouble() < 0.3 ? SiteKind.Radio
                : SiteKind.Cabinet;
            var r = _rng.NextDouble();
            site.Lifecycle = site.Kind == SiteKind.Splice ? Lifecycle.InService
                : r < 0.02 ? Lifecycle.Planned
                : r < 0.03 ? Lifecycle.UnderConstruction
                : Lifecycle.InService;
        }

        // Nothing can be in service behind a site that is not.
        foreach (var site in access.OrderBy(s => s.Depth).ThenBy(s => s.Id))
        {
            if (site.Parent!.Lifecycle == Lifecycle.Planned || (site.Parent.Lifecycle == Lifecycle.UnderConstruction && site.Lifecycle == Lifecycle.InService))
            {
                site.Lifecycle = site.Parent.Lifecycle;
            }
        }

        foreach (var site in access)
        {
            var (prefix, name) = site.Kind switch
            {
                SiteKind.Radio => ("RAD", "Radiosite"),
                SiteKind.Cabinet => ("SKP", "Skåp"),
                _ => ("SKV", "Skarvpunkt"),
            };
            var n = NextCode(prefix);
            site.Code = $"{prefix}-{n:000000}";
            site.Name = $"{name} {n}";
        }
    }

    /// <summary>Aggregation sites per hub, ordered by bearing and cut into rings of four to eight that start and end at the hub.</summary>
    private List<List<Site>> BuildRings(List<Site> hubs, List<Site> aggregations)
    {
        var rings = new List<List<Site>>();
        foreach (var hub in hubs)
        {
            var members = aggregations.Where(a => a.Hub == hub)
                .OrderBy(a => Math.Atan2(a.Y - hub.Y, a.X - hub.X)).ThenBy(a => a.Id).ToList();
            var i = 0;
            while (i < members.Count)
            {
                var size = _rng.Next(4, 9);
                if (members.Count - (i + size) < 3)
                {
                    size = members.Count - i;
                }
                var ring = new List<Site> { hub };
                ring.AddRange(members.Skip(i).Take(size));
                foreach (var site in ring.Skip(1))
                {
                    site.Parent = hub;
                }
                rings.Add(ring);
                i += size;
            }
        }
        return rings;
    }

    /// <summary>Minimum spanning tree between hubs plus each hub's nearest non-neighbour, which gives a mesh.</summary>
    private static List<(Site A, Site B)> BuildBackbone(List<Site> hubs)
    {
        var edges = new List<(Site A, Site B)>();
        var inTree = new HashSet<Site> { hubs[0] };
        while (inTree.Count < hubs.Count)
        {
            var (a, b) = inTree
                .SelectMany(t => hubs.Where(h => !inTree.Contains(h)).Select(h => (t, h)))
                .MinBy(e => e.t.DistanceTo(e.h));
            edges.Add(Ordered(a, b));
            inTree.Add(b);
        }
        foreach (var hub in hubs)
        {
            var extra = hubs.Where(h => h != hub && !edges.Contains(Ordered(hub, h))).MinBy(hub.DistanceTo);
            if (extra is not null)
            {
                edges.Add(Ordered(hub, extra));
            }
        }
        return edges;

        static (Site, Site) Ordered(Site a, Site b) => a.Id < b.Id ? (a, b) : (b, a);
    }

    // ---------------------------------------------------------------- equipment

    private void EquipSite(Site site)
    {
        var state = new SiteState();
        _state[site.Id] = state;
        if (site.Kind == SiteKind.Splice)
        {
            return;
        }

        var (building, room, racks) = site.Kind switch
        {
            SiteKind.Hub => ("Byggnad A", "Nodrum", 6),
            SiteKind.Aggregation => ("Byggnad A", "Nodrum", 3),
            SiteKind.Radio => ("Teknikbod", "Utrustningsrum", 1),
            _ => ("Skåp", "Skåpsutrymme", 1),
        };
        var buildingId = NewLocation(site, null, "building", building, null);
        var roomId = NewLocation(site, buildingId, "room", room, null);
        for (var r = 1; r <= racks; r++)
        {
            state.Racks.Add(NewLocation(site, roomId, "rack", $"Rack {r}", 42));
        }
        site.RackId = state.Racks[0];

        switch (site.Kind)
        {
            case SiteKind.Hub:
                for (var i = 0; i < 2; i++)
                {
                    var chassis = Add(site, "acme-cr-8");
                    state.Chassis.Add(chassis);
                    AddCard(site, chassis, "acme-lc-4c", "8");
                }
                var core = Add(site, "acme-cx-32");
                Connect(core.Terminal("et-0/0/1"), Card(state.Chassis[0], "8").Terminal("et-8/0/1"), ConnectionKind.Patch, site);
                Connect(core.Terminal("et-0/0/2"), Card(state.Chassis[1], "8").Terminal("et-8/0/1"), ConnectionKind.Patch, site);
                Add(site, "acme-ax-48");
                AddPower(site, batteries: 2, pdus: 2);
                break;

            case SiteKind.Aggregation:
                state.Router = Add(site, "acme-ar-10");
                AddAggregationSwitch(site, state);
                if (_rng.NextDouble() < 0.2)
                {
                    Add(site, "acme-sdh-63");
                }
                AddPower(site, batteries: 1, pdus: 1);
                break;

            case SiteKind.Radio:
                EquipRadio(site, state);
                break;

            case SiteKind.Cabinet:
                var r = _rng.NextDouble();
                state.Switch = Add(site, r < 0.5 ? "acme-ax-24" : r < 0.9 ? "acme-ax-48" : "acme-ax-48p");
                state.PatchPanel = Add(site, "acme-pp-24", label: $"Kundanslutning {site.Code}");
                for (var p = 1; p <= 12; p++)
                {
                    Connect(state.PatchPanel.Terminal(p.ToString(CultureInfo.InvariantCulture)), state.Switch.Terminal($"ge-0/0/{p}"), ConnectionKind.Patch, site);
                }
                if (_rng.NextDouble() < 0.2)
                {
                    Add(site, "acme-pdu-12");
                }
                break;

            default:
                break;
        }
    }

    private void EquipRadio(Site site, SiteState state)
    {
        state.Baseband = Add(site, "acme-bb-6");
        state.Ethernet = Add(site, "acme-ix-8");
        Connect(state.Baseband.Terminal("mgmt"), state.Ethernet.Terminal("ge-0/0/1"), ConnectionKind.Patch, site);
        var (_, pdu) = AddPower(site, batteries: 1, pdus: 1);
        var pduOut = 1;

        var sectors = _rng.Next(1, 4);
        for (var s = 0; s < sectors; s++)
        {
            var fourPort = _rng.NextDouble() < 0.6;
            var radio = Add(site, fourPort ? "acme-rr-4" : "acme-rr-2");
            var antenna = Add(site, "acme-ant-4p", sector: s);
            for (var p = 1; p <= (fourPort ? 4 : 2); p++)
            {
                Connect(radio.Terminal($"ant{p}"), antenna.Terminal($"port{p}"), ConnectionKind.Internal, site);
            }
            Connect(radio.Terminal(fourPort ? "cpri1" : "cpri"), state.Baseband.Terminal($"cpri{s + 1}"), ConnectionKind.Internal, site);
            Connect(radio.Terminal("dc"), pdu!.Terminal($"out{pduOut++}"), ConnectionKind.Internal, site);
        }
        if (_rng.NextDouble() < 0.2)
        {
            var link = Add(site, "acme-mw-1");
            Connect(link.Terminal("eth"), state.Ethernet.Terminal("ge-0/0/2"), ConnectionKind.Patch, site);
            Connect(link.Terminal("dc"), pdu!.Terminal($"out{pduOut}"), ConnectionKind.Internal, site);
        }
    }

    private (Equipment Rectifier, Equipment? Pdu) AddPower(Site site, int batteries, int pdus)
    {
        var rectifier = Add(site, "acme-rect-48");
        for (var b = 0; b < batteries; b++)
        {
            var battery = Add(site, "acme-bat-100");
            Connect(battery.Terminal("plus"), rectifier.Terminal($"out{8 - b}"), ConnectionKind.Internal, site);
        }
        Equipment? first = null;
        for (var p = 0; p < pdus; p++)
        {
            var pdu = Add(site, "acme-pdu-12");
            Connect(pdu.Terminal("in"), rectifier.Terminal($"out{p + 1}"), ConnectionKind.Internal, site);
            first ??= pdu;
        }
        return (rectifier, first);
    }

    private Equipment AddAggregationSwitch(Site site, SiteState state)
    {
        var sw = Add(site, "acme-ax-48");
        state.Switches.Add(sw);
        Connect(sw.Terminal("xe-0/1/1"), state.Router!.Terminal($"ge-0/0/{state.Switches.Count}"), ConnectionKind.Patch, site);
        state.NextSwitchPort = 0;
        return sw;
    }

    /// <summary>Next free access port on the aggregation switches, adding a switch when they are full.</summary>
    private (Equipment Switch, long Port) AggregationPort(Site aggregation)
    {
        var state = _state[aggregation.Id];
        if (state.NextSwitchPort >= 48)
        {
            AddAggregationSwitch(aggregation, state);
        }
        var sw = state.Switches[^1];
        return (sw, sw.Terminal($"ge-0/0/{++state.NextSwitchPort}"));
    }

    /// <summary>Next free line port on the hub routers, adding a 24-port card when needed.</summary>
    private long HubPort(Site hub)
    {
        var state = _state[hub.Id];
        if (state.FreeRouterPorts.Count == 0)
        {
            var slot = (state.NextCardSlot / 2) + 1;
            var chassis = state.Chassis[state.NextCardSlot % 2];
            if (slot > 7)
            {
                throw new InvalidOperationException($"Hub {hub.Code} has no free card slots.");
            }
            state.NextCardSlot++;
            var card = AddCard(hub, chassis, "acme-lc-24x", slot.ToString(CultureInfo.InvariantCulture));
            state.FreeRouterPorts.AddRange(Enumerable.Range(0, card.Ports.Count).Select(card.TerminalAt).Reverse());
        }
        var port = state.FreeRouterPorts[^1];
        state.FreeRouterPorts.RemoveAt(state.FreeRouterPorts.Count - 1);
        return port;
    }

    private Equipment Card(Equipment chassis, string slot) =>
        _net.Equipment.First(e => e.ParentId == chassis.Id && e.Slot == slot);

    private Equipment Add(Site site, string typeKey, int sector = 0, string? label = null)
    {
        var state = _state[site.Id];
        var rack = state.Racks[state.NextRack++ % state.Racks.Count];
        return NewEquipment(site, typeKey, rack, null, null, sector, label);
    }

    private Equipment AddCard(Site site, Equipment chassis, string typeKey, string slot) =>
        NewEquipment(site, typeKey, null, chassis.Id, slot, 0, null);

    private Equipment NewEquipment(Site site, string typeKey, long? locationId, long? parentId, string? slot, int sector, string? label)
    {
        var type = _catalog.Find(typeKey) ?? throw new InvalidOperationException($"Unknown type {typeKey}.");
        if (!_layouts.TryGetValue((typeKey, slot), out var layout))
        {
            layout = new PortLayout(PortExpansion.Expand(type, slot));
            _layouts[(typeKey, slot)] = layout;
        }
        var state = _state[site.Id];
        var n = state.ModelCount[type.Model] = state.ModelCount.GetValueOrDefault(type.Model) + 1;
        var id = _nextEquipment++;
        var equipment = new Equipment
        {
            Id = id,
            Type = type,
            SiteId = site.Id,
            LocationId = locationId,
            ParentId = parentId,
            Slot = slot,
            Name = $"{site.Code} {type.Model} {n}",
            Attributes = Attributes.For(typeKey, type.Category, id, site, _rng, sector, label),
            Lifecycle = site.Lifecycle,
            FirstTerminal = _net.Terminals + 1,
            Layout = layout,
        };
        _net.Terminals += layout.Ports.Count;
        _net.Equipment.Add(equipment);
        return equipment;
    }

    // ---------------------------------------------------------------- cables

    private static int Links(Site site) =>
        (site.Kind switch { SiteKind.Radio => 2, SiteKind.Cabinet => 1, _ => 0 }) + site.Children.Sum(Links);

    private static string FiberCable(int needed) => $"fiber-{FiberSizes.First(s => s >= Math.Max(needed, 24))}";

    private Cable NewCable(string typeKey, Site a, Site b, bool terminate = true)
    {
        var type = _catalog.FindCable(typeKey) ?? throw new InvalidOperationException($"Unknown cable type {typeKey}.");
        var prefix = type.Medium == "fiber" ? "K" : "KK";
        var cable = new Cable
        {
            Id = _nextCable++,
            Type = type,
            Code = $"{prefix}-{NextCode(prefix):000000}",
            A = a,
            B = b,
            Coordinates = Route(a, b),
            Lifecycle = Weakest(a.Lifecycle, b.Lifecycle),
            FirstConductor = _nextConductor,
            FirstEndTerminal = _net.Terminals + 1,
        };
        _nextConductor += type.ConductorCount;
        _net.Terminals += 2L * type.ConductorCount;
        _net.Cables.Add(cable);

        if (terminate)
        {
            foreach (var end in new[] { a, b }.Where(s => s.Kind != SiteKind.Splice))
            {
                var ports = OdfPorts(end, type.ConductorCount);
                if (end == a)
                {
                    cable.OdfPortsA = ports;
                }
                else
                {
                    cable.OdfPortsB = ports;
                }
                for (var n = 1; n <= type.ConductorCount; n++)
                {
                    Connect(cable.End(n, end), ports[n - 1], ConnectionKind.Splice, end);
                }
            }
        }
        return cable;
    }

    /// <summary>
    /// Consecutive ports on the site's ODFs for terminating a cable. Cables on a site share ODFs, and a new one is
    /// mounted when the current one is full, sized for what is still needed.
    /// </summary>
    private long[] OdfPorts(Site site, int count)
    {
        var state = _state[site.Id];
        var ports = new long[count];
        for (var i = 0; i < count; i++)
        {
            if (state.Odf is null || state.NextOdfPort >= state.Odf.Ports.Count)
            {
                var remaining = Math.Max(state.OdfNeed, count - i);
                state.Odf = Add(site, remaining >= 96 ? "acme-odf-96" : remaining > 24 ? "acme-odf-48" : "acme-odf-24", label: $"ODF {site.Code}");
                state.NextOdfPort = 0;
            }
            ports[i] = state.Odf.TerminalAt(state.NextOdfPort++);
            state.OdfNeed--;
        }
        return ports;
    }

    /// <summary>A splice point joins its single branch straight through, conductor n to conductor n.</summary>
    private void SpliceThrough(Site site)
    {
        var down = site.Children[0].Uplink!;
        var up = site.Uplink!;
        for (var n = 1; n <= down.Count; n++)
        {
            Connect(down.End(n, site), up.End(n, site), ConnectionKind.Splice, site);
        }
    }

    /// <summary>A gently bending line between two sites, never a straight copy of anything real.</summary>
    private double[] Route(Site a, Site b)
    {
        var bends = _rng.Next(1, 4);
        var length = a.DistanceTo(b);
        var (nx, ny) = length > 0 ? (-(b.Y - a.Y) / length, (b.X - a.X) / length) : (0.0, 0.0);
        var coordinates = new double[(bends + 2) * 2];
        coordinates[0] = a.X;
        coordinates[1] = a.Y;
        for (var i = 1; i <= bends; i++)
        {
            var t = (double)i / (bends + 1);
            var offset = (_rng.NextDouble() - 0.5) * 0.15 * length;
            coordinates[2 * i] = a.X + ((b.X - a.X) * t) + (nx * offset);
            coordinates[(2 * i) + 1] = a.Y + ((b.Y - a.Y) * t) + (ny * offset);
        }
        coordinates[^2] = b.X;
        coordinates[^1] = b.Y;
        return coordinates;
    }

    // ---------------------------------------------------------------- wiring and circuits

    /// <summary>
    /// Patches <paramref name="start"/> into the first cable and follows <paramref name="path"/>, cross-connecting
    /// in the ODFs of intermediate sites and passing straight through splice points. Returns the ODF port where the
    /// path arrives; the traversed terminals are appended to <paramref name="hops"/>.
    /// </summary>
    private long Traverse(List<Site> path, List<Cable> cables, long start, List<long> hops, Site owner)
    {
        hops.Add(start);
        var fiber = cables[0].Allocate();
        var odf = cables[0].OdfPort(fiber, path[0]);
        Connect(start, odf, ConnectionKind.Patch, owner);
        hops.Add(odf);
        for (var i = 0; ; i++)
        {
            var cable = cables[i];
            var to = path[i + 1];
            hops.Add(cable.End(fiber, path[i]));
            hops.Add(cable.End(fiber, to));
            if (to.Kind == SiteKind.Splice)
            {
                cables[i + 1].Reserve(fiber);
                continue;
            }
            var arrive = cable.OdfPort(fiber, to);
            hops.Add(arrive);
            if (i == cables.Count - 1)
            {
                return arrive;
            }
            var next = cables[i + 1];
            var nextFiber = next.Allocate();
            var leave = next.OdfPort(nextFiber, to);
            Connect(arrive, leave, ConnectionKind.Patch, owner);
            hops.Add(leave);
            fiber = nextFiber;
        }
    }

    private void WireBackbone(Site a, Site b, Cable cable)
    {
        var muxA = Add(a, "acme-ot-40");
        var muxB = Add(b, "acme-ot-40");

        long Line(Equipment from, Equipment to, Site fromSite, Site toSite)
        {
            var hops = new List<long>();
            var arrive = Traverse([fromSite, toSite], [cable], from.Terminal("line-out"), hops, fromSite);
            var lineIn = to.Terminal("line-in");
            Connect(arrive, lineIn, ConnectionKind.Patch, toSite);
            hops.Add(lineIn);
            return NewCircuit("physical", hops, Lifecycle.InService);
        }
        var forward = Line(muxA, muxB, a, b);
        var backward = Line(muxB, muxA, b, a);

        var wavelengths = _rng.Next(2, 7);
        for (var w = 1; w <= wavelengths; w++)
        {
            var routerA = HubPort(a);
            var routerB = HubPort(b);
            var clientA = muxA.Terminal($"ch{w}");
            var clientB = muxB.Terminal($"ch{w}");
            Connect(routerA, clientA, ConnectionKind.Patch, a);
            Connect(routerB, clientB, ConnectionKind.Patch, b);
            var lambdaOut = NewChannel(muxA.Terminal("line-out"), "wavelength", w);
            var lambdaIn = NewChannel(muxB.Terminal("line-in"), "wavelength", w);
            var transmission = NewCircuit("transmission",
                [routerA, clientA, muxA.Terminal("line-out"), muxB.Terminal("line-in"), clientB, routerB],
                Lifecycle.InService,
                [null, null, lambdaOut, lambdaIn, null, null]);
            _net.Dependencies.Add((transmission, forward));
            _net.Dependencies.Add((transmission, backward));
            var service = NewService($"Stamnätslänk {a.Code}–{b.Code} λ{w}", "core-link", """{"capacityGbps":100}""", Lifecycle.InService);
            _net.ServiceCircuits.Add((service, transmission));
        }
    }

    /// <summary>Each aggregation router gets a path to the hub along the shorter way round its ring.</summary>
    private void WireRing(List<Site> ring, List<Cable> cables)
    {
        for (var i = 1; i < ring.Count; i++)
        {
            var site = ring[i];
            var state = _state[site.Id];
            var clockwise = i <= ring.Count - i;
            var path = new List<Site>();
            var pathCables = new List<Cable>();
            if (clockwise)
            {
                for (var j = i; j > 0; j--)
                {
                    path.Add(ring[j]);
                    pathCables.Add(cables[j - 1]);
                }
            }
            else
            {
                for (var j = i; j < ring.Count; j++)
                {
                    path.Add(ring[j]);
                    pathCables.Add(cables[j]);
                }
            }
            path.Add(ring[0]);

            var hops = new List<long>();
            var arrive = Traverse(path, pathCables, state.Router!.Terminal("xe-0/1/1"), hops, site);
            var hubPort = HubPort(ring[0]);
            Connect(arrive, hubPort, ConnectionKind.Patch, ring[0]);
            hops.Add(hubPort);
            state.HubPort = hubPort;
            state.RingCircuit = NewCircuit("physical", hops, Lifecycle.InService);
        }
    }

    private void WireAccess(Site site)
    {
        if (site.Kind == SiteKind.Splice)
        {
            return;
        }
        var state = _state[site.Id];
        var aggregation = site.Aggregation!;
        var aggregationState = _state[aggregation.Id];

        // The branch towards the aggregation site.
        var path = new List<Site>();
        var cables = new List<Cable>();
        for (var s = site; s != aggregation; s = s.Parent!)
        {
            path.Add(s);
            cables.Add(s.Uplink!);
        }
        path.Add(aggregation);

        (long Circuit, Equipment Switch, long Port) Physical(long start)
        {
            var hops = new List<long>();
            var arrive = Traverse(path, cables, start, hops, site);
            var (sw, port) = AggregationPort(aggregation);
            Connect(arrive, port, ConnectionKind.Patch, aggregation);
            hops.Add(port);
            return (NewCircuit("physical", hops, site.Lifecycle), sw, port);
        }

        // A VLAN from the access handoff through the aggregation switch and router to the hub router port.
        long Logical((long Circuit, Equipment Switch, long Port) access, long first, long? second, long? customerPort)
        {
            var vlan = aggregationState.NextVlan++;
            var router = aggregationState.Router!;
            var switchIndex = aggregationState.Switches.IndexOf(access.Switch) + 1;
            var terminals = new List<long>();
            var channels = new List<long?>();
            if (customerPort is { } customer)
            {
                terminals.Add(customer);
                channels.Add(null);
            }
            foreach (var t in new[] { first, second ?? 0, access.Port, access.Switch.Terminal("xe-0/1/1"), router.Terminal($"ge-0/0/{switchIndex}"), router.Terminal("xe-0/1/1"), aggregationState.HubPort }.Where(t => t != 0))
            {
                terminals.Add(t);
                channels.Add(NewChannel(t, "vlan", vlan));
            }
            var logical = NewCircuit("logical", terminals, site.Lifecycle, channels);
            _net.Dependencies.Add((logical, access.Circuit));
            _net.Dependencies.Add((logical, aggregationState.RingCircuit));
            return logical;
        }

        if (site.Kind == SiteKind.Radio)
        {
            var backhaul = Physical(state.Baseband!.Terminal("bh1"));
            Physical(state.Ethernet!.Terminal("xe-0/1/1"));
            var logical = Logical(backhaul, state.Baseband.Terminal("bh1"), null, null);
            var service = NewService($"Mobil backhaul {site.Code}", "mobile-backhaul", """{"capacityMbps":1000}""", site.Lifecycle);
            _net.ServiceCircuits.Add((service, logical));
        }
        else
        {
            var uplink = Physical(state.Switch!.Terminal("xe-0/1/1"));
            var services = _rng.Next(1, 4);
            for (var i = 1; i <= services; i++)
            {
                var customerPort = state.PatchPanel!.Terminal(i.ToString(CultureInfo.InvariantCulture));
                var logical = Logical(uplink, state.Switch.Terminal($"ge-0/0/{i}"), state.Switch.Terminal("xe-0/1/1"), customerPort);
                var bandwidth = new[] { 100, 500, 1000 }[_rng.Next(3)];
                var service = NewService($"Ethernet {site.Code} #{i}", "ethernet", $$"""{"bandwidthMbps":{{bandwidth}}}""", site.Lifecycle);
                _net.ServiceCircuits.Add((service, logical));
            }
        }
    }

    // ---------------------------------------------------------------- rows

    private Site NewSite(SiteKind kind, double x, double y, string code, string name)
    {
        var site = new Site { Id = _net.Sites.Count + 1, Code = code, Name = name, Kind = kind, X = x, Y = y };
        _net.Sites.Add(site);
        return site;
    }

    private long NewLocation(Site site, long? parent, string kind, string name, short? rackUnits)
    {
        var id = _nextLocation++;
        _net.Locations.Add(new LocationRow(id, site.Id, parent, kind, name, rackUnits, site.Lifecycle));
        return id;
    }

    private void Connect(long a, long b, ConnectionKind kind, Site site) =>
        _net.Connections.Add(new Connection(Math.Min(a, b), Math.Max(a, b), kind, site.Lifecycle != Lifecycle.InService));

    private long NewChannel(long terminal, string kind, int number)
    {
        var id = _nextChannel++;
        _net.Channels.Add(new ChannelRow(id, terminal, kind, number));
        return id;
    }

    private long NewCircuit(string layer, List<long> hops, string lifecycle, List<long?>? channels = null)
    {
        var id = _nextCircuit++;
        var prefix = layer switch { "physical" => "FYS", "transmission" => "TRM", _ => "LOG" };
        _net.Circuits.Add(new CircuitRow(id, $"{prefix}-{NextCode(prefix):0000000}", layer, hops[0], hops[^1], lifecycle));
        for (var i = 0; i < hops.Count; i++)
        {
            _net.Hops.Add(new Hop(id, i, hops[i], channels?[i]));
        }
        return id;
    }

    private long NewService(string name, string type, string attributes, string lifecycle)
    {
        var id = _nextService++;
        _net.Services.Add(new ServiceRow(id, $"TJ-{NextCode("TJ"):0000000}", name, type, attributes, lifecycle));
        return id;
    }

    private int NextCode(string prefix) => _codeCounters[prefix] = _codeCounters.GetValueOrDefault(prefix) + 1;

    private static string Weakest(string a, string b) =>
        a == Lifecycle.Planned || b == Lifecycle.Planned ? Lifecycle.Planned
        : a == Lifecycle.UnderConstruction || b == Lifecycle.UnderConstruction ? Lifecycle.UnderConstruction
        : Lifecycle.InService;

    private static double Distance(double x1, double y1, double x2, double y2) =>
        Math.Sqrt(((x1 - x2) * (x1 - x2)) + ((y1 - y2) * (y1 - y2)));

    internal static string ConductorColor(int n) => FiberColors[(n - 1) % FiberColors.Length];

    internal static JsonElement ParseAttributes(string json) => JsonDocument.Parse(json).RootElement;
}
