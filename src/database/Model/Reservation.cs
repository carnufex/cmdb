using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmdb.Database.Model;

/// <summary>
/// A plan or a service holding a resource (#25, ADR-0005): a terminal, a conductor (a fibre, both its ends), a slot
/// on equipment or a channel. One active holder per resource; a plan's reservations are released when it is applied
/// or cancelled. Plans also want the terminals their operations connect; those claims come from the operations.
/// </summary>
public class Reservation
{
    public long Id { get; set; }

    /// <summary>terminal, conductor, slot, channel or subduct (ADR-0014).</summary>
    public required string ResourceKind { get; set; }

    /// <summary>The terminal, conductor, channel or subduct id; for a slot, the equipment id.</summary>
    public long ResourceId { get; set; }

    /// <summary>The slot name, for slots only.</summary>
    public string? Slot { get; set; }

    /// <summary>plan or service.</summary>
    public required string HolderKind { get; set; }
    public long HolderId { get; set; }

    public string Reason { get; set; } = "";
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
}

internal sealed class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> e)
    {
        e.ToTable("reservation", t =>
        {
            t.HasCheckConstraint("ck_reservation_resource", "resource_kind IN ('terminal', 'conductor', 'slot', 'channel', 'subduct')");
            t.HasCheckConstraint("ck_reservation_slot", "(resource_kind = 'slot') = (slot IS NOT NULL)");
            t.HasCheckConstraint("ck_reservation_holder", "holder_kind IN ('plan', 'service')");
        });
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.Property(x => x.Reason).HasDefaultValue("").HasSentinel(null!);
        // One active holder per resource.
        e.HasIndex(x => new { x.ResourceKind, x.ResourceId, x.Slot })
            .IsUnique().AreNullsDistinct(false).HasFilter("released_at IS NULL").HasDatabaseName("ix_reservation_active");
        e.HasIndex(x => new { x.HolderKind, x.HolderId }).HasFilter("released_at IS NULL");
    }
}
