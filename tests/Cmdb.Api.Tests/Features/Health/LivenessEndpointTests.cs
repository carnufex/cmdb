using Cmdb.Api.Features.Health;
using FastEndpoints;

namespace Cmdb.Api.Tests.Features.Health;

public sealed class LivenessEndpointTests
{
    [Fact]
    public async Task Returns_ok()
    {
        var endpoint = Factory.Create<LivenessEndpoint>();

        await endpoint.HandleAsync(TestContext.Current.CancellationToken);

        endpoint.Response.Status.ShouldBe("ok");
        endpoint.HttpContext.Response.StatusCode.ShouldBe(200);
    }
}
