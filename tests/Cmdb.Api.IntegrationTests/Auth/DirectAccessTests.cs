using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.Database.Scopes;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Auth;

/// <summary>
/// Row-level security for roles that read the database directly (ADR-0012, #96): they see what their scopes show
/// and nothing else, while the owning role the API uses is not affected.
/// </summary>
public sealed class DirectAccessTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string North = "POLYGON((200000 6950000, 1000000 6950000, 1000000 7800000, 200000 7800000, 200000 6950000))";

    [Fact]
    public async Task A_direct_role_sees_only_its_scope()
    {
        await using var owner = await NetworkAsync();
        var role = await RoleAsync(owner);
        await ScopeAsync(owner, "direkt-nord", role, hidden: "{}");
        await using var direct = Connect(owner, role);

        var sites = await Scalar(direct, "SELECT count(*) FROM site");
        sites.ShouldBeGreaterThan(0);
        sites.ShouldBe(await Scalar(owner, "SELECT count(*) FROM scope_site WHERE scope_key = 'direkt-nord'"));
        sites.ShouldBeLessThan(await Scalar(owner, "SELECT count(*) FROM site"));
        (await Scalar(direct, $"SELECT count(*) FROM site s WHERE NOT ST_Intersects(s.geom, ST_GeomFromText('{North}', 3006))")).ShouldBe(0);

        // Children follow their parents.
        (await Scalar(direct, "SELECT count(*) FROM equipment")).ShouldBe(await Scalar(owner,
            "SELECT count(*) FROM equipment e JOIN scope_site z ON z.site_id = e.site_id AND z.scope_key = 'direkt-nord'"));
        (await Scalar(direct, "SELECT count(*) FROM port")).ShouldBe(await Scalar(owner,
            "SELECT count(*) FROM port p JOIN equipment e ON e.id = p.equipment_id JOIN scope_site z ON z.site_id = e.site_id AND z.scope_key = 'direkt-nord'"));
        (await Scalar(direct, "SELECT count(*) FROM cable")).ShouldBe(await Scalar(owner, "SELECT count(*) FROM scope_cable WHERE scope_key = 'direkt-nord'"));
        (await Scalar(direct, "SELECT count(*) FROM service")).ShouldBe(await Scalar(owner, "SELECT count(*) FROM scope_service WHERE scope_key = 'direkt-nord'"));
        (await Scalar(direct, "SELECT count(*) FROM circuit_hop")).ShouldBe(await Scalar(owner,
            "SELECT count(*) FROM circuit_hop h JOIN scope_circuit z ON z.circuit_id = h.circuit_id AND z.scope_key = 'direkt-nord'"));

        // The owner is not subject to RLS.
        (await Scalar(owner, "SELECT count(*) FROM site")).ShouldBeGreaterThan(sites);
    }

    [Fact]
    public async Task A_direct_role_gets_no_other_tables_and_cannot_widen_its_scope()
    {
        await using var owner = await NetworkAsync();
        var role = await RoleAsync(owner);
        await ScopeAsync(owner, "direkt-nord", role, hidden: "{}");
        await using var direct = Connect(owner, role);

        foreach (var table in new[] { "terminal", "connection", "conductor_end", "graph_change", "user_preference" })
        {
            (await Should.ThrowAsync<PostgresException>(() => Scalar(direct, $"SELECT count(*) FROM {table}")))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
        (await Should.ThrowAsync<PostgresException>(() => Scalar(direct, "UPDATE access_scope SET db_roles = '{}' RETURNING 1")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => Scalar(direct, "DELETE FROM scope_site RETURNING 1")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // A session setting from the withdrawn step 2 does nothing.
        var sites = await Scalar(direct, "SELECT count(*) FROM site");
        (await Scalar(direct, "SELECT count(*) FROM (SELECT set_config('cmdb.scopes', '*', false)) x, site")).ShouldBe(sites);
    }

    [Fact]
    public async Task A_role_without_a_scope_sees_nothing_and_hidden_attributes_are_not_granted()
    {
        await using var owner = await NetworkAsync();
        var hiding = await RoleAsync(owner);
        await ScopeAsync(owner, "direkt-dold", hiding, hidden: "{serialNumber}");
        await using var direct = Connect(owner, hiding);

        (await Scalar(direct, "SELECT count(*) FROM equipment")).ShouldBeGreaterThan(0);
        (await Should.ThrowAsync<PostgresException>(() => Scalar(direct, "SELECT count(attributes) FROM equipment")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // Leaving the scope removes everything, even with the grants still in place.
        await Execute(owner, "UPDATE access_scope SET db_roles = '{}' WHERE key = 'direkt-dold'");
        (await Scalar(direct, "SELECT count(*) FROM site")).ShouldBe(0);
        (await Scalar(direct, "SELECT count(*) FROM port")).ShouldBe(0);
        (await Scalar(direct, "SELECT count(*) FROM service_circuit")).ShouldBe(0);
    }

    private async Task<NpgsqlDataSource> NetworkAsync()
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(1, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        return db;
    }

    private static async Task<string> RoleAsync(NpgsqlDataSource owner)
    {
        var role = $"direct_{Guid.NewGuid():N}";
        await Execute(owner, $"CREATE ROLE {role} LOGIN PASSWORD 'direct-test'");
        return role;
    }

    private static async Task ScopeAsync(NpgsqlDataSource owner, string key, string role, string hidden)
    {
        await Execute(owner, $$"""
            INSERT INTO access_scope (key, name, area, site_types, hidden_attributes, plans, crossing_mode, groups, db_roles, reason, granted_by, approved_by)
            VALUES ('{{key}}', '{{key}}', ST_GeomFromText('{{North}}', 3006), '{}', '{{hidden}}', '{}', 'whole', '{}', ARRAY['{{role}}'], 'test', 'a', 'b')
            """);
        await ScopeVisibility.RefreshAsync(owner, Ct);
        await DirectAccess.GrantAsync(owner, Ct);
    }

    private NpgsqlDataSource Connect(NpgsqlDataSource owner, string role) =>
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(owner.ConnectionString).Database,
            Username = role,
            Password = "direct-test",
            Pooling = false,
        }.ConnectionString);

    private static async Task Execute(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<long> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
