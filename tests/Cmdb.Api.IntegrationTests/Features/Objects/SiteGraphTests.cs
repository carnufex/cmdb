using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Sites;

namespace Cmdb.Api.IntegrationTests.Features.Objects;

/// <summary>The neighbourhood graph lens's data (#20).</summary>
public sealed class SiteGraphTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_radio_site_has_its_cable_and_its_backhaul_to_the_aggregation_site()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        long id;
        await using (var cmd = db.CreateCommand("SELECT id FROM site WHERE site_type = 'radio' ORDER BY id LIMIT 1"))
        {
            id = (long)(await cmd.ExecuteScalarAsync(Ct))!;
        }
        using var client = NetworkFixture.Client(api);

        var graph = (await client.GetFromJsonAsync<SiteGraph>($"/api/sites/{id}/graph", Ct))!;

        graph.Site.Id.ShouldBe(id);
        graph.Edges.ShouldContain(e => e.Layer == "physical" && e.CableId != null && e.Code!.StartsWith("K-", StringComparison.Ordinal));
        var logical = graph.Edges.Where(e => e.Layer == "logical").ShouldHaveSingleItem();
        logical.Circuits.ShouldBeGreaterThanOrEqualTo(1);
        graph.Nodes.Select(n => n.Id).ShouldContain(logical.Target);
        graph.Edges.ShouldAllBe(e => e.Source == id || e.Target == id);
        graph.Nodes.Select(n => n.Id).ShouldBe(graph.Edges.Select(e => e.Source == id ? e.Target : e.Source).Distinct(), ignoreOrder: true);
        graph.Truncated.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_site_is_not_found()
    {
        using var client = factory.CreateAuthenticatedClient();

        (await client.GetAsync("/api/sites/999999999/graph", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
