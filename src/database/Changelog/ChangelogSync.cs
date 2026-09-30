using System.Text.Json;
using Cmdb.Database.Model;
using Microsoft.EntityFrameworkCore;

namespace Cmdb.Database.Changelog;

/// <summary>
/// Writes <c>changelog/entries.json</c> into <c>changelog_entry</c> (#82): inserts and updates by key and removes posts
/// no longer in the file. A post gets its publication time the first time it is synced as published.
/// </summary>
public static class ChangelogSync
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Entry(string Key, string Version, DateOnly Date, string Category, string Title, string Body, string? Action,
        int[] Issues, bool Published);

    public static IReadOnlyList<Entry> Embedded()
    {
        using var stream = typeof(ChangelogSync).Assembly.GetManifestResourceStream("changelog/entries.json")!;
        return JsonSerializer.Deserialize<List<Entry>>(stream, Json) ?? [];
    }

    /// <returns>How many posts were written or removed.</returns>
    public static async Task<int> SyncAsync(CmdbDbContext db, IReadOnlyList<Entry> entries, CancellationToken ct = default)
    {
        var existing = await db.ChangelogEntries.ToDictionaryAsync(e => e.Key, StringComparer.Ordinal, ct);
        var keys = entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in existing.Values.Where(e => !keys.Contains(e.Key)))
        {
            db.ChangelogEntries.Remove(gone);
        }
        foreach (var entry in entries)
        {
            if (!existing.TryGetValue(entry.Key, out var row))
            {
                row = new ChangelogEntry { Key = entry.Key, Version = entry.Version, Category = entry.Category, Title = entry.Title, Body = entry.Body };
                db.ChangelogEntries.Add(row);
            }
            row.Version = entry.Version;
            row.Date = entry.Date;
            row.Category = entry.Category;
            row.Title = entry.Title;
            row.Body = entry.Body;
            row.Action = entry.Action;
            row.Issues = entry.Issues;
            row.Published = entry.Published;
            row.PublishedAt = entry.Published ? row.PublishedAt ?? DateTimeOffset.UtcNow : null;
        }
        return await db.SaveChangesAsync(ct);
    }
}
