using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Health;

namespace Cmdb.Api.IntegrationTests.Features.Health;

public sealed class HealthTests(ApiFactory factory)
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    public async Task Reports_ok_when_database_is_reachable(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<HealthResponse>(ct))!.Status.ShouldBe("ok");
    }
}
