using Cmdb.Graph;
using Npgsql;

namespace Cmdb.Api.Features.Graph;

/// <summary>
/// Keeps the change stream's outbox small (#78): every hour, rows older than <c>Graph:OutboxRetentionDays</c>
/// (7) are removed. Readers further behind than that reload in full instead of catching up.
/// </summary>
public sealed partial class GraphChangePruning(Cmdb.Api.Auth.SystemDb system, IConfiguration config, ILogger<GraphChangePruning> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retention = TimeSpan.FromDays(config.GetValue("Graph:OutboxRetentionDays", 7.0));
        var interval = TimeSpan.FromMinutes(config.GetValue("Graph:OutboxPruneMinutes", 60.0));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = await PostgresGraphChangeFeed.PruneAsync(system.Source, retention, ct: stoppingToken);
                if (deleted > 0)
                {
                    Pruned(logger, deleted, retention.TotalDays);
                }
            }
            catch (NpgsqlException ex) when (!stoppingToken.IsCancellationRequested)
            {
                PruneFailed(logger, ex);
            }
            await Task.Delay(interval, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Rows} change stream rows older than {Days} days")]
    private static partial void Pruned(ILogger logger, int rows, double days);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pruning the change stream failed; trying again next interval")]
    private static partial void PruneFailed(ILogger logger, Exception exception);
}
