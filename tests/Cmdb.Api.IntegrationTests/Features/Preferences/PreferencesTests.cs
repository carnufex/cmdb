using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Preferences;

namespace Cmdb.Api.IntegrationTests.Features.Preferences;

public sealed class PreferencesTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Theme_defaults_to_dark_and_is_saved_per_user()
    {
        using var alice = factory.CreateAuthenticatedClient("pref-alice");
        using var bob = factory.CreateAuthenticatedClient("pref-bob");

        (await alice.GetFromJsonAsync<PreferencesResponse>("/api/me/preferences", Ct))!.Theme.ShouldBe("dark");
        (await alice.PutAsJsonAsync("/api/me/preferences", new { theme = "light" }, Ct)).EnsureSuccessStatusCode();

        (await alice.GetFromJsonAsync<PreferencesResponse>("/api/me/preferences", Ct))!.Theme.ShouldBe("light");
        (await bob.GetFromJsonAsync<PreferencesResponse>("/api/me/preferences", Ct))!.Theme.ShouldBe("dark");
    }

    [Fact]
    public async Task Rejects_unknown_themes()
    {
        using var client = factory.CreateAuthenticatedClient("pref-carol");

        var response = await client.PutAsJsonAsync("/api/me/preferences", new { theme = "neon" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
