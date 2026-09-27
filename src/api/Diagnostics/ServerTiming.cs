using System.Diagnostics;
using System.Globalization;

namespace Cmdb.Api.Diagnostics;

/// <summary>
/// Adds <c>Server-Timing: app;dur=…</c> to API responses so the browser can tell server time from network time
/// (the performance panel, #56). Covers everything from authentication to the first byte of the response.
/// </summary>
public static class ServerTiming
{
    public static IApplicationBuilder UseServerTiming(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal))
            {
                await next();
                return;
            }
            var start = Stopwatch.GetTimestamp();
            context.Response.OnStarting(() =>
            {
                var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                context.Response.Headers.Append("Server-Timing", string.Create(CultureInfo.InvariantCulture, $"app;dur={ms:0.0}"));
                return Task.CompletedTask;
            });
            await next();
        });
}
