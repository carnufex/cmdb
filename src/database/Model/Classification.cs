using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmdb.Database.Model;

/// <summary>
/// A classification an object has (#176, ADR-0017): a level in a schema from the catalog (criticality 1–5, say). Which
/// levels exist, and which kinds of object take them, is the catalog's to say; the table only holds what was set. Derived
/// levels (a rack inherits what its equipment has, #177) are worked out, not stored here.
/// </summary>
public class Classification
{
    public long Id { get; set; }

    /// <summary>site, equipment, cable or service.</summary>
    public required string ObjectType { get; set; }
    public long ObjectId { get; set; }
    public required string SchemaKey { get; set; }
    public int Level { get; set; }

    /// <summary>set (by a person or an applied plan) or imported (migrated or loaded).</summary>
    public string Source { get; set; } = "set";
    public required string SetBy { get; set; }
    public DateTimeOffset SetAt { get; set; }
}

internal sealed class ClassificationConfiguration : IEntityTypeConfiguration<Classification>
{
    public void Configure(EntityTypeBuilder<Classification> e)
    {
        e.ToTable("classification", t =>
        {
            t.HasCheckConstraint("ck_classification_object_type", "object_type IN ('site', 'equipment', 'cable', 'service')");
            t.HasCheckConstraint("ck_classification_source", "source IN ('set', 'imported')");
            t.HasCheckConstraint("ck_classification_level", "level > 0");
        });
        e.Property(x => x.Source).HasDefaultValue("set").HasSentinel(null!);
        e.Property(x => x.SetAt).HasDefaultValueSql("now()");
        // One level per schema and object; the lookups are by object and by schema and level.
        e.HasIndex(x => new { x.SchemaKey, x.ObjectType, x.ObjectId }).IsUnique();
        e.HasIndex(x => new { x.ObjectType, x.ObjectId });
        e.HasIndex(x => new { x.SchemaKey, x.Level });
    }
}
