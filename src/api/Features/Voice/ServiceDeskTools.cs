using System.ComponentModel;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Cmdb.Api.Auth;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NpgsqlTypes;

namespace Cmdb.Api.Features.Voice;

public sealed record QueueStatus(int Waiting, int Minutes, string Message);

public sealed record ServiceRequestCreated(string Kind, string Message, bool NumberSentBySms);

public sealed record CallbackBooked(int Minutes, string Message);

public sealed record CatalogItem(string Id, string Name, int DeliveryDays);

/// <summary>
/// The service desk's and IT self-service's tools (ADR-0016, #151), served on their own voice endpoints. A broken
/// access tag, a password reset and an equipment order need a verified caller (the same one-time code as the operations
/// agent, ADR-0015); the queue and a callback from a person are open to anyone. Request numbers go by SMS, never spoken.
/// All synthetic: no real access control, identity or ordering system behind them.
/// </summary>
[McpServerToolType]
public sealed class ServiceDeskTools(SystemDb system, IHttpContextAccessor http)
{
    /// <summary>Minutes before a person calls back: a base plus a slot per open callback ahead in the queue.</summary>
    internal const int BaseMinutes = 3, MinutesPerCallback = 5;

    public static readonly IReadOnlyList<CatalogItem> Catalog =
    [
        new("laptop", "Bärbar dator (standard)", 5),
        new("headset", "Headset med brusreducering", 2),
        new("screen", "Skärm 27 tum", 3),
        new("dock", "Dockningsstation", 3),
        new("phone", "Mobiltelefon", 4),
        new("keyboard-mouse", "Tangentbord och mus", 2),
    ];

    private ClaimsPrincipal User => http.HttpContext!.User;
    private string Conversation => User.FindFirstValue(VoiceClaims.Conversation) ?? "unknown";
    private string? Employee => User.FindFirstValue(VoiceClaims.Employee);

    [McpServerTool(Name = "queue_status", Title = "Kötid", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("How long until a person can call back: callers waiting and the estimated minutes. Open to anyone. Use it " +
        "when you cannot help and offer a callback (\"Det är cirka X minuters kö\").")]
    public Task<QueueStatus> QueueStatus(CancellationToken ct = default) =>
        VoiceAudit.RunAsync(system, User, "queue_status", async () =>
        {
            var waiting = await WaitingAsync(ct);
            var minutes = Minutes(waiting);
            return new QueueStatus(waiting, minutes, $"Cirka {minutes} minuters kö.");
        });

    [McpServerTool(Name = "request_callback", Title = "Boka uppringning", ReadOnly = false, Idempotent = false, OpenWorld = false)]
    [Description("Books a callback from a person when you cannot help. Open to anyone; for a verified caller the name and " +
        "number are taken from the employee register and the ones given here are ignored. Ask for the name and a number " +
        "only if the caller is not verified. Say the estimated wait it returns.")]
    public Task<CallbackBooked> RequestCallback(
        [Description("What the caller needs help with, in one sentence.")] string topic,
        [Description("The caller's name, if not verified. Empty otherwise.")] string name = "",
        [Description("The number to call back, if not verified. Empty otherwise.")] string phone = "",
        CancellationToken ct = default) =>
        VoiceAudit.RunAsync(system, User, "request_callback", async () =>
        {
            var caller = await CallerAsync(ct);
            var callerName = caller?.Name ?? Clip(name.Trim(), 100);
            var number = caller?.Phone ?? Clip(phone.Trim(), 30);
            if (callerName.Length == 0 || number.Length == 0)
            {
                throw new McpException("Ange uppringarens namn och ett nummer att ringa upp på.");
            }
            var waiting = await WaitingAsync(ct);
            await InsertAsync("callback", caller, callerName, number, Clip(topic, 500), new { }, ct);
            var minutes = Minutes(waiting);
            return new CallbackBooked(minutes, $"Uppringning bokad. En person ringer upp om cirka {minutes} minuter.");
        });

    [McpServerTool(Name = "report_tag_fault", Title = "Passertagg fungerar inte", ReadOnly = false, Idempotent = false, OpenWorld = false)]
    [Description("A caller's access tag (passertagg, passerkort) has stopped working: blocks the old tag and orders a new " +
        "one, collected at the reception. Verified callers only. The request number is sent by SMS; do not read it out.")]
    public Task<ServiceRequestCreated> ReportTagFault(
        [Description("What happened, in one sentence, e.g. \"Taggen fungerar inte på entrén sedan i morse\".")] string description,
        CancellationToken ct = default) =>
        VoiceAudit.RunAsync(system, User, "report_tag_fault", async () =>
        {
            var caller = await RequireVerifiedAsync(ct);
            var number = await InsertAsync("tag", caller, caller.Name, caller.Phone, Clip(description, 500), new { blocked = true }, ct);
            await VoiceSessions.SmsAsync(system.Source, caller,
                $"Service desk: ärende {number}. Din gamla passertagg är spärrad. En ny finns att hämta i receptionen från i morgon kl. 8.", ct);
            return new ServiceRequestCreated("tag", "Den gamla taggen är spärrad och en ny beställd, att hämta i receptionen från i morgon kl. 8.", true);
        }, r => r.Kind);

    [McpServerTool(Name = "reset_password", Title = "Återställ lösenord", ReadOnly = false, Idempotent = false, OpenWorld = false)]
    [Description("Sends a password reset link by SMS to the verified caller's registered phone, valid 15 minutes. " +
        "Verified callers only. Never ask for or accept the old or new password on the call.")]
    public Task<ServiceRequestCreated> ResetPassword(
        [Description("Which account, e.g. \"datorinloggning\" or \"e-post\". Empty for the usual login.")] string account = "",
        CancellationToken ct = default) =>
        VoiceAudit.RunAsync(system, User, "reset_password", async () =>
        {
            var caller = await RequireVerifiedAsync(ct);
            var what = account.Trim().Length > 0 ? Clip(account.Trim(), 100) : "inloggning";
            var number = await InsertAsync("password", caller, caller.Name, caller.Phone, $"Återställ lösenord: {what}", new { account = what }, ct);
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            await VoiceSessions.SmsAsync(system.Source, caller,
                $"IT-självhjälp: återställ lösenordet för {what} på https://itsjalvhjalp.example.invalid/reset/{token} (gäller i 15 minuter). Ärende {number}.", ct);
            return new ServiceRequestCreated("password", "En länk för att återställa lösenordet är skickad med SMS och gäller i 15 minuter.", true);
        }, r => r.Kind);

    [McpServerTool(Name = "equipment_catalog", Title = "Utrustningskatalog", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What equipment can be ordered, with delivery time in working days. Open to anyone.")]
    public Task<IReadOnlyList<CatalogItem>> EquipmentCatalog() =>
        VoiceAudit.RunAsync(system, User, "equipment_catalog", () => Task.FromResult(Catalog));

    [McpServerTool(Name = "order_equipment", Title = "Beställ utrustning", ReadOnly = false, Idempotent = false, OpenWorld = false)]
    [Description("Orders an item from equipment_catalog for the verified caller, delivered to their usual workplace. " +
        "Verified callers only; confirm the item with the caller first. The order number is sent by SMS; do not read it out.")]
    public Task<ServiceRequestCreated> OrderEquipment(
        [Description("The item's id from equipment_catalog, e.g. \"headset\".")] string item,
        [Description("Why it is needed, in one sentence.")] string reason,
        CancellationToken ct = default) =>
        VoiceAudit.RunAsync(system, User, "order_equipment", async () =>
        {
            var caller = await RequireVerifiedAsync(ct);
            var wanted = Catalog.FirstOrDefault(c => string.Equals(c.Id, item.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new McpException($"Okänd artikel. Välj bland: {string.Join(", ", Catalog.Select(c => c.Id))}.");
            var number = await InsertAsync("equipment", caller, caller.Name, caller.Phone, $"{wanted.Name}: {Clip(reason, 400)}",
                new { item = wanted.Id, wanted.DeliveryDays }, ct);
            await VoiceSessions.SmsAsync(system.Source, caller,
                $"IT-självhjälp: beställning {number}, {wanted.Name}. Leverans om cirka {wanted.DeliveryDays} arbetsdagar.", ct);
            return new ServiceRequestCreated("equipment", $"{wanted.Name} är beställd, leverans om cirka {wanted.DeliveryDays} arbetsdagar.", true);
        }, r => r.Kind);

    internal static int Minutes(int waiting) => BaseMinutes + MinutesPerCallback * waiting;

    public static string Number(long id) => string.Create(CultureInfo.InvariantCulture, $"SR-{id:D5}");

    private async Task<int> WaitingAsync(CancellationToken ct)
    {
        await using var cmd = system.Source.CreateCommand("SELECT count(*) FROM service_request WHERE kind = 'callback' AND status = 'open'");
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<VoiceCallerInfo?> CallerAsync(CancellationToken ct) =>
        Employee is { } employee ? await VoiceSessions.CallerAsync(system.Source, employee, ct) : null;

    private async Task<VoiceCallerInfo> RequireVerifiedAsync(CancellationToken ct) => await CallerAsync(ct)
        ?? throw new McpException("Uppringaren är inte verifierad. Be om anställningsnummer och använd request_verification_code och verify_caller först.");

    private async Task<string> InsertAsync(string kind, VoiceCallerInfo? caller, string name, string phone, string summary, object details,
        CancellationToken ct)
    {
        await using var cmd = system.Source.CreateCommand("""
            INSERT INTO service_request (kind, employee_id, caller_name, phone, conversation_id, summary, details)
            VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id
            """);
        cmd.Parameters.Add(new() { Value = kind });
        cmd.Parameters.Add(new() { Value = (object?)caller?.EmployeeId ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = name });
        cmd.Parameters.Add(new() { Value = phone });
        cmd.Parameters.Add(new() { Value = Conversation });
        cmd.Parameters.Add(new() { Value = summary });
        cmd.Parameters.Add(new() { Value = JsonSerializer.SerializeToDocument(details, JsonSerializerOptions.Web), NpgsqlDbType = NpgsqlDbType.Jsonb });
        return Number((long)(await cmd.ExecuteScalarAsync(ct))!);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
