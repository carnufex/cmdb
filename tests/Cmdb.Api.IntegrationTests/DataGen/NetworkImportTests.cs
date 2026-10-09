using System.Net.Http.Json;
using System.Text.Json;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.DataGen.Exchange;
using Cmdb.Graph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.DataGen;

/// <summary>
/// Importing an existing network into production (#210): the exchange format round-trips the generated network,
/// a second run changes nothing, changes are matched on external ids, and problems stop the import before it writes.
/// </summary>
public sealed class NetworkImportTests(ApiFactory factory) : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("cmdb-import-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Example => Path.Combine(AppContext.BaseDirectory, "exempel", "import");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task An_exported_network_imports_to_the_same_network_and_a_second_run_changes_nothing()
    {
        var network = NetworkBuilder.Build(5, Scale.Small, TypeCatalog.Current);
        await using var generated = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(generated, network, reset: false, TextWriter.Null, ct: Ct);
        ExchangeExport.Write(network, _folder);

        await using var imported = await factory.NewDatabaseAsync();
        var first = await ImportAsync(imported, _folder);
        first.Errors.ShouldBeEmpty();
        first.Written.ShouldBeTrue();
        Count(first, "sites").Created.ShouldBe(network.Sites.Count);
        Count(first, "equipment").Created.ShouldBe(network.Equipment.Count);

        foreach (var (name, sql) in Signatures)
        {
            var (expected, actual) = (await Rows(generated, sql), await Rows(imported, sql));
            actual.Except(expected).Take(5).ShouldBeEmpty($"{name} only after the import");
            expected.Except(actual).Take(5).ShouldBeEmpty($"{name} missing after the import");
            actual.Count.ShouldBe(expected.Count, name);
        }
        await using (var conn = await imported.OpenConnectionAsync(Ct))
        {
            (await IntegrityCheck.RunAsync(conn, TextWriter.Null, Ct)).ShouldBeEmpty();
        }
        (await Scalar(imported, "SELECT count(*) FROM site WHERE source_system = 'test' AND external_id IS NOT NULL AND last_confirmed_at IS NOT NULL"))
            .ShouldBe(network.Sites.Count);

        var again = await ImportAsync(imported, _folder);
        again.Errors.ShouldBeEmpty();
        again.Counts.ShouldAllBe(c => c.Created == 0 && c.Updated == 0 && c.Missing == 0);
        (await Scalar(imported, "SELECT count(*) FROM terminal")).ShouldBe(network.Terminals);
    }

    [Fact]
    public async Task Changes_are_matched_on_external_ids_and_links_follow_the_file()
    {
        Copy(Example, _folder);
        await using var db = await factory.NewDatabaseAsync();
        (await ImportAsync(db, _folder)).Errors.ShouldBeEmpty();

        Edit(ExchangeFormat.Sites, "Exempelradio", "Radiosite Exempel");
        Edit(ExchangeFormat.Equipment, "SN-EX-0005", "SN-EX-0055");
        Edit(ExchangeFormat.Services, ",1000", ",2500");
        File.WriteAllText(Path.Combine(_folder, ExchangeFormat.ServiceCircuits), "service,circuit\n");
        var result = await ImportAsync(db, _folder);

        result.Errors.ShouldBeEmpty();
        Count(result, "sites").ShouldBe(new ImportCount("sites", 0, 1, 1, 0));
        Count(result, "equipment").Updated.ShouldBe(1);
        Count(result, "services").Updated.ShouldBe(1);
        (await Text(db, "SELECT name FROM site WHERE external_id = 'ex-site-2'")).ShouldBe("Radiosite Exempel");
        (await Text(db, "SELECT attributes->>'serialNumber' FROM equipment WHERE external_id = 'ex-e5'")).ShouldBe("SN-EX-0055");
        (await Text(db, "SELECT attributes->>'bandwidthMbps' FROM service WHERE external_id = 'ex-t-1'")).ShouldBe("2500");
        (await Scalar(db, "SELECT count(*) FROM service_circuit")).ShouldBe(0);
        // A changed row does not create ports or conductors again.
        (await Scalar(db, "SELECT count(*) FROM conductor")).ShouldBe(12);
    }

    [Fact]
    public async Task Every_problem_is_reported_by_file_and_row_and_nothing_is_written()
    {
        Copy(Example, _folder);
        Edit(ExchangeFormat.Equipment, "acme-ax-24", "acme-ax-99");
        File.AppendAllText(Path.Combine(_folder, ExchangeFormat.Ports), "ex-e3,99\n");
        Edit(ExchangeFormat.Connections, "ex-c1,1,B", "ex-c1,13,B");
        Edit(ExchangeFormat.Locations, "ex-k2,ex-site-2", "ex-k2,ex-site-9");
        Edit(ExchangeFormat.Services, ",1000", ",många");
        Edit(ExchangeFormat.Sites, ",24", ",9999");
        await using var db = await factory.NewDatabaseAsync();

        var result = await ImportAsync(db, _folder);

        result.Written.ShouldBeFalse();
        var errors = result.Errors.Select(e => e.ToString()).ToList();
        errors.ShouldContain(e => e.StartsWith("equipment.csv:6 ex-e5:", StringComparison.Ordinal) && e.Contains("acme-ax-99", StringComparison.Ordinal));
        errors.ShouldContain(e => e.StartsWith("ports.csv:6 ex-e3:", StringComparison.Ordinal) && e.Contains("porten 99", StringComparison.Ordinal));
        errors.ShouldContain(e => e.StartsWith("connections.csv:3", StringComparison.Ordinal) && e.Contains("har 12 ledare", StringComparison.Ordinal));
        errors.ShouldContain(e => e.StartsWith("locations.csv:5 ex-k2:", StringComparison.Ordinal) && e.Contains("ex-site-9", StringComparison.Ordinal));
        errors.ShouldContain(e => e.StartsWith("services.csv:2 ex-t-1:", StringComparison.Ordinal) && e.Contains("bandwidthMbps", StringComparison.Ordinal));
        errors.ShouldContain(e => e.StartsWith("sites.csv:2 ex-site-1:", StringComparison.Ordinal) && e.Contains("schema", StringComparison.Ordinal));
        (await Scalar(db, "SELECT count(*) FROM site")).ShouldBe(0);
    }

    [Fact]
    public async Task Codes_and_models_are_not_taken_over()
    {
        await using var db = await factory.NewDatabaseAsync();
        (await ImportAsync(db, Example, "system-a")).Errors.ShouldBeEmpty();

        // Another source system cannot reuse a code, and the same one cannot swap a model under existing equipment.
        var other = await ImportAsync(db, Example, "system-b");
        other.Errors.Select(e => e.Message).ShouldContain("koden EX-NAV-1 används redan av en annan site");
        Copy(Example, _folder);
        Edit(ExchangeFormat.Equipment, "acme-odf-24,in_service,,,\nex-e5", "acme-odf-48,in_service,,,\nex-e5");
        var swapped = await ImportAsync(db, _folder, "system-a");
        swapped.Errors.ShouldContain(e => e.Object == "ex-e4" && e.Message.Contains("modellen kan inte bytas", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_example_imports_and_its_service_runs_through_the_cable()
    {
        await using var db = await factory.NewDatabaseAsync();
        (await ImportAsync(db, Example)).Errors.ShouldBeEmpty();

        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        using var client = NetworkFixture.Client(api);

        var cable = await Scalar(db, "SELECT id FROM cable WHERE code = 'EX-K-1'");
        var impact = await client.GetFromJsonAsync<JsonElement>($"/api/cables/{cable}/impact", Ct);
        impact.GetRawText().ShouldContain("EX-TJ-1");
    }

    /// <summary>Each part of a network described without ids, so two databases holding the same network compare equal.</summary>
    private static readonly (string Name, string Sql)[] Signatures =
    [
        ("sites", "SELECT concat_ws('|', code, name, site_type, lifecycle, round(ST_X(geom)), round(ST_Y(geom))) FROM site"),
        ("locations", "SELECT concat_ws('|', s.code, l.kind, l.name, p.name, l.rack_units) FROM location l JOIN site s ON s.id = l.site_id LEFT JOIN location p ON p.id = l.parent_id"),
        ("equipment", """
            SELECT concat_ws('|', e.name, t.key, s.code, l.name, p.name, e.slot, e.lifecycle, e.rack_position, e.attributes::text, (SELECT count(*) FROM port WHERE equipment_id = e.id))
            FROM equipment e JOIN equipment_type t ON t.id = e.equipment_type_id JOIN site s ON s.id = e.site_id
            LEFT JOIN location l ON l.id = e.location_id LEFT JOIN equipment p ON p.id = e.parent_id
            """),
        ("cables", """
            SELECT concat_ws('|', c.code, t.key, a.code, b.code, c.lifecycle, round(c.length_m), (SELECT count(*) FROM conductor WHERE cable_id = c.id))
            FROM cable c JOIN cable_type t ON t.id = c.cable_type_id JOIN site a ON a.id = c.a_site_id JOIN site b ON b.id = c.b_site_id
            """),
        ("connections", $"SELECT concat_ws('|', least({Label("a_terminal_id")}, {Label("b_terminal_id")}), greatest({Label("a_terminal_id")}, {Label("b_terminal_id")}), kind, lifecycle) FROM connection"),
        ("circuits", $"SELECT concat_ws('|', code, layer, lifecycle, {Label("a_terminal_id")}, {Label("b_terminal_id")}) FROM circuit"),
        ("hops", $"""
            SELECT concat_ws('|', c.code, h.seq, {Label("h.terminal_id")}, ch.kind, ch.number)
            FROM circuit_hop h JOIN circuit c ON c.id = h.circuit_id LEFT JOIN channel ch ON ch.id = h.channel_id
            """),
        ("dependencies", "SELECT c.code || '|' || k.code FROM circuit_dependency d JOIN circuit c ON c.id = d.circuit_id JOIN circuit k ON k.id = d.carrier_id"),
        ("services", "SELECT concat_ws('|', code, name, service_type, lifecycle, attributes::text) FROM service"),
        ("service circuits", "SELECT s.code || '|' || c.code FROM service_circuit sc JOIN service s ON s.id = sc.service_id JOIN circuit c ON c.id = sc.circuit_id"),
    ];

    /// <summary>A terminal as equipment and port name, or cable code, conductor number and side.</summary>
    private static string Label(string terminal) => $"""
        (SELECT coalesce((SELECT e.name || ' ' || p.name FROM port p JOIN equipment e ON e.id = p.equipment_id WHERE p.terminal_id = {terminal}),
                         (SELECT k.code || '/' || c.number || ce.side FROM conductor_end ce JOIN conductor c ON c.id = ce.conductor_id
                          JOIN cable k ON k.id = c.cable_id WHERE ce.terminal_id = {terminal})))
        """;

    private static Task<ImportResult> ImportAsync(NpgsqlDataSource db, string folder, string source = "test") =>
        NetworkImport.RunAsync(db, folder, source, TypeCatalog.Current, dryRun: false, TextWriter.Null, Ct);

    private static ImportCount Count(ImportResult result, string kind) => result.Counts.Single(c => c.Kind == kind);

    private void Edit(string file, string from, string to)
    {
        var path = Path.Combine(_folder, file);
        var text = File.ReadAllText(path);
        text.ShouldContain(from);
        File.WriteAllText(path, text.Replace(from, to, StringComparison.Ordinal));
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*.csv"))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static async Task<List<string>> Rows(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand($"SELECT r FROM ({sql}) q(r) ORDER BY r COLLATE \"C\"");
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
        }
        return rows;
    }

    private static async Task<long> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> Text(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return (string)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}
