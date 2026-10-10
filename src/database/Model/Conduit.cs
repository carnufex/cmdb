using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NetTopologySuite.Geometries;

namespace Cmdb.Database.Model;

/// <summary>A duct model, synced from <c>catalog/duct-types/</c> (ADR-0014): an outer size and a template for its subducts.</summary>
public class DuctType
{
    public long Id { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }
    public required string Manufacturer { get; set; }
    public required string Model { get; set; }
    public int OuterDiameterMm { get; set; }
    public int SubductCount { get; set; }
    public int SubductInnerDiameterMm { get; set; }
    public string? ColorCode { get; set; }
}

/// <summary>
/// A stretch of trench, ploughed line, aerial line or existing conduit between two sites (ADR-0014): what ducts lie in. It
/// carries no signal and is not in the graph.
/// </summary>
public class RouteSegment : Tracked
{
    public long Id { get; set; }
    public required string Code { get; set; }
    public long ASiteId { get; set; }
    public Site ASite { get; set; } = null!;
    public long BSiteId { get; set; }
    public Site BSite { get; set; } = null!;

    /// <summary>trench, plough, aerial or existing.</summary>
    public required string Construction { get; set; }

    public string? Owner { get; set; }

    /// <summary>
    /// Trunk conduit (#243): carries a cable of 96 fibres or more (backbone, rings). Drawn at every zoom level, like the large
    /// cables; the rest only in detail.
    /// </summary>
    public bool Trunk { get; set; }

    /// <summary>In SWEREF 99 TM (EPSG:3006).</summary>
    public required LineString Geom { get; set; }

    /// <summary>Derived by the database from the geometry.</summary>
    public double LengthM { get; private set; }

    public string Attributes { get; set; } = "{}";
}

/// <summary>A duct of a duct type along an ordered run of route segments; it may itself lie in a subduct of another duct.</summary>
public class Duct : Tracked
{
    public long Id { get; set; }
    public required string Code { get; set; }
    public long DuctTypeId { get; set; }
    public DuctType DuctType { get; set; } = null!;

    /// <summary>The subduct this duct is pulled into (duct in duct), if any.</summary>
    public long? ParentSubductId { get; set; }

    public string Attributes { get; set; } = "{}";
}

/// <summary>The duct's place on a route segment: segment number <see cref="Seq"/> from its start.</summary>
public class DuctSegment
{
    public long DuctId { get; set; }
    public int Seq { get; set; }
    public long RouteSegmentId { get; set; }
}

/// <summary>Tube number N in a duct, generated from its type's template like ports from a port template.</summary>
public class Subduct
{
    public long Id { get; set; }
    public long DuctId { get; set; }
    public Duct Duct { get; set; } = null!;
    public int Number { get; set; }
    public string? Color { get; set; }

    /// <summary>
    /// empty, cable (a cable's path runs through it) or blown_fibre. Reserved is not stored: it is a reservation (#25)
    /// of the subduct.
    /// </summary>
    public string Occupancy { get; set; } = "empty";
}

/// <summary>A cable's way through the conduit: subduct number <see cref="Seq"/> from its A end.</summary>
public class CablePath
{
    public long CableId { get; set; }
    public int Seq { get; set; }
    public long SubductId { get; set; }
}

internal sealed class DuctTypeConfiguration : IEntityTypeConfiguration<DuctType>
{
    public void Configure(EntityTypeBuilder<DuctType> e)
    {
        e.ToTable("duct_type", t => t.HasCheckConstraint("ck_duct_type_subducts", "subduct_count > 0 AND subduct_inner_diameter_mm > 0 AND outer_diameter_mm > 0"));
        e.HasIndex(x => x.Key).IsUnique();
    }
}

internal sealed class RouteSegmentConfiguration : IEntityTypeConfiguration<RouteSegment>
{
    public void Configure(EntityTypeBuilder<RouteSegment> e)
    {
        e.ConfigureTracked("route_segment");
        e.ToTable(t =>
        {
            t.HasCheckConstraint("ck_route_segment_distinct_ends", "a_site_id <> b_site_id");
            t.HasCheckConstraint("ck_route_segment_construction", "construction IN ('trench', 'plough', 'aerial', 'existing')");
        });
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.HasIndex(x => x.Code).IsUnique();
        e.HasOne(x => x.ASite).WithMany().HasForeignKey(x => x.ASiteId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.BSite).WithMany().HasForeignKey(x => x.BSiteId).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => x.ASiteId);
        e.HasIndex(x => x.BSiteId);
        e.Property(x => x.Geom).HasColumnType("geometry(LineString, 3006)");
        e.HasIndex(x => x.Geom).HasMethod("gist");
        // National map tiles draw only trunk conduit (#243): its own small index.
        e.HasIndex(x => x.Geom, "ix_route_segment_trunk_geom").HasMethod("gist").HasFilter("trunk").HasDatabaseName("ix_route_segment_trunk_geom");
        e.Property(x => x.LengthM).HasComputedColumnSql("ST_Length(geom)", stored: true);
        e.Property(x => x.Attributes).HasColumnType("jsonb").HasDefaultValueSql("'{}'");
    }
}

internal sealed class DuctConfiguration : IEntityTypeConfiguration<Duct>
{
    public void Configure(EntityTypeBuilder<Duct> e)
    {
        e.ConfigureTracked("duct");
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.HasIndex(x => x.Code).IsUnique();
        e.HasOne(x => x.DuctType).WithMany().OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Subduct>().WithMany().HasForeignKey(x => x.ParentSubductId).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => x.ParentSubductId).HasFilter("parent_subduct_id IS NOT NULL");
        e.Property(x => x.Attributes).HasColumnType("jsonb").HasDefaultValueSql("'{}'");
    }
}

internal sealed class DuctSegmentConfiguration : IEntityTypeConfiguration<DuctSegment>
{
    public void Configure(EntityTypeBuilder<DuctSegment> e)
    {
        e.ToTable("duct_segment", t => t.HasCheckConstraint("ck_duct_segment_seq", "seq >= 0"));
        e.HasKey(x => new { x.DuctId, x.Seq });
        e.HasOne<Duct>().WithMany().HasForeignKey(x => x.DuctId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<RouteSegment>().WithMany().HasForeignKey(x => x.RouteSegmentId).OnDelete(DeleteBehavior.Restrict);
        // Impact per route segment (#237) and the map ask which ducts lie on a segment.
        e.HasIndex(x => x.RouteSegmentId);
        e.HasIndex(x => new { x.DuctId, x.RouteSegmentId }).IsUnique();
    }
}

internal sealed class SubductConfiguration : IEntityTypeConfiguration<Subduct>
{
    public void Configure(EntityTypeBuilder<Subduct> e)
    {
        e.ToTable("subduct", t =>
        {
            t.HasCheckConstraint("ck_subduct_occupancy", "occupancy IN ('empty', 'cable', 'blown_fibre')");
            t.HasCheckConstraint("ck_subduct_number", "number > 0");
        });
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.HasOne(x => x.Duct).WithMany().OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.DuctId, x.Number }).IsUnique();
        e.Property(x => x.Occupancy).HasDefaultValue("empty").HasSentinel(null!);
    }
}

internal sealed class CablePathConfiguration : IEntityTypeConfiguration<CablePath>
{
    public void Configure(EntityTypeBuilder<CablePath> e)
    {
        e.ToTable("cable_path", t => t.HasCheckConstraint("ck_cable_path_seq", "seq >= 0"));
        e.HasKey(x => new { x.CableId, x.Seq });
        e.HasOne<Cable>().WithMany().HasForeignKey(x => x.CableId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Subduct>().WithMany().HasForeignKey(x => x.SubductId).OnDelete(DeleteBehavior.Restrict);
        // A subduct holds one cable.
        e.HasIndex(x => x.SubductId).IsUnique();
    }
}
