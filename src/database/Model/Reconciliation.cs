using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmdb.Database.Model;

/// <summary>
/// One run of reconciliation (#216, ADR-0020): a source's data compared with cmdb. What differed went into plans, one
/// brought into production at once for the attributes the source is trusted with and one for review; the rest is the
/// report.
/// </summary>
public class Reconciliation
{
    public long Id { get; set; }
    public required string SourceSystem { get; set; }

    /// <summary>The account that ran it: the integration's, or a person's.</summary>
    public required string RunBy { get; set; }
    public bool DryRun { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public double ElapsedMs { get; set; }

    /// <summary>The plan with changes for review, if there were any.</summary>
    public long? ReviewPlanId { get; set; }

    /// <summary>The plan brought into production without review, if there were trusted changes.</summary>
    public long? AppliedPlanId { get; set; }

    /// <summary>Counts per object type, deviations and errors as JSON (ReconciliationReport in the API).</summary>
    public string Report { get; set; } = "{}";
}

internal sealed class ReconciliationConfiguration : IEntityTypeConfiguration<Reconciliation>
{
    public void Configure(EntityTypeBuilder<Reconciliation> e)
    {
        e.ToTable("reconciliation", t => t.HasCheckConstraint("ck_reconciliation_report", "jsonb_typeof(report) = 'object'"));
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.Property(x => x.Report).HasColumnType("jsonb").HasDefaultValueSql("'{}'");
        e.Property(x => x.StartedAt).HasDefaultValueSql("now()");
        e.HasOne<Plan>().WithMany().HasForeignKey(x => x.ReviewPlanId).OnDelete(DeleteBehavior.SetNull);
        e.HasOne<Plan>().WithMany().HasForeignKey(x => x.AppliedPlanId).OnDelete(DeleteBehavior.SetNull);
        e.HasIndex(x => new { x.SourceSystem, x.StartedAt });
    }
}
