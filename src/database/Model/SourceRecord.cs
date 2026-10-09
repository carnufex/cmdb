using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmdb.Database.Model;

/// <summary>
/// What one source system last said about one object (#215, ADR-0019): its id there, when it last confirmed the object,
/// and the value it reported per attribute. An object can have one from every source that knows it. The
/// <see cref="Tracked"/> columns on the object stay as the source that created it.
/// </summary>
public class SourceRecord
{
    public long Id { get; set; }

    /// <summary>site, location, equipment, cable, circuit or service.</summary>
    public required string ObjectType { get; set; }
    public long ObjectId { get; set; }
    public required string SourceSystem { get; set; }
    public required string ExternalId { get; set; }
    public DateTimeOffset ConfirmedAt { get; set; }

    /// <summary>
    /// When a reconciliation (#216) first ran without the source reporting the object; null while it does. The object
    /// is never removed for it, only reported.
    /// </summary>
    public DateTimeOffset? MissingSince { get; set; }

    /// <summary>
    /// The value per attribute as the source reported it: <c>name</c>, <c>lifecycle</c>, <c>position</c> and so on, and
    /// <c>attributes.&lt;key&gt;</c> for the object's own attributes. Geometries that are lines are kept as a hash.
    /// </summary>
    public string Reported { get; set; } = "{}";
}

internal sealed class SourceRecordConfiguration : IEntityTypeConfiguration<SourceRecord>
{
    public void Configure(EntityTypeBuilder<SourceRecord> e)
    {
        e.ToTable("source_record", t =>
        {
            t.HasCheckConstraint("ck_source_record_object_type",
                "object_type IN ('site', 'location', 'equipment', 'cable', 'circuit', 'service')");
            t.HasCheckConstraint("ck_source_record_reported", "jsonb_typeof(reported) = 'object'");
        });
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.Property(x => x.Reported).HasColumnType("jsonb").HasDefaultValueSql("'{}'");
        e.Property(x => x.ConfirmedAt).HasDefaultValueSql("now()");
        // One record per source and object, and a source's id names one object of each kind.
        e.HasIndex(x => new { x.ObjectType, x.SourceSystem, x.ExternalId }).IsUnique();
        e.HasIndex(x => new { x.ObjectType, x.ObjectId, x.SourceSystem }).IsUnique();
    }
}
