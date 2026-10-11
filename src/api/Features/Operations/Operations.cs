using System.Security.Claims;
using System.Text.Json;
using Cmdb.Api.Auth;
using Npgsql;

namespace Cmdb.Api.Features.Operations;

/// <summary>
/// The command layer (#23, ADR-0006): a write starts an operation in its own transaction, saying who (the signed-in user
/// and the client), what (the command and its arguments) and why. History rows and the change stream written in the same
/// transaction point to it. A write that does not start one still gets one from the database's statement triggers, but
/// without a person or a reason, so every write path through the API starts its own.
/// </summary>
public static class Operations
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Starts the transaction's operation and returns its id.</summary>
    public static async Task<long> BeginAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ClaimsPrincipal user, string command, object? payload,
        string? why, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT cmdb_begin_operation($1, $2, $3, $4::jsonb, $5)", conn, tx);
        cmd.Parameters.Add(new() { Value = Actor(user) });
        cmd.Parameters.Add(new() { Value = (object?)user.FindFirst(CmdbClaims.Client)?.Value ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = command });
        cmd.Parameters.Add(new() { Value = JsonSerializer.Serialize(payload ?? new { }, Json) });
        cmd.Parameters.Add(new() { Value = (object?)why ?? DBNull.Value });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Who an operation is by: the user's name, or the subject for a service account without one.</summary>
    public static string Actor(ClaimsPrincipal user) =>
        user.FindFirst(CmdbClaims.Username)?.Value ?? user.FindFirst(CmdbClaims.Subject)?.Value ?? "anonym";
}
