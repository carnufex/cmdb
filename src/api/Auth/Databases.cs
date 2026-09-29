using System.Collections.Concurrent;
using Cmdb.Database;
using Npgsql;

namespace Cmdb.Api.Auth;

/// <summary>
/// Connections for the system's own work, which sees everything (<c>cmdb.scopes = *</c>): graph loading, the change
/// stream, pruning, scope refresh and migrations. Never used for a request.
/// </summary>
public sealed class SystemDb(NpgsqlDataSource source)
{
    public NpgsqlDataSource Source => source;
}

/// <summary>
/// Connections for one request (#22, step 2): every session carries the caller's scopes in <c>cmdb.scopes</c>, which
/// the row-level security policies read. Postgres enforces the scopes even if a query forgets the application filter.
/// </summary>
public sealed class RequestDb(NpgsqlDataSource source)
{
    public NpgsqlDataSource Source => source;

    public NpgsqlCommand CreateCommand(string sql) => source.CreateCommand(sql);

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct) => source.OpenConnectionAsync(ct);

    public static implicit operator NpgsqlDataSource(RequestDb db) => db.Source;
}

/// <summary>
/// One pool per scope combination, with the scopes set as a startup option on every connection. A pool without a
/// setting (<see cref="Deny"/>) sees nothing: forgetting to choose a pool fails closed.
/// </summary>
public sealed class ScopedDataSources(string connectionString) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _pools = new(StringComparer.Ordinal);

    /// <summary>Sees everything; for <see cref="SystemDb"/> only.</summary>
    public NpgsqlDataSource System => Pool("*");

    /// <summary>Sees nothing; the default <see cref="NpgsqlDataSource"/> in the container.</summary>
    public NpgsqlDataSource Deny => Pool("none");

    public NpgsqlDataSource For(UserScope scope) =>
        Pool(scope.IsEmpty ? "none" : scope.Unrestricted ? "*" : string.Join(',', scope.Keys));

    private NpgsqlDataSource Pool(string scopes) => _pools.GetOrAdd(scopes, s =>
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Options = $"-c cmdb.scopes={s}" };
        if (s is not "*" and not "none")
        {
            // Scope combinations are few, but each has its own pool; keep them small.
            builder.MaxPoolSize = Math.Min(builder.MaxPoolSize, 30);
        }
        return CmdbDatabase.CreateDataSource(builder.ConnectionString);
    });

    public async ValueTask DisposeAsync()
    {
        foreach (var pool in _pools.Values)
        {
            await pool.DisposeAsync();
        }
    }
}
