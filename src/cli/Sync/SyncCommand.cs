using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cmdb.Cli.Sync;

/// <summary>
/// <c>cmdb sync &lt;adapter&gt;</c> (#217): runs the adapter into a temporary folder, packs the files and sends them to
/// reconciliation (<c>POST /api/reconciliations</c>) with the integration's own account, so the run stays inside the
/// integration's scopes (ADR-0020, ADR-0021). Prints the report; <c>--dry-run</c> reconciles without plans.
/// </summary>
internal static class SyncCommand
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Every adapter in this assembly, by name.</summary>
    public static IReadOnlyDictionary<string, IAdapter> Adapters { get; } = typeof(IAdapter).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IAdapter).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
        .Select(t => (IAdapter)Activator.CreateInstance(t)!)
        .ToDictionary(a => a.Name, StringComparer.Ordinal);

    public static async Task<int> RunAsync(Arguments args, Api api, IReadOnlyDictionary<string, string?> env, HttpClient sourceHttp,
        TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (args.Positional.Count == 0)
        {
            var list = new StringBuilder("Adaptrar:\n");
            foreach (var a in Adapters.Values.OrderBy(a => a.Name, StringComparer.Ordinal))
            {
                list.AppendLine(CultureInfo.InvariantCulture, $"  {a.Name,-20} {a.Description}");
            }
            await output.WriteAsync(list.ToString());
            return ExitCodes.Usage;
        }
        var name = args.Positional[0];
        if (!Adapters.TryGetValue(name, out var adapter))
        {
            throw new CliException(ExitCodes.Usage, $"Okänd adapter: {name}. Kör cmdb sync för att se vilka som finns.");
        }
        var source = args.Option("source") ?? adapter.Source;
        var config = await ConfigAsync(args.Option("config") ?? env.GetValueOrDefault("CMDB_SYNC_CONFIG"), ct);
        var context = new AdapterContext(adapter.Name, config, env, sourceHttp, error);

        var folder = args.Option("out") ?? Directory.CreateTempSubdirectory("cmdb-sync-").FullName;
        Directory.CreateDirectory(folder);
        try
        {
            var started = DateTimeOffset.UtcNow;
            using (var writer = new ExchangeWriter(folder))
            {
                try
                {
                    await adapter.ReadAsync(context, writer, ct);
                }
                catch (AdapterException ex)
                {
                    throw new CliException(ExitCodes.Failed, $"{adapter.Name}: {ex.Message}");
                }
                await error.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{adapter.Name} läste {string.Join(", ", writer.Rows.Select(r => $"{r.Value} {r.Key}"))} på {(DateTimeOffset.UtcNow - started).TotalSeconds:0.0} s."));
            }
            foreach (var warning in context.Warnings.Take(50))
            {
                await error.WriteLineAsync($"  varning: {warning}");
            }
            if (context.Warnings.Count > 50)
            {
                await error.WriteLineAsync($"  … och {context.Warnings.Count - 50} varningar till.");
            }
            if (args.Option("out") is not null)
            {
                await output.WriteLineAsync($"Filerna ligger i {folder}. Inget skickades.");
                return ExitCodes.Ok;
            }

            var report = await SendAsync(api, folder, source, args.Options.ContainsKey("dry-run"), ct);
            await output.WriteAsync(args.Json ? report.ToJsonString(Indented) + Environment.NewLine : ReconciliationText.Report(report, context.Warnings.Count));
            return report["errors"] is JsonArray { Count: > 0 } ? ExitCodes.Failed : ExitCodes.Ok;
        }
        finally
        {
            if (args.Option("out") is null)
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private static async Task<JsonNode> SendAsync(Api api, string folder, string source, bool dryRun, CancellationToken ct)
    {
        var zip = Path.Combine(Path.GetTempPath(), $"cmdb-sync-{Guid.NewGuid():N}.zip");
        try
        {
            await ZipFile.CreateFromDirectoryAsync(folder, zip, CompressionLevel.Fastest, includeBaseDirectory: false, ct);
            await using var file = File.OpenRead(zip);
            using var form = new MultipartFormDataContent
            {
                { new StringContent(source), "source" },
                { new StringContent(dryRun ? "true" : "false"), "dryRun" },
            };
            var content = new StreamContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(content, "file", "export.zip");
            return await api.PostContentAsync("/api/reconciliations", form, ct);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    private static async Task<JsonObject> ConfigAsync(string? path, CancellationToken ct)
    {
        if (path is null)
        {
            return [];
        }
        if (!File.Exists(path))
        {
            throw new CliException(ExitCodes.Usage, $"Konfigurationen {path} finns inte.");
        }
        try
        {
            return JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject
                ?? throw new CliException(ExitCodes.Usage, $"Konfigurationen {path} är inget JSON-objekt.");
        }
        catch (JsonException ex)
        {
            throw new CliException(ExitCodes.Usage, $"Konfigurationen {path} är ogiltig JSON: {ex.Message}");
        }
    }
}

/// <summary>A reconciliation report as compact text, for <c>cmdb sync</c> and <c>cmdb reconciliation</c>.</summary>
internal static class ReconciliationText
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        ["site"] = "siter",
        ["equipment"] = "utrustning",
        ["cable"] = "kablar",
        ["service"] = "tjänster",
    };

    public static string Report(JsonNode report, int warnings = 0)
    {
        var text = new StringBuilder();
        var dry = report["dryRun"]?.GetValue<bool>() == true;
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Avstämning {report["id"]} mot {Text.Get(report, "source")}{(dry ? " (provkörning)" : "")} på {report["elapsedMs"]?.GetValue<double>() / 1000:0.0} s"));
        foreach (var count in report["counts"]?.AsArray() ?? [])
        {
            var type = Text.Get(count, "objectType");
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  {Types.GetValueOrDefault(type, type),-11} {N(count, "reported")} rapporterade, {N(count, "matched")} matchade ({N(count, "linked")} kopplade), "
                + $"{N(count, "new")} nya, {N(count, "changed")} ändrade, {N(count, "missing")} saknas, {N(count, "deviations")} avvikelser, "
                + $"{N(count, "outsideScope")} utanför omfånget"));
        }
        var plans = (report["reviewPlanId"] is JsonValue review ? $", plan för granskning plan:{review}" : "")
            + (report["appliedPlanId"] is JsonValue applied ? $", införd plan:{applied}" : "");
        text.AppendLine($"  {N(report, "operations")} operationer{plans}");
        if (Text.Get(report, "autoApplyProblem") is { Length: > 0 } problem)
        {
            text.AppendLine($"  Den betrodda planen fördes inte in: {problem}");
        }
        if (report["reasons"] is JsonObject { Count: > 0 } reasons)
        {
            text.AppendLine("  Avvikelser: " + string.Join(", ", reasons.Select(r => $"{r.Key} {r.Value}")));
        }
        foreach (var deviation in report["deviations"]?.AsArray().Take(20) ?? [])
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"    {Text.Get(deviation, "objectType")} {Text.Get(deviation, "externalId")} {Text.Get(deviation, "attribute")}: "
                + $"{deviation!["source"]?.ToJsonString() ?? "–"} i källan, {deviation["cmdb"]?.ToJsonString() ?? "–"} i cmdb ({Text.Get(deviation, "reason")})"));
        }
        foreach (var note in report["notReconciled"]?.AsArray() ?? [])
        {
            text.AppendLine($"  {note}");
        }
        foreach (var e in report["errors"]?.AsArray() ?? [])
        {
            text.AppendLine($"  fel: {e}");
        }
        if (warnings > 0)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {warnings} varningar från adaptern (se ovan)"));
        }
        return text.ToString();
    }

    public static string List(JsonNode runs)
    {
        var text = new StringBuilder();
        foreach (var run in runs.AsArray())
        {
            Text.Line(text, $"reconciliation:{Text.Get(run, "id")}", Text.Get(run, "source"), Text.Get(run, "startedAt"), Text.Get(run, "runBy"),
                run!["dryRun"]?.GetValue<bool>() == true ? "provkörning" : null,
                run["reviewPlanId"] is JsonValue r ? $"granskas i plan:{r}" : null,
                run["appliedPlanId"] is JsonValue a ? $"införd plan:{a}" : null);
        }
        return text.Length == 0 ? "Inga avstämningar.\n" : text.ToString();
    }

    private static string N(JsonNode? node, string name) => node?[name]?.ToString() ?? "0";
}
