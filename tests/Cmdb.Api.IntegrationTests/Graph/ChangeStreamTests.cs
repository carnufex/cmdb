using System.Net.Http.Json;
using Cmdb.Api.Features.Trace;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.Graph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Graph;

/// <summary>The change stream (#11): outbox, ordering, patching, and several API instances following it.</summary>
public sealed class ChangeStreamTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Changes_reach_the_outbox_in_the_writing_transaction_and_bulk_loads_leave_one_reload()
    {
        await using var db = await SmallNetworkAsync(3);
        (await Rows(db)).ShouldBe([("reload", 0L)]);
        var (a, b) = await FreePortsAsync(db);

        await using (var conn = await db.OpenConnectionAsync(Ct))
        {
            await using var tx = await conn.BeginTransactionAsync(Ct);
            await Exec(conn, $"INSERT INTO connection (a_terminal_id, b_terminal_id, kind, lifecycle) VALUES ({a}, {b}, 'patch', 'in_service')");
            await tx.RollbackAsync(Ct);
        }
        (await Rows(db)).Count.ShouldBe(1);

        await Exec(db, $"INSERT INTO connection (a_terminal_id, b_terminal_id, kind, lifecycle) VALUES ({a}, {b}, 'patch', 'in_service')");
        (await Rows(db)).Skip(1).ShouldBe([("terminal", a), ("terminal", b)], ignoreOrder: true);
    }

    [Fact]
    public async Task A_read_never_passes_a_transaction_that_is_still_running()
    {
        await using var db = await SmallNetworkAsync(4);
        var feed = new PostgresGraphChangeFeed(db);
        var start = await feed.PositionAsync(Ct);
        var cable = await Scalar(db, "SELECT id FROM cable ORDER BY id LIMIT 1");
        var other = await Scalar(db, "SELECT id FROM cable ORDER BY id DESC LIMIT 1");

        await using var slow = await db.OpenConnectionAsync(Ct);
        await using var slowTx = await slow.BeginTransactionAsync(Ct);
        await Exec(slow, $"UPDATE cable SET lifecycle = 'decommissioning' WHERE id = {cable}"); // takes the lower xid
        await Exec(db, $"UPDATE cable SET lifecycle = 'decommissioning' WHERE id = {other}"); // commits first

        var first = await feed.ReadAsync(start, Ct);
        first.Keys.Count.ShouldBe(0); // the later transaction is held back behind the running one

        await slowTx.CommitAsync(Ct);
        // Transactions are cluster wide, so other tests can hold the horizon back for a moment.
        var seen = new HashSet<long>();
        var watermark = first.Watermark;
        for (var i = 0; i < 100 && seen.Count < 2; i++)
        {
            var batch = await feed.ReadAsync(watermark, Ct);
            batch.Rows.CableLifecycles.ShouldAllBe(l => l == (byte)Cmdb.Graph.Lifecycle.Decommissioning);
            seen.UnionWith(batch.Keys.Cables);
            watermark = batch.Watermark;
            await Task.Delay(50, Ct);
        }
        seen.ShouldBe([cable, other], ignoreOrder: true);
    }

    [Fact]
    public async Task A_patched_graph_is_identical_to_a_fresh_load()
    {
        await using var db = await SmallNetworkAsync(5);
        var feed = new PostgresGraphChangeFeed(db);
        var before = await GraphLoader.LoadAsync(db, Ct);
        var (a, b) = await FreePortsAsync(db);

        // A little of everything: a new patch, a removed connection, a cable lifecycle, a rerouted service and new equipment.
        await Exec(db, $"INSERT INTO connection (a_terminal_id, b_terminal_id, kind, lifecycle) VALUES ({a}, {b}, 'patch', 'planned')");
        await Exec(db, "DELETE FROM connection WHERE id = (SELECT id FROM connection WHERE kind = 'splice' ORDER BY id LIMIT 1)");
        await Exec(db, "UPDATE cable SET lifecycle = 'decommissioning' WHERE id = (SELECT min(id) FROM cable)");
        await Exec(db, "DELETE FROM service_circuit WHERE (service_id, circuit_id) = (SELECT service_id, circuit_id FROM service_circuit ORDER BY 1, 2 LIMIT 1)");
        await Exec(db, """
            WITH e AS (
                INSERT INTO equipment (equipment_type_id, site_id, location_id, name, lifecycle)
                SELECT t.id, l.site_id, l.id, 'Change stream test', 'planned'
                FROM equipment_type t, (SELECT id, site_id FROM location WHERE kind = 'rack' ORDER BY id LIMIT 1) l
                WHERE t.key = 'acme-bb-6'
                RETURNING id
            ), t AS (INSERT INTO terminal (kind) VALUES ('port') RETURNING id)
            INSERT INTO port (terminal_id, equipment_id, name, port_type, position) SELECT t.id, e.id, 'x1', 'sfp', 1 FROM t, e
            """);

        // Other tests' transactions can hold the horizon back, so follow the stream the way the API does.
        var target = await Scalar(db, "SELECT pg_snapshot_xmax(pg_current_snapshot())::text::bigint");
        var patched = before;
        var watermark = before.Version;
        var keys = 0;
        while ((long)PostgresGraphChangeFeed.Xid(watermark)!.Value < target)
        {
            var batch = await feed.ReadAsync(watermark, Ct);
            batch.Reload.ShouldBeFalse();
            if (batch.Keys.Count > 0)
            {
                patched = GraphChanges.Apply(patched, batch);
                keys += batch.Keys.Count;
            }
            watermark = batch.Watermark;
            await Task.Delay(20, Ct);
        }
        keys.ShouldBeGreaterThanOrEqualTo(6);
        var fresh = GraphBuilder.Build(GraphData.From(await GraphLoader.LoadAsync(db, Ct)), patched.Version);

        Bytes(patched).ShouldBe(Bytes(fresh));
        patched.EdgeCount.ShouldBe(before.EdgeCount); // one added, one removed
    }

    [Fact]
    public async Task Connections_equipment_and_circuits_become_deltas_and_folding_them_in_gives_a_fresh_load()
    {
        await using var db = await SmallNetworkAsync(8);
        var feed = new PostgresGraphChangeFeed(db);
        var before = await GraphLoader.LoadAsync(db, Ct);
        var (a, b) = await FreePortsAsync(db);

        // Patching, a removed splice, a cable lifecycle (#81) and new equipment with its ports patched together (#119):
        // every batch is a delta.
        await Exec(db, $"INSERT INTO connection (a_terminal_id, b_terminal_id, kind, lifecycle) VALUES ({a}, {b}, 'patch', 'planned')");
        await Exec(db, "DELETE FROM connection WHERE id = (SELECT id FROM connection WHERE kind = 'splice' ORDER BY id LIMIT 1)");
        await Exec(db, "UPDATE cable SET lifecycle = 'decommissioning' WHERE id = (SELECT min(id) FROM cable)");
        await Exec(db, """
            WITH e AS (
                INSERT INTO equipment (equipment_type_id, site_id, location_id, name, lifecycle)
                SELECT t.id, l.site_id, l.id, 'Delta test', 'planned'
                FROM equipment_type t, (SELECT id, site_id FROM location WHERE kind = 'rack' ORDER BY id LIMIT 1) l
                WHERE t.key = 'acme-bb-6'
                RETURNING id
            ), t AS (INSERT INTO terminal (kind) SELECT 'port' FROM generate_series(1, 2) RETURNING id),
            p AS (
                INSERT INTO port (terminal_id, equipment_id, name, port_type, position)
                SELECT t.id, e.id, 'x' || row_number() OVER (ORDER BY t.id), 'sfp', (row_number() OVER (ORDER BY t.id))::int FROM t, e
                RETURNING terminal_id
            )
            INSERT INTO connection (a_terminal_id, b_terminal_id, kind, lifecycle) SELECT min(terminal_id), max(terminal_id), 'patch', 'planned' FROM p
            """);
        var equipment = await Scalar(db, "SELECT id FROM equipment WHERE name = 'Delta test'");

        // Circuits (#121): a service link dropped, a circuit removed with its rows, and a new one riding on a physical
        // circuit and carrying a service.
        await Exec(db, "DELETE FROM service_circuit WHERE (service_id, circuit_id) = (SELECT service_id, circuit_id FROM service_circuit ORDER BY 1, 2 LIMIT 1)");
        var removed = await Scalar(db, """
            SELECT c.id FROM circuit c
            WHERE NOT EXISTS (SELECT 1 FROM circuit_dependency d WHERE d.carrier_id = c.id)
            ORDER BY c.id DESC LIMIT 1
            """);
        await Exec(db, $"""
            DELETE FROM service_circuit WHERE circuit_id = {removed};
            DELETE FROM circuit_dependency WHERE circuit_id = {removed};
            DELETE FROM circuit_hop WHERE circuit_id = {removed};
            DELETE FROM circuit WHERE id = {removed};
            """);
        await Exec(db, """
            WITH ends AS (SELECT min(terminal_id) AS a, max(terminal_id) AS b FROM (SELECT terminal_id FROM port ORDER BY terminal_id LIMIT 2) x),
            c AS (INSERT INTO circuit (code, layer, a_terminal_id, b_terminal_id) SELECT 'DELTA-121', 'transmission', a, b FROM ends RETURNING id, a_terminal_id, b_terminal_id),
            h AS (INSERT INTO circuit_hop (circuit_id, seq, terminal_id) SELECT id, 0, a_terminal_id FROM c UNION ALL SELECT id, 1, b_terminal_id FROM c),
            d AS (INSERT INTO circuit_dependency (circuit_id, carrier_id) SELECT c.id, (SELECT min(id) FROM circuit WHERE layer = 'physical') FROM c)
            INSERT INTO service_circuit (service_id, circuit_id) SELECT (SELECT min(id) FROM service), id FROM c
            """);
        var circuit = await Scalar(db, "SELECT id FROM circuit WHERE code = 'DELTA-121'");

        var target = await Scalar(db, "SELECT pg_snapshot_xmax(pg_current_snapshot())::text::bigint");
        var graph = before;
        var watermark = before.Version;
        var batches = new List<GraphChangeBatch>();
        while ((long)PostgresGraphChangeFeed.Xid(watermark)!.Value < target)
        {
            var batch = await feed.ReadAsync(watermark, Ct);
            if (batch.Keys.Count > 0)
            {
                graph = GraphChanges.TryDelta(graph, batch).ShouldNotBeNull();
                batches.Add(batch);
            }
            watermark = batch.Watermark;
            await Task.Delay(20, Ct);
        }
        graph.IsOverlay.ShouldBeTrue();
        graph.OverlayNodes.ShouldBeGreaterThanOrEqualTo(6);
        graph.TryGetEquipment(equipment, out var added).ShouldBeTrue();
        var ports = graph.PortsOf(added).ToArray();
        ports.Length.ShouldBe(2);
        graph.Neighbours(ports[0]).ToArray().ShouldBe([ports[1]]);
        graph.TryGetCircuit(removed, out _).ShouldBeFalse();
        graph.TryGetCircuit(circuit, out var newCircuit).ShouldBeTrue();
        graph.HopsOf(newCircuit).Length.ShouldBe(2);
        graph.CarriersOf(newCircuit).Length.ShouldBe(1);
        graph.DependentsOf(graph.CarriersOf(newCircuit)[0]).ToArray().ShouldContain(newCircuit);
        graph.ServicesOf(newCircuit).Length.ShouldBe(1);

        var fresh = GraphBuilder.Build(GraphData.From(await GraphLoader.LoadAsync(db, Ct)), graph.Version);
        Bytes(GraphChanges.Compact(before, batches)).ShouldBe(Bytes(fresh));
        Bytes(GraphChanges.Flatten(graph, batches).ShouldNotBeNull()).ShouldBe(Bytes(fresh));
        graph.TryGetNode(a, out var node).ShouldBeTrue();
        graph.Neighbours(node).ToArray().Select(graph.TerminalId).ShouldContain(b);
    }

    [Fact]
    public async Task Two_api_instances_follow_the_same_changes()
    {
        await using var db = await SmallNetworkAsync(6);
        await using var first = Api(db);
        await using var second = Api(db);
        var network = NetworkBuilder.Build(6, Scale.Small, TypeCatalog.Embedded);
        var circuit = network.Circuits.First(c => c.Layer == "physical" && c.Lifecycle == "in_service");
        var hops = network.Hops.Where(h => h.CircuitId == circuit.Id).OrderBy(h => h.Seq).Select(h => h.TerminalId).ToArray();
        var (x, y) = (Math.Min(hops[1], hops[2]), Math.Max(hops[1], hops[2]));
        var removed = await Scalar(db, $"SELECT id FROM connection WHERE a_terminal_id = {x} AND b_terminal_id = {y}");

        await Exec(db, $"UPDATE connection SET valid_to = now() WHERE id = {removed}");
        foreach (var api in new[] { first, second })
        {
            await ApiFactory.GraphCaughtUpAsync(api.Services, db);
            var trace = await TraceAsync(api, circuit.A);
            trace.Physical!.Hops.Count.ShouldBe(2, "the route now ends where the connection was");
        }

        await Exec(db, $"UPDATE connection SET valid_to = NULL WHERE id = {removed}");
        foreach (var api in new[] { first, second })
        {
            await ApiFactory.GraphCaughtUpAsync(api.Services, db);
            var trace = await TraceAsync(api, circuit.A);
            trace.Physical!.Hops.Select(h => h.TerminalId).ShouldBe(hops);
            var change = api.Services.GetRequiredService<GraphHolder>().LastChange.ShouldNotBeNull();
            change.Keys.ShouldBe(2);
            change.Mode.ShouldBe("delta");
        }
    }

    [Fact]
    public async Task Pruning_removes_old_rows_and_sends_readers_that_far_behind_to_a_full_reload()
    {
        await using var db = await SmallNetworkAsync(7);
        var feed = new PostgresGraphChangeFeed(db);
        var old = await feed.PositionAsync(Ct);
        await Exec(db, "UPDATE cable SET lifecycle = 'decommissioning' WHERE id = (SELECT min(id) FROM cable)");
        await Exec(db, "UPDATE graph_change SET created_at = now() - interval '8 days'");
        // Transactions in other tests hold the cluster-wide horizon back; wait until it has passed the first update.
        var updated = (ulong)await Scalar(db, "SELECT max(tx)::text::bigint FROM graph_change");
        var recent = await feed.PositionAsync(Ct);
        for (var i = 0; i < 200 && PostgresGraphChangeFeed.Xid(recent) <= updated; i++)
        {
            await Task.Delay(50, Ct);
            recent = await feed.PositionAsync(Ct);
        }
        await Exec(db, "UPDATE cable SET lifecycle = 'in_service' WHERE id = (SELECT min(id) FROM cable)");

        var deleted = await PostgresGraphChangeFeed.PruneAsync(db, TimeSpan.FromDays(7), batchSize: 1, ct: Ct);

        var cable = await Scalar(db, "SELECT min(id) FROM cable");
        deleted.ShouldBe(3); // the bulk load's reload marker and the first update's old and new row, one per batch
        (await Rows(db)).ShouldBe([("cable", cable), ("cable", cable)]);
        (await feed.ReadAsync(old, Ct)).Reload.ShouldBeTrue();
        var current = await feed.ReadAsync(recent, Ct);
        current.Reload.ShouldBeFalse();
    }

    private async Task<NpgsqlDataSource> SmallNetworkAsync(int seed)
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(seed, Scale.Small, TypeCatalog.Embedded), reset: false, TextWriter.Null, ct: Ct);
        return db;
    }

    private WebApplicationFactory<Program> Api(NpgsqlDataSource db)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        api.Services.GetRequiredService<GraphHolder>().Ready.Wait(TimeSpan.FromSeconds(60), Ct);
        return api;
    }

    private static async Task<TraceResult> TraceAsync(WebApplicationFactory<Program> api, long terminal)
    {
        using var client = NetworkFixture.Client(api);
        return (await client.GetFromJsonAsync<TraceResult>($"/api/trace?terminal={terminal}", Ct))!;
    }

    /// <summary>Two unconnected ports on active equipment, a &lt; b.</summary>
    private static async Task<(long A, long B)> FreePortsAsync(NpgsqlDataSource db)
    {
        await using var cmd = db.CreateCommand("""
            SELECT p.terminal_id FROM port p
            WHERE NOT EXISTS (SELECT 1 FROM connection c WHERE c.a_terminal_id = p.terminal_id OR c.b_terminal_id = p.terminal_id)
            ORDER BY p.terminal_id LIMIT 2
            """);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var ids = new List<long>();
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return (ids[0], ids[1]);
    }

    private static async Task<List<(string Kind, long Key)>> Rows(NpgsqlDataSource db)
    {
        var rows = new List<(string, long)>();
        await using var cmd = db.CreateCommand("SELECT kind, key FROM graph_change ORDER BY id");
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.GetInt64(1)));
        }
        return rows;
    }

    private static async Task<long> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static byte[] Bytes(Cmdb.Graph.Graph g)
    {
        using var stream = new MemoryStream();
        GraphSnapshot.Write(g, stream);
        return stream.ToArray();
    }
}
