using System.IO.Compression;
using Cmdb.Api.Auth;
using Cmdb.Database;
using Npgsql;

namespace Cmdb.Api.Features.Operations;

/// <summary>
/// Looks after the operation log (#23, ADR-0013): at start it sets whether operations are hash chained
/// (<c>Operations:HashChain</c>), and every few hours it makes the coming months' and days' partitions and drops the read
/// log's days older than <c>ReadLog:RetentionDays</c> (90). With <c>ReadLog:ArchivePath</c> set, a day is first exported
/// there as gzipped CSV, so it can be kept elsewhere for longer.
/// </summary>
public sealed partial class OperationLogMaintenance(SystemDb system, IConfiguration config, ILogger<OperationLogMaintenance> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(config.GetValue("Operations:MaintenanceHours", 6.0));
        try
        {
            await SetHashChainAsync(config.GetValue("Operations:HashChain", false), stoppingToken);
        }
        catch (NpgsqlException ex) when (!stoppingToken.IsCancellationRequested)
        {
            Failed(logger, ex);
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is NpgsqlException or IOException && !stoppingToken.IsCancellationRequested)
            {
                Failed(logger, ex);
            }
            await Task.Delay(interval, stoppingToken);
        }
    }

    public async Task SetHashChainAsync(bool on, CancellationToken ct)
    {
        await using var cmd = system.Source.CreateCommand("UPDATE operation_config SET hash_chain = $1 WHERE id = 1 AND hash_chain IS DISTINCT FROM $1");
        cmd.Parameters.Add(new() { Value = on });
        if (await cmd.ExecuteNonQueryAsync(ct) > 0)
        {
            HashChain(logger, on);
        }
    }

    /// <summary>One round: partitions ahead, then the read log's old days.</summary>
    public async Task RunAsync(DateTimeOffset now, CancellationToken ct)
    {
        var made = await OperationLog.EnsurePartitionsAsync(system.Source, now, ct: ct);
        if (made > 0)
        {
            PartitionsMade(logger, made);
        }
        var retention = TimeSpan.FromDays(config.GetValue("ReadLog:RetentionDays", 90.0));
        var archive = config["ReadLog:ArchivePath"];
        var cutoff = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero) - retention;
        foreach (var (name, day) in await OperationLog.ReadLogPartitionsBeforeAsync(system.Source, cutoff, ct))
        {
            if (!string.IsNullOrEmpty(archive))
            {
                await ExportAsync(name, Path.Combine(archive, $"{name}.csv.gz"), ct);
            }
            await using var drop = system.Source.CreateCommand($"DROP TABLE {name}");
            await drop.ExecuteNonQueryAsync(ct);
            ReadLogDropped(logger, day);
        }
        // Rows the default partition caught before their day had a partition.
        await using var old = system.Source.CreateCommand("DELETE FROM read_log_default WHERE at < $1");
        old.Parameters.Add(new() { Value = cutoff });
        await old.ExecuteNonQueryAsync(ct);
    }

    private async Task ExportAsync(string partition, string file, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        await using (var conn = await system.Source.OpenConnectionAsync(ct))
        using (var reader = await conn.BeginTextExportAsync($"COPY {partition} TO STDOUT (FORMAT csv, HEADER)", ct))
        await using (var output = File.Create(temp))
        await using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
        await using (var writer = new StreamWriter(gzip))
        {
            var buffer = new char[64 * 1024];
            int n;
            while ((n = await reader.ReadAsync(buffer, ct)) > 0)
            {
                await writer.WriteAsync(buffer.AsMemory(0, n), ct);
            }
        }
        File.Move(temp, file, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Operation hash chain turned {On}")]
    private static partial void HashChain(ILogger logger, bool on);

    [LoggerMessage(Level = LogLevel.Information, Message = "Made {Count} operation log partition(s)")]
    private static partial void PartitionsMade(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dropped the read log of {Day}")]
    private static partial void ReadLogDropped(ILogger logger, DateTimeOffset day);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operation log maintenance failed; trying again next interval")]
    private static partial void Failed(ILogger logger, Exception exception);
}
