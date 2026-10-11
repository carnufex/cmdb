using System.Text.Json;
using Cmdb.Api.Auth;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Operations;

public sealed record HistoryRequest(string Type, long Id);

/// <summary>A field that changed; geometry and hidden attributes say only that they changed, without values.</summary>
public sealed record FieldChange(string Field, string? Before, string? After);

/// <summary>
/// One step in an object's history: <c>created</c> (its first recorded version), <c>changed</c> or <c>deleted</c>, with the
/// operation that did it when one is known.
/// </summary>
public sealed record HistoryEntry(DateTimeOffset At, string Kind, OperationInfo? Operation, IReadOnlyList<FieldChange> Changes);

public sealed record ObjectHistory(string Type, long Id, IReadOnlyList<HistoryEntry> Entries);

/// <summary>
/// An object's versions (#23, ADR-0013): the current row and the superseded ones in its history table, newest first, as
/// what changed between them and which operation changed it. The object must be in the caller's scopes now; a deleted one
/// is visible only to a caller who sees the whole network, since its scope can no longer be decided. Attributes the
/// scopes hide stay hidden, and positions too when the scopes hide them.
/// </summary>
public sealed class HistoryEndpoint(RequestDb db) : Endpoint<HistoryRequest, ObjectHistory>
{
    /// <summary>The table and the scope check per type, with $1 the id and $2 the scope keys.</summary>
    private static readonly Dictionary<string, (string Table, string Visible)> Types = new(StringComparer.Ordinal)
    {
        ["site"] = ("site", ScopeSql.Site("$1", 2)),
        ["equipment"] = ("equipment", $"EXISTS (SELECT 1 FROM equipment e WHERE e.id = $1 AND {ScopeSql.Site("e.site_id", 2)})"),
        ["cable"] = ("cable", ScopeSql.Cable("$1", 2)),
        ["circuit"] = ("circuit", ScopeSql.Circuit("$1", 2)),
        ["service"] = ("service", ScopeSql.Service("$1", 2)),
    };

    /// <summary>Bookkeeping that is not a change of the object.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal) { "sys_from", "last_confirmed_at", "length_m" };

    private static readonly HashSet<string> Geometry = new(StringComparer.Ordinal) { "geom" };

    public override void Configure() => Get("/history/{type}/{id}");

    public override async Task HandleAsync(HistoryRequest req, CancellationToken ct)
    {
        if (!Types.TryGetValue(req.Type, out var type))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var scope = HttpContext.Scope();
        // The versions oldest first: superseded ones with the operation that superseded them, then the current row.
        await using var cmd = db.CreateCommand($"""
            WITH visible AS (SELECT {type.Visible} AS ok)
            SELECT v.row::text, v.sys_from, v.sys_to, o.id, o.at, o.actor, o.client, o.command, o.payload::text, o.why, o.hash IS NOT NULL
            FROM (SELECT to_jsonb(h) - 'sys_to' - 'operation_id' AS row, h.sys_from, h.sys_to, h.operation_id
                  FROM {type.Table}_history h WHERE h.id = $1
                  UNION ALL
                  SELECT to_jsonb(c), c.sys_from, NULL, NULL FROM {type.Table} c WHERE c.id = $1) v
            LEFT JOIN operation o ON o.id = v.operation_id
            WHERE (SELECT ok FROM visible)
            ORDER BY v.sys_to NULLS LAST, v.sys_from
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = req.Id });
        cmd.Parameters.Add(scope.Parameter());
        var versions = new List<(JsonElement Row, DateTimeOffset From, DateTimeOffset? To, OperationInfo? Op)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                using var row = JsonDocument.Parse(reader.GetString(0));
                versions.Add((row.RootElement.Clone(), reader.GetFieldValue<DateTimeOffset>(1),
                    reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                    reader.IsDBNull(3) ? null : OperationRows.Read(reader, 3)));
            }
        }
        if (versions.Count == 0)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (versions[^1].To is not null && !scope.Unrestricted)
        {
            // Deleted: nothing current to decide the scope by.
            await Send.NotFoundAsync(ct);
            return;
        }

        var entries = new List<HistoryEntry> { new(versions[0].From, "created", null, []) };
        for (var i = 0; i < versions.Count; i++)
        {
            var (row, _, to, op) = versions[i];
            if (to is null)
            {
                break;
            }
            entries.Add(i + 1 < versions.Count
                ? new HistoryEntry(to.Value, "changed", op, Diff(row, versions[i + 1].Row, scope))
                : new HistoryEntry(to.Value, "deleted", op, []));
        }
        entries.Reverse();
        await Send.OkAsync(new ObjectHistory(req.Type, req.Id, entries), ct);
    }

    private static List<FieldChange> Diff(JsonElement before, JsonElement after, UserScope scope)
    {
        var changes = new List<FieldChange>();
        foreach (var field in after.EnumerateObject())
        {
            if (Ignored.Contains(field.Name))
            {
                continue;
            }
            var old = before.TryGetProperty(field.Name, out var b) ? b : default;
            if (field.Name == "attributes" && field.Value.ValueKind == JsonValueKind.Object)
            {
                Attributes(old, field.Value, scope, changes);
                continue;
            }
            if (JsonText(old) == JsonText(field.Value))
            {
                continue;
            }
            if (Geometry.Contains(field.Name))
            {
                if (!scope.HidesCoordinates)
                {
                    changes.Add(new FieldChange(field.Name, null, null));
                }
                continue;
            }
            changes.Add(new FieldChange(field.Name, Value(old), Value(field.Value)));
        }
        return changes;
    }

    private static void Attributes(JsonElement before, JsonElement after, UserScope scope, List<FieldChange> changes)
    {
        var keys = after.EnumerateObject().Select(p => p.Name)
            .Union(before.ValueKind == JsonValueKind.Object ? before.EnumerateObject().Select(p => p.Name) : []);
        foreach (var key in keys.Order(StringComparer.Ordinal))
        {
            if (scope.HiddenAttributes.Contains(key))
            {
                continue;
            }
            var old = before.ValueKind == JsonValueKind.Object && before.TryGetProperty(key, out var b) ? b : default;
            var now = after.TryGetProperty(key, out var a) ? a : default;
            if (JsonText(old) != JsonText(now))
            {
                changes.Add(new FieldChange("attributes." + key, Value(old), Value(now)));
            }
        }
    }

    private static string? JsonText(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? null : e.GetRawText();

    private static string? Value(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        JsonValueKind.String => e.GetString(),
        _ => e.GetRawText(),
    };
}
