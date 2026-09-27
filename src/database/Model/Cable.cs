using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NetTopologySuite.Geometries;

namespace Cmdb.Database.Model;

/// <summary>A cable model, synced from catalog/cable-types.json.</summary>
public class CableType
{
    public long Id { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }
    public CableMedium Medium { get; set; }
    public int ConductorCount { get; set; }
    public string? ColorCode { get; set; }
}

/// <summary>A stretch between two sites or splice points.</summary>
public class Cable : Tracked
{
    public long Id { get; set; }
    public long CableTypeId { get; set; }
    public CableType CableType { get; set; } = null!;
    public required string Code { get; set; }
    public long ASiteId { get; set; }
    public Site ASite { get; set; } = null!;
    public long BSiteId { get; set; }
    public Site BSite { get; set; } = null!;

    /// <summary>Route in SWEREF 99 TM (EPSG:3006).</summary>
    public required LineString Geom { get; set; }

    /// <summary>Derived by the database from the geometry.</summary>
    public double LengthM { get; private set; }

    public string Attributes { get; set; } = "{}";
}

/// <summary>Fibre, pair or wire number N in a cable. Its two ends are terminals.</summary>
public class Conductor
{
    public long Id { get; set; }
    public long CableId { get; set; }
    public Cable Cable { get; set; } = null!;
    public int Number { get; set; }
    public string? Color { get; set; }
}

internal sealed class CableTypeConfiguration : IEntityTypeConfiguration<CableType>
{
    public void Configure(EntityTypeBuilder<CableType> e)
    {
        e.ToTable("cable_type", t => t.HasCheckConstraint("ck_cable_type_conductor_count", "conductor_count > 0"));
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.HasIndex(x => x.Key).IsUnique();
    }
}

internal sealed class CableConfiguration : IEntityTypeConfiguration<Cable>
{
    public void Configure(EntityTypeBuilder<Cable> e)
    {
        e.ConfigureTracked("cable");
        e.ToTable(t => t.HasCheckConstraint("ck_cable_distinct_ends", "a_site_id <> b_site_id"));
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.HasIndex(x => x.Code).IsUnique();
        e.HasIndex(x => x.Code, "ix_cable_code_pattern").HasOperators("text_pattern_ops").HasDatabaseName("ix_cable_code_pattern");
        e.HasIndex(x => x.Code, "ix_cable_code_trgm").HasMethod("gin").HasOperators("gin_trgm_ops").HasDatabaseName("ix_cable_code_trgm");
        e.HasOne(x => x.CableType).WithMany().OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.ASite).WithMany().HasForeignKey(x => x.ASiteId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.BSite).WithMany().HasForeignKey(x => x.BSiteId).OnDelete(DeleteBehavior.Restrict);
        e.Property(x => x.Geom).HasColumnType("geometry(LineString, 3006)");
        e.HasIndex(x => x.Geom).HasMethod("gist");
        e.Property(x => x.LengthM).HasComputedColumnSql("ST_Length(geom)", stored: true);
        e.Property(x => x.Attributes).HasColumnType("jsonb").HasDefaultValueSql("'{}'");
    }
}

internal sealed class ConductorConfiguration : IEntityTypeConfiguration<Conductor>
{
    public void Configure(EntityTypeBuilder<Conductor> e)
    {
        e.ToTable("conductor", t => t.HasCheckConstraint("ck_conductor_number", "number > 0"));
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.HasOne(x => x.Cable).WithMany().OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => new { x.CableId, x.Number }).IsUnique();
    }
}
