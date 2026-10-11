using System.Text.Json.Nodes;
using Cmdb.Cli.Sync;

namespace Cmdb.Api.IntegrationTests.Features.Reconciliation;

/// <summary>What every adapter (#217) gets: settings, secrets only from the environment, paging and the exchange format.</summary>
public sealed class AdapterTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("cmdb-adapter-test-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static AdapterContext Context(JsonObject config, Dictionary<string, string?> env) =>
        new("acme-monitor", config, env, new HttpClient(), TextWriter.Null);

    [Fact]
    public void Settings_come_from_the_environment_before_the_file_and_secrets_only_from_the_environment()
    {
        var context = Context(new JsonObject { ["url"] = "http://file", ["token"] = "in-file", ["models"] = new JsonObject { ["CR-8"] = "acme-cr-8" } },
            new() { ["CMDB_SYNC_ACME_MONITOR_URL"] = "http://env" });

        context.Setting("url").ShouldBe("http://env");
        context.Map("models")["CR-8"].ShouldBe("acme-cr-8");
        Should.Throw<AdapterException>(() => context.Secret("token")).Message.ShouldContain("CMDB_SYNC_ACME_MONITOR_TOKEN");
        Should.Throw<AdapterException>(() => context.Setting("region")).Message.ShouldContain("CMDB_SYNC_ACME_MONITOR_REGION");
    }

    [Fact]
    public async Task Paging_reads_every_page_and_stops_on_a_page_that_points_back()
    {
        var all = await AdapterContext.PagedAsync<int>((next, _) => Task.FromResult(next switch
        {
            null => new Page<int>([1, 2], "b"),
            "b" => new Page<int>([3], "c"),
            _ => new Page<int>([], null),
        }), TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        all.ShouldBe([1, 2, 3]);

        await Should.ThrowAsync<AdapterException>(async () =>
            await AdapterContext.PagedAsync<int>((next, _) => Task.FromResult(new Page<int>([1], next == "a" ? "b" : "a")), TestContext.Current.CancellationToken)
                .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_writer_quotes_where_needed_and_writes_only_the_files_used()
    {
        using (var writer = new ExchangeWriter(_folder))
        {
            writer.Site("s1", "EX-1", "Nav, norra", "hub", lat: 63.8, lon: 20.2);
            writer.Equipment("e1", "s1", "Router \"1\"", "acme-cr-8", location: "s1/Rack 1", attributes: new JsonObject { ["serialNumber"] = "SN-1" });
            writer.Rows["sites.csv"].ShouldBe(1);
            Should.Throw<ArgumentException>(() => writer.Row("sites.csv", ["id"], "x"));
        }

        Directory.GetFiles(_folder).Select(Path.GetFileName).Order().ShouldBe(["equipment.csv", "sites.csv"]);
        File.ReadAllLines(Path.Combine(_folder, "sites.csv"))[1].ShouldBe("s1,EX-1,\"Nav, norra\",hub,,,63.8,20.2,,");
        File.ReadAllLines(Path.Combine(_folder, "equipment.csv"))[1]
            .ShouldBe("e1,s1,s1/Rack 1,,,\"Router \"\"1\"\"\",acme-cr-8,,,\"{\"\"serialNumber\"\":\"\"SN-1\"\"}\"");
    }
}
