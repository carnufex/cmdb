using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Cmdb.Api.Features.CurrentUser;
using Microsoft.IdentityModel.Tokens;

namespace Cmdb.Api.IntegrationTests.Features.CurrentUser;

public sealed class MeTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Returns_the_user_and_their_groups()
    {
        using var client = factory.CreateAuthenticatedClient("cmdb-demo-region", ["cmdb-region-nord"]);

        var me = await client.GetFromJsonAsync<MeResponse>("/api/me", Ct);

        me.ShouldNotBeNull();
        me.Username.ShouldBe("cmdb-demo-region");
        me.Email.ShouldBe("cmdb-demo-region@cmdb.local");
        me.Groups.ShouldBe(["cmdb-region-nord"]);
    }

    [Fact]
    public async Task Requires_a_token()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/me", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    public static TheoryData<string> InvalidTokens => new()
    {
        ApiFactory.Token(audience: "some-other-client"),
        ApiFactory.Token(expires: DateTime.UtcNow.AddMinutes(-6)),
        ApiFactory.Token(key: new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test" }),
    };

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public async Task Rejects_tokens_for_another_audience_expired_or_wrongly_signed(string token)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var response = await client.GetAsync("/api/me", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
