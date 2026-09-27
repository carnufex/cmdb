using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmdb.Database.Model;

/// <summary>
/// Anything a connection can attach to. Subtypes reference (id, kind), so a terminal is exactly one of
/// port or conductor end. See docs/domanmodell.md.
/// </summary>
public class Terminal
{
    public long Id { get; set; }
    public TerminalKind Kind { get; set; }
}

public class Port
{
    public long TerminalId { get; set; }
    public TerminalKind Kind { get; private set; } = TerminalKind.Port;
    public Terminal Terminal { get; set; } = null!;
    public long EquipmentId { get; set; }
    public Equipment Equipment { get; set; } = null!;
    public required string Name { get; set; }
    public required string PortType { get; set; }
    public string? PortGroup { get; set; }

    /// <summary>1-based position in the type's port template.</summary>
    public int Position { get; set; }
}

public class ConductorEnd
{
    public long TerminalId { get; set; }
    public TerminalKind Kind { get; private set; } = TerminalKind.ConductorEnd;
    public Terminal Terminal { get; set; } = null!;
    public long ConductorId { get; set; }
    public Conductor Conductor { get; set; } = null!;

    /// <summary>A or B.</summary>
    public char Side { get; set; }
}

/// <summary>Undirected edge between two terminals, stored with a &lt; b so each pair has one row.</summary>
public class Connection : Tracked
{
    public long Id { get; set; }
    public long ATerminalId { get; set; }
    public Terminal ATerminal { get; set; } = null!;
    public long BTerminalId { get; set; }
    public Terminal BTerminal { get; set; } = null!;
    public ConnectionKind Kind { get; set; }
}

internal sealed class TerminalConfiguration : IEntityTypeConfiguration<Terminal>
{
    public void Configure(EntityTypeBuilder<Terminal> e)
    {
        e.ToTable("terminal");
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.HasAlternateKey(x => new { x.Id, x.Kind });
    }
}

internal sealed class PortConfiguration : IEntityTypeConfiguration<Port>
{
    public void Configure(EntityTypeBuilder<Port> e)
    {
        e.ToTable("port", t => t.HasCheckConstraint("ck_port_kind", "kind = 'port'"));
        e.HasKey(x => x.TerminalId);
        e.Property(x => x.TerminalId).ValueGeneratedNever();
        e.Property(x => x.Kind).HasDefaultValue(TerminalKind.Port).HasSentinel((TerminalKind)(-1));
        e.HasOne(x => x.Terminal).WithOne()
            .HasForeignKey<Port>(x => new { x.TerminalId, x.Kind })
            .HasPrincipalKey<Terminal>(x => new { x.Id, x.Kind })
            .OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.Equipment).WithMany(x => x.Ports).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => new { x.EquipmentId, x.Name }).IsUnique();
    }
}

internal sealed class ConductorEndConfiguration : IEntityTypeConfiguration<ConductorEnd>
{
    public void Configure(EntityTypeBuilder<ConductorEnd> e)
    {
        e.ToTable("conductor_end", t =>
        {
            t.HasCheckConstraint("ck_conductor_end_kind", "kind = 'conductor_end'");
            t.HasCheckConstraint("ck_conductor_end_side", "side IN ('A', 'B')");
        });
        e.HasKey(x => x.TerminalId);
        e.Property(x => x.TerminalId).ValueGeneratedNever();
        e.Property(x => x.Kind).HasDefaultValue(TerminalKind.ConductorEnd).HasSentinel((TerminalKind)(-1));
        e.Property(x => x.Side).HasColumnType("char(1)");
        e.HasOne(x => x.Terminal).WithOne()
            .HasForeignKey<ConductorEnd>(x => new { x.TerminalId, x.Kind })
            .HasPrincipalKey<Terminal>(x => new { x.Id, x.Kind })
            .OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.Conductor).WithMany().OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => new { x.ConductorId, x.Side }).IsUnique();
    }
}

internal sealed class ConnectionConfiguration : IEntityTypeConfiguration<Connection>
{
    public void Configure(EntityTypeBuilder<Connection> e)
    {
        e.ConfigureTracked("connection");
        e.ToTable(t => t.HasCheckConstraint("ck_connection_ordered", "a_terminal_id < b_terminal_id"));
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.HasOne(x => x.ATerminal).WithMany().HasForeignKey(x => x.ATerminalId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.BTerminal).WithMany().HasForeignKey(x => x.BTerminalId).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => new { x.ATerminalId, x.BTerminalId }).IsUnique().HasFilter("valid_to IS NULL").HasDatabaseName("ix_connection_current_pair");
        e.HasIndex(x => x.BTerminalId);
    }
}
