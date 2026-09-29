using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Cmdb.Api.IntegrationTests.Agents;

public sealed class OpenApiTests(ApiFactory factory)
{
    [Fact]
    public async Task The_description_is_public_and_covers_the_reads_agents_need()
    {
        using var anonymous = factory.CreateClient();

        var spec = await anonymous.GetFromJsonAsync<JsonElement>("/api/openapi.json", TestContext.Current.CancellationToken);

        var paths = spec.GetProperty("paths");
        foreach (var path in new[] { "/api/search", "/api/sites/{id}", "/api/query/sites", "/api/cables/{id}/impact", "/api/sites/{id}/neighbourhood" })
        {
            paths.TryGetProperty(path, out _).ShouldBeTrue(path);
        }
        paths.GetProperty("/api/query/sites").GetProperty("post").GetProperty("summary").GetString().ShouldStartWith("Advanced search");
        spec.GetProperty("info").GetProperty("title").GetString()!.ShouldContain("syntetisk");
    }

    [Fact]
    public async Task The_data_behind_it_still_needs_a_token()
    {
        using var anonymous = factory.CreateClient();

        (await anonymous.GetAsync("/api/search?q=HUB", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
