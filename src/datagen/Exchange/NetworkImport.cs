using Cmdb.Exchange;
using System.Diagnostics;
using System.Globalization;
using Cmdb.Catalog;
using Cmdb.Database;
using Cmdb.Database.Provenance;
using Npgsql;
using NpgsqlTypes;

namespace Cmdb.DataGen.Exchange;

/// <summary>Created, changed, unchanged, and in the database for the source system but not in the files.</summary>
internal sealed record ImportCount(string Kind, long Created, long Updated, long Unchanged, long Missing);

internal sealed record ImportResult(bool Written, IReadOnlyList<ImportError> Errors, IReadOnlyList<ImportCount> Counts, double ElapsedSeconds);

/// <summary>
/// Imports an existing network in the exchange format into production (#210). Every row is checked first, against the
/// catalog, the other files and what the source system already has in the database; with any problem nothing is
/// written. Then the files are written in one transaction: objects are matched on source system and external id,
/// new ones inserted, changed ones updated, and ports and conductors created from the catalog for new equipment and
/// cables. Objects in the database that are missing from the files are counted, not removed (#216 handles that).
/// </summary>
/// <remarks>
/// This is system work like the data generator: it runs as its own step with its own database role, never through
/// the API, so no user's scope is bypassed. It sees everything (<c>cmdb.scopes = *</c>) because it writes everything.
/// </remarks>
internal static class NetworkImport
{
    public static async Task<ImportResult> RunAsync(NpgsqlDataSource db, string folder, string source, TypeCatalog catalog, bool dryRun,
        TextWriter log, CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        if (!Directory.Exists(folder))
        {
            return new ImportResult(false, [new ImportError(folder, 0, "", "mappen finns inte")], [], 0);
        }
        await CmdbDatabase.MigrateAsync(db, ct);
        await using (var context = CmdbDatabase.CreateContext(db))
        {
            await CatalogSync.SyncAsync(context, catalog, ct);
        }

        var errors = new List<ImportError>();
        var data = ExchangeFormat.Read(folder, catalog, errors);
        log.WriteLine($"Read {folder} in {total.Elapsed.TotalSeconds:0.0} s");

        await using var conn = await db.OpenConnectionAsync(ct);
        await Exec(conn, "SET cmdb.scopes = '*'", ct);
        var existing = await Existing.LoadAsync(conn, source, ct);
        Check(data, existing, catalog, errors);
        var counts = Counts(data, existing);
        if (errors.Count > 0 || dryRun)
        {
            return new ImportResult(false, errors, counts, total.Elapsed.TotalSeconds);
        }

        var write = new Writer(conn, source, catalog, log, ct);
        try
        {
            await write.RunAsync(data, errors);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation
            or PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.NotNullViolation)
        {
            // What the checks above cannot see, such as a location name already used by an object from another source.
            errors.Add(new ImportError(ex.TableName ?? "", 0, "", $"databasen avvisade importen ({ex.ConstraintName}): {ex.MessageText}"));
        }
        if (errors.Count > 0)
        {
            return new ImportResult(false, errors, counts, total.Elapsed.TotalSeconds);
        }
        await Cmdb.Database.Scopes.ScopeVisibility.RefreshAsync(db, ct);
        log.WriteLine($"Imported in {total.Elapsed.TotalSeconds:0.0} s");
        var missing = counts.ToDictionary(c => c.Kind, c => c.Missing);
        return new ImportResult(true, [], [.. write.Counts.Select(c => c with { Missing = missing.GetValueOrDefault(c.Kind) })], total.Elapsed.TotalSeconds);
    }

    /// <summary>What the source system already has in the database, by external id, and the codes other objects use.</summary>
    private sealed class Existing
    {
        public Dictionary<string, (long Id, string Code)> Sites { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (long Id, long SiteId)> Locations { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (long Id, string Type, long SiteId, string? Slot)> Equipment { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (long Id, string Type, long A, long B)> Cables { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> Circuits { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> Services { get; } = new(StringComparer.Ordinal);

        /// <summary>Codes used by objects that are not this source's, per table.</summary>
        public Dictionary<string, HashSet<string>> ForeignCodes { get; } = [];

        public static async Task<Existing> LoadAsync(NpgsqlConnection conn, string source, CancellationToken ct)
        {
            var e = new Existing();
            await Read(conn, "SELECT external_id, id, code FROM site WHERE source_system = $1 AND external_id IS NOT NULL", source,
                r => e.Sites[r.GetString(0)] = (r.GetInt64(1), r.GetString(2)), ct);
            await Read(conn, "SELECT external_id, id, site_id FROM location WHERE source_system = $1 AND external_id IS NOT NULL", source,
                r => e.Locations[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2)), ct);
            await Read(conn, """
                SELECT e.external_id, e.id, t.key, e.site_id, e.slot FROM equipment e JOIN equipment_type t ON t.id = e.equipment_type_id
                WHERE e.source_system = $1 AND e.external_id IS NOT NULL
                """, source, r => e.Equipment[r.GetString(0)] = (r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4)), ct);
            await Read(conn, """
                SELECT c.external_id, c.id, t.key, c.a_site_id, c.b_site_id FROM cable c JOIN cable_type t ON t.id = c.cable_type_id
                WHERE c.source_system = $1 AND c.external_id IS NOT NULL
                """, source, r => e.Cables[r.GetString(0)] = (r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4)), ct);
            await Read(conn, "SELECT external_id, id FROM circuit WHERE source_system = $1 AND external_id IS NOT NULL", source,
                r => e.Circuits[r.GetString(0)] = r.GetInt64(1), ct);
            await Read(conn, "SELECT external_id, id FROM service WHERE source_system = $1 AND external_id IS NOT NULL", source,
                r => e.Services[r.GetString(0)] = r.GetInt64(1), ct);
            foreach (var table in new[] { "site", "cable", "circuit", "service" })
            {
                var codes = e.ForeignCodes[table] = new HashSet<string>(StringComparer.Ordinal);
                await Read(conn, $"SELECT code FROM {table} WHERE source_system IS DISTINCT FROM $1 OR external_id IS NULL", source,
                    r => codes.Add(r.GetString(0)), ct);
            }
            return e;
        }
    }

    /// <summary>References between the files and to what is already in the database, ports and conductors that exist.</summary>
    private static void Check(ExchangeData data, Existing existing, TypeCatalog catalog, List<ImportError> errors)
    {
        void Fail(string file, int row, string obj, string message) => errors.Add(new ImportError(file, row, obj, message));

        // Sites: a code another object already uses cannot be taken.
        var siteIds = data.Sites.Select(s => s.Id).Concat(existing.Sites.Keys).ToHashSet(StringComparer.Ordinal);
        foreach (var s in data.Sites.Where(s => existing.ForeignCodes["site"].Contains(s.Code)))
        {
            Fail(ExchangeFormat.Sites, s.Row, s.Id, $"koden {s.Code} används redan av en annan site");
        }
        // Which site each site id is, to compare sites between files and the database.
        var siteDbIds = existing.Sites.ToDictionary(p => p.Key, p => p.Value.Id, StringComparer.Ordinal);

        // Locations: in a known site, under a parent in the same site, no cycles.
        var locations = data.Locations.ToDictionary(l => l.Id, StringComparer.Ordinal);
        var siteOfDbSite = existing.Sites.ToDictionary(p => p.Value.Id, p => p.Key);
        string? LocationSite(string id) =>
            locations.TryGetValue(id, out var l) ? l.Site
            : existing.Locations.TryGetValue(id, out var d) ? siteOfDbSite.GetValueOrDefault(d.SiteId) : null;
        foreach (var l in data.Locations)
        {
            if (!siteIds.Contains(l.Site))
            {
                Fail(ExchangeFormat.Locations, l.Row, l.Id, $"siten {l.Site} finns varken i sites.csv eller i databasen");
            }
            if (l.Parent is { } parent)
            {
                if (LocationSite(parent) is not { } parentSite)
                {
                    Fail(ExchangeFormat.Locations, l.Row, l.Id, $"parent {parent} finns varken i filen eller i databasen");
                }
                else if (parentSite != l.Site)
                {
                    Fail(ExchangeFormat.Locations, l.Row, l.Id, $"parent {parent} ligger i en annan site ({parentSite})");
                }
            }
            var seen = new HashSet<string>(StringComparer.Ordinal) { l.Id };
            for (var p = l.Parent; p is not null && locations.TryGetValue(p, out var next); p = next.Parent)
            {
                if (!seen.Add(p))
                {
                    Fail(ExchangeFormat.Locations, l.Row, l.Id, "locations bildar en cirkel via parent");
                    break;
                }
            }
        }
        foreach (var duplicate in data.Locations.GroupBy(l => (l.Site, l.Parent, l.Name)).Where(g => g.Count() > 1))
        {
            var first = duplicate.First();
            Fail(ExchangeFormat.Locations, duplicate.Skip(1).First().Row, duplicate.Skip(1).First().Id,
                $"namnet {first.Name} finns redan under samma parent på rad {first.Row}");
        }

        // Equipment: in a location of its own site or a slot of other equipment there; the model cannot change.
        var equipment = data.Equipment.ToDictionary(e => e.Id, StringComparer.Ordinal);
        (string Type, string Site, string? Slot)? EquipmentInfo(string id) =>
            equipment.TryGetValue(id, out var e) ? (e.Type, e.Site, e.Slot)
            : existing.Equipment.TryGetValue(id, out var d) && siteOfDbSite.TryGetValue(d.SiteId, out var site) ? (d.Type, site, d.Slot) : null;
        foreach (var e in data.Equipment)
        {
            if (!siteIds.Contains(e.Site))
            {
                Fail(ExchangeFormat.Equipment, e.Row, e.Id, $"siten {e.Site} finns varken i sites.csv eller i databasen");
            }
            if (e.Location is { } location && LocationSite(location) is var locationSite && locationSite != e.Site)
            {
                Fail(ExchangeFormat.Equipment, e.Row, e.Id, locationSite is null
                    ? $"location {location} finns varken i locations.csv eller i databasen"
                    : $"location {location} ligger i en annan site ({locationSite})");
            }
            if (e.Parent is { } parent)
            {
                if (EquipmentInfo(parent) is not { } p)
                {
                    Fail(ExchangeFormat.Equipment, e.Row, e.Id, $"parent {parent} finns varken i equipment.csv eller i databasen");
                }
                else if (p.Site != e.Site)
                {
                    Fail(ExchangeFormat.Equipment, e.Row, e.Id, $"parent {parent} står i en annan site ({p.Site})");
                }
                else if (catalog.Find(p.Type) is { } parentType && catalog.Find(e.Type) is { } type)
                {
                    var slot = parentType.SlotList.FirstOrDefault(s => s.Name == e.Slot);
                    if (slot is null)
                    {
                        Fail(ExchangeFormat.Equipment, e.Row, e.Id, $"{p.Type} har ingen slot {e.Slot} ({string.Join(", ", parentType.SlotList.Select(s => s.Name))})");
                    }
                    else if (!slot.Accepts.Contains(type.Category))
                    {
                        Fail(ExchangeFormat.Equipment, e.Row, e.Id, $"slot {e.Slot} i {p.Type} tar {string.Join(", ", slot.Accepts)}, inte {type.Category}");
                    }
                }
            }
            if (existing.Equipment.TryGetValue(e.Id, out var current) && current.Type != e.Type)
            {
                Fail(ExchangeFormat.Equipment, e.Row, e.Id, $"modellen kan inte bytas ({current.Type} till {e.Type}); ta bort utrustningen och skapa en ny");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal) { e.Id };
            for (var p = e.Parent; p is not null && equipment.TryGetValue(p, out var next); p = next.Parent)
            {
                if (!seen.Add(p))
                {
                    Fail(ExchangeFormat.Equipment, e.Row, e.Id, "utrustningen sitter i sig själv via parent");
                    break;
                }
            }
        }
        foreach (var duplicate in data.Equipment.Where(e => e.Parent is not null).GroupBy(e => (e.Parent, e.Slot)).Where(g => g.Count() > 1))
        {
            Fail(ExchangeFormat.Equipment, duplicate.Skip(1).First().Row, duplicate.Skip(1).First().Id,
                $"slot {duplicate.Key.Slot} i {duplicate.Key.Parent} är redan upptagen på rad {duplicate.First().Row}");
        }

        // Ports: named as in the model's port template.
        var portNames = new Dictionary<(string, string?), HashSet<string>>();
        HashSet<string>? Ports(string equipmentId)
        {
            if (EquipmentInfo(equipmentId) is not { } info || catalog.Find(info.Type) is not { } type)
            {
                return null;
            }
            if (!portNames.TryGetValue((info.Type, info.Slot), out var names))
            {
                names = portNames[(info.Type, info.Slot)] = PortExpansion.Expand(type, info.Slot).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            }
            return names;
        }
        foreach (var p in data.Ports)
        {
            if (Ports(p.Equipment) is not { } names)
            {
                Fail(ExchangeFormat.Ports, p.Row, p.Equipment, "utrustningen finns varken i equipment.csv eller i databasen");
            }
            else if (!names.Contains(p.Name))
            {
                Fail(ExchangeFormat.Ports, p.Row, p.Equipment, $"porten {p.Name} finns inte i modellens portmall ({EquipmentInfo(p.Equipment)!.Value.Type})");
            }
        }

        // Cables: between two known sites; the type and the ends cannot change (moving ends is a plan, #187).
        var cables = data.Cables.ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (var c in data.Cables)
        {
            foreach (var end in new[] { c.A, c.B }.Where(end => !siteIds.Contains(end)))
            {
                Fail(ExchangeFormat.Cables, c.Row, c.Id, $"siten {end} finns varken i sites.csv eller i databasen");
            }
            if (existing.ForeignCodes["cable"].Contains(c.Code))
            {
                Fail(ExchangeFormat.Cables, c.Row, c.Id, $"koden {c.Code} används redan av en annan kabel");
            }
            if (existing.Cables.TryGetValue(c.Id, out var current))
            {
                if (current.Type != c.Type)
                {
                    Fail(ExchangeFormat.Cables, c.Row, c.Id, $"kabeltypen kan inte bytas ({current.Type} till {c.Type})");
                }
                if (siteDbIds.GetValueOrDefault(c.A) != current.A || siteDbIds.GetValueOrDefault(c.B) != current.B)
                {
                    Fail(ExchangeFormat.Cables, c.Row, c.Id, "kabelns ändar kan inte flyttas i en import; flytta dem i en plan");
                }
            }
        }
        int? Conductors(string cableId) =>
            cables.TryGetValue(cableId, out var c) ? catalog.FindCable(c.Type)?.ConductorCount
            : existing.Cables.TryGetValue(cableId, out var d) ? catalog.FindCable(d.Type)?.ConductorCount : null;

        string? TerminalProblem(XTerminal t)
        {
            if (t.IsPort)
            {
                return Ports(t.Equipment!) is not { } names ? $"utrustningen {t.Equipment} finns varken i equipment.csv eller i databasen"
                    : names.Contains(t.Port!) ? null
                    : $"porten {t.Port} finns inte på {t.Equipment} ({EquipmentInfo(t.Equipment!)!.Value.Type})";
            }
            return Conductors(t.Cable!) is not { } count ? $"kabeln {t.Cable} finns varken i cables.csv eller i databasen"
                : t.Conductor <= count ? null
                : $"kabeln {t.Cable} har {count} ledare, inte {t.Conductor}";
        }
        foreach (var c in data.Connections)
        {
            foreach (var problem in new[] { TerminalProblem(c.A), TerminalProblem(c.B) }.OfType<string>())
            {
                Fail(ExchangeFormat.Connections, c.Row, $"{c.A}–{c.B}", problem);
            }
        }

        // Circuits: a new circuit needs its path; every reference is to a known circuit.
        var circuitIds = data.Circuits.Select(c => c.Id).Concat(existing.Circuits.Keys).ToHashSet(StringComparer.Ordinal);
        var withHops = data.Hops.Select(h => h.Circuit).ToHashSet(StringComparer.Ordinal);
        foreach (var c in data.Circuits)
        {
            if (existing.ForeignCodes["circuit"].Contains(c.Code))
            {
                Fail(ExchangeFormat.Circuits, c.Row, c.Id, $"koden {c.Code} används redan av en annan krets");
            }
            if (!withHops.Contains(c.Id) && !existing.Circuits.ContainsKey(c.Id))
            {
                Fail(ExchangeFormat.Circuits, c.Row, c.Id, "kretsen saknar väg i circuit-hops.csv");
            }
        }
        foreach (var h in data.Hops)
        {
            if (!circuitIds.Contains(h.Circuit))
            {
                Fail(ExchangeFormat.Hops, h.Row, h.Circuit, "kretsen finns varken i circuits.csv eller i databasen");
            }
            if (TerminalProblem(h.Terminal) is { } problem)
            {
                Fail(ExchangeFormat.Hops, h.Row, h.Circuit, problem);
            }
        }
        foreach (var d in data.Dependencies)
        {
            foreach (var id in new[] { d.Circuit, d.Carrier }.Where(id => !circuitIds.Contains(id)))
            {
                Fail(ExchangeFormat.Dependencies, d.Row, d.Circuit, $"kretsen {id} finns varken i circuits.csv eller i databasen");
            }
        }

        var serviceIds = data.Services.Select(s => s.Id).Concat(existing.Services.Keys).ToHashSet(StringComparer.Ordinal);
        foreach (var s in data.Services.Where(s => existing.ForeignCodes["service"].Contains(s.Code)))
        {
            Fail(ExchangeFormat.Services, s.Row, s.Id, $"koden {s.Code} används redan av en annan tjänst");
        }
        foreach (var s in data.ServiceCircuits)
        {
            if (!serviceIds.Contains(s.Service))
            {
                Fail(ExchangeFormat.ServiceCircuits, s.Row, s.Service, "tjänsten finns varken i services.csv eller i databasen");
            }
            if (!circuitIds.Contains(s.Circuit))
            {
                Fail(ExchangeFormat.ServiceCircuits, s.Row, s.Service, $"kretsen {s.Circuit} finns varken i circuits.csv eller i databasen");
            }
        }
    }

    /// <summary>What a dry run reports: new objects and those in the database but not in the files. Changes are counted when written.</summary>
    private static List<ImportCount> Counts(ExchangeData data, Existing existing)
    {
        ImportCount Count<T>(string kind, IEnumerable<T> rows, Func<T, string> id, ICollection<string> current)
        {
            var ids = rows.Select(id).ToHashSet(StringComparer.Ordinal);
            var matched = ids.Count(current.Contains);
            return new ImportCount(kind, ids.Count - matched, 0, matched, current.Count - matched);
        }
        return
        [
            Count("sites", data.Sites, s => s.Id, existing.Sites.Keys),
            Count("locations", data.Locations, l => l.Id, existing.Locations.Keys),
            Count("equipment", data.Equipment, e => e.Id, existing.Equipment.Keys),
            Count("cables", data.Cables, c => c.Id, existing.Cables.Keys),
            Count("circuits", data.Circuits, c => c.Id, existing.Circuits.Keys),
            Count("services", data.Services, s => s.Id, existing.Services.Keys),
        ];
    }

    /// <summary>Writes checked files in one transaction, through temporary tables and set-based statements.</summary>
    private sealed class Writer(NpgsqlConnection conn, string source, TypeCatalog catalog, TextWriter log, CancellationToken ct)
    {
        private const string Lifecycle = "lifecycle_state";

        public List<ImportCount> Counts { get; } = [];

        public async Task RunAsync(ExchangeData data, List<ImportError> errors)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            // Bulk rows skip the change stream's triggers (#11); one 'reload' entry at the end tells the graph to start over.
            await Exec(conn, "SET LOCAL cmdb.bulk = 'on'", ct);
            await Exec(conn, "SET LOCAL synchronous_commit = off", ct);

            await SitesAsync(data.Sites);
            await LocationsAsync(data.Locations);
            await EquipmentAsync(data.Equipment);
            await CablesAsync(data.Cables);
            await ConnectionsAsync(data.Connections, errors);
            await CircuitsAsync(data.Circuits, data.Hops, errors);
            if (data.Files.Contains(ExchangeFormat.Dependencies))
            {
                await LinksAsync("circuit_dependency", "circuit_id", "circuit", "carrier_id", "circuit",
                    data.Dependencies.Select(d => (d.Circuit, d.Carrier)), "circuit dependencies");
            }
            await ServicesAsync(data.Services);
            if (data.Files.Contains(ExchangeFormat.ServiceCircuits))
            {
                await LinksAsync("service_circuit", "service_id", "service", "circuit_id", "circuit",
                    data.ServiceCircuits.Select(s => (s.Service, s.Circuit)), "service circuits");
            }
            if (errors.Count > 0)
            {
                await tx.RollbackAsync(ct);
                return;
            }

            // Rack positions (#173) for equipment the files did not place.
            await Exec(conn, Cmdb.Database.RackStacking.Backfill, ct);
            await SourceRecordsAsync();
            await Exec(conn, "SET LOCAL cmdb.bulk = 'off'", ct);
            await Exec(conn, "INSERT INTO graph_change (kind, key) VALUES ('reload', 0)", ct);
            await tx.CommitAsync(ct);
            // Rows written in bulk leave the search indexes loosely packed (#245); rebuilt without blocking, the database is in use.
            await Cmdb.Database.SearchIndexes.RebuildAsync(conn, concurrently: true, ct);
            await Exec(conn, "ANALYZE", ct);
        }

        private async Task SitesAsync(List<XSite> sites)
        {
            if (sites.Count == 0)
            {
                return;
            }
            var sw = Stopwatch.StartNew();
            await Exec(conn, """
                CREATE TEMP TABLE x_site (ext text PRIMARY KEY, code text, name text, site_type text, x float8, y float8, lifecycle text, attributes jsonb) ON COMMIT DROP
                """, ct);
            await Copy("x_site", "ext, code, name, site_type, x, y, lifecycle, attributes", sites, (w, s) =>
            {
                w.Write(s.Id);
                w.Write(s.Code);
                w.Write(s.Name);
                w.Write(s.SiteType);
                w.Write(s.X);
                w.Write(s.Y);
                w.Write(s.Lifecycle);
                w.Write(s.Attributes, NpgsqlDbType.Jsonb);
            });
            var updated = await Exec(conn, $"""
                UPDATE site s SET code = x.code, name = x.name, site_type = x.site_type, geom = ST_SetSRID(ST_MakePoint(x.x, x.y), 3006),
                       lifecycle = x.lifecycle::{Lifecycle}, attributes = x.attributes
                FROM x_site x
                WHERE s.source_system = $1 AND s.external_id = x.ext
                  AND ((s.code, s.name, s.site_type, s.lifecycle::text, s.attributes) IS DISTINCT FROM (x.code, x.name, x.site_type, x.lifecycle, x.attributes)
                       OR NOT ST_Equals(s.geom, ST_SetSRID(ST_MakePoint(x.x, x.y), 3006)))
                """, ct, source);
            var created = await Exec(conn, $"""
                INSERT INTO site (code, name, site_type, geom, lifecycle, attributes, source_system, external_id, last_confirmed_at)
                SELECT x.code, x.name, x.site_type, ST_SetSRID(ST_MakePoint(x.x, x.y), 3006), x.lifecycle::{Lifecycle}, x.attributes, $1, x.ext, now()
                FROM x_site x
                WHERE NOT EXISTS (SELECT 1 FROM site s WHERE s.source_system = $1 AND s.external_id = x.ext)
                """, ct, source);
            await Confirm("site", "x_site");
            Done("sites", sites.Count, created, updated, sw);
        }

        private async Task LocationsAsync(List<XLocation> locations)
        {
            if (locations.Count == 0)
            {
                return;
            }
            var sw = Stopwatch.StartNew();
            // Parents before children: a location's depth in the file decides the round it is written in.
            var byId = locations.ToDictionary(l => l.Id, StringComparer.Ordinal);
            int Depth(XLocation l) => l.Parent is { } p && byId.TryGetValue(p, out var parent) ? 1 + Depth(parent) : 0;
            await Exec(conn, """
                CREATE TEMP TABLE x_location (ext text PRIMARY KEY, site text, parent text, kind text, name text, rack_units int2, depth int) ON COMMIT DROP
                """, ct);
            await Copy("x_location", "ext, site, parent, kind, name, rack_units, depth", locations, (w, l) =>
            {
                w.Write(l.Id);
                w.Write(l.Site);
                Text(w, l.Parent);
                w.Write(l.Kind);
                w.Write(l.Name);
                Short(w, l.RackUnits);
                w.Write(Depth(l));
            });
            const string resolved = """
                SELECT x.ext, s.id AS site_id, p.id AS parent_id, x.kind, x.name, x.rack_units, x.depth
                FROM x_location x
                JOIN site s ON s.source_system = $1 AND s.external_id = x.site
                LEFT JOIN location p ON p.source_system = $1 AND p.external_id = x.parent
                """;
            var updated = await Exec(conn, $"""
                UPDATE location l SET site_id = r.site_id, parent_id = r.parent_id, kind = r.kind, name = r.name, rack_units = r.rack_units
                FROM ({resolved}) r
                WHERE l.source_system = $1 AND l.external_id = r.ext
                  AND (l.site_id, l.parent_id, l.kind, l.name, l.rack_units) IS DISTINCT FROM (r.site_id, r.parent_id, r.kind, r.name, r.rack_units)
                """, ct, source);
            long created = 0;
            var depths = locations.Select(Depth).DefaultIfEmpty().Max();
            for (var depth = 0; depth <= depths; depth++)
            {
                created += await Exec(conn, $"""
                    INSERT INTO location (site_id, parent_id, kind, name, rack_units, lifecycle, source_system, external_id, last_confirmed_at)
                    SELECT r.site_id, r.parent_id, r.kind, r.name, r.rack_units, 'in_service', $1, r.ext, now()
                    FROM ({resolved}) r
                    WHERE r.depth = {depth} AND NOT EXISTS (SELECT 1 FROM location l WHERE l.source_system = $1 AND l.external_id = r.ext)
                    """, ct, source);
            }
            await Confirm("location", "x_location");
            Done("locations", locations.Count, created, updated, sw);
        }

        private async Task EquipmentAsync(List<XEquipment> equipment)
        {
            if (equipment.Count == 0)
            {
                return;
            }
            var sw = Stopwatch.StartNew();
            var byId = equipment.ToDictionary(e => e.Id, StringComparer.Ordinal);
            int Depth(XEquipment e) => e.Parent is { } p && byId.TryGetValue(p, out var parent) ? 1 + Depth(parent) : 0;
            await Exec(conn, """
                CREATE TEMP TABLE x_equipment (ext text PRIMARY KEY, site text, location text, parent text, slot text, name text, type text,
                                               lifecycle text, rack_position int2, attributes jsonb, depth int, ord int) ON COMMIT DROP
                """, ct);
            await Copy("x_equipment", "ext, site, location, parent, slot, name, type, lifecycle, rack_position, attributes, depth, ord", equipment.Select((e, i) => (e, i)), (w, x) =>
            {
                var (e, i) = x;
                w.Write(e.Id);
                w.Write(e.Site);
                Text(w, e.Location);
                Text(w, e.Parent);
                Text(w, e.Slot);
                w.Write(e.Name);
                w.Write(e.Type);
                w.Write(e.Lifecycle);
                Short(w, e.RackPosition);
                w.Write(e.Attributes, NpgsqlDbType.Jsonb);
                w.Write(Depth(e));
                w.Write(i);
            });
            const string resolved = """
                SELECT x.ext, t.id AS type_id, s.id AS site_id, l.id AS location_id, p.id AS parent_id, x.slot, x.name, x.lifecycle,
                       x.rack_position, x.attributes, x.depth, x.ord
                FROM x_equipment x
                JOIN equipment_type t ON t.key = x.type
                JOIN site s ON s.source_system = $1 AND s.external_id = x.site
                LEFT JOIN location l ON l.source_system = $1 AND l.external_id = x.location
                LEFT JOIN equipment p ON p.source_system = $1 AND p.external_id = x.parent
                """;
            // A rack position the files leave out stays as it is, or is filled in by stacking (#173) for new equipment.
            var updated = await Exec(conn, $"""
                UPDATE equipment e SET site_id = r.site_id, location_id = r.location_id, parent_id = r.parent_id, slot = r.slot, name = r.name,
                       lifecycle = r.lifecycle::{Lifecycle}, attributes = r.attributes,
                       rack_position = CASE WHEN r.parent_id IS NOT NULL OR r.location_id IS DISTINCT FROM e.location_id THEN r.rack_position
                                            ELSE coalesce(r.rack_position, e.rack_position) END
                FROM ({resolved}) r
                WHERE e.source_system = $1 AND e.external_id = r.ext
                  AND ((e.site_id, e.location_id, e.parent_id, e.slot, e.name, e.lifecycle::text, e.attributes)
                       IS DISTINCT FROM (r.site_id, r.location_id, r.parent_id, r.slot, r.name, r.lifecycle, r.attributes)
                       OR r.rack_position IS NOT NULL AND r.rack_position IS DISTINCT FROM e.rack_position)
                """, ct, source);
            var created = new List<(long Id, string Type, string? Slot)>();
            var depths = equipment.Select(Depth).DefaultIfEmpty().Max();
            for (var depth = 0; depth <= depths; depth++)
            {
                await Read(conn, $"""
                    INSERT INTO equipment (equipment_type_id, site_id, location_id, parent_id, slot, name, lifecycle, rack_position, attributes,
                                           source_system, external_id, last_confirmed_at)
                    SELECT r.type_id, r.site_id, r.location_id, r.parent_id, r.slot, r.name, r.lifecycle::{Lifecycle}, r.rack_position, r.attributes,
                           $1, r.ext, now()
                    FROM ({resolved}) r
                    WHERE r.depth = {depth} AND NOT EXISTS (SELECT 1 FROM equipment e WHERE e.source_system = $1 AND e.external_id = r.ext)
                    ORDER BY r.ord
                    RETURNING id, external_id
                    """, source, r => created.Add((r.GetInt64(0), byId[r.GetString(1)].Type, byId[r.GetString(1)].Slot)), ct);
            }
            await Confirm("equipment", "x_equipment");

            // Ports of new equipment, from the catalog's port template.
            var layouts = new Dictionary<(string, string?), IReadOnlyList<Port>>();
            var ports = new List<(long Equipment, Port Port)>();
            foreach (var (id, typeKey, slot) in created)
            {
                if (!layouts.TryGetValue((typeKey, slot), out var layout))
                {
                    layout = layouts[(typeKey, slot)] = PortExpansion.Expand(catalog.Find(typeKey)!, slot);
                }
                ports.AddRange(layout.Select(p => (id, p)));
            }
            var terminals = await NextIdsAsync("terminal", ports.Count);
            await Copy("terminal", "id, kind", terminals, (w, t) =>
            {
                w.Write(t);
                w.Write("port", NpgsqlDbType.Text);
            });
            await Copy("port", "terminal_id, equipment_id, name, port_type, port_group, position", ports.Select((p, i) => (Terminal: terminals[i], p.Equipment, p.Port)), (w, p) =>
            {
                w.Write(p.Terminal);
                w.Write(p.Equipment);
                w.Write(p.Port.Name);
                w.Write(p.Port.Type);
                Text(w, p.Port.Group);
                w.Write(p.Port.Position);
            });
            Done("equipment", equipment.Count, created.Count, updated, sw, $"{ports.Count:N0} ports");
        }

        private async Task CablesAsync(List<XCable> cables)
        {
            if (cables.Count == 0)
            {
                return;
            }
            var sw = Stopwatch.StartNew();
            await Exec(conn, """
                CREATE TEMP TABLE x_cable (ext text PRIMARY KEY, code text, type text, a text, b text, lifecycle text, route text, attributes jsonb) ON COMMIT DROP
                """, ct);
            await Copy("x_cable", "ext, code, type, a, b, lifecycle, route, attributes", cables, (w, c) =>
            {
                w.Write(c.Id);
                w.Write(c.Code);
                w.Write(c.Type);
                w.Write(c.A);
                w.Write(c.B);
                w.Write(c.Lifecycle);
                Text(w, c.Route is { } route ? Wkt(route) : null);
                w.Write(c.Attributes, NpgsqlDbType.Jsonb);
            });
            // Without a route a cable runs straight between its sites.
            const string resolved = """
                SELECT x.ext, x.code, t.id AS type_id, a.id AS a_id, b.id AS b_id, x.lifecycle, x.attributes,
                       coalesce(ST_GeomFromText(x.route, 3006), ST_MakeLine(ST_PointOnSurface(a.geom), ST_PointOnSurface(b.geom))) AS geom
                FROM x_cable x
                JOIN cable_type t ON t.key = x.type
                JOIN site a ON a.source_system = $1 AND a.external_id = x.a
                JOIN site b ON b.source_system = $1 AND b.external_id = x.b
                """;
            var updated = await Exec(conn, $"""
                UPDATE cable c SET code = r.code, lifecycle = r.lifecycle::{Lifecycle}, attributes = r.attributes, geom = r.geom
                FROM ({resolved}) r
                WHERE c.source_system = $1 AND c.external_id = r.ext
                  AND ((c.code, c.lifecycle::text, c.attributes) IS DISTINCT FROM (r.code, r.lifecycle, r.attributes) OR NOT ST_Equals(c.geom, r.geom))
                """, ct, source);
            var created = new List<(long Id, string Type)>();
            var types = cables.ToDictionary(c => c.Id, c => c.Type, StringComparer.Ordinal);
            await Read(conn, $"""
                INSERT INTO cable (cable_type_id, code, a_site_id, b_site_id, geom, lifecycle, attributes, source_system, external_id, last_confirmed_at)
                SELECT r.type_id, r.code, r.a_id, r.b_id, r.geom, r.lifecycle::{Lifecycle}, r.attributes, $1, r.ext, now()
                FROM ({resolved}) r
                WHERE NOT EXISTS (SELECT 1 FROM cable c WHERE c.source_system = $1 AND c.external_id = r.ext)
                RETURNING id, external_id
                """, source, r => created.Add((r.GetInt64(0), types[r.GetString(1)])), ct);
            await Confirm("cable", "x_cable");

            // Conductors of new cables and their two ends, numbered from 1 as the cable type says.
            var conductors = created.SelectMany(c => Enumerable.Range(1, catalog.FindCable(c.Type)!.ConductorCount).Select(n => (Cable: c.Id, c.Type, Number: n))).ToList();
            var conductorIds = await NextIdsAsync("conductor", conductors.Count);
            var ends = await NextIdsAsync("terminal", 2 * conductors.Count);
            await Copy("conductor", "id, cable_id, number, color", conductors.Select((c, i) => (Id: conductorIds[i], c.Cable, c.Type, c.Number)), (w, c) =>
            {
                w.Write(c.Id);
                w.Write(c.Cable);
                w.Write(c.Number);
                Text(w, catalog.FindCable(c.Type)!.ColorCode is not null ? NetworkBuilder.ConductorColor(c.Number) : null);
            });
            await Copy("terminal", "id, kind", ends, (w, t) =>
            {
                w.Write(t);
                w.Write("conductor_end", NpgsqlDbType.Text);
            });
            await Copy("conductor_end", "terminal_id, conductor_id, side", ends.Select((t, i) => (Terminal: t, Conductor: conductorIds[i / 2], Side: i % 2 == 0 ? "A" : "B")), (w, e) =>
            {
                w.Write(e.Terminal);
                w.Write(e.Conductor);
                w.Write(e.Side, NpgsqlDbType.Char);
            });
            Done("cables", cables.Count, created.Count, updated, sw, $"{conductors.Count:N0} conductors");
        }

        private async Task ConnectionsAsync(List<XConnection> connections, List<ImportError> errors)
        {
            if (connections.Count == 0)
            {
                return;
            }
            var sw = Stopwatch.StartNew();
            await Exec(conn, $"""
                CREATE TEMP TABLE x_connection (row int, {TerminalColumns("a")}, {TerminalColumns("b")}, kind text, lifecycle text,
                                                a_id bigint, b_id bigint) ON COMMIT DROP
                """, ct);
            await Copy("x_connection", $"row, {TerminalNames("a")}, {TerminalNames("b")}, kind, lifecycle", connections, (w, c) =>
            {
                w.Write(c.Row);
                Terminal(w, c.A);
                Terminal(w, c.B);
                w.Write(c.Kind);
                w.Write(c.Lifecycle);
            });
            await ResolveAsync("x_connection", "a");
            await ResolveAsync("x_connection", "b");
            await Unresolved("x_connection", "a_id IS NULL OR b_id IS NULL", ExchangeFormat.Connections, "terminalen hittades inte efter skrivning", errors);

            // A pair is one connection however it is written; the newest row decides kind and lifecycle.
            await Exec(conn, """
                CREATE TEMP TABLE x_pair ON COMMIT DROP AS
                SELECT DISTINCT ON (least(a_id, b_id), greatest(a_id, b_id)) least(a_id, b_id) AS a_id, greatest(a_id, b_id) AS b_id, kind, lifecycle
                FROM x_connection WHERE a_id IS NOT NULL AND b_id IS NOT NULL
                ORDER BY least(a_id, b_id), greatest(a_id, b_id), row DESC
                """, ct);
            var updated = await Exec(conn, $"""
                UPDATE connection c SET kind = x.kind::connection_kind, lifecycle = x.lifecycle::{Lifecycle}
                FROM x_pair x
                WHERE c.a_terminal_id = x.a_id AND c.b_terminal_id = x.b_id AND c.valid_to IS NULL
                  AND (c.kind::text, c.lifecycle::text) IS DISTINCT FROM (x.kind, x.lifecycle)
                """, ct);
            var created = await Exec(conn, $"""
                INSERT INTO connection (a_terminal_id, b_terminal_id, kind, lifecycle, source_system)
                SELECT x.a_id, x.b_id, x.kind::connection_kind, x.lifecycle::{Lifecycle}, $1
                FROM x_pair x
                WHERE NOT EXISTS (SELECT 1 FROM connection c WHERE c.a_terminal_id = x.a_id AND c.b_terminal_id = x.b_id AND c.valid_to IS NULL)
                """, ct, source);
            var pairs = await Scalar(conn, "SELECT count(*) FROM x_pair", ct);
            Done("connections", pairs, created, updated, sw);
        }

        private async Task CircuitsAsync(List<XCircuit> circuits, List<XHop> hops, List<ImportError> errors)
        {
            if (circuits.Count == 0 && hops.Count == 0)
            {
                return;
            }
            var sw = Stopwatch.StartNew();
            await Exec(conn, $"""
                CREATE TEMP TABLE x_hop (row int, circuit text, seq int, {TerminalColumns("t")}, channel_kind text, channel_number int, t_id bigint,
                                         channel_id bigint) ON COMMIT DROP
                """, ct);
            await Copy("x_hop", $"row, circuit, seq, {TerminalNames("t")}, channel_kind, channel_number", hops, (w, h) =>
            {
                w.Write(h.Row);
                w.Write(h.Circuit);
                w.Write(h.Seq);
                Terminal(w, h.Terminal);
                Text(w, h.ChannelKind);
                if (h.ChannelNumber is { } n)
                {
                    w.Write(n);
                }
                else
                {
                    w.WriteNull();
                }
            });
            await ResolveAsync("x_hop", "t");
            await Unresolved("x_hop", "t_id IS NULL", ExchangeFormat.Hops, "terminalen hittades inte efter skrivning", errors);
            // Channels (a wavelength, timeslot or VLAN on a terminal) are created as the hops name them.
            await Exec(conn, """
                INSERT INTO channel (terminal_id, kind, number)
                SELECT DISTINCT t_id, channel_kind::channel_kind, channel_number FROM x_hop WHERE channel_kind IS NOT NULL AND t_id IS NOT NULL
                ON CONFLICT (terminal_id, kind, number) DO NOTHING
                """, ct);
            await Exec(conn, """
                UPDATE x_hop x SET channel_id = c.id FROM channel c
                WHERE c.terminal_id = x.t_id AND c.kind::text = x.channel_kind AND c.number = x.channel_number
                """, ct);

            await Exec(conn, "CREATE TEMP TABLE x_circuit (ext text PRIMARY KEY, code text, layer text, lifecycle text) ON COMMIT DROP", ct);
            await Copy("x_circuit", "ext, code, layer, lifecycle", circuits, (w, c) =>
            {
                w.Write(c.Id);
                w.Write(c.Code);
                w.Write(c.Layer);
                w.Write(c.Lifecycle);
            });
            // A circuit runs from its first hop to its last.
            await Exec(conn, """
                CREATE TEMP TABLE x_ends ON COMMIT DROP AS
                SELECT circuit, (array_agg(t_id ORDER BY seq))[1] AS a_id, (array_agg(t_id ORDER BY seq DESC))[1] AS b_id
                FROM x_hop GROUP BY circuit
                """, ct);
            var updated = await Exec(conn, $"""
                UPDATE circuit c SET code = x.code, layer = x.layer::circuit_layer, lifecycle = x.lifecycle::{Lifecycle},
                       a_terminal_id = coalesce(e.a_id, c.a_terminal_id), b_terminal_id = coalesce(e.b_id, c.b_terminal_id)
                FROM x_circuit x LEFT JOIN x_ends e ON e.circuit = x.ext
                WHERE c.source_system = $1 AND c.external_id = x.ext
                  AND (c.code, c.layer::text, c.lifecycle::text, c.a_terminal_id, c.b_terminal_id)
                      IS DISTINCT FROM (x.code, x.layer, x.lifecycle, coalesce(e.a_id, c.a_terminal_id), coalesce(e.b_id, c.b_terminal_id))
                """, ct, source);
            var created = await Exec(conn, $"""
                INSERT INTO circuit (code, layer, a_terminal_id, b_terminal_id, lifecycle, source_system, external_id, last_confirmed_at)
                SELECT x.code, x.layer::circuit_layer, e.a_id, e.b_id, x.lifecycle::{Lifecycle}, $1, x.ext, now()
                FROM x_circuit x JOIN x_ends e ON e.circuit = x.ext
                WHERE NOT EXISTS (SELECT 1 FROM circuit c WHERE c.source_system = $1 AND c.external_id = x.ext)
                """, ct, source);
            await Confirm("circuit", "x_circuit");

            // A circuit's path is replaced when it differs from the file's.
            // Parameters are not allowed in CREATE TABLE AS, so temporary tables that need the source are filled by INSERT.
            await Exec(conn, "CREATE TEMP TABLE x_path (circuit_id bigint, seq int, t_id bigint, channel_id bigint) ON COMMIT DROP", ct);
            await Exec(conn, """
                INSERT INTO x_path SELECT c.id, x.seq, x.t_id, x.channel_id FROM x_hop x JOIN circuit c ON c.source_system = $1 AND c.external_id = x.circuit
                """, ct, source);
            await Exec(conn, """
                CREATE TEMP TABLE x_changed ON COMMIT DROP AS
                SELECT circuit_id FROM (
                    (SELECT circuit_id, seq, t_id, channel_id FROM x_path
                     EXCEPT SELECT h.circuit_id, h.seq, h.terminal_id, h.channel_id FROM circuit_hop h WHERE h.circuit_id IN (SELECT circuit_id FROM x_path))
                    UNION ALL
                    (SELECT h.circuit_id, h.seq, h.terminal_id, h.channel_id FROM circuit_hop h WHERE h.circuit_id IN (SELECT circuit_id FROM x_path)
                     EXCEPT SELECT circuit_id, seq, t_id, channel_id FROM x_path)
                ) d GROUP BY circuit_id
                """, ct);
            await Exec(conn, "DELETE FROM circuit_hop WHERE circuit_id IN (SELECT circuit_id FROM x_changed)", ct);
            var paths = await Exec(conn, """
                INSERT INTO circuit_hop (circuit_id, seq, terminal_id, channel_id)
                SELECT circuit_id, seq, t_id, channel_id FROM x_path WHERE circuit_id IN (SELECT circuit_id FROM x_changed)
                """, ct);
            Done("circuits", circuits.Count, created, updated, sw, $"{paths:N0} hops written");
        }

        private async Task ServicesAsync(List<XService> services)
        {
            if (services.Count == 0)
            {
                return;
            }
            var sw = Stopwatch.StartNew();
            await Exec(conn, """
                CREATE TEMP TABLE x_service (ext text PRIMARY KEY, code text, name text, service_type text, lifecycle text, attributes jsonb) ON COMMIT DROP
                """, ct);
            await Copy("x_service", "ext, code, name, service_type, lifecycle, attributes", services, (w, s) =>
            {
                w.Write(s.Id);
                w.Write(s.Code);
                w.Write(s.Name);
                w.Write(s.Type);
                w.Write(s.Lifecycle);
                w.Write(s.Attributes, NpgsqlDbType.Jsonb);
            });
            var updated = await Exec(conn, $"""
                UPDATE service s SET code = x.code, name = x.name, service_type = x.service_type, lifecycle = x.lifecycle::{Lifecycle}, attributes = x.attributes
                FROM x_service x
                WHERE s.source_system = $1 AND s.external_id = x.ext
                  AND (s.code, s.name, s.service_type, s.lifecycle::text, s.attributes) IS DISTINCT FROM (x.code, x.name, x.service_type, x.lifecycle, x.attributes)
                """, ct, source);
            var created = await Exec(conn, $"""
                INSERT INTO service (code, name, service_type, lifecycle, attributes, source_system, external_id, last_confirmed_at)
                SELECT x.code, x.name, x.service_type, x.lifecycle::{Lifecycle}, x.attributes, $1, x.ext, now()
                FROM x_service x
                WHERE NOT EXISTS (SELECT 1 FROM service s WHERE s.source_system = $1 AND s.external_id = x.ext)
                """, ct, source);
            await Confirm("service", "x_service");
            Done("services", services.Count, created, updated, sw);
        }

        /// <summary>
        /// Links between two of this source's objects (circuit to carrier, service to circuit), as the file lists them: the
        /// file is the whole truth for links between objects from the source, so links it no longer has are removed.
        /// </summary>
        private async Task LinksAsync(string table, string fromColumn, string fromTable, string toColumn, string toTable,
            IEnumerable<(string From, string To)> links, string label)
        {
            var sw = Stopwatch.StartNew();
            var temp = $"x_{table}";
            await Exec(conn, $"CREATE TEMP TABLE {temp} (from_ext text, to_ext text) ON COMMIT DROP", ct);
            await Copy(temp, "from_ext, to_ext", links, (w, l) =>
            {
                w.Write(l.From);
                w.Write(l.To);
            });
            await Exec(conn, $"CREATE TEMP TABLE {temp}_ids (from_id bigint, to_id bigint) ON COMMIT DROP", ct);
            await Exec(conn, $"""
                INSERT INTO {temp}_ids SELECT DISTINCT f.id, t.id FROM {temp} x
                JOIN {fromTable} f ON f.source_system = $1 AND f.external_id = x.from_ext
                JOIN {toTable} t ON t.source_system = $1 AND t.external_id = x.to_ext
                """, ct, source);
            var removed = await Exec(conn, $"""
                DELETE FROM {table} l USING {fromTable} f, {toTable} t
                WHERE f.id = l.{fromColumn} AND t.id = l.{toColumn} AND f.source_system = $1 AND t.source_system = $1
                  AND NOT EXISTS (SELECT 1 FROM {temp}_ids x WHERE x.from_id = l.{fromColumn} AND x.to_id = l.{toColumn})
                """, ct, source);
            var created = await Exec(conn, $"""
                INSERT INTO {table} ({fromColumn}, {toColumn}) SELECT from_id, to_id FROM {temp}_ids
                ON CONFLICT DO NOTHING
                """, ct);
            var count = await Scalar(conn, $"SELECT count(*) FROM {temp}_ids", ct);
            Done(label, count, created, removed, sw, "updated = removed");
        }

        private static string TerminalColumns(string p) => $"{p}_equipment text, {p}_port text, {p}_cable text, {p}_conductor int, {p}_side char(1)";

        private static string TerminalNames(string p) => $"{p}_equipment, {p}_port, {p}_cable, {p}_conductor, {p}_side";

        private static void Terminal(NpgsqlBinaryImporter w, XTerminal t)
        {
            Text(w, t.Equipment);
            Text(w, t.Port);
            Text(w, t.Cable);
            if (t.IsPort)
            {
                w.WriteNull();
                w.WriteNull();
            }
            else
            {
                w.Write(t.Conductor);
                w.Write(t.Side.ToString(), NpgsqlDbType.Char);
            }
        }

        /// <summary>Fills <c>{p}_id</c> with the terminal: a port by equipment and name, or a conductor end by cable, number and side.</summary>
        private async Task ResolveAsync(string table, string p)
        {
            await Exec(conn, $"""
                UPDATE {table} x SET {p}_id = pt.terminal_id
                FROM equipment e JOIN port pt ON pt.equipment_id = e.id
                WHERE e.source_system = $1 AND e.external_id = x.{p}_equipment AND pt.name = x.{p}_port
                """, ct, source);
            await Exec(conn, $"""
                UPDATE {table} x SET {p}_id = ce.terminal_id
                FROM cable c JOIN conductor k ON k.cable_id = c.id JOIN conductor_end ce ON ce.conductor_id = k.id
                WHERE c.source_system = $1 AND c.external_id = x.{p}_cable AND k.number = x.{p}_conductor AND ce.side = x.{p}_side
                """, ct, source);
        }

        private async Task Unresolved(string table, string condition, string file, string message, List<ImportError> errors)
        {
            await Read(conn, $"SELECT row FROM {table} WHERE {condition} ORDER BY row LIMIT 100", null,
                r => errors.Add(new ImportError(file, r.GetInt32(0), "", message)), ct);
        }

        /// <summary>The object types written, with their temporary tables, for the source records.</summary>
        private readonly List<(string Table, string Temp)> _confirmed = [];

        /// <summary>Marks every object the files name as confirmed by the source now.</summary>
        private async Task Confirm(string table, string temp)
        {
            _confirmed.Add((table, temp));
            await Exec(conn, $"""
                UPDATE {table} t SET last_confirmed_at = now() FROM {temp} x WHERE t.source_system = $1 AND t.external_id = x.ext
                """, ct, source);
        }

        /// <summary>
        /// What the source said about each object it names (#215, ADR-0019): its id, now as the time it confirmed it,
        /// and the values as written, once everything (circuit ends, rack positions) is in place.
        /// </summary>
        private async Task SourceRecordsAsync()
        {
            var sw = Stopwatch.StartNew();
            var rows = 0L;
            foreach (var (table, temp) in _confirmed)
            {
                rows += await Exec(conn, $"""
                    INSERT INTO source_record (object_type, object_id, source_system, external_id, confirmed_at, reported)
                    SELECT '{table}', t.id, $1, t.external_id, now(), {ReportedValues.Sql(table, "t")}
                    FROM {table} t JOIN {temp} x ON t.source_system = $1 AND t.external_id = x.ext
                    ON CONFLICT (object_type, object_id, source_system) DO UPDATE
                        SET external_id = excluded.external_id, confirmed_at = excluded.confirmed_at, reported = excluded.reported
                    """, ct, source);
            }
            log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {"source records",-22} {rows,10:N0} rows {sw.Elapsed.TotalSeconds,6:0.0} s"));
        }

        private async Task<long[]> NextIdsAsync(string table, int count)
        {
            var ids = new List<long>(count);
            if (count > 0)
            {
                await Read(conn, $"SELECT nextval(pg_get_serial_sequence('{table}', 'id')) FROM generate_series(1, {count.ToString(CultureInfo.InvariantCulture)})",
                    null, r => ids.Add(r.GetInt64(0)), ct);
            }
            return [.. ids];
        }

        private async Task Copy<T>(string table, string columns, IEnumerable<T> rows, Action<NpgsqlBinaryImporter, T> write)
        {
            await using var importer = await conn.BeginBinaryImportAsync($"COPY {table} ({columns}) FROM STDIN (FORMAT BINARY)", ct);
            foreach (var row in rows)
            {
                await importer.StartRowAsync(ct);
                write(importer, row);
            }
            await importer.CompleteAsync(ct);
        }

        private void Done(string kind, long total, long created, long updated, Stopwatch sw, string? note = null)
        {
            Counts.Add(new ImportCount(kind, created, updated, total - created - updated, 0));
            log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {kind,-22} {total,10:N0} rows: {created,9:N0} new {updated,9:N0} changed {sw.Elapsed.TotalSeconds,6:0.0} s{(note is null ? "" : $" ({note})")}"));
        }

        private static string Wkt(double[] route) => "LINESTRING(" + string.Join(", ", Enumerable.Range(0, route.Length / 2)
            .Select(i => string.Create(CultureInfo.InvariantCulture, $"{route[2 * i]:R} {route[(2 * i) + 1]:R}"))) + ")";

        private static void Text(NpgsqlBinaryImporter w, string? value)
        {
            if (value is null)
            {
                w.WriteNull();
            }
            else
            {
                w.Write(value);
            }
        }

        private static void Short(NpgsqlBinaryImporter w, short? value)
        {
            if (value is { } v)
            {
                w.Write(v);
            }
            else
            {
                w.WriteNull();
            }
        }
    }

    private static async Task<long> Exec(NpgsqlConnection conn, string sql, CancellationToken ct, string? source = null)
    {
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
        if (source is not null)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = source, NpgsqlDbType = NpgsqlDbType.Text });
        }
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> Scalar(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task Read(NpgsqlConnection conn, string sql, string? source, Action<NpgsqlDataReader> row, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
        if (source is not null)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = source, NpgsqlDbType = NpgsqlDbType.Text });
        }
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            row(reader);
        }
    }
}
