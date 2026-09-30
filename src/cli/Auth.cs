using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cmdb.Cli;

/// <summary>
/// Where the CLI gets its access token (#86), in order:
/// <list type="number">
/// <item><c>CMDB_TOKEN</c>, a bearer token as is;</item>
/// <item>a service account: <c>CMDB_CLIENT_ID</c>, <c>CMDB_USERNAME</c> and <c>CMDB_PASSWORD</c> (client credentials);</item>
/// <item>the token cache from <c>cmdb login</c> (PKCE with the public client <c>cmdb-mcp</c>), refreshed when it runs out.</item>
/// </list>
/// The identity provider is found from the API's protected-resource metadata, so nothing but the API's URL is configured.
/// </summary>
internal sealed class Auth(HttpClient http, Uri api, IReadOnlyDictionary<string, string?> env)
{
    public const string PublicClient = "cmdb-mcp";
    public const int CallbackPort = 33418;

    private static readonly string CacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "cmdb", "token.json");

    public async Task<string?> TokenAsync(CancellationToken ct)
    {
        if (env.GetValueOrDefault("CMDB_TOKEN") is { Length: > 0 } token)
        {
            return token;
        }
        if (env.GetValueOrDefault("CMDB_CLIENT_ID") is { Length: > 0 } client
            && env.GetValueOrDefault("CMDB_USERNAME") is { Length: > 0 } user
            && env.GetValueOrDefault("CMDB_PASSWORD") is { Length: > 0 } password)
        {
            var endpoints = await EndpointsAsync(client, ct);
            var response = await PostAsync(endpoints.Token, new()
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = client,
                ["username"] = user,
                ["password"] = password,
                ["scope"] = "openid profile email",
            }, ct);
            return response["access_token"]?.GetValue<string>();
        }
        var cache = Read();
        if (cache is null)
        {
            return null;
        }
        if (cache.Expires > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return cache.AccessToken;
        }
        if (cache.RefreshToken is null)
        {
            return null;
        }
        var endpointsForRefresh = await EndpointsAsync(PublicClient, ct);
        var refreshed = await PostAsync(endpointsForRefresh.Token, new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = PublicClient,
            ["refresh_token"] = cache.RefreshToken,
        }, ct);
        return Save(refreshed).AccessToken;
    }

    /// <summary>Signs in in the browser with PKCE and caches the tokens.</summary>
    public async Task<string> LoginAsync(TextWriter output, CancellationToken ct)
    {
        var endpoints = await EndpointsAsync(PublicClient, ct);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var redirect = $"http://127.0.0.1:{CallbackPort}/callback";
        var url = $"{endpoints.Authorize}?response_type=code&client_id={PublicClient}&redirect_uri={Uri.EscapeDataString(redirect)}"
            + $"&scope={Uri.EscapeDataString("openid profile email offline_access")}&code_challenge={challenge}&code_challenge_method=S256&state={state}";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{CallbackPort}/");
        listener.Start();
        output.WriteLine("Öppnar inloggningen i webbläsaren. Om den inte öppnas, gå till:");
        output.WriteLine(url);
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No browser here; the printed link is enough.
        }
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5), ct);
        var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url!.Query);
        var ok = query["state"] == state && query["code"] is not null;
        var page = Encoding.UTF8.GetBytes(ok ? "Inloggad i cmdb. Du kan stänga fliken." : "Inloggningen misslyckades.");
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.OutputStream.WriteAsync(page, ct);
        context.Response.Close();
        if (!ok)
        {
            throw new CliException(ExitCodes.Auth, "Inloggningen misslyckades: " + (query["error_description"] ?? query["error"] ?? "fel state"));
        }
        var tokens = await PostAsync(endpoints.Token, new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = PublicClient,
            ["code"] = query["code"]!,
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier,
        }, ct);
        return Save(tokens).AccessToken;
    }

    public static bool Logout()
    {
        if (!File.Exists(CacheFile))
        {
            return false;
        }
        File.Delete(CacheFile);
        return true;
    }

    private sealed record Endpoints(string Authorize, string Token);

    private sealed record Cached(string AccessToken, string? RefreshToken, DateTimeOffset Expires);

    /// <summary>The API names its authorization server; an application's issuer is …/application/o/&lt;slug&gt;/.</summary>
    private async Task<Endpoints> EndpointsAsync(string client, CancellationToken ct)
    {
        var resource = await http.GetFromJsonAsync<JsonObject>(new Uri(api, "/.well-known/oauth-protected-resource"), ct)
            ?? throw new CliException(ExitCodes.Auth, "API:t anger ingen identitetsleverantör.");
        var server = resource["authorization_servers"]?[0]?.GetValue<string>()
            ?? throw new CliException(ExitCodes.Auth, "API:t anger ingen identitetsleverantör.");
        // The metadata names the MCP application's issuer; other applications live next to it.
        var issuer = server.TrimEnd('/');
        issuer = issuer[..(issuer.LastIndexOf('/') + 1)] + client + "/";
        var config = await http.GetFromJsonAsync<JsonObject>(new Uri(issuer + ".well-known/openid-configuration"), ct)
            ?? throw new CliException(ExitCodes.Auth, "Hittar inte identitetsleverantörens konfiguration.");
        return new Endpoints(config["authorization_endpoint"]!.GetValue<string>(), config["token_endpoint"]!.GetValue<string>());
    }

    private async Task<JsonObject> PostAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new CliException(ExitCodes.Auth, $"Identitetsleverantören svarade {(int)response.StatusCode}: {body}");
        }
        return JsonNode.Parse(body)!.AsObject();
    }

    private static Cached? Read()
    {
        try
        {
            return File.Exists(CacheFile) ? JsonSerializer.Deserialize<Cached>(File.ReadAllText(CacheFile)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Cached Save(JsonObject tokens)
    {
        var cached = new Cached(tokens["access_token"]!.GetValue<string>(), tokens["refresh_token"]?.GetValue<string>(),
            DateTimeOffset.UtcNow.AddSeconds(tokens["expires_in"]?.GetValue<int>() ?? 300));
        Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
        File.WriteAllText(CacheFile, JsonSerializer.Serialize(cached));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(CacheFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return cached;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
