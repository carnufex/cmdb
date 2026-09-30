using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmdb.Database.Model;

/// <summary>
/// A plan (ADR-0005, #24): an ordered change set on top of production and the plans it depends on. The view of a plan
/// is production + its dependencies in dependency order + the plan itself.
/// </summary>
public class Plan
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";

    /// <summary>draft, applied or cancelled.</summary>
    public string Status { get; set; } = "draft";

    /// <summary>
    /// Set when something the plan builds on changed: a dependency was cancelled, or applying one made some of this
    /// plan's operations no longer fit. Cleared when the plan is edited.
    /// </summary>
    public string? Flag { get; set; }

    /// <summary>Bumped on every change to the plan, its operations or its dependencies; plan views are cached on it.</summary>
    public int Version { get; set; } = 1;

    public required string CreatedBy { get; set; }

    /// <summary>
    /// How the plan was made: api (the web and REST) or mcp (an agent, #64). A plan from an agent is only ever brought
    /// into production by a person.
    /// </summary>
    public string CreatedVia { get; set; } = "api";

    /// <summary>The OAuth client that created it (azp), e.g. cmdb-web, cmdb-mcp or cmdb-agents.</summary>
    public string? Client { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? AppliedBy { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}

/// <summary>An edge in the plan dependency graph: <see cref="PlanId"/> builds on <see cref="DependsOnId"/>.</summary>
public class PlanDependency
{
    public long PlanId { get; set; }
    public long DependsOnId { get; set; }
}

/// <summary>
/// One change in a plan, in order. <see cref="Kind"/> is connect, disconnect, set_lifecycle or rename, and
/// <see cref="Payload"/> holds its arguments as JSON.
/// </summary>
public class PlanOperation
{
    public long Id { get; set; }
    public long PlanId { get; set; }
    public int Seq { get; set; }
    public required string Kind { get; set; }
    public string Payload { get; set; } = "{}";
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> e)
    {
        e.ToTable("plan", t =>
        {
            t.HasCheckConstraint("ck_plan_status", "status IN ('draft', 'applied', 'cancelled')");
            t.HasCheckConstraint("ck_plan_created_via", "created_via IN ('api', 'mcp')");
        });
        e.Property(x => x.CreatedVia).HasDefaultValue("api").HasSentinel("");
        e.Property(x => x.Status).HasDefaultValue("draft").HasSentinel("");
        e.Property(x => x.Version).HasDefaultValue(1).HasSentinel(0);
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        e.HasIndex(x => x.Status);
    }
}

internal sealed class PlanDependencyConfiguration : IEntityTypeConfiguration<PlanDependency>
{
    public void Configure(EntityTypeBuilder<PlanDependency> e)
    {
        e.ToTable("plan_dependency", t => t.HasCheckConstraint("ck_plan_dependency_self", "plan_id <> depends_on_id"));
        e.HasKey(x => new { x.PlanId, x.DependsOnId });
        e.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Plan>().WithMany().HasForeignKey(x => x.DependsOnId).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => x.DependsOnId);
    }
}

internal sealed class PlanOperationConfiguration : IEntityTypeConfiguration<PlanOperation>
{
    public void Configure(EntityTypeBuilder<PlanOperation> e)
    {
        e.ToTable("plan_operation", t =>
        {
            t.HasCheckConstraint("ck_plan_operation_kind",
                "kind IN ('connect', 'disconnect', 'set_lifecycle', 'rename', 'set_attributes', 'create_site', 'create_equipment', 'create_cable')");
            t.HasCheckConstraint("ck_plan_operation_payload", "jsonb_typeof(payload) = 'object'");
        });
        e.Property(x => x.Payload).HasColumnType("jsonb").HasDefaultValueSql("'{}'");
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.PlanId, x.Seq }).IsUnique();
    }
}
