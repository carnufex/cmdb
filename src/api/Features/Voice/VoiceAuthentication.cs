using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Cmdb.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Cmdb.Api.Features.Voice;

/// <summary>
/// The voice channel's authentication (ADR-0015): the voice platform presents the endpoint's secret, the agent's id and the
/// conversation id. Each endpoint has its own secret (<c>Voice:Secrets:Noc|ServiceDesk|It</c>, #181), so a secret only
/// opens its own agent's tools; the shared <c>Voice:Secret</c> stands in only for an endpoint without one. With
/// <c>Voice:Agents:…</c> set, the agent id the platform sends must be that endpoint's agent; a mismatch is logged and refused.
/// An unverified call carries no groups, so its access scope is nothing. Once the caller has verified with a one-time
/// code, the call carries that employee's groups until the session expires, and every read goes through the same
/// access scopes, masks and filters as the web app.
/// </summary>
public sealed partial class VoiceAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    SystemDb system, IConfiguration config) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Voice";
    public const string ConversationHeader = "X-Conversation-Id";
    public const string AgentHeader = "X-Agent-Id";

    /// <summary>The configuration name of each voice endpoint.</summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        [VoiceChannels.Noc] = "Noc",
        [VoiceChannels.ServiceDesk] = "ServiceDesk",
        [VoiceChannels.It] = "It",
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Voice endpoint {Endpoint} called by agent {Agent}, expected {Expected}")]
    private static partial void WrongAgent(ILogger logger, string endpoint, string agent, string expected);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var channel = VoiceChannels.For(Request.Path);
        var name = channel is null ? null : Names[channel];
        var secret = name is null ? config["Voice:Secret"] : config[$"Voice:Secrets:{name}"] is { Length: > 0 } own ? own : config["Voice:Secret"];
        if (string.IsNullOrEmpty(secret) || secret == "unset")
        {
            return AuthenticateResult.Fail("The voice channel is not configured.");
        }
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[7..]), Encoding.UTF8.GetBytes(secret)))
        {
            return AuthenticateResult.Fail("Wrong voice secret.");
        }
        if (name is not null && config[$"Voice:Agents:{name}"] is { Length: > 0 } expected)
        {
            var agent = Request.Headers[AgentHeader].ToString();
            if (!string.Equals(agent, expected, StringComparison.Ordinal))
            {
                WrongAgent(Logger, channel!, agent.Length > 64 ? agent[..64] : agent, expected);
                return AuthenticateResult.Fail("Wrong voice agent.");
            }
        }
        var conversation = Request.Headers[ConversationHeader].ToString();
        if (string.IsNullOrWhiteSpace(conversation) || conversation.Length > 128 || conversation.StartsWith("{{", StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail("The conversation id is missing.");
        }

        var claims = new List<Claim>
        {
            new(CmdbClaims.Subject, $"voice:{conversation}"),
            new(CmdbClaims.Client, VoiceClaims.Client),
            new(VoiceClaims.Conversation, conversation),
        };
        var caller = await VoiceSessions.VerifiedCallerAsync(system.Source, conversation, Context.RequestAborted);
        if (caller is not null)
        {
            claims.Add(new(CmdbClaims.Username, caller.Name));
            claims.Add(new(VoiceClaims.Employee, caller.EmployeeId));
            claims.Add(new(VoiceClaims.Role, caller.Role));
            claims.AddRange(caller.Groups.Select(g => new Claim(CmdbClaims.Groups, g)));
        }
        else
        {
            claims.Add(new(CmdbClaims.Username, "driftagent"));
        }
        var identity = new ClaimsIdentity(claims, SchemeName, CmdbClaims.Username, CmdbClaims.Groups);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

public static class VoiceClaims
{
    public const string Client = "voice-agent";
    public const string Conversation = "voice_conversation";
    public const string Employee = "voice_employee";
    public const string Role = "voice_role";
}
