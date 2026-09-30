using Npgsql;

namespace Cmdb.Api.Auth;

/// <summary>
/// Connections for the system's own work, which sees everything: graph loading, the change stream, pruning, scope
/// refresh and migrations. Never used for a request.
/// </summary>
public sealed class SystemDb(NpgsqlDataSource source) : IAsyncDisposable
{
    public NpgsqlDataSource Source => source;

    /// <summary>The pool is shared with <see cref="RequestDb"/>; it closes with the container.</summary>
    public ValueTask DisposeAsync() => source.DisposeAsync();
}

/// <summary>
/// Connections for one request. The API connects as the table owner, which row-level security does not apply to
/// (ADR-0012): every query on a request path applies the caller's scopes itself (<see cref="ScopeSql"/>, #22). The
/// separate type keeps request paths and system work apart in review.
/// </summary>
public sealed class RequestDb(NpgsqlDataSource source)
{
    public NpgsqlDataSource Source => source;

    public NpgsqlCommand CreateCommand(string sql) => source.CreateCommand(sql);

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct) => source.OpenConnectionAsync(ct);

    public static implicit operator NpgsqlDataSource(RequestDb db) => db.Source;
}
