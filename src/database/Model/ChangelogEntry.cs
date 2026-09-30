using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmdb.Database.Model;

/// <summary>
/// A post in the user-facing change log (#82). Written from issue, PR and diff with the standard prompt
/// (docs/changelog-prompt.md), reviewed in a PR in <c>changelog/entries.json</c>, and synced here with the migrations.
/// Only published posts are shown.
/// </summary>
public class ChangelogEntry
{
    public required string Key { get; set; }

    /// <summary>The deployment it arrived in, e.g. sha-3a87e9a.</summary>
    public required string Version { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>nytt, förbättrat or rättat.</summary>
    public required string Category { get; set; }

    public required string Title { get; set; }
    public required string Body { get; set; }

    /// <summary>What the user needs to do, if anything.</summary>
    public string? Action { get; set; }

    public int[] Issues { get; set; } = [];
    public bool Published { get; set; }

    /// <summary>When the post was first published here, for "unread".</summary>
    public DateTimeOffset? PublishedAt { get; set; }
}

internal sealed class ChangelogEntryConfiguration : IEntityTypeConfiguration<ChangelogEntry>
{
    public void Configure(EntityTypeBuilder<ChangelogEntry> e)
    {
        e.ToTable("changelog_entry", t => t.HasCheckConstraint("ck_changelog_entry_category", "category IN ('nytt', 'förbättrat', 'rättat')"));
        e.HasKey(x => x.Key);
        e.HasIndex(x => x.Date);
    }
}
