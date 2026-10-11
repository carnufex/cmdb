using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Cmdb.Api.Agents;
using Cmdb.Api.Auth;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using NpgsqlTypes;

namespace Cmdb.Api.Features.Operations;

/// <summary>One reading call (ADR-0013 §4): who, through which surface, the question, the scopes it was answered in and a
/// hash of the answer, never the answer itself.</summary>
public sealed record ReadLogEntry(DateTimeOffset At, string Actor, string? Client, string Surface, string Query, string Scope,
    byte[]? ResultHash, int? Rows, short Status, float ElapsedMs);

/// <summary>
/// The read log (#23): every signed-in GET under <c>/api</c> and every MCP call. The answer streams through a hashing
/// wrapper on its way out, so nothing is buffered and the response is not slower to start; the entry is queued and
/// written in batches outside the request (<see cref="ReadLogWriter"/>).
/// </summary>
public static class ReadLogMiddleware
{
    /// <summary>How much of an MCP request is read to name the question: the method, the tool and its arguments.</summary>
    private const int McpPeek = 16 * 1024;

    private const int QueryLength = 2000;

    /// <summary>Logs every reading call; <c>ReadLog:Enabled</c> = false turns it off (for measuring what it costs).</summary>
    public static IApplicationBuilder UseReadLog(this IApplicationBuilder app) =>
        !app.ApplicationServices.GetRequiredService<IConfiguration>().GetValue("ReadLog:Enabled", true) ? app : app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var surface = path.StartsWithSegments("/api", StringComparison.Ordinal) && HttpMethods.IsGet(context.Request.Method) ? "api"
                : path.StartsWithSegments(McpSetup.Path, StringComparison.Ordinal) && HttpMethods.IsPost(context.Request.Method) ? "mcp"
                : Features.Voice.VoiceChannels.For(path) is not null && HttpMethods.IsPost(context.Request.Method) ? "voice"
                : null;
            if (surface is null)
            {
                await next();
                return;
            }

            var started = Stopwatch.GetTimestamp();
            var query = surface == "api" ? path.Value + context.Request.QueryString.Value : await McpQueryAsync(context.Request);
            var original = context.Features.Get<IHttpResponseBodyFeature>()!;
            await using var hashing = new HashingStream(original.Stream);
            var body = new StreamResponseBodyFeature(hashing, original);
            context.Features.Set<IHttpResponseBodyFeature>(body);
            try
            {
                await next();
                await body.CompleteAsync();
            }
            finally
            {
                context.Features.Set(original);
                // Authentication runs in the endpoint's authorisation for some surfaces, so who it was is known only now.
                if (context.User.Identity?.IsAuthenticated == true)
                {
                    var user = context.User;
                    var scope = context.Scope();
                    context.RequestServices.GetRequiredService<ReadLogWriter>().Enqueue(new ReadLogEntry(
                        DateTimeOffset.UtcNow, Operations.Actor(user), user.FindFirst(CmdbClaims.Client)?.Value, surface,
                        query.Length > QueryLength ? query[..QueryLength] : query,
                        scope.Unrestricted ? "*" : scope.IsEmpty ? "-" : scope.Signature,
                        hashing.Written > 0 ? hashing.Hash() : null, hashing.Rows, (short)context.Response.StatusCode,
                        (float)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                }
            }
        });

    /// <summary>The JSON-RPC method, and for a tool call its name and arguments, as the question.</summary>
    private static async Task<string> McpQueryAsync(HttpRequest request)
    {
        request.EnableBuffering(McpPeek);
        var buffer = new byte[McpPeek];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await request.Body.ReadAsync(buffer.AsMemory(read), request.HttpContext.RequestAborted)) > 0)
        {
            read += n;
        }
        request.Body.Position = 0;
        try
        {
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, read));
            var root = doc.RootElement;
            var method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "?" : "?";
            if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("name", out var name))
            {
                // Arguments as name=value, with strings unescaped so the question reads as it was asked.
                var args = p.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                    ? string.Join(", ", a.EnumerateObject().Select(x => $"{x.Name}={(x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() : x.Value.GetRawText())}"))
                    : "";
                return $"{request.Path.Value} {method} {name.GetString()} {args}".TrimEnd();
            }
            return $"{request.Path.Value} {method}";
        }
        catch (JsonException)
        {
            return $"{request.Path.Value} (oläslig begäran, {read} byte)";
        }
    }
}

/// <summary>
/// Passes the response through while hashing it (sha256) and, for a JSON array, counting its elements: a small state
/// machine over the bytes that tracks depth and strings, so nothing is parsed or held.
/// </summary>
internal sealed class HashingStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int _depth;
    private bool _inString;
    private bool _escape;
    private bool _array;
    private bool _first = true;
    private int _elements;
    private bool _pendingElement;

    public long Written { get; private set; }

    /// <summary>The number of elements when the answer was a JSON array; otherwise none.</summary>
    public int? Rows => _array ? _elements : null;

    public byte[] Hash() => _hash.GetHashAndReset();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        Observe(buffer.AsSpan(offset, count));
        inner.Write(buffer, offset, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Observe(buffer.Span);
        await inner.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Observe(ReadOnlySpan<byte> bytes)
    {
        _hash.AppendData(bytes);
        Written += bytes.Length;
        foreach (var b in bytes)
        {
            if (_first)
            {
                if (b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t')
                {
                    continue;
                }
                _first = false;
                if (b != (byte)'[')
                {
                    // Not an array: stop counting.
                    _depth = -1;
                    return;
                }
                _array = true;
                _depth = 1;
                continue;
            }
            if (_depth < 0)
            {
                return;
            }
            if (_inString)
            {
                if (_escape)
                {
                    _escape = false;
                }
                else if (b == (byte)'\\')
                {
                    _escape = true;
                }
                else if (b == (byte)'"')
                {
                    _inString = false;
                }
                continue;
            }
            switch (b)
            {
                case (byte)'"':
                    _inString = true;
                    Element();
                    break;
                case (byte)'[' or (byte)'{':
                    Element();
                    _depth++;
                    break;
                case (byte)']' or (byte)'}':
                    _depth--;
                    break;
                case (byte)',':
                    if (_depth == 1)
                    {
                        _pendingElement = false;
                    }
                    break;
                case (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t':
                    break;
                default:
                    Element();
                    break;
            }
        }
    }

    /// <summary>A value starting at the array's own level is one more element.</summary>
    private void Element()
    {
        if (_depth == 1 && !_pendingElement)
        {
            _elements++;
            _pendingElement = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Writes the read log in batches with binary COPY, outside every request's transaction: at most a second after a call,
/// or sooner when a batch fills. When the database cannot keep up the queue is bounded; entries that do not fit are
/// counted and reported instead of slowing the answers down.
/// </summary>
public sealed partial class ReadLogWriter(SystemDb system, IConfiguration config, ILogger<ReadLogWriter> logger) : BackgroundService
{
    private readonly Channel<ReadLogEntry> _queue = Channel.CreateBounded<ReadLogEntry>(new BoundedChannelOptions(
        config.GetValue("ReadLog:QueueLength", 100_000))
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private long _dropped;

    public void Enqueue(ReadLogEntry entry)
    {
        if (!_queue.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(config.GetValue("ReadLog:FlushMilliseconds", 1000.0));
        var batchSize = config.GetValue("ReadLog:BatchSize", 2000);
        var batch = new List<ReadLogEntry>(batchSize);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                // A second's worth of calls goes in one batch.
                await Task.Delay(interval, stoppingToken);
                while (_queue.Reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                    if (batch.Count == batchSize)
                    {
                        await WriteAsync(batch, stoppingToken);
                        batch.Clear();
                    }
                }
                await WriteAsync(batch, stoppingToken);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        // What is still queued at shutdown is written before the process ends.
        while (_queue.Reader.TryRead(out var entry))
        {
            batch.Add(entry);
        }
        await WriteAsync(batch, CancellationToken.None);
    }

    private async Task WriteAsync(List<ReadLogEntry> batch, CancellationToken ct)
    {
        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
        {
            Dropped(logger, dropped);
        }
        if (batch.Count == 0)
        {
            return;
        }
        try
        {
            await using var conn = await system.Source.OpenConnectionAsync(ct);
            await using var copy = await conn.BeginBinaryImportAsync(
                "COPY read_log (at, actor, client, surface, query, scope, result_hash, rows, status, elapsed_ms) FROM STDIN (FORMAT BINARY)", ct);
            foreach (var e in batch)
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(e.At, NpgsqlDbType.TimestampTz, ct);
                await copy.WriteAsync(e.Actor, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(copy, e.Client, NpgsqlDbType.Text, ct);
                await copy.WriteAsync(e.Surface, NpgsqlDbType.Text, ct);
                await copy.WriteAsync(e.Query, NpgsqlDbType.Text, ct);
                await copy.WriteAsync(e.Scope, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(copy, e.ResultHash, NpgsqlDbType.Bytea, ct);
                await WriteNullableAsync(copy, e.Rows, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(e.Status, NpgsqlDbType.Smallint, ct);
                await copy.WriteAsync(e.ElapsedMs, NpgsqlDbType.Real, ct);
            }
            await copy.CompleteAsync(ct);
        }
        catch (NpgsqlException ex)
        {
            WriteFailed(logger, ex, batch.Count);
        }
    }

    private static Task WriteNullableAsync<T>(NpgsqlBinaryImporter copy, T? value, NpgsqlDbType type, CancellationToken ct) =>
        value is null ? copy.WriteNullAsync(ct) : copy.WriteAsync(value, type, ct);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The read log's queue was full; {Entries} entries were not logged")]
    private static partial void Dropped(ILogger logger, long entries);

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing {Entries} read log entries failed")]
    private static partial void WriteFailed(ILogger logger, Exception exception, int entries);
}
