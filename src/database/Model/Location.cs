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

    /// <summary>A rack's row in its room, from 1 (#256). Null when its place is not known.</summary>
    public short? RoomRow { get; set; }

    /// <summary>A rack's place in its row, from 1 (#256).</summary>
    public short? RoomSlot { get; set; }

    /// <summary>Which way a rack's front faces along its row: front or back (#256), for aisles.</summary>
    public string? Facing { get; set; }
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
            // Only racks have a place in their room, and a place is a row and a slot together (#256).
            t.HasCheckConstraint("ck_location_room_place",
                "(room_row IS NULL AND room_slot IS NULL AND facing IS NULL) OR (kind = 'rack' AND room_row > 0 AND room_slot > 0)");
            t.HasCheckConstraint("ck_location_facing", "facing IN ('front', 'back')");
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
