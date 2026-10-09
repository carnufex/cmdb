using System.Text.Json;
using System.Text.Json.Nodes;
using Cmdb.Api.Auth;
using Cmdb.Catalog;
using Cmdb.Database.Provenance;
using Npgsql;

namespace Cmdb.Api.Features.Objects;

/// <summary>What one source system says about an object (#215, ADR-0019).</summary>
/// <param name="Origin">The source that created the object (its own <c>source_system</c>).</param>
/// <param name="Values">The source's value per attribute, as it last reported them.</param>
public sealed record ObjectSource(string Source, string ExternalId, DateTimeOffset ConfirmedAt, bool Origin, IReadOnlyList<SourceValue> Values);

/// <param name="Value">Null for the links to other objects (placement, ends) and a cable's route, which are compared but
/// not shown: they are internal ids or a hash, and could name objects outside the user's scope.</param>
/// <param name="Current">Whether the object still has this value; false when it changed in cmdb after the source confirmed it.</param>
/// <param name="Owner">Whether this source owns the attribute by the catalog's source priority.</param>
public sealed record SourceValue(string Attribute, JsonElement? Value, bool Current, bool Owner);

/// <summary>Reads the source records of one object for its panel and for MCP, masked like the object's attributes.</summary>
public static class Sources
{
    private static readonly HashSet<string> Compared = new(StringComparer.Ordinal) { "placement", "ends", "route" };

    /// <summary>The object's sources, most recently confirmed first; empty when no source has reported it.</summary>
    /// <remarks>The caller has already checked that the object is visible in <paramref name="scope"/>.</remarks>
    public static async Task<IReadOnlyList<ObjectSource>> LoadAsync(NpgsqlConnection conn, string objectType, long id, UserScope scope,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"""
            SELECT r.source_system, r.external_id, r.confirmed_at, r.reported::text, {ReportedValues.Sql(objectType, "t")}::text,
                   t.source_system IS NOT DISTINCT FROM r.source_system
            FROM source_record r JOIN {ReportedValues.Table(objectType)} t ON t.id = r.object_id
            WHERE r.object_type = $1 AND r.object_id = $2
            ORDER BY r.confirmed_at DESC, r.source_system
            """, conn)
        {
            Parameters = { new() { Value = objectType }, new() { Value = id } },
        };
        var rows = new List<(string Source, string ExternalId, DateTimeOffset ConfirmedAt, JsonObject Reported, JsonObject Now, bool Origin)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2),
                    Mask(JsonNode.Parse(reader.GetString(3))!.AsObject(), scope), JsonNode.Parse(reader.GetString(4))!.AsObject(),
                    reader.GetBoolean(5)));
            }
        }
        if (rows.Count == 0)
        {
            return [];
        }

        var priority = SourcePriority.Current;
        var owners = rows.SelectMany(r => r.Reported.Select(v => (Attribute: v.Key, r.Source, r.ConfirmedAt)))
            .GroupBy(v => v.Attribute)
            .ToDictionary(g => g.Key, g => priority.Owner(objectType, g.Key, g.Select(v => (v.Source, v.ConfirmedAt))));
        var now = rows[0].Now;
        return [.. rows.Select(r => new ObjectSource(r.Source, r.ExternalId, r.ConfirmedAt, r.Origin,
            [.. r.Reported.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => new SourceValue(
                v.Key,
                Compared.Contains(v.Key) ? null : JsonSerializer.SerializeToElement(v.Value),
                JsonNode.DeepEquals(v.Value, now[v.Key]),
                owners[v.Key] == r.Source))]))];
    }

    // What the scope hides from the object's attributes it hides here too: the attribute itself, and positions and routes
    // when coordinates are hidden (ADR-0007, CLAUDE.md rule 5).
    private static JsonObject Mask(JsonObject reported, UserScope scope)
    {
        foreach (var hidden in scope.HiddenAttributes)
        {
            reported.Remove(ReportedValues.AttributePrefix + hidden);
        }
        if (scope.HidesCoordinates)
        {
            reported.Remove("position");
            reported.Remove("route");
        }
        return reported;
    }
}
