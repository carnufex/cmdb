using System.ComponentModel;
using System.Text;
using Cmdb.Database;
using Cmdb.Database.Model;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Cmdb.Api.Features.Changelog;

public sealed record ChangelogPost(string Key, string Version, DateOnly Date, string Category, string Title, string Body, string? Action,
    IReadOnlyList<int> Issues, bool Unread);

/// <param name="Unread">Posts published since the user last opened the change log.</param>
public sealed record ChangelogResponse(IReadOnlyList<ChangelogPost> Posts, int Unread);

/// <summary>
/// The user-facing change log (#82): published posts, newest first, and which of them the caller has not seen. A user
/// who has never opened it counts the last three weeks as unread, not the whole history.
/// </summary>
public sealed class GetChangelogEndpoint(CmdbDbContext db) : EndpointWithoutRequest<ChangelogResponse>
{
    public override void Configure() => Get("/changelog");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var subject = User.FindFirst("sub")?.Value ?? "";
        var readAt = await db.UserPreferences.Where(p => p.Subject == subject).Select(p => p.ChangelogReadAt).SingleOrDefaultAsync(ct)
            ?? DateTimeOffset.UtcNow.AddDays(-21);
        var posts = await ChangelogQuery.PublishedAsync(db, ct);
        var result = posts.Select(p => new ChangelogPost(p.Key, p.Version, p.Date, p.Category, p.Title, p.Body, p.Action, p.Issues,
            p.PublishedAt > readAt)).ToList();
        await Send.OkAsync(new ChangelogResponse(result, result.Count(p => p.Unread)), ct);
    }
}

/// <summary>Marks the change log as read for the caller.</summary>
public sealed class ReadChangelogEndpoint(CmdbDbContext db) : EndpointWithoutRequest
{
    public override void Configure() => Post("/changelog/read");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var subject = User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(subject))
        {
            ThrowError("The token has no subject.");
        }
        var preference = await db.UserPreferences.SingleOrDefaultAsync(p => p.Subject == subject, ct);
        if (preference is null)
        {
            preference = new UserPreference { Subject = subject };
            db.UserPreferences.Add(preference);
        }
        preference.ChangelogReadAt = DateTimeOffset.UtcNow;
        preference.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}

internal static class ChangelogQuery
{
    public static Task<List<ChangelogEntry>> PublishedAsync(CmdbDbContext db, CancellationToken ct) =>
        db.ChangelogEntries.Where(e => e.Published).OrderByDescending(e => e.Date).ThenByDescending(e => e.PublishedAt).ThenByDescending(e => e.Key)
            .Take(200).ToListAsync(ct);
}

/// <summary>The change log for agents (#82), as markdown with issue references.</summary>
[McpServerResourceType]
public sealed class McpChangelog(CmdbDbContext db)
{
    [McpServerResource(UriTemplate = "cmdb://changelog", Name = "changelog", Title = "Händelselogg", MimeType = "text/markdown")]
    [Description("What has changed in the CMDB for users, newest first, with issue references (#nr in carnufex/cmdb).")]
    public async Task<string> Changelog(CancellationToken ct)
    {
        var text = new StringBuilder("# Händelselogg\n");
        foreach (var post in await ChangelogQuery.PublishedAsync(db, ct))
        {
            text.Append($"\n## {post.Date:yyyy-MM-dd} · {post.Title} ({post.Category})\n\n{post.Body}\n");
            if (post.Action is not null)
            {
                text.Append($"\n*{post.Action}*\n");
            }
            text.Append($"\n{string.Join(", ", post.Issues.Select(i => $"#{i}"))} · {post.Version}\n");
        }
        return text.ToString();
    }
}
