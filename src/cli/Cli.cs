using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cmdb.Cli;

/// <summary>Exit codes a script can branch on.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Usage = 1;
    public const int NotFound = 2;
    public const int Auth = 3;
    public const int Failed = 4;
}

public sealed class CliException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// The <c>cmdb</c> command line (#86): the same REST API as the web app and the MCP tools, so scopes, plans and logging
/// apply alike. Compact text by default, with a link to the object in the UI; <c>--json</c> prints the API's answer.
/// </summary>
public static class CliApp
{
    public const string Usage = """
        cmdb – CMDB (syntetisk data) från terminalen

        Användning: cmdb <kommando> [argument] [--json] [--url https://cmdb.rosenvall.se]

          search <text> [--limit N]           Snabbsök på kod, namn eller id
          get <ref|kod>                       Ett objekt: site:12, equipment:34, cable:56, service:7, circuit:8 eller en kod
          find-sites [--type T] [--lifecycle L] [--model KEY] [--category C] [--service-type S] [--limit N]
                                              Avancerad sökning
          impact <ref>                        Vad ett avbrott på en kabel, utrustning eller site påverkar
          trace <ref>                         Spåra service:ID, circuit:ID eller terminal:ID
          neighbourhood <site> [--hops N]     Siter inom 1–3 kabelhopp
          catalog                             Sitetyper, livscykler och utrustningsmodeller
          plans                               Planer du kan se
          plan <plan:ID>                      En plans ändringar mot produktion
          whoami                              Vem du är och ditt behörighetsomfång
          sync <adapter> [--dry-run] [--source S] [--config FIL] [--out MAPP]
                                              Läs ett källsystem och stäm av det mot cmdb (#217); utan adapter listas de
          reconciliations                     Tidigare avstämningar
          reconciliation <id>                 En avstämnings rapport
          login | logout                      Logga in i webbläsaren (PKCE) eller glöm inloggningen

        Inloggning: CMDB_TOKEN, eller CMDB_CLIENT_ID + CMDB_USERNAME + CMDB_PASSWORD för ett tjänstekonto, eller cmdb login.
        Adaptrar: inställningar i CMDB_SYNC_<ADAPTER>_<NYCKEL> eller --config, hemligheter bara i miljön (docs/adaptrar.md).
        Felkoder: 0 ok, 1 fel användning, 2 finns inte, 3 inte inloggad eller behörig, 4 annat fel.
        """;

    /// <param name="sourceHttp">The client adapters reach their source systems with; by default a client of its own.</param>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, HttpClient? http = null,
        IReadOnlyDictionary<string, string?>? environment = null, HttpClient? sourceHttp = null, CancellationToken ct = default)
    {
        var env = environment ?? Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.Ordinal);
        Arguments parsed;
        try
        {
            parsed = Arguments.Parse(args);
        }
        catch (CliException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return ex.Code;
        }
        if (parsed.Command is null or "help" or "--help" or "-h")
        {
            await output.WriteAsync(Usage);
            return parsed.Command is null ? ExitCodes.Usage : ExitCodes.Ok;
        }

        var url = new Uri(parsed.Option("url") ?? env.GetValueOrDefault("CMDB_URL") ?? "https://cmdb.rosenvall.se");
        var owned = http is null;
        // Reconciling a large source takes minutes (#216), longer than HttpClient's default 100 s.
        http ??= new HttpClient { Timeout = parsed.Command == "sync" ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(100) };
        var ownsSource = sourceHttp is null && parsed.Command == "sync";
        sourceHttp ??= ownsSource ? new HttpClient { Timeout = TimeSpan.FromMinutes(5) } : http;
        try
        {
            var auth = new Auth(http, url, env);
            if (parsed.Command == "logout")
            {
                await output.WriteLineAsync(Auth.Logout() ? "Utloggad." : "Inte inloggad.");
                return ExitCodes.Ok;
            }
            var token = parsed.Command == "login" ? await auth.LoginAsync(output, ct) : await auth.TokenAsync(ct);
            if (parsed.Command == "login")
            {
                parsed = parsed with { Command = "whoami" };
            }
            var client = new Api(http, url, token);
            if (parsed.Command == "sync")
            {
                return await Sync.SyncCommand.RunAsync(parsed, client, env, sourceHttp, output, error, ct);
            }
            await Commands.RunAsync(parsed, client, output, ct);
            return ExitCodes.Ok;
        }
        catch (CliException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return ex.Code;
        }
        catch (HttpRequestException ex)
        {
            await error.WriteLineAsync($"Kunde inte nå {url}: {ex.Message}");
            return ExitCodes.Failed;
        }
        finally
        {
            if (owned)
            {
                http.Dispose();
            }
            if (ownsSource)
            {
                sourceHttp.Dispose();
            }
        }
    }
}

/// <summary>Command, positional arguments and --options (a flag without a value is "true").</summary>
public sealed record Arguments(string? Command, IReadOnlyList<string> Positional, IReadOnlyDictionary<string, List<string>> Options)
{
    public bool Json => Options.ContainsKey("json");

    public string? Option(string name) => Options.TryGetValue(name, out var values) ? values[^1] : null;

    public IReadOnlyList<string> All(string name) => Options.TryGetValue(name, out var values) ? values : [];

    public int Number(string name, int fallback, int min, int max)
    {
        if (Option(name) is not { } text)
        {
            return fallback;
        }
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max
            ? n
            : throw new CliException(ExitCodes.Usage, $"--{name} ska vara ett tal {min}–{max}.");
    }

    public string Required(int index, string what) =>
        index < Positional.Count ? Positional[index] : throw new CliException(ExitCodes.Usage, $"Ange {what}. Se cmdb help.");

    private static readonly HashSet<string> Flags = ["json", "dry-run"];

    public static Arguments Parse(string[] args)
    {
        string? command = null;
        var positional = new List<string>();
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                var name = arg[2..];
                string value;
                if (name.Contains('=', StringComparison.Ordinal))
                {
                    (name, value) = (name[..name.IndexOf('=', StringComparison.Ordinal)], name[(name.IndexOf('=', StringComparison.Ordinal) + 1)..]);
                }
                else if (Flags.Contains(name))
                {
                    value = "true";
                }
                else if (i + 1 < args.Length)
                {
                    value = args[++i];
                }
                else
                {
                    throw new CliException(ExitCodes.Usage, $"--{name} saknar värde.");
                }
                if (!options.TryGetValue(name, out var list))
                {
                    options[name] = list = [];
                }
                list.Add(value);
            }
            else if (command is null)
            {
                command = arg;
            }
            else
            {
                positional.Add(arg);
            }
        }
        return new Arguments(command, positional, options);
    }
}

/// <summary>The REST API with the caller's token; errors become exit codes.</summary>
public sealed class Api(HttpClient http, Uri baseUrl, string? token)
{
    public Uri BaseUrl => baseUrl;

    public Task<JsonNode> GetAsync(string path, CancellationToken ct) => SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, path)), ct);

    public Task<JsonNode> PostAsync(string path, object body, CancellationToken ct) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, path)) { Content = JsonContent.Create(body) }, ct);

    /// <summary>POSTs a body as is, e.g. a multipart form with a file; the caller owns the content.</summary>
    public Task<JsonNode> PostContentAsync(string path, HttpContent content, CancellationToken ct) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, path)) { Content = content }, ct, disposeContent: false);

    /// <summary>A link that opens the object in the web app.</summary>
    public string Link(string type, long id) => new Uri(baseUrl, $"/?p={type}:{id}").ToString();

    private async Task<JsonNode> SendAsync(HttpRequestMessage request, CancellationToken ct, bool disposeContent = true)
    {
        using (disposeContent ? request : null)
        {
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => throw new CliException(ExitCodes.Auth,
                    token is null ? "Inte inloggad. Kör cmdb login, eller sätt CMDB_TOKEN." : "Inloggningen gäller inte längre. Kör cmdb login."),
                HttpStatusCode.Forbidden => throw new CliException(ExitCodes.Auth, "Du har inte behörighet till det här."),
                HttpStatusCode.NotFound => throw new CliException(ExitCodes.NotFound, "Finns inte, eller ligger utanför ditt omfång."),
                _ when !response.IsSuccessStatusCode => throw new CliException(ExitCodes.Failed, $"API:t svarade {(int)response.StatusCode}: {Detail(body)}"),
                _ => JsonNode.Parse(body.Length == 0 ? "null" : body) ?? JsonValue.Create(0),
            };
        }
    }

    private static string Detail(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            return node?["detail"]?.GetValue<string>()
                ?? string.Join(" ", node?["errors"]?.AsObject().SelectMany(e => e.Value!.AsArray().Select(v => v!.GetValue<string>())) ?? [body]);
        }
        catch (JsonException)
        {
            return body;
        }
    }
}

/// <summary>A reference like "site:12": type and id.</summary>
public readonly record struct Ref(string Type, long Id)
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["site"] = "sites",
        ["equipment"] = "equipment",
        ["cable"] = "cables",
        ["service"] = "services",
        ["circuit"] = "circuits",
    };

    public string Path => Paths[Type];

    public static bool TryParse(string text, out Ref value)
    {
        value = default;
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !long.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return false;
        }
        var type = text[..colon];
        value = new Ref(type, id);
        return type is "site" or "equipment" or "cable" or "service" or "circuit" or "terminal" or "plan";
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Type}:{Id}");
}

internal static class Text
{
    public static string Get(JsonNode? node, string name) => node?[name] is JsonValue v ? v.ToString() : "";

    public static long Id(JsonNode? node) => node?["id"]?.GetValue<long>() ?? 0;

    public static void Line(StringBuilder text, params string?[] parts) =>
        text.AppendLine(string.Join("  ", parts.Where(p => !string.IsNullOrWhiteSpace(p))));
}
