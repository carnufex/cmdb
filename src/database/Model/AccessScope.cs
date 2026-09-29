using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NetTopologySuite.Geometries;

namespace Cmdb.Database.Model;

/// <summary>
/// An access scope (ADR-0007, #22): area × site types × attributes × plans × time. Everything is denied until a
/// scope grants it. Granted with a reason and approved by a second person.
/// </summary>
public class AccessScope
{
    public required string Key { get; set; }
    public required string Name { get; set; }

    /// <summary>Where the scope applies, in EPSG:3006. Null means the whole network.</summary>
    public Geometry? Area { get; set; }

    /// <summary>Site types the scope shows; empty means all.</summary>
    public string[] SiteTypes { get; set; } = [];

    /// <summary>Attribute keys removed from answers, e.g. serialNumber; "coordinates" hides positions.</summary>
    public string[] HiddenAttributes { get; set; } = [];

    /// <summary>Plans (#24) the scope may see; empty means production only.</summary>
    public string[] Plans { get; set; } = [];

    /// <summary>
    /// Objects crossing the area's edge: "whole" shows a cable when either end is inside, "clip" only when both
    /// are (and clips its geometry in tiles).
    /// </summary>
    public string CrossingMode { get; set; } = "whole";

    /// <summary>Identity provider groups the scope is granted to.</summary>
    public string[] Groups { get; set; } = [];

    /// <summary>
    /// Database roles that read the database directly with this scope (ADR-0012): integrations, reports, export.
    /// Row-level security limits them to what the scope shows. The API does not use it.
    /// </summary>
    public string[] DbRoles { get; set; } = [];

    /// <summary>After this the scope grants nothing until renewed.</summary>
    public DateTimeOffset? ValidTo { get; set; }

    public required string Reason { get; set; }
    public required string GrantedBy { get; set; }
    public required string ApprovedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>What a scope shows, materialised so that SQL, the graph engine and RLS agree (#22).</summary>
public class ScopeSite
{
    public required string ScopeKey { get; set; }
    public long SiteId { get; set; }
}

public class ScopeCable
{
    public required string ScopeKey { get; set; }
    public long CableId { get; set; }
}

public class ScopeCircuit
{
    public required string ScopeKey { get; set; }
    public long CircuitId { get; set; }
}

public class ScopeService
{
    public required string ScopeKey { get; set; }
    public long ServiceId { get; set; }
}

internal sealed class AccessScopeConfiguration : IEntityTypeConfiguration<AccessScope>
{
    public void Configure(EntityTypeBuilder<AccessScope> e)
    {
        e.ToTable("access_scope", t =>
        {
            t.HasCheckConstraint("ck_access_scope_two_persons", "approved_by <> granted_by");
            t.HasCheckConstraint("ck_access_scope_crossing", "crossing_mode IN ('whole', 'clip')");
            t.HasCheckConstraint("ck_access_scope_area", "area IS NULL OR GeometryType(area) IN ('POLYGON', 'MULTIPOLYGON')");
        });
        e.HasKey(x => x.Key);
        e.Property(x => x.Area).HasColumnType("geometry(Geometry, 3006)");
        e.Property(x => x.CrossingMode).HasDefaultValue("whole").HasSentinel("");
        e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
    }
}

internal sealed class ScopeSiteConfiguration : IEntityTypeConfiguration<ScopeSite>
{
    public void Configure(EntityTypeBuilder<ScopeSite> e)
    {
        e.ToTable("scope_site");
        e.HasKey(x => new { x.ScopeKey, x.SiteId });
        e.HasOne<AccessScope>().WithMany().HasForeignKey(x => x.ScopeKey).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => x.SiteId);
    }
}

internal sealed class ScopeCableConfiguration : IEntityTypeConfiguration<ScopeCable>
{
    public void Configure(EntityTypeBuilder<ScopeCable> e)
    {
        e.ToTable("scope_cable");
        e.HasKey(x => new { x.ScopeKey, x.CableId });
        e.HasOne<AccessScope>().WithMany().HasForeignKey(x => x.ScopeKey).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => x.CableId);
    }
}

internal sealed class ScopeCircuitConfiguration : IEntityTypeConfiguration<ScopeCircuit>
{
    public void Configure(EntityTypeBuilder<ScopeCircuit> e)
    {
        e.ToTable("scope_circuit");
        e.HasKey(x => new { x.ScopeKey, x.CircuitId });
        e.HasOne<AccessScope>().WithMany().HasForeignKey(x => x.ScopeKey).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => x.CircuitId);
    }
}

internal sealed class ScopeServiceConfiguration : IEntityTypeConfiguration<ScopeService>
{
    public void Configure(EntityTypeBuilder<ScopeService> e)
    {
        e.ToTable("scope_service");
        e.HasKey(x => new { x.ScopeKey, x.ServiceId });
        e.HasOne<AccessScope>().WithMany().HasForeignKey(x => x.ScopeKey).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => x.ServiceId);
    }
}
