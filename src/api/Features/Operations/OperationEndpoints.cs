using System.Text.Json;
using Cmdb.Api.Auth;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Operations;

public sealed record OperationInfo(long Id, DateTimeOffset At, string Actor, string? Client, string Command, JsonElement Payload, string? Why,
    bool Chained);

public sealed record OperationListRequest
{
    /// <summary>Operations before this id, for the next page.</summary>
    public long? Before { get; init; }
    public int? Limit { get; init; }
    public string? Actor { get; init; }
}

public sealed record OperationList(IReadOnlyList<OperationInfo> Items, long? Next);

/// <summary>
/// The operation log (#23), newest first. An operation's arguments name objects anywhere in the network, so only a caller
/// whose scopes cover all of it may read the log; everyone else gets 403.
/// </summary>
public sealed class OperationListEndpoint(RequestDb db) : Endpoint<OperationListRequest, OperationList>
{
    public override void Configure() => Get("/operations");

    public override async Task HandleAsync(OperationListRequest req, CancellationToken ct)
    {
        if (!HttpContext.Scope().Unrestricted)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var limit = Math.Clamp(req.Limit ?? 50, 1, 500);
        await using var cmd = db.CreateCommand("""
            SELECT id, at, actor, client, command, payload::text, why, hash IS NOT NULL FROM operation
            WHERE ($1::bigint IS NULL OR id < $1) AND ($2::text IS NULL OR actor = $2)
            ORDER BY id DESC LIMIT $3
            """);
        cmd.Parameters.Add(new() { Value = (object?)req.Before ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new() { Value = (object?)req.Actor ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = limit + 1 });
        var items = new List<OperationInfo>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                items.Add(OperationRows.Read(reader, 0));
            }
        }
        var next = items.Count > limit ? items[limit - 1].Id : (long?)null;
        await Send.OkAsync(new OperationList(items.Take(limit).ToList(), next), ct);
    }
}

public sealed record ChainBreak(long Id, string Reason);

public sealed record ChainVerification(bool Enabled, long Operations, long Chained, bool Intact, IReadOnlyList<ChainBreak> Breaks);

/// <summary>
/// Checks the hash chain (#23): every chained operation's hash is recomputed from what is stored and its link to the
/// previous chained operation compared. A row changed afterwards, or one removed from the middle, breaks the chain there.
/// </summary>
public sealed class OperationVerifyEndpoint(RequestDb db) : EndpointWithoutRequest<ChainVerification>
{
    public override void Configure() => Get("/operations/verify");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!HttpContext.Scope().Unrestricted)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        await using var cmd = db.CreateCommand("""
            SELECT (SELECT hash_chain FROM operation_config WHERE id = 1), (SELECT count(*) FROM operation),
                   (SELECT count(*) FROM operation WHERE hash IS NOT NULL)
            """);
        bool enabled;
        long total, chained;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            enabled = !reader.IsDBNull(0) && reader.GetBoolean(0);
            total = reader.GetInt64(1);
            chained = reader.GetInt64(2);
        }
        var breaks = new List<ChainBreak>();
        await using (var check = db.CreateCommand("""
            SELECT id, hash <> sha256(coalesce(prev_hash, ''::bytea) || text), prev_hash IS DISTINCT FROM previous
            FROM (SELECT o.id, o.hash, o.prev_hash, cmdb_operation_text(o) AS text, lag(o.hash) OVER (ORDER BY o.id) AS previous
                  FROM operation o WHERE o.hash IS NOT NULL) c
            WHERE hash <> sha256(coalesce(prev_hash, ''::bytea) || text) OR prev_hash IS DISTINCT FROM previous
            ORDER BY id LIMIT 100
            """))
        await using (var reader = await check.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                breaks.Add(new ChainBreak(reader.GetInt64(0), reader.GetBoolean(1)
                    ? "Raden stämmer inte med sin hash: den har ändrats i efterhand."
                    : "Länken till föregående operation saknas: en operation har tagits bort eller lagts in i efterhand."));
            }
        }
        await Send.OkAsync(new ChainVerification(enabled, total, chained, breaks.Count == 0, breaks), ct);
    }
}

internal static class OperationRows
{
    /// <summary>Reads id, at, actor, client, command, payload, why and whether it is chained, from <paramref name="first"/>.</summary>
    public static OperationInfo Read(NpgsqlDataReader reader, int first)
    {
        using var payload = JsonDocument.Parse(reader.GetString(first + 5));
        return new OperationInfo(reader.GetInt64(first), reader.GetFieldValue<DateTimeOffset>(first + 1), reader.GetString(first + 2),
            reader.IsDBNull(first + 3) ? null : reader.GetString(first + 3), reader.GetString(first + 4), payload.RootElement.Clone(),
            reader.IsDBNull(first + 6) ? null : reader.GetString(first + 6), reader.GetBoolean(first + 7));
    }
}
