using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Graph;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Features.Trace;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.Graph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Graph;

/// <summary>
/// Tracing and impact at a time (#302, ADR-0013 §6): the graph as it was in service then, or as the CMDB knew it then,
/// loaded beside the current graph and kept for the next question.
/// </summary>
public sealed class GraphTimeTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task At_the_present_the_graph_at_a_time_is_the_current_graph()
    {
        var (db, api) = await ApiAsync(59);
        await using var _ = api;
        var now = api.Services.GetRequiredService<GraphHolder>().Require();
        foreach (var knowledge in new[] { false, true })
        {
            var then = await GraphLoader.LoadAsync(db, new GraphTime(DateTimeOffset.UtcNow.AddMinutes(1), knowledge), Ct);
            (then.NodeCount, then.EdgeCount, then.CircuitCount, then.ServiceCount, then.CableCount)
                .ShouldBe((now.NodeCount, now.EdgeCount, now.CircuitCount, now.ServiceCount, now.CableCount));
        }
    }

    [Fact]
    public async Task A_service_is_traced_as_it_was_and_as_it_was_known()
    {
        var (db, api) = await ApiAsync(60);
        await using var _ = api;
        using var client = NetworkFixture.Client(api);
        // A service in service whose physical path runs through a splice.
        var service = await Scalar<long>(db, """
            SELECT min(sc.service_id) FROM service_circuit sc JOIN service s ON s.id = sc.service_id AND s.lifecycle = 'in_service'
            """);
        var trace = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?service={service}", Ct))!;
        var physical = trace.Circuits.First(c => c.Layer == "physical");
        var built = await Scalar<DateTime>(db, $"SELECT valid_from FROM circuit WHERE id = {physical.Circuit.Id}");

        // Before the network was built the service was not there; once its circuits were, it is traced as now.
        (await client.GetAsync($"/api/trace?service={service}&at=1985-01-01T00:00:00Z", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var then = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?service={service}&at={Iso(built.AddDays(1))}", Ct))!;
        then.Circuits.Select(c => c.Circuit.Id).ShouldBe(trace.Circuits.Select(c => c.Circuit.Id));

        // A connection on the physical route is taken away. What the CMDB knew before still has it; today it is gone.
        var cut = await Scalar<DateTime>(db, "SELECT now()");
        await Task.Delay(20, Ct);
        var connection = await Scalar<long>(db, $"""
            SELECT min(c.id) FROM connection c WHERE c.kind = 'splice' AND c.valid_to IS NULL
              AND (c.a_terminal_id = ANY(ARRAY[{string.Join(',', physical.Hops.Select(h => h.TerminalId))}]::bigint[])
                OR c.b_terminal_id = ANY(ARRAY[{string.Join(',', physical.Hops.Select(h => h.TerminalId))}]::bigint[]))
            """);
        var terminal = await Scalar<long>(db, $"SELECT a_terminal_id FROM connection WHERE id = {connection}");
        await Exec(db, $"DELETE FROM connection WHERE id = {connection}");
        await ApiFactory.GraphCaughtUpAsync(api.Services, db);

        int Hops(TraceResult r) => r.Physical?.Hops.Count ?? 0;
        var after = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?terminal={terminal}", Ct))!;
        var known = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?terminal={terminal}&at={Iso(cut)}&mode=knowledge", Ct))!;
        var real = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?terminal={terminal}&at={Iso(cut)}&mode=reality", Ct))!;
        Hops(known).ShouldBeGreaterThan(Hops(after));
        // A deletion is a correction: as far as we know today it was never there, so reality agrees with today.
        Hops(real).ShouldBe(Hops(after));

        // The same time again is the same graph.
        var times = api.Services.GetRequiredService<GraphAtTime>();
        var key = new GraphTime(new DateTimeOffset(cut, TimeSpan.Zero), Knowledge: true);
        times.GetAsync(key).ShouldBeSameAs(times.GetAsync(key));
    }

    [Fact]
    public async Task Impact_is_asked_at_a_time_through_the_api_and_the_agents_tool()
    {
        var (db, api) = await ApiAsync(61);
        await using var _ = api;
        using var client = NetworkFixture.Client(api);
        var cable = await Scalar<long>(db, """
            SELECT p.cable_id FROM conductor p JOIN conductor_end e ON e.conductor_id = p.id JOIN circuit_hop h ON h.terminal_id = e.terminal_id
            GROUP BY 1 ORDER BY count(*) DESC, 1 LIMIT 1
            """);
        var now = (await client.GetFromJsonAsync<Impact>($"/api/cables/{cable}/impact", Ct))!;
        now.Circuits.ShouldBeGreaterThan(0);
        var early = (await client.GetFromJsonAsync<Impact>($"/api/cables/{cable}/impact?at=1985-01-01T00:00:00Z", Ct))!;
        early.Circuits.ShouldBe(0);
        var later = (await client.GetFromJsonAsync<Impact>($"/api/cables/{cable}/impact?at={Iso(DateTime.UtcNow.AddMinutes(1))}", Ct))!;
        later.Circuits.ShouldBe(now.Circuits);

        (await client.GetAsync($"/api/cables/{cable}/impact?at=2010-01-01T00:00:00Z&mode=other", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync($"/api/cables/{cable}/impact?at=2010-01-01T00:00:00Z&plan=1", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var http = NetworkFixture.Client(api);
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http, ownsHttpClient: true),
            cancellationToken: Ct);
        var result = await mcp.CallToolAsync("impact", new Dictionary<string, object?> { ["reference"] = $"cable:{cable}", ["at"] = "1985-01-01" }, cancellationToken: Ct);
        result.IsError.ShouldNotBe(true);
        string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text)).ShouldContain("\"circuits\":0");
    }

    private static string Iso(DateTime utc) => Uri.EscapeDataString(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToString("o"));

    private async Task<(NpgsqlDataSource Db, WebApplicationFactory<Program> Api)> ApiAsync(int seed)
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(seed, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        return (db, api);
    }

    private static async Task<T> Scalar<T>(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(Ct);
    }
}
