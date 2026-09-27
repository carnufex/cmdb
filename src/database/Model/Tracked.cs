using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmdb.Database.Model;

/// <summary>
/// Lifecycle, valid time and provenance, carried by every network object. A plain base class, not EF
/// inheritance: each object keeps its own table.
/// </summary>
public abstract class Tracked
{
    public LifecycleState Lifecycle { get; set; } = LifecycleState.Planned;

    /// <summary>When this was true in reality (valid time). Full bitemporality arrives in phase 3.</summary>
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }

    public string? SourceSystem { get; set; }
    public string? ExternalId { get; set; }
    public DateTimeOffset? LastConfirmedAt { get; set; }
}

internal static class TrackedConfiguration
{
    public static void ConfigureTracked<T>(this EntityTypeBuilder<T> e, string table)
        where T : Tracked
    {
        e.Property(x => x.Lifecycle).HasDefaultValue(LifecycleState.Planned).HasSentinel((LifecycleState)(-1));
        e.Property(x => x.ValidFrom).HasDefaultValueSql("now()");
        e.ToTable(table, t => t.HasCheckConstraint($"ck_{table}_valid_time", "valid_to IS NULL OR valid_to > valid_from"));
    }
}
