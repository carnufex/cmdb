using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmdb.Database.Model;

/// <summary>A model from the type catalog, synced from catalog/equipment-types. Data, not schema.</summary>
public class EquipmentType
{
    public long Id { get; set; }
    public required string Key { get; set; }
    public required string Manufacturer { get; set; }
    public required string Model { get; set; }
    public required string Category { get; set; }
    public short? RackUnits { get; set; }
    public string Panel { get; set; } = """{"rows": 1, "columns": 1}""";
    public string AttributeSchema { get; set; } = "{}";
    public string PortTemplate { get; set; } = "[]";
    public string SlotTemplate { get; set; } = "[]";
}

/// <summary>An instance of a type, mounted in a location or in a slot of other equipment.</summary>
public class Equipment : Tracked
{
    public long Id { get; set; }
    public long EquipmentTypeId { get; set; }
    public EquipmentType EquipmentType { get; set; } = null!;

    /// <summary>Denormalised for scope filtering and site queries.</summary>
    public long SiteId { get; set; }
    public Site Site { get; set; } = null!;
    public long? LocationId { get; set; }
    public Location? Location { get; set; }
    public long? ParentId { get; set; }
    public Equipment? Parent { get; set; }
    public string? Slot { get; set; }
    public required string Name { get; set; }
    public string Attributes { get; set; } = "{}";
    public List<Port> Ports { get; } = [];
}

internal sealed class EquipmentTypeConfiguration : IEntityTypeConfiguration<EquipmentType>
{
    public void Configure(EntityTypeBuilder<EquipmentType> e)
    {
        e.ToTable("equipment_type", t =>
        {
            t.HasCheckConstraint("ck_equipment_type_rack_units", "rack_units > 0");
            t.HasCheckConstraint("ck_equipment_type_port_template", "jsonb_typeof(port_template) = 'array'");
            t.HasCheckConstraint("ck_equipment_type_slot_template", "jsonb_typeof(slot_template) = 'array'");
        });
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.HasIndex(x => x.Key).IsUnique();
        e.HasIndex(x => new { x.Manufacturer, x.Model }).IsUnique();
        e.Property(x => x.Panel).HasColumnType("jsonb").HasDefaultValueSql("""'{"rows": 1, "columns": 1}'""");
        e.Property(x => x.AttributeSchema).HasColumnType("jsonb").HasDefaultValueSql("'{}'");
        e.Property(x => x.PortTemplate).HasColumnType("jsonb").HasDefaultValueSql("'[]'");
        e.Property(x => x.SlotTemplate).HasColumnType("jsonb").HasDefaultValueSql("'[]'");
    }
}

internal sealed class EquipmentConfiguration : IEntityTypeConfiguration<Equipment>
{
    public void Configure(EntityTypeBuilder<Equipment> e)
    {
        e.ConfigureTracked("equipment");
        e.ToTable(t =>
        {
            t.HasCheckConstraint("ck_equipment_placement", "(location_id IS NOT NULL) <> (parent_id IS NOT NULL)");
            t.HasCheckConstraint("ck_equipment_slot", "(parent_id IS NULL) = (slot IS NULL)");
            t.HasCheckConstraint("ck_equipment_not_own_parent", "parent_id IS DISTINCT FROM id");
        });
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.HasOne(x => x.EquipmentType).WithMany().OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.Site).WithMany().OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.Location).WithMany().OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.Parent).WithMany().OnDelete(DeleteBehavior.Restrict);
        e.Property(x => x.Attributes).HasColumnType("jsonb").HasDefaultValueSql("'{}'");
        e.HasIndex(x => new { x.ParentId, x.Slot }).IsUnique().HasFilter("parent_id IS NOT NULL");
    }
}
