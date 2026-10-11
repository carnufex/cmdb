using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cmdb.Cli.Sync;

/// <summary>
/// A thin integration (#217): reads one source system and writes what it knows in the exchange format. Matching,
/// source priority, deviations and permissions are reconciliation's (#216, ADR-0020), never the adapter's.
/// <c>cmdb sync</c> finds every adapter in this assembly, so a new one is a class and nothing else.
/// </summary>
public interface IAdapter
{
    /// <summary>The adapter's name on the command line, e.g. <c>acme-monitor</c>.</summary>
    string Name { get; }

    /// <summary>The source system's name in cmdb (source priority, source records), unless <c>--source</c> says otherwise.</summary>
    string Source { get; }

    /// <summary>One line for <c>cmdb sync</c> without an adapter.</summary>
    string Description { get; }

    Task ReadAsync(AdapterContext context, ExchangeWriter output, CancellationToken ct);
}

/// <summary>A source the adapter cannot read, or a setting it lacks: the run stops and nothing is sent.</summary>
public sealed class AdapterException(string message) : Exception(message);

/// <summary>One page from a source, and how to ask for the next (null when it was the last).</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? Next);

/// <summary>
/// What an adapter gets: settings, secrets, an HTTP client for the source and a place for warnings.
/// <list type="bullet">
/// <item>Settings come from the environment, <c>CMDB_SYNC_&lt;ADAPTER&gt;_&lt;KEY&gt;</c> (e.g. <c>CMDB_SYNC_ACME_MONITOR_URL</c>),
/// or else from the JSON file in <c>--config</c> or <c>CMDB_SYNC_CONFIG</c>.</item>
/// <item>Secrets come from the environment only, so they can be mounted from a Secret or an ExternalSecret and never
/// sit in a configuration file.</item>
/// </list>
/// </summary>
public sealed class AdapterContext(string adapter, JsonObject config, IReadOnlyDictionary<string, string?> env, HttpClient http, TextWriter log)
{
    private const int MaxPages = 100_000;
    private readonly List<string> _warnings = [];

    public HttpClient Http => http;

    public TextWriter Log => log;

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>A setting the adapter cannot do without.</summary>
    public string Setting(string key) =>
        OptionalSetting(key) ?? throw new AdapterException($"{adapter} saknar inställningen '{key}': sätt {EnvName(key)} eller '{key}' i konfigurationen.");

    public string? OptionalSetting(string key) =>
        env.GetValueOrDefault(EnvName(key)) is { Length: > 0 } value ? value
        : config[key] is JsonValue v && v.ToString() is { Length: > 0 } text ? text
        : null;

    /// <summary>A secret, from the environment only.</summary>
    public string Secret(string key) =>
        env.GetValueOrDefault(EnvName(key)) is { Length: > 0 } value
            ? value
            : throw new AdapterException($"{adapter} saknar hemligheten {EnvName(key)} i miljön.");

    /// <summary>A table from the configuration, e.g. the source's model names to the catalog's keys.</summary>
    public IReadOnlyDictionary<string, string> Map(string key) =>
        config[key] is JsonObject map
            ? map.Where(p => p.Value is JsonValue).ToDictionary(p => p.Key, p => p.Value!.ToString(), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Something the adapter left out or guessed; reported with the run, never silently dropped.</summary>
    public void Warn(string message) => _warnings.Add(message);

    /// <summary>
    /// GETs JSON from the source. Rate limits and server errors are retried a few times, honouring Retry-After;
    /// anything else stops the run.
    /// </summary>
    public async Task<JsonNode> GetJsonAsync(Uri url, string? bearer, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (bearer is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                try
                {
                    return JsonNode.Parse(body) ?? throw new AdapterException($"{url.GetLeftPart(UriPartial.Path)} svarade utan innehåll.");
                }
                catch (JsonException)
                {
                    throw new AdapterException($"{url.GetLeftPart(UriPartial.Path)} svarade inte med JSON.");
                }
            }
            var retry = response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (!retry || attempt >= 4)
            {
                throw new AdapterException($"{url.GetLeftPart(UriPartial.Path)} svarade {(int)response.StatusCode}.");
            }
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
            await Task.Delay(wait < TimeSpan.FromMinutes(1) ? wait : TimeSpan.FromMinutes(1), ct);
        }
    }

    /// <summary>
    /// Reads a paged source to the end: <paramref name="page"/> gets null for the first page, then each page's
    /// <see cref="Page{T}.Next"/>. Page numbers, cursors and next links all fit. A page that points back to one
    /// already read stops the run instead of looping.
    /// </summary>
    public static async IAsyncEnumerable<T> PagedAsync<T>(Func<string?, CancellationToken, Task<Page<T>>> page,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? next = null;
        for (var pages = 0; ; pages++)
        {
            if (pages >= MaxPages)
            {
                throw new AdapterException($"Källan har fler än {MaxPages} sidor.");
            }
            var current = await page(next, ct);
            foreach (var item in current.Items)
            {
                yield return item;
            }
            if (current.Next is null)
            {
                yield break;
            }
            if (!seen.Add(current.Next))
            {
                throw new AdapterException($"Källan pekar tillbaka på sidan '{current.Next}'.");
            }
            next = current.Next;
        }
    }

    private string EnvName(string key) =>
        $"CMDB_SYNC_{Upper(adapter)}_{Upper(key)}";

    private static string Upper(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_'));
}
