using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Cmdb.Database.Scopes;
using Cmdb.Graph;
using Npgsql;

namespace Cmdb.Api.Auth;

/// <summary>
/// The scopes a request may see (ADR-0007, #22): the union of the valid scopes granted to the caller's groups.
/// Empty means nothing is visible; every read path filters by <see cref="Keys"/>.
/// </summary>
/// <param name="Unrestricted">
/// A granted scope covers the whole network (no area, all site types): queries skip the scope filter entirely, which
/// Postgres folds away at planning because the keys parameter is null.
/// </param>
/// <param name="Clips">
/// A granted scope clips cables at its edge (<c>crossing_mode = 'clip'</c>): geometry outside the scope's area is cut
/// away in tiles and trace routes, unless another granted scope shows the cable whole.
/// </param>
/// <param name="Plans">Plans (#24) the scopes show, by id as text; "*" is every plan.</param>
public sealed record UserScope(string[] Keys, IReadOnlySet<string> HiddenAttributes, bool Unrestricted = false, bool Clips = false,
    IReadOnlySet<string>? Plans = null)
{
    /// <summary>Whether the plan is visible: production is always, a plan only when a scope names it or all plans.</summary>
    public bool SeesPlan(long id) => Plans is { } plans && (plans.Contains("*") || plans.Contains(id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public static UserScope None { get; } = new([], new HashSet<string>());

    public bool IsEmpty => Keys.Length == 0;

    /// <summary>The scope keys as a query parameter: null when unrestricted, so the filter folds to true.</summary>
    public NpgsqlParameter Parameter() => new()
    {
        Value = Unrestricted ? DBNull.Value : Keys,
        NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
    };

    /// <summary>
    /// Positions are hidden ("coordinates" among the hidden attributes): no map tiles, no x/y on sites, search hits,
    /// query results or the neighbourhood graph, no trace route, and no ordering by distance.
    /// </summary>
    public bool HidesCoordinates => HiddenAttributes.Contains(HiddenCoordinates);

    public const string HiddenCoordinates = "coordinates";

    /// <summary>Identifies the combination, for caching masks.</summary>
    public string Signature => string.Join(',', Keys);

    /// <summary>Attributes without the ones a scope hides (conservatively: hidden if any of the scopes hides it).</summary>
    public string MaskAttributes(string json)
    {
        if (HiddenAttributes.Count == 0 || JsonNode.Parse(json) is not JsonObject obj)
        {
            return json;
        }
        foreach (var key in HiddenAttributes)
        {
            obj.Remove(key);
        }
        return obj.ToJsonString();
    }
}

public sealed record ScopeDefinition(string Key, string Name, string[] Groups, string[] HiddenAttributes, DateTimeOffset? ValidTo, bool Unrestricted = false, bool Clips = false,
    string[]? Plans = null);

/// <summary>The scope definitions, reloaded after every refresh of what they show.</summary>
public sealed class ScopeRegistry(SystemDb system)
{
    private volatile IReadOnlyList<ScopeDefinition> _scopes = [];
    private int _version;

    /// <summary>Bumped when the definitions or the materialised visibility change; graph masks are rebuilt.</summary>
    public int Version => _version;

    public IReadOnlyList<ScopeDefinition> All => _scopes;

    public async Task LoadAsync(CancellationToken ct)
    {
        var scopes = new List<ScopeDefinition>();
        await using var cmd = system.Source.CreateCommand(
            """
            SELECT key, name, groups, hidden_attributes, valid_to, area IS NULL AND cardinality(site_types) = 0,
                   area IS NOT NULL AND crossing_mode = 'clip', plans
            FROM access_scope ORDER BY key
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            scopes.Add(new ScopeDefinition(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<string[]>(2),
                reader.GetFieldValue<string[]>(3), reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4), reader.GetBoolean(5),
                reader.GetBoolean(6), reader.GetFieldValue<string[]>(7)));
        }
        _scopes = scopes;
        Interlocked.Increment(ref _version);
    }

    public UserScope For(ClaimsPrincipal user)
    {
        var groups = user.FindAll(CmdbClaims.Groups).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var granted = _scopes.Where(s => (s.ValidTo is null || s.ValidTo > now) && s.Groups.Any(groups.Contains)).ToList();
        return granted.Count == 0
            ? UserScope.None
            : new UserScope([.. granted.Select(s => s.Key).Order(StringComparer.Ordinal)],
                granted.SelectMany(s => s.HiddenAttributes).ToHashSet(StringComparer.Ordinal),
                granted.Any(s => s.Unrestricted),
                !granted.Any(s => s.Unrestricted) && granted.Any(s => s.Clips),
                granted.SelectMany(s => s.Plans ?? []).ToHashSet(StringComparer.Ordinal));
    }
}

/// <summary>
/// Keeps the materialised visibility current: at start, every few minutes, and soon after the change stream brings
/// new cables or circuits. Until the first refresh has run, scopes show nothing.
/// </summary>
public sealed partial class ScopeRefreshService(SystemDb system, ScopeRegistry registry, IConfiguration config, ILogger<ScopeRefreshService> logger)
    : BackgroundService
{
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes after the first refresh.</summary>
    public Task Ready => _ready.Task;

    /// <summary>Asks for a refresh soon; repeated requests while one is pending collapse into one.</summary>
    public void Request() => _requests.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(config.GetValue("Scopes:RefreshMinutes", 5.0));
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Refreshing materialises visibility; skip it when the database was just refreshed by a migration step.
                if (!first || config.GetValue("Scopes:RefreshOnStart", true))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await ScopeVisibility.RefreshAsync(system.Source, stoppingToken);
                    Refreshed(logger, sw.Elapsed.TotalSeconds);
                }
                await registry.LoadAsync(stoppingToken);
                _ready.TrySetResult();
                first = false;
            }
            catch (NpgsqlException ex) when (!stoppingToken.IsCancellationRequested)
            {
                RefreshFailed(logger, ex);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(first ? TimeSpan.FromSeconds(10) : interval);
            try
            {
                await _requests.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Access scope visibility refreshed in {Seconds:0.00} s")]
    private static partial void Refreshed(ILogger logger, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refreshing access scope visibility failed; retrying")]
    private static partial void RefreshFailed(ILogger logger, Exception exception);
}

/// <summary>A scope combination's visibility over the graph's arrays: one bool per site, cable, circuit and service.</summary>
public sealed class GraphMask
{
    public required bool[] Sites { get; init; }
    public required bool[] Cables { get; init; }
    public required bool[] Circuits { get; init; }
    public required bool[] Services { get; init; }

    /// <summary>A port by its equipment's site, a conductor end by its cable.</summary>
    public bool NodeVisible(Cmdb.Graph.Graph g, int node)
    {
        var site = g.SiteIndexOfNode(node);
        return site >= 0 ? SiteVisible(site) : CableVisible(g.CableIndexOfNode(node));
    }

    /// <summary>
    /// Masks are built for production. Planned objects (#107) have indexes after production's and are visible to
    /// whoever sees the plan; which plans a caller sees is itself decided by the scopes.
    /// </summary>
    public bool SiteVisible(int site) => site >= Sites.Length || Sites[site];

    public bool CableVisible(int cable) => cable >= Cables.Length || Cables[cable];

    public bool CircuitVisible(int circuit) => circuit >= Circuits.Length || Circuits[circuit];

    public bool ServiceVisible(int service) => service >= Services.Length || Services[service];
}

/// <summary>Builds and caches graph masks per scope combination, graph instance and registry version (#22).</summary>
public sealed class ScopeMasks(SystemDb system, ScopeRegistry registry)
{
    private readonly Lock _gate = new();
    private (Cmdb.Graph.Graph Graph, int Version) _for;
    private Dictionary<string, Task<GraphMask>> _cache = [];

    public Task<GraphMask> GetAsync(Cmdb.Graph.Graph g, UserScope scope, CancellationToken ct)
    {
        // A plan view (#24) shares every object index with its base, so it shares the base's masks too.
        g = g.Base;
        lock (_gate)
        {
            if (!ReferenceEquals(_for.Graph, g) || _for.Version != registry.Version)
            {
                _for = (g, registry.Version);
                _cache = [];
            }
            if (!_cache.TryGetValue(scope.Signature, out var mask) || mask.IsFaulted)
            {
                mask = scope.Unrestricted ? Task.FromResult(Everything(g)) : BuildAsync(g, scope.Keys, ct);
                _cache[scope.Signature] = mask;
            }
            return mask;
        }
    }

    private static GraphMask Everything(Cmdb.Graph.Graph g)
    {
        static bool[] All(int n)
        {
            var a = new bool[n];
            Array.Fill(a, true);
            return a;
        }
        return new GraphMask { Sites = All(g.SiteCount), Cables = All(g.CableCount), Circuits = All(g.CircuitCount), Services = All(g.ServiceCount) };
    }

    private async Task<GraphMask> BuildAsync(Cmdb.Graph.Graph g, string[] keys, CancellationToken ct)
    {
        var mask = new GraphMask
        {
            Sites = new bool[g.SiteCount],
            Cables = new bool[g.CableCount],
            Circuits = new bool[g.CircuitCount],
            Services = new bool[g.ServiceCount],
        };
        if (keys.Length == 0)
        {
            return mask;
        }
        await using var conn = await system.Source.OpenConnectionAsync(CancellationToken.None);
        await using var batch = new NpgsqlBatch(conn)
        {
            BatchCommands =
            {
                new("SELECT DISTINCT site_id FROM scope_site WHERE scope_key = ANY($1)") { Parameters = { new() { Value = keys } } },
                new("SELECT DISTINCT cable_id FROM scope_cable WHERE scope_key = ANY($1)") { Parameters = { new() { Value = keys } } },
                new("SELECT DISTINCT circuit_id FROM scope_circuit WHERE scope_key = ANY($1)") { Parameters = { new() { Value = keys } } },
                new("SELECT DISTINCT service_id FROM scope_service WHERE scope_key = ANY($1)") { Parameters = { new() { Value = keys } } },
            },
        };
        await using var reader = await batch.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            if (g.TryGetSite(reader.GetInt64(0), out var i))
            {
                mask.Sites[i] = true;
            }
        }
        await reader.NextResultAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            if (g.TryGetCable(reader.GetInt64(0), out var i))
            {
                mask.Cables[i] = true;
            }
        }
        await reader.NextResultAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            if (g.TryGetCircuit(reader.GetInt64(0), out var i))
            {
                mask.Circuits[i] = true;
            }
        }
        await reader.NextResultAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            if (g.TryGetService(reader.GetInt64(0), out var i))
            {
                mask.Services[i] = true;
            }
        }
        return mask;
    }
}

public static class ScopeHttpExtensions
{
    /// <summary>The caller's scopes for this request (none when anonymous).</summary>
    public static UserScope Scope(this HttpContext context) => context.RequestServices.GetRequiredService<UserScope>();
}

/// <summary>
/// SQL fragments that keep a query inside the caller's scopes. <c>$n</c> is <see cref="UserScope.Parameter"/>: the
/// text[] of scope keys, or null for an unrestricted caller, which Postgres folds to true when planning.
/// </summary>
public static class ScopeSql
{
    public static string Site(string idColumn, int param) =>
        $"(${param}::text[] IS NULL OR EXISTS (SELECT 1 FROM scope_site z WHERE z.scope_key = ANY(${param}) AND z.site_id = {idColumn}))";

    public static string Cable(string idColumn, int param) =>
        $"(${param}::text[] IS NULL OR EXISTS (SELECT 1 FROM scope_cable z WHERE z.scope_key = ANY(${param}) AND z.cable_id = {idColumn}))";

    public static string Circuit(string idColumn, int param) =>
        $"(${param}::text[] IS NULL OR EXISTS (SELECT 1 FROM scope_circuit z WHERE z.scope_key = ANY(${param}) AND z.circuit_id = {idColumn}))";

    public static string Service(string idColumn, int param) =>
        $"(${param}::text[] IS NULL OR EXISTS (SELECT 1 FROM scope_service z WHERE z.scope_key = ANY(${param}) AND z.service_id = {idColumn}))";

    /// <summary>A route segment (ADR-0014): shown when its geometry crosses one of the caller's scope areas.</summary>
    public static string RouteSegment(string idColumn, int param) =>
        $"(${param}::text[] IS NULL OR EXISTS (SELECT 1 FROM scope_route_segment z WHERE z.scope_key = ANY(${param}) AND z.route_segment_id = {idColumn}))";

    /// <summary>
    /// A route segment's geometry as the caller may see it: whole for an unrestricted caller or when a scope's area covers
    /// it, otherwise cut at the edge of the caller's areas, as cables in clip mode (ADR-0014). <paramref name="alias"/> is
    /// the route segment row.
    /// </summary>
    public static string RouteSegmentGeometry(string alias, int param, UserScope scope) => scope.Unrestricted
        ? $"{alias}.geom"
        : $"""
            coalesce((SELECT CASE WHEN bool_or(a.area IS NULL OR ST_CoveredBy({alias}.geom, a.area)) THEN {alias}.geom
                                  ELSE ST_Intersection({alias}.geom, ST_Union(a.area)) END
                      FROM access_scope a WHERE a.key = ANY(${param}) AND (a.area IS NULL OR a.area && {alias}.geom)), {alias}.geom)
            """;

    /// <summary>
    /// A cable's geometry as the caller may see it: whole, or cut to the areas of the clipping scopes that show it when
    /// no scope shows it whole (<see cref="UserScope.Clips"/>). <paramref name="alias"/> is the cable row.
    /// </summary>
    public static string CableGeometry(string alias, int param, UserScope scope) => !scope.Clips
        ? $"{alias}.geom"
        : $"""
            coalesce((SELECT CASE WHEN bool_or(a.area IS NULL OR a.crossing_mode = 'whole') THEN {alias}.geom
                                  ELSE ST_Intersection({alias}.geom, ST_Union(a.area)) END
                      FROM scope_cable z JOIN access_scope a ON a.key = z.scope_key
                      WHERE z.scope_key = ANY(${param}) AND z.cable_id = {alias}.id), {alias}.geom)
            """;
}
