using System.ComponentModel;
using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Plans;
using Cmdb.Exchange;
using FastEndpoints;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cmdb.Api.Features.Reconciliation;

/// <summary>What one run found for one object type (#216).</summary>
public sealed class ReconciliationCount
{
    public required string ObjectType { get; init; }
    public int Reported { get; set; }

    /// <summary>Found in cmdb, on the source and its id or by a matching rule.</summary>
    public int Matched { get; set; }

    /// <summary>Of those matched, linked to the source this run by a matching rule.</summary>
    public int Linked { get; set; }

    /// <summary>Created in the plan for review.</summary>
    public int New { get; set; }
    public int Changed { get; set; }
    public int Unchanged { get; set; }
    public int Deviations { get; set; }

    /// <summary>Reported by the source before, and not this time; marked, never removed.</summary>
    public int Missing { get; set; }

    /// <summary>Rows naming objects outside the caller's scopes: counted, not named.</summary>
    public int OutsideScope { get; set; }
}

/// <summary>
/// A difference that did not become an operation. Reasons: owned-by-other (another source owns the attribute by the
/// source priority), not-allowed (the priority leaves the source out), no-operation (no plan operation changes the
/// attribute yet), ambiguous (a matching rule found more than one object), missing (no longer reported), code-taken,
/// outside-scope, unknown-site and cannot-create (no plan operation creates it yet).
/// </summary>
public sealed record ReconciliationDeviation(string ObjectType, long? ObjectId, string ExternalId, string Attribute, JsonNode? Source,
    JsonNode? Cmdb, string Reason);

/// <param name="Reasons">Deviations per reason, all of them; <paramref name="Deviations"/> holds the first 1 000.</param>
/// <param name="ReviewPlanId">The plan with changes for review.</param>
/// <param name="AppliedPlanId">The plan with changes the source is trusted with, brought into production at once.</param>
/// <param name="AutoApplyProblem">Why the trusted plan could not be brought in; it is then left as a draft.</param>
public sealed record ReconciliationReport(long Id, string Source, bool DryRun, IReadOnlyList<ReconciliationCount> Counts, int Operations,
    IReadOnlyDictionary<string, int> Reasons, IReadOnlyList<ReconciliationDeviation> Deviations, IReadOnlyList<string> Errors,
    IReadOnlyList<string> NotReconciled, long? ReviewPlanId, long? AppliedPlanId, string? AutoApplyProblem, double ElapsedMs);

/// <param name="Errors">Errors in the files; such a run compared nothing.</param>
public sealed record ReconciliationSummary(long Id, string Source, string RunBy, DateTimeOffset StartedAt, bool DryRun, double ElapsedMs,
    long? ReviewPlanId, long? AppliedPlanId, int Errors);

public sealed class ReconcileRequest
{
    /// <summary>The source system, as in the source priority, e.g. acme-nms.</summary>
    public string Source { get; set; } = "";
    public bool DryRun { get; set; }

    /// <summary>The exchange format's CSV files (#210) in a zip archive.</summary>
    public IFormFile? File { get; set; }
}

public static class Reconciliations
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Largest archive accepted, and largest it may unpack to.</summary>
    public const long MaxUpload = 256L * 1024 * 1024, MaxUnpacked = 2L * 1024 * 1024 * 1024;

    private static readonly string[] Files =
    [
        ExchangeFormat.Sites, ExchangeFormat.Locations, ExchangeFormat.Equipment, ExchangeFormat.Ports, ExchangeFormat.Cables,
        ExchangeFormat.Connections, ExchangeFormat.Circuits, ExchangeFormat.Hops, ExchangeFormat.Dependencies, ExchangeFormat.Services,
        ExchangeFormat.ServiceCircuits,
    ];

    public static bool ValidSource(string source) => Regex.IsMatch(source, "^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Unpacks the exchange files of an archive into <paramref name="folder"/>: only the format's file names (in any
    /// folder of the archive), each once, and no more than <see cref="MaxUnpacked"/> in all.
    /// </summary>
    public static async Task<string?> UnpackAsync(Stream zip, string folder, CancellationToken ct)
    {
        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
            {
                if (!Files.Contains(entry.Name) || !seen.Add(entry.Name))
                {
                    return $"{entry.FullName}: arkivet får bara innehålla utbytesformatets filer ({string.Join(", ", Files)}), var och en en gång.";
                }
                await using var input = entry.Open();
                await using var output = File.Create(Path.Combine(folder, entry.Name));
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxUnpacked)
                    {
                        return "Arkivet packas upp till mer än 2 GB.";
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            return seen.Count == 0 ? "Arkivet innehåller inga filer i utbytesformatet." : null;
        }
        catch (InvalidDataException)
        {
            return "Filen är inget zip-arkiv.";
        }
    }
}

/// <summary>
/// Runs reconciliation (#216, ADR-0020) on a source's data in the exchange format, inside the caller's scopes: an
/// integration with its own account and scope, or a person. <c>dryRun</c> reports without creating plans or confirming.
/// </summary>
public sealed class ReconcileEndpoint(Reconciler reconciler) : Endpoint<ReconcileRequest, ReconciliationReport>
{
    public override void Configure()
    {
        Post("/reconciliations");
        Roles("cmdb-full", "cmdb-integration");
        AllowFileUploads();
        Options(b => b.WithMetadata(new RequestSizeLimitAttribute(Reconciliations.MaxUpload))
            .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = Reconciliations.MaxUpload }));
    }

    public override async Task HandleAsync(ReconcileRequest req, CancellationToken ct)
    {
        if (!Reconciliations.ValidSource(req.Source))
        {
            AddError(r => r.Source, "source är källsystemets namn: små bokstäver, siffror, punkt, bindestreck och understreck.");
        }
        if (req.File is null)
        {
            AddError(r => r.File!, "file är utbytesformatets filer i ett zip-arkiv.");
        }
        ThrowIfAnyErrors();

        var folder = Directory.CreateTempSubdirectory("cmdb-reconcile-").FullName;
        try
        {
            await using (var zip = req.File!.OpenReadStream())
            {
                if (await Reconciliations.UnpackAsync(zip, folder, ct) is { } problem)
                {
                    AddError(r => r.File!, problem);
                    ThrowIfAnyErrors();
                }
            }
            await Send.OkAsync(await reconciler.RunAsync(User, HttpContext.Scope(), req.Source, folder, req.DryRun, ct), ct);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public sealed class ReconciliationIdRequest
{
    public long Id { get; set; }
}

/// <summary>
/// Earlier runs and their reports. A report is built inside the scopes of whoever ran it, so a caller sees their own
/// runs, and an unrestricted caller every run. Reading needs no role, like other reads (ADR-0021).
/// </summary>
public static class ReconciliationReads
{
    public static async Task<List<ReconciliationSummary>> ListAsync(RequestDb db, ClaimsPrincipal user, UserScope scope, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, source_system, run_by, started_at, dry_run, elapsed_ms, review_plan_id, applied_plan_id,
                   coalesce(jsonb_array_length(report->'errors'), 0) FROM reconciliation
            WHERE $1 OR run_by = $2 ORDER BY id DESC LIMIT 100
            """);
        cmd.Parameters.Add(new() { Value = scope.Unrestricted });
        cmd.Parameters.Add(new() { Value = PlanSql.Actor(user) });
        var runs = new List<ReconciliationSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            runs.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetBoolean(4),
                reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.GetInt32(8)));
        }
        return runs;
    }

    public static async Task<ReconciliationReport?> GetAsync(RequestDb db, ClaimsPrincipal user, UserScope scope, long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT report::text FROM reconciliation WHERE id = $1 AND ($2 OR run_by = $3)");
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = scope.Unrestricted });
        cmd.Parameters.Add(new() { Value = PlanSql.Actor(user) });
        return await cmd.ExecuteScalarAsync(ct) is string json
            ? JsonSerializer.Deserialize<ReconciliationReport>(json, Reconciliations.Json)! with { Id = id }
            : null;
    }
}

public sealed class ListReconciliationsEndpoint(RequestDb db) : EndpointWithoutRequest<List<ReconciliationSummary>>
{
    public override void Configure() => Get("/reconciliations");

    public override async Task HandleAsync(CancellationToken ct) =>
        await Send.OkAsync(await ReconciliationReads.ListAsync(db, User, HttpContext.Scope(), ct), ct);
}

public sealed class GetReconciliationEndpoint(RequestDb db) : Endpoint<ReconciliationIdRequest, ReconciliationReport>
{
    public override void Configure() => Get("/reconciliations/{id}");

    public override async Task HandleAsync(ReconciliationIdRequest req, CancellationToken ct)
    {
        if (await ReconciliationReads.GetAsync(db, User, HttpContext.Scope(), req.Id, ct) is not { } report)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(report, ct);
    }
}

/// <summary>Reconciliation reports for agents (#217): what a source system differs in, and the plans it made.</summary>
[McpServerToolType]
public sealed class ReconciliationTools(RequestDb db, IHttpContextAccessor http)
{
    [McpServerTool(Name = "list_reconciliations", Title = "Avstämningar", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Earlier reconciliations of source systems against cmdb (#216), newest first: source, who ran it, whether it was a dry " +
        "run, and the plan for review and the plan brought in at once. Your own runs, or all when your scope is unrestricted.")]
    public Task<List<ReconciliationSummary>> ListReconciliations(CancellationToken ct = default) =>
        ReconciliationReads.ListAsync(db, http.HttpContext!.User, http.HttpContext.Scope(), ct);

    [McpServerTool(Name = "get_reconciliation", Title = "Avstämningsrapport", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One reconciliation's report: per object type what the source reported, matched, linked, created, changed and no " +
        "longer reports; deviations with the source's and cmdb's value and why they did not become changes; errors in the files. " +
        "Open the plans with preview_plan.")]
    public async Task<ReconciliationReport> GetReconciliation(
        [Description("The reconciliation's id, from list_reconciliations.")] long id,
        CancellationToken ct = default) =>
        await ReconciliationReads.GetAsync(db, http.HttpContext!.User, http.HttpContext.Scope(), id, ct)
            ?? throw new McpException($"Avstämning {id} finns inte, eller kördes av någon annan.");
}
