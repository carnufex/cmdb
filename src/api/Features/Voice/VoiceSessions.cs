using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Cmdb.Api.Features.Voice;

public sealed record VoiceCallerInfo(string EmployeeId, string Name, string Role, string Phone, string[] Groups);

public enum CodeRequest
{
    /// <summary>A code was sent, or the employee id is unknown: the answer is the same, so ids cannot be probed.</summary>
    Sent,

    /// <summary>Three codes already in this call, or three wrong attempts: offer a human instead.</summary>
    Locked,
}

public enum CodeCheck
{
    Verified,
    Wrong,
    Expired,
    Locked,
    NoCode,
}

/// <summary>
/// Step-up verification on the voice channel (ADR-0015, #134): a six-digit code to the phone registered for the
/// employee id, valid 5 minutes with 3 attempts; a correct code verifies the call for 30 minutes. Codes are stored
/// hashed; the SMS gateway is a stub, an outbox the web app's agent panel shows.
/// </summary>
public static class VoiceSessions
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    public const int MaxAttempts = 3;
    public const int MaxCodesPerCall = 3;

    public static async Task<VoiceCallerInfo?> VerifiedCallerAsync(NpgsqlDataSource db, string conversation, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT c.employee_id, c.name, c.role, c.phone, c.groups
            FROM voice_session s JOIN voice_caller c ON c.employee_id = s.employee_id
            WHERE s.conversation_id = $1 AND s.expires_at > now()
            """);
        cmd.Parameters.Add(new() { Value = conversation });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Caller(reader) : null;
    }

    public static async Task<VoiceCallerInfo?> CallerAsync(NpgsqlDataSource db, string employeeId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT employee_id, name, role, phone, groups FROM voice_caller WHERE employee_id = $1");
        cmd.Parameters.Add(new() { Value = Normalize(employeeId) });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Caller(reader) : null;
    }

    /// <summary>Sends a new code for the employee in this call, unless the call has used up its codes or attempts.</summary>
    public static async Task<CodeRequest> RequestCodeAsync(NpgsqlDataSource db, string conversation, string employeeId, CancellationToken ct)
    {
        employeeId = Normalize(employeeId);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using (var count = new NpgsqlCommand(
            "SELECT count(*), coalesce(sum(attempts), 0) FROM voice_challenge WHERE conversation_id = $1", conn))
        {
            count.Parameters.Add(new() { Value = conversation });
            await using var reader = await count.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            if (reader.GetInt64(0) >= MaxCodesPerCall || reader.GetInt64(1) >= MaxAttempts)
            {
                return CodeRequest.Locked;
            }
        }
        var caller = await CallerAsync(db, employeeId, ct);
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO voice_challenge (conversation_id, employee_id, code_hash, attempts, expires_at) VALUES ($1, $2, $3, 0, now() + $4)", conn, tx))
        {
            insert.Parameters.Add(new() { Value = conversation });
            insert.Parameters.Add(new() { Value = employeeId });
            insert.Parameters.Add(new() { Value = Hash(conversation, employeeId, code) });
            insert.Parameters.Add(new() { Value = CodeLifetime });
            await insert.ExecuteNonQueryAsync(ct);
        }
        if (caller is not null)
        {
            await using var sms = new NpgsqlCommand("INSERT INTO voice_sms (to_phone, employee_id, body) VALUES ($1, $2, $3)", conn, tx);
            sms.Parameters.Add(new() { Value = caller.Phone });
            sms.Parameters.Add(new() { Value = caller.EmployeeId });
            sms.Parameters.Add(new() { Value = $"Din kod till Driftagenten: {code}. Gäller i 5 minuter. Lämna aldrig ut den till någon annan än agenten i samtalet." });
            await sms.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return CodeRequest.Sent;
    }

    /// <summary>Checks a code against the latest one sent in this call; a match verifies the call for the employee.</summary>
    public static async Task<(CodeCheck Result, int AttemptsLeft)> VerifyAsync(NpgsqlDataSource db, string conversation, string employeeId, string code,
        CancellationToken ct)
    {
        employeeId = Normalize(employeeId);
        code = new string(code.Where(char.IsAsciiDigit).ToArray());
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        long id;
        int attempts;
        bool expired;
        string hash;
        await using (var latest = new NpgsqlCommand("""
            SELECT id, attempts, expires_at <= now(), code_hash FROM voice_challenge
            WHERE conversation_id = $1 AND employee_id = $2 AND used_at IS NULL
            ORDER BY id DESC LIMIT 1 FOR UPDATE
            """, conn, tx))
        {
            latest.Parameters.Add(new() { Value = conversation });
            latest.Parameters.Add(new() { Value = employeeId });
            await using var reader = await latest.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return (CodeCheck.NoCode, 0);
            }
            (id, attempts, expired, hash) = (reader.GetInt64(0), reader.GetInt32(1), reader.GetBoolean(2), reader.GetString(3));
        }
        var total = await ScalarAsync(conn, tx, "SELECT coalesce(sum(attempts), 0) FROM voice_challenge WHERE conversation_id = $1", conversation, ct);
        if (total >= MaxAttempts)
        {
            return (CodeCheck.Locked, 0);
        }
        if (expired)
        {
            return (CodeCheck.Expired, MaxAttempts - (int)total);
        }
        var match = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(Hash(conversation, employeeId, code)));
        // A known employee is required as well: a code "sent" to an unknown id matches nothing.
        var known = await CallerAsync(db, employeeId, ct) is not null;
        if (!match || !known)
        {
            await ExecAsync(conn, tx, "UPDATE voice_challenge SET attempts = attempts + 1 WHERE id = $1", id, ct);
            await tx.CommitAsync(ct);
            var left = MaxAttempts - (int)total - 1;
            return (left <= 0 ? CodeCheck.Locked : CodeCheck.Wrong, Math.Max(left, 0));
        }
        await ExecAsync(conn, tx, "UPDATE voice_challenge SET used_at = now() WHERE id = $1", id, ct);
        await using (var session = new NpgsqlCommand("""
            INSERT INTO voice_session (conversation_id, employee_id, verified_at, expires_at) VALUES ($1, $2, now(), now() + $3)
            ON CONFLICT (conversation_id) DO UPDATE SET employee_id = excluded.employee_id, verified_at = now(), expires_at = excluded.expires_at
            """, conn, tx))
        {
            session.Parameters.Add(new() { Value = conversation });
            session.Parameters.Add(new() { Value = employeeId });
            session.Parameters.Add(new() { Value = SessionLifetime });
            await session.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return (CodeCheck.Verified, MaxAttempts - (int)total);
    }

    /// <summary>Spoken ids arrive as "ett noll noll ett" turned into "1 0 0 1" or "1001": digits only.</summary>
    public static string Normalize(string employeeId) => new(employeeId.Where(char.IsAsciiDigit).ToArray());

    private static string Hash(string conversation, string employeeId, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{conversation}\n{employeeId}\n{code}")));

    private static VoiceCallerInfo Caller(NpgsqlDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<string[]>(4));

    private static async Task<long> ScalarAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, string value, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new() { Value = value });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new() { Value = id });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
