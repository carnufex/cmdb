using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cmdb.Api.Features.CurrentUser;

namespace Cmdb.Api.IntegrationTests.Agents;

/// <summary>Which tokens the API trusts, how MCP clients discover the IdP, and the agent rate limit (#62).</summary>
public sealed class AgentIdentityTests(ApiFactory factory)
{
    private const string IdpBase = "https://idp.test/application/o/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("cmdb-mcp")]
    [InlineData("cmdb-agents")]
    public async Task Accepts_tokens_from_the_other_cmdb_applications_and_names_the_client(string application)
    {
        using var client = Client(ApiFactory.Token("agent-" + application, issuer: $"{IdpBase}{application}/", audience: application));

        var me = await client.GetFromJsonAsync<MeResponse>("/api/me", Ct);

        me!.Client.ShouldBe(application);
    }

    [Theory]
    [InlineData("argocd", "argocd")]
    [InlineData("argocd", "cmdb-web")]
    [InlineData("cmdb-mcp", "argocd")]
    public async Task Rejects_tokens_for_other_applications_on_the_same_idp(string application, string audience)
    {
        using var client = Client(ApiFactory.Token("intruder", issuer: $"{IdpBase}{application}/", audience: audience));

        var response = await client.GetAsync("/api/me", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mcp_clients_discover_the_authorization_server_from_a_401()
    {
        using var anonymous = factory.CreateClient();

        var challenge = await anonymous.PostAsync("/mcp", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), Ct);
        var metadataUrl = challenge.Headers.WwwAuthenticate.ToString();
        var metadata = await anonymous.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource", Ct);

        challenge.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        metadataUrl.ShouldContain("resource_metadata=");
        metadata.GetProperty("resource").GetString().ShouldBe("http://localhost/mcp");
        metadata.GetProperty("authorization_servers")[0].GetString().ShouldBe($"{IdpBase}cmdb-mcp/");
        metadata.GetProperty("scopes_supported").EnumerateArray().Select(s => s.GetString()).ShouldContain("profile");
    }

    [Fact]
    public async Task Agent_clients_are_rate_limited_but_people_are_not()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var agent = Client(ApiFactory.Token($"agent-{tag}", issuer: $"{IdpBase}cmdb-agents/", audience: "cmdb-agents"));
        using var person = factory.CreateAuthenticatedClient($"person-{tag}");

        var agentCodes = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => agent.GetAsync("/api/me", Ct)));
        var personCodes = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => person.GetAsync("/api/me", Ct)));

        agentCodes.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests).ShouldBeGreaterThan(0);
        personCodes.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
    }

    private HttpClient Client(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }
}
