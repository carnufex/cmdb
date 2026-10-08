using Cmdb.Api.Auth;
using Cmdb.Graph;
using Npgsql;

namespace Cmdb.Api.Features.Voice;

/// <summary>How a service stands when the faulty object is out: another path works, every path runs through it, or it has one path.</summary>
public enum Redundancy
{
    Working,
    False,
    None,
}

public sealed record FaultService(long Id, string Code, string? Name, string ServiceType, bool Critical, Redundancy Redundancy, int Paths);

/// <param name="Services">Affected services the caller may see, critical first and those without a working path first.</param>
/// <param name="Down">Services with no working path.</param>
public sealed record Fault(string Reference, long SiteId, string SiteCode, string SiteName, int Affected, int Down, int CriticalDown,
    int FalseRedundancy, string Priority, IReadOnlyList<FaultService> Services);

/// <summary>Priority by rules (#135), never by the agent: what the fault does to services, not how the caller sounds.</summary>
public static class IncidentPriority
{
    /// <summary>
    /// P1: a critical service has no working path. P2: a critical service is affected but still has one, or another
    /// service has none. P3: everything affected still has a working path, or nothing is affected.
    /// </summary>
    public static string For(IEnumerable<FaultService> services)
    {
        var list = services.ToList();
        if (list.Any(s => s.Critical && s.Redundancy != Redundancy.Working))
        {
            return "P1";
        }
        return list.Any(s => s.Critical || s.Redundancy != Redundancy.Working) ? "P2" : "P3";
    }
}

/// <summary>
/// What a fault at a site, on equipment or on a cable does (#133, #137): the impact analysis of the graph (#10), and for each affected
/// service whether another of its circuits avoids the fault. Two circuits that both run through the fault are false
/// redundancy: the network looks protected on paper and is not. Within the caller's access scopes (#22).
/// </summary>
public static class FaultAnalysis
{
    public static async Task<Fault?> RunAsync(Cmdb.Graph.Graph g, GraphMask mask, NpgsqlDataSource db, string type, long id, CancellationToken ct)
    {
        long siteId;
        ImpactResult result;
        if (type == "site" && g.TryGetSite(id, out var site) && mask.SiteVisible(site))
        {
            siteId = id;
            result = GraphImpact.OfSite(g, site);
        }
        else if (type == "equipment" && g.TryGetEquipment(id, out var equipment) && mask.SiteVisible(g.SiteIndexOfEquipment(equipment)))
        {
            siteId = g.SiteId(g.SiteIndexOfEquipment(equipment));
            result = GraphImpact.OfEquipment(g, equipment);
        }
        else if (type == "cable" && g.TryGetCable(id, out var cable) && mask.CableVisible(cable))
        {
            // A cable belongs to no site; its A end names the place.
            await using var cmd = db.CreateCommand("SELECT a_site_id FROM cable WHERE id = $1");
            cmd.Parameters.Add(new() { Value = id });
            siteId = (long)(await cmd.ExecuteScalarAsync(ct))!;
            result = GraphImpact.OfCable(g, cable);
        }
        else
        {
            return null;
        }

        var affected = result.Circuits.ToHashSet();
        var visible = result.Services.Where(mask.ServiceVisible).ToList();
        var redundancy = visible.ToDictionary(s => s, s =>
        {
            var paths = g.CircuitsOf(s);
            return (paths.Length, Redundancy: paths.ToArray().Any(c => !affected.Contains(c)) ? Redundancy.Working
                : paths.Length > 1 ? Redundancy.False : Redundancy.None);
        });

        var ids = visible.Select(g.ServiceId).ToArray();
        var rows = new Dictionary<long, (string Code, string Name, string Type, bool Critical)>();
        await using (var cmd = db.CreateCommand("""
            SELECT s.id, s.code, s.name, s.service_type,
                   EXISTS (SELECT 1 FROM classification c
                           WHERE c.object_type = 'service' AND c.object_id = s.id AND c.schema_key = 'criticality' AND c.level >= $2)
            FROM service s WHERE s.id = ANY($1)
            """))
        {
            cmd.Parameters.Add(new() { Value = ids });
            // The level that counts as critical is the schema's to say (#176).
            cmd.Parameters.Add(new() { Value = Cmdb.Catalog.ClassificationCatalog.Current.Find("criticality")!.CriticalFrom });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows[reader.GetInt64(0)] = (reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4));
            }
        }
        var services = visible
            .Select(s =>
            {
                var sid = g.ServiceId(s);
                (string Code, string Name, string Type, bool Critical) row = rows.TryGetValue(sid, out var r) ? r : ($"#{sid}", "", "", false);
                return new FaultService(sid, row.Code, row.Name, row.Type, row.Critical, redundancy[s].Redundancy, redundancy[s].Length);
            })
            .OrderByDescending(s => s.Critical)
            .ThenBy(s => s.Redundancy == Redundancy.Working)
            .ThenByDescending(s => s.Redundancy == Redundancy.False)
            .ThenBy(s => s.Code, StringComparer.Ordinal)
            .ToList();

        string code = "", name = "";
        await using (var cmd = db.CreateCommand("SELECT code, name FROM site WHERE id = $1"))
        {
            cmd.Parameters.Add(new() { Value = siteId });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                (code, name) = (reader.GetString(0), reader.GetString(1));
            }
        }
        return new Fault($"{type}:{id}", siteId, code, name, services.Count,
            services.Count(s => s.Redundancy != Redundancy.Working),
            services.Count(s => s.Critical && s.Redundancy != Redundancy.Working),
            services.Count(s => s.Redundancy == Redundancy.False),
            IncidentPriority.For(services), services);
    }
}
