using System.Globalization;

namespace Cmdb.Api.IntegrationTests.Diagnostics;

public sealed class ServerTimingTests(ApiFactory factory)
{
    [Fact]
    public async Task Api_responses_carry_the_server_time()
    {
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/me", TestContext.Current.CancellationToken);

        var header = response.Headers.GetValues("Server-Timing").Single();
        header.ShouldStartWith("app;dur=");
        double.Parse(header["app;dur=".Length..], CultureInfo.InvariantCulture).ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Also_rejected_requests_are_timed_but_probes_are_not()
    {
        using var client = factory.CreateClient();

        var unauthorized = await client.GetAsync("/api/me", TestContext.Current.CancellationToken);
        var probe = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        unauthorized.Headers.Contains("Server-Timing").ShouldBeTrue();
        probe.Headers.Contains("Server-Timing").ShouldBeFalse();
    }
}
