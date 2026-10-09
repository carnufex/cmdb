using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmdb.Database.Model;

/// <summary>Hierarchy inside a site: building → room → rack → position.</summary>
public class Location : Tracked
{
    public long Id { get; set; }
    public long SiteId { get; set; }
    public Site Site { get; set; } = null!;
    public long? ParentId { get; set; }
    public Location? Parent { get; set; }

    /// <summary>building, room, rack or position.</summary>
    public required string Kind { get; set; }
    public required string Name { get; set; }
    public short? RackUnits { get; set; }
    public string Attributes { get; set; } = "{}";
}

internal sealed class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> e)
    {
        e.ConfigureTracked("location");
        e.ToTable(t =>
        {
            t.HasCheckConstraint("ck_location_kind", "kind IN ('building', 'room', 'rack', 'position')");
            t.HasCheckConstraint("ck_location_rack_units", "rack_units > 0");
            t.HasCheckConstraint("ck_location_not_own_parent", "parent_id IS DISTINCT FROM id");
        });
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.HasOne(x => x.Site).WithMany().OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.Parent).WithMany().OnDelete(DeleteBehavior.Restrict);
        e.Property(x => x.Attributes).HasColumnType("jsonb").HasDefaultValueSql("'{}'");
        e.HasIndex(x => new { x.SiteId, x.ParentId, x.Name }).IsUnique().AreNullsDistinct(false);
        // Imports match objects on their source system and id there (#210).
        e.HasIndex(x => new { x.SourceSystem, x.ExternalId }).IsUnique().HasFilter("external_id IS NOT NULL");
    }
}
