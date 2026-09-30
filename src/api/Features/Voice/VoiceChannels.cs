using System.Diagnostics;
using System.Security.Claims;
using Cmdb.Api.Auth;
using ModelContextProtocol;

namespace Cmdb.Api.Features.Voice;

/// <summary>
/// The voice agents' MCP endpoints (ADR-0016, #151): one per agent, each serving only that agent's tools, so an agent
/// cannot reach another agent's tools whatever its prompt says. Same secret, verification and scopes (ADR-0015).
/// </summary>
public static class VoiceChannels
{
    public const string Noc = "/voice/mcp";
    public const string ServiceDesk = "/voice/servicedesk/mcp";
    public const string It = "/voice/it/mcp";

    private static readonly string[] Verification = ["request_verification_code", "verify_caller"];
    private static readonly string[] Callback = ["queue_status", "request_callback"];

    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Tools = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        [Noc] = Set([.. VoiceTools.Names, .. Callback]),
        [ServiceDesk] = Set([.. Verification, .. Callback, "report_tag_fault"]),
        [It] = Set([.. Verification, .. Callback, "reset_password", "equipment_catalog", "order_equipment"]),
    };

    /// <summary>Every tool served on a voice endpoint; none of them is served on /mcp.</summary>
    public static readonly IReadOnlySet<string> AllTools = Set([.. Tools.Values.SelectMany(t => t)]);

    /// <summary>The voice endpoint a request is for, or null for any other path.</summary>
    public static string? For(PathString path) =>
        Tools.Keys.FirstOrDefault(p => path.StartsWithSegments(p, StringComparison.Ordinal));

    private static HashSet<string> Set(IEnumerable<string> names) => new(names, StringComparer.Ordinal);
}

/// <summary>Logs every voice tool call (ADR-0015): tool, outcome, time, conversation and verified employee.</summary>
internal static class VoiceAudit
{
    public static async Task<T> RunAsync<T>(SystemDb system, ClaimsPrincipal user, string tool, Func<Task<T>> run,
        Func<T, string>? outcome = null, string? employeeId = null, Func<T, string?>? reference = null)
    {
        var sw = Stopwatch.StartNew();
        var result = "error";
        string? about = null;
        try
        {
            var value = await run();
            result = outcome?.Invoke(value) ?? "ok";
            about = reference?.Invoke(value);
            return value;
        }
        catch (McpException)
        {
            result = "refused";
            throw;
        }
        finally
        {
            await using var cmd = system.Source.CreateCommand("""
                INSERT INTO voice_tool_call (conversation_id, employee_id, tool, outcome, milliseconds, reference) VALUES ($1, $2, $3, $4, $5, $6)
                """);
            cmd.Parameters.Add(new() { Value = user.FindFirstValue(VoiceClaims.Conversation) ?? "unknown" });
            cmd.Parameters.Add(new() { Value = (object?)(user.FindFirstValue(VoiceClaims.Employee) ?? employeeId) ?? DBNull.Value });
            cmd.Parameters.Add(new() { Value = tool });
            cmd.Parameters.Add(new() { Value = result });
            cmd.Parameters.Add(new() { Value = (int)sw.ElapsedMilliseconds });
            cmd.Parameters.Add(new() { Value = (object?)about ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
