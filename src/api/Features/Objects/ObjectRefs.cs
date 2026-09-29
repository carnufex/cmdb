using System.Text.Json;
using Npgsql;

namespace Cmdb.Api.Features.Objects;

/// <summary>A link to another object. The web app opens it in a panel.</summary>
public sealed record ObjectRef(string Type, long Id, string Code, string? Name = null, string? Lifecycle = null)
{
    /// <summary>Stands in for an object outside the caller's scope (#22): no id, no code, nothing to follow.</summary>
    public static ObjectRef Hidden(string type) => new(type, 0, "Dold", "Utanför ditt omfång");
}

/// <summary>
/// What a terminal is, in words and as a link: "port ge-0/0/3 on SKP-000123 AX-24 1" or
/// "fibre 7 (A) in K-000456". Used wherever connections and circuit hops are shown.
/// </summary>
public sealed record TerminalRef(long TerminalId, string Label, ObjectRef Owner, ObjectRef Site);

internal static class Terminals
{
    /// <summary>Describes the given terminals in one query. Unknown ids are left out.</summary>
    public static async Task<Dictionary<long, TerminalRef>> DescribeAsync(NpgsqlConnection conn, IReadOnlyCollection<long> ids, Cmdb.Api.Auth.UserScope scope, CancellationToken ct)
    {
        var result = new Dictionary<long, TerminalRef>();
        if (ids.Count == 0)
        {
            return result;
        }

        await using var cmd = new NpgsqlCommand($"""
            SELECT t.id, 'port', p.name, e.id, e.name, e.lifecycle::text, s.id, s.code, s.name, s.lifecycle::text, NULL::int, NULL::char,
                   {Cmdb.Api.Auth.ScopeSql.Site("s.id", 2)}
            FROM unnest($1::bigint[]) AS t(id)
            JOIN port p ON p.terminal_id = t.id
            JOIN equipment e ON e.id = p.equipment_id
            JOIN site s ON s.id = e.site_id
            UNION ALL
            SELECT t.id, 'conductor_end', NULL, c.id, c.code, c.lifecycle::text, s.id, s.code, s.name, s.lifecycle::text, co.number, ce.side,
                   {Cmdb.Api.Auth.ScopeSql.Cable("c.id", 2)}
            FROM unnest($1::bigint[]) AS t(id)
            JOIN conductor_end ce ON ce.terminal_id = t.id
            JOIN conductor co ON co.id = ce.conductor_id
            JOIN cable c ON c.id = co.cable_id
            JOIN site s ON s.id = CASE WHEN ce.side = 'A' THEN c.a_site_id ELSE c.b_site_id END
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = ids.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter { Value = scope.Keys });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            if (!reader.GetBoolean(12))
            {
                // Outside the caller's scope: a neutral placeholder, not even the kind of object behind it.
                result[id] = new TerminalRef(id, "Utanför ditt omfång", ObjectRef.Hidden(reader.GetString(1) == "port" ? "equipment" : "cable"), ObjectRef.Hidden("site"));
                continue;
            }
            var site = new ObjectRef("site", reader.GetInt64(6), reader.GetString(7), reader.GetString(8), reader.GetString(9));
            result[id] = reader.GetString(1) == "port"
                ? new TerminalRef(id, $"Port {reader.GetString(2)}",
                    new ObjectRef("equipment", reader.GetInt64(3), reader.GetString(4), null, reader.GetString(5)), site)
                : new TerminalRef(id, $"Ledare {reader.GetInt32(10)} ({reader.GetChar(11)}-ände)",
                    new ObjectRef("cable", reader.GetInt64(3), reader.GetString(4), null, reader.GetString(5)), site);
        }
        return result;
    }

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
