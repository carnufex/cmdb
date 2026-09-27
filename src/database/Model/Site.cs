using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NetTopologySuite.Geometries;

namespace Cmdb.Database.Model;

public class Site : Tracked
{
    public long Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string SiteType { get; set; }

    /// <summary>Point or polygon in SWEREF 99 TM (EPSG:3006).</summary>
    public required Geometry Geom { get; set; }

    public string Attributes { get; set; } = "{}";
}

internal sealed class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> e)
    {
        e.ConfigureTracked("site");
        e.ToTable(t => t.HasCheckConstraint("ck_site_geom_type", "GeometryType(geom) IN ('POINT', 'POLYGON', 'MULTIPOLYGON')"));
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.HasIndex(x => x.Code).IsUnique();
        e.Property(x => x.Geom).HasColumnType("geometry(Geometry, 3006)");
        e.HasIndex(x => x.Geom).HasMethod("gist");
        e.Property(x => x.Attributes).HasColumnType("jsonb").HasDefaultValueSql("'{}'");
        e.HasIndex(x => new { x.SourceSystem, x.ExternalId }).IsUnique().HasFilter("external_id IS NOT NULL");
    }
}
