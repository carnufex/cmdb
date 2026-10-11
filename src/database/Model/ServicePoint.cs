using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NetTopologySuite.Geometries;

namespace Cmdb.Database.Model;

/// <summary>
/// A service point (#292, ADR-0021): an address or customer the network is to reach, such as a house, a block of flats, a
/// business or a mast. It is not a site: it has no equipment or locations and is not in the graph. Fibres are sized and nodes
/// placed after them, and when it is connected it names the site, and the port if known, that serves it.
/// </summary>
public class ServicePoint
{
    public long Id { get; set; }
    public required string Code { get; set; }
    public string? Name { get; set; }
    public string? Address { get; set; }

    /// <summary>A type in the catalog's <c>service-point-types.json</c>.</summary>
    public required string TypeKey { get; set; }

    /// <summary>Homes or premises behind the point: a block of flats has one per flat. Fibres are sized by it.</summary>
    public int Units { get; set; } = 1;

    /// <summary><see cref="ServicePointStatus"/>: planned, ordered or connected.</summary>
    public string Status { get; set; } = ServicePointStatus.Planned;

    /// <summary>Point in SWEREF 99 TM (EPSG:3006).</summary>
    public required Point Geom { get; set; }

    public string? SourceSystem { get; set; }
    public string? ExternalId { get; set; }

    /// <summary>The site that serves it, once connected.</summary>
    public long? SiteId { get; set; }

    /// <summary>The port that serves it, when known.</summary>
    public long? TerminalId { get; set; }

    public string Attributes { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class ServicePointStatus
{
    public const string Planned = "planned";
    public const string Ordered = "ordered";
    public const string Connected = "connected";

    public static readonly IReadOnlyList<string> All = [Planned, Ordered, Connected];
}

internal sealed class ServicePointConfiguration : IEntityTypeConfiguration<ServicePoint>
{
    public void Configure(EntityTypeBuilder<ServicePoint> e)
    {
        e.ToTable("service_point", t =>
        {
            t.HasCheckConstraint("ck_service_point_status", "status IN ('planned', 'ordered', 'connected')");
            t.HasCheckConstraint("ck_service_point_units", "units >= 1");
        });
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.Property(x => x.Geom).HasColumnType("geometry(Point, 3006)");
        e.Property(x => x.Attributes).HasColumnType("jsonb").HasDefaultValueSql("'{}'");
        e.Property(x => x.Units).HasDefaultValue(1);
        e.Property(x => x.Status).HasDefaultValue(ServicePointStatus.Planned);
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        e.HasIndex(x => x.Code).IsUnique();
        e.HasIndex(x => x.Code, "ix_service_point_code_trgm").HasMethod("gin").HasOperators("gin_trgm_ops").HasDatabaseName("ix_service_point_code_trgm");
        e.HasIndex(x => x.Address, "ix_service_point_address_trgm").HasMethod("gin").HasOperators("gin_trgm_ops").HasDatabaseName("ix_service_point_address_trgm");
        e.HasIndex(x => x.Geom).HasMethod("gist");
        e.HasIndex(x => new { x.SourceSystem, x.ExternalId }).IsUnique().HasFilter("external_id IS NOT NULL");
        e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.SetNull);
        e.HasOne<Port>().WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.SetNull);
        e.HasIndex(x => x.SiteId);
    }
}
