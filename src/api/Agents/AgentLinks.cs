using System.Globalization;

namespace Cmdb.Api.Agents;

/// <summary>
/// Links from agent answers into the web UI, so a human can see exactly what the agent saw. The web app and the API
/// share an origin behind nginx, so the link is built from the request (or <c>Agents:WebUrl</c> when set).
/// </summary>
public sealed class AgentLinks(IHttpContextAccessor http, IConfiguration config)
{
    private string? _base;

    public string Base => _base ??= ResolveBase();

    public string For(string type, long id) =>
        string.Create(CultureInfo.InvariantCulture, $"{Base}/?p={type}:{id}");

    public static string Ref(string type, long id) => string.Create(CultureInfo.InvariantCulture, $"{type}:{id}");

    private string ResolveBase()
    {
        if (config["Agents:WebUrl"] is { Length: > 0 } configured)
        {
            return configured.TrimEnd('/');
        }
        var request = http.HttpContext?.Request;
        if (request is null)
        {
            return "";
        }
        var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? request.Scheme;
        return $"{scheme}://{request.Host}";
    }
}
