using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NetTopologySuite.Geometries;

namespace Cmdb.Database.Model;

/// <summary>
/// A project (#293, ADR-0021): an area built out in stages, which are plans that build on each other (ADR-0005). The
/// project holds them together with the area and who is responsible; the plans themselves do not change.
/// </summary>
public class Project
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";

    /// <summary>Polygon in SWEREF 99 TM (EPSG:3006).</summary>
    public required Geometry Area { get; set; }

    public required string Owner { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A project's stage: a plan, in its place in the order. A plan belongs to one project at most.</summary>
public class ProjectPlan
{
    public long ProjectId { get; set; }
    public long PlanId { get; set; }
    public int Seq { get; set; }
}

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> e)
    {
        e.ToTable("project", t => t.HasCheckConstraint("ck_project_area", "GeometryType(area) IN ('POLYGON', 'MULTIPOLYGON')"));
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.Property(x => x.Area).HasColumnType("geometry(Geometry, 3006)");
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        e.HasIndex(x => x.Name).IsUnique();
        e.HasIndex(x => x.Area).HasMethod("gist");
    }
}

internal sealed class ProjectPlanConfiguration : IEntityTypeConfiguration<ProjectPlan>
{
    public void Configure(EntityTypeBuilder<ProjectPlan> e)
    {
        e.ToTable("project_plan");
        e.HasKey(x => new { x.ProjectId, x.PlanId });
        e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => x.PlanId).IsUnique();
        e.HasIndex(x => new { x.ProjectId, x.Seq }).IsUnique();
    }
}
