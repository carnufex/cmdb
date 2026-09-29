using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmdb.Database.Model;

/// <summary>
/// The change stream's outbox (#11): one row per changed key, written by triggers in the same transaction as the
/// change. The graph engine re-reads the key's current rows, so a row says what changed, not how.
/// </summary>
public class GraphChange
{
    public long Id { get; set; }

    /// <summary>The writing transaction (<c>xid8</c>); readers use it as a gap-free watermark.</summary>
    public ulong Tx { get; set; }

    /// <summary>equipment, cable, terminal, circuit, or reload after a bulk load.</summary>
    public required string Kind { get; set; }

    public long Key { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class GraphChangeConfiguration : IEntityTypeConfiguration<GraphChange>
{
    public void Configure(EntityTypeBuilder<GraphChange> e)
    {
        e.ToTable("graph_change", t => t.HasCheckConstraint("ck_graph_change_kind", "kind IN ('equipment', 'cable', 'terminal', 'circuit', 'reload')"));
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.Property(x => x.Tx).HasColumnType("xid8").HasDefaultValueSql("pg_current_xact_id()");
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.HasIndex(x => x.Tx);
    }
}

/// <summary>
/// How far the outbox has been pruned (#78): one row. A reader whose watermark is at or below <see cref="Tx"/> may
/// have missed changes and must reload in full.
/// </summary>
public class GraphChangePruned
{
    public int Id { get; set; } = 1;

    /// <summary>The highest transaction id removed from <c>graph_change</c>.</summary>
    public ulong Tx { get; set; }

    public DateTimeOffset PrunedAt { get; set; }
}

internal sealed class GraphChangePrunedConfiguration : IEntityTypeConfiguration<GraphChangePruned>
{
    public void Configure(EntityTypeBuilder<GraphChangePruned> e)
    {
        e.ToTable("graph_change_pruned", t => t.HasCheckConstraint("ck_graph_change_pruned_single", "id = 1"));
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.Tx).HasColumnType("xid8");
        e.Property(x => x.PrunedAt).HasDefaultValueSql("now()");
    }
}
