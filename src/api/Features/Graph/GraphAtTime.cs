using System.Diagnostics;
using Cmdb.Graph;

namespace Cmdb.Api.Features.Graph;

/// <summary>
/// The graph at a time (#302, ADR-0013 §6): loaded from the current tables and their history on the first question about
/// that time, shared by every question that waits for it, and kept for the next ones. Only the latest few are kept
/// (<c>Graph:TimeTravelCacheSize</c>, 1): a graph at full scale is some 230 MB. The current graph is not touched.
/// </summary>
public sealed partial class GraphAtTime(Cmdb.Api.Auth.SystemDb system, IConfiguration config, ILogger<GraphAtTime> logger)
{
    private readonly Lock _gate = new();
    private readonly LinkedList<(GraphTime Time, Task<Cmdb.Graph.Graph> Graph)> _cache = new();
    private readonly int _size = Math.Max(1, config.GetValue("Graph:TimeTravelCacheSize", 1));

    public Task<Cmdb.Graph.Graph> GetAsync(GraphTime time)
    {
        // To the second: the web asks for the end of a day, and a finer key would only fill the cache.
        var at = time.At.ToUniversalTime();
        var key = time with { At = new DateTimeOffset(at.Ticks - (at.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero) };
        lock (_gate)
        {
            for (var node = _cache.First; node is not null; node = node.Next)
            {
                if (node.Value.Time == key && !node.Value.Graph.IsFaulted)
                {
                    _cache.Remove(node);
                    _cache.AddFirst(node);
                    return node.Value.Graph;
                }
            }
            // Not tied to the asking request: a cancelled request leaves the graph to the next one.
            var graph = Task.Run(() => LoadAsync(key));
            _cache.AddFirst((key, graph));
            while (_cache.Count > _size)
            {
                _cache.RemoveLast();
            }
            return graph;
        }
    }

    private async Task<Cmdb.Graph.Graph> LoadAsync(GraphTime time)
    {
        var sw = Stopwatch.StartNew();
        var graph = await GraphLoader.LoadAsync(system.Source, time);
        Loaded(logger, time.At, time.Knowledge ? "knowledge" : "reality", sw.Elapsed.TotalSeconds, graph.NodeCount);
        return graph;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Graph at {At} ({Mode}) loaded in {Seconds:0.0} s, {Nodes} terminals")]
    private static partial void Loaded(ILogger logger, DateTimeOffset at, string mode, double seconds, int nodes);
}

/// <summary>The time and mode a graph question is asked at (#302): none is now.</summary>
public static class GraphTimes
{
    /// <summary>For an MCP tool: the graph at the time given as text, or <paramref name="now"/> without one.</summary>
    public static async Task<Cmdb.Graph.Graph> ForToolAsync(Cmdb.Graph.Graph now, GraphAtTime times, string? at, string? mode)
    {
        if (at is null)
        {
            return mode is null or "reality" or "knowledge" ? now : throw new ModelContextProtocol.McpException("mode is reality or knowledge.");
        }
        if (!DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            || !TryParse(t, mode, out var time))
        {
            throw new ModelContextProtocol.McpException("Give at as an ISO 8601 date or time, and mode as reality or knowledge.");
        }
        return await times.GetAsync(time!);
    }

    /// <summary>The time asked for, or null for now; false when the mode is not one of reality or knowledge.</summary>
    public static bool TryParse(DateTimeOffset? at, string? mode, out GraphTime? time)
    {
        time = null;
        if (mode is not (null or "reality" or "knowledge"))
        {
            return false;
        }
        if (at is { } t)
        {
            time = new GraphTime(t, mode == "knowledge");
        }
        return true;
    }
}
