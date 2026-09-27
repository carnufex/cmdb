using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmdb.Database.Model;

// Logical layer: channels, circuits across layers and the services they carry. A circuit is an ordered
// path of terminals (CircuitHop) in one layer and may ride on circuits in lower layers
// (CircuitDependency). Impact analysis walks terminal -> hop -> circuit -> dependency (upwards) -> service.

public enum CircuitLayer
{
    Physical,
    Transmission,
    Logical,
}

public enum ChannelKind
{
    Wavelength,
    Timeslot,
    Vlan,
}

/// <summary>Capacity on a terminal: a wavelength on a line port, a timeslot, a VLAN.</summary>
public class Channel
{
    public long Id { get; set; }
    public long TerminalId { get; set; }
    public Terminal Terminal { get; set; } = null!;
    public ChannelKind Kind { get; set; }
    public int Number { get; set; }
}

public class Service : Tracked
{
    public long Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string ServiceType { get; set; }
    public string Attributes { get; set; } = "{}";
}

public class Circuit : Tracked
{
    public long Id { get; set; }
    public required string Code { get; set; }
    public CircuitLayer Layer { get; set; }
    public long ATerminalId { get; set; }
    public Terminal ATerminal { get; set; } = null!;
    public long BTerminalId { get; set; }
    public Terminal BTerminal { get; set; } = null!;
    public List<CircuitHop> Hops { get; } = [];
}

public class CircuitHop
{
    public long CircuitId { get; set; }
    public Circuit Circuit { get; set; } = null!;
    public int Seq { get; set; }
    public long TerminalId { get; set; }
    public Terminal Terminal { get; set; } = null!;
    public long? ChannelId { get; set; }
    public Channel? Channel { get; set; }
}

/// <summary>The circuit rides on the carrier: a logical circuit over physical ones, a wavelength over a fibre path.</summary>
public class CircuitDependency
{
    public long CircuitId { get; set; }
    public Circuit Circuit { get; set; } = null!;
    public long CarrierId { get; set; }
    public Circuit Carrier { get; set; } = null!;
}

public class ServiceCircuit
{
    public long ServiceId { get; set; }
    public Service Service { get; set; } = null!;
    public long CircuitId { get; set; }
    public Circuit Circuit { get; set; } = null!;
}

internal sealed class ChannelConfiguration : IEntityTypeConfiguration<Channel>
{
    public void Configure(EntityTypeBuilder<Channel> e)
    {
        e.ToTable("channel", t => t.HasCheckConstraint("ck_channel_number", "number >= 0"));
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.HasOne(x => x.Terminal).WithMany().OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => new { x.TerminalId, x.Kind, x.Number }).IsUnique();
    }
}

internal sealed class ServiceConfiguration : IEntityTypeConfiguration<Service>
{
    public void Configure(EntityTypeBuilder<Service> e)
    {
        e.ConfigureTracked("service");
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.HasIndex(x => x.Code).IsUnique();
        e.HasIndex(x => x.Code, "ix_service_code_pattern").HasOperators("text_pattern_ops").HasDatabaseName("ix_service_code_pattern");
        e.HasIndex(x => x.Code, "ix_service_code_trgm").HasMethod("gin").HasOperators("gin_trgm_ops").HasDatabaseName("ix_service_code_trgm");
        e.HasIndex(x => x.Name, "ix_service_name_trgm").HasMethod("gin").HasOperators("gin_trgm_ops").HasDatabaseName("ix_service_name_trgm");
        e.Property(x => x.Attributes).HasColumnType("jsonb").HasDefaultValueSql("'{}'");
    }
}

internal sealed class CircuitConfiguration : IEntityTypeConfiguration<Circuit>
{
    public void Configure(EntityTypeBuilder<Circuit> e)
    {
        e.ConfigureTracked("circuit");
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.HasIndex(x => x.Code).IsUnique();
        e.HasIndex(x => x.Code, "ix_circuit_code_pattern").HasOperators("text_pattern_ops").HasDatabaseName("ix_circuit_code_pattern");
        e.HasIndex(x => x.Code, "ix_circuit_code_trgm").HasMethod("gin").HasOperators("gin_trgm_ops").HasDatabaseName("ix_circuit_code_trgm");
        e.HasOne(x => x.ATerminal).WithMany().HasForeignKey(x => x.ATerminalId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.BTerminal).WithMany().HasForeignKey(x => x.BTerminalId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CircuitHopConfiguration : IEntityTypeConfiguration<CircuitHop>
{
    public void Configure(EntityTypeBuilder<CircuitHop> e)
    {
        e.ToTable("circuit_hop", t => t.HasCheckConstraint("ck_circuit_hop_seq", "seq >= 0"));
        e.HasKey(x => new { x.CircuitId, x.Seq });
        e.HasOne(x => x.Circuit).WithMany(x => x.Hops).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.Terminal).WithMany().OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.Channel).WithMany().OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => x.TerminalId);
    }
}

internal sealed class CircuitDependencyConfiguration : IEntityTypeConfiguration<CircuitDependency>
{
    public void Configure(EntityTypeBuilder<CircuitDependency> e)
    {
        e.ToTable("circuit_dependency", t => t.HasCheckConstraint("ck_circuit_dependency_not_self", "circuit_id <> carrier_id"));
        e.HasKey(x => new { x.CircuitId, x.CarrierId });
        e.HasOne(x => x.Circuit).WithMany().HasForeignKey(x => x.CircuitId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.Carrier).WithMany().HasForeignKey(x => x.CarrierId).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => x.CarrierId);
    }
}

internal sealed class ServiceCircuitConfiguration : IEntityTypeConfiguration<ServiceCircuit>
{
    public void Configure(EntityTypeBuilder<ServiceCircuit> e)
    {
        e.ToTable("service_circuit");
        e.HasKey(x => new { x.ServiceId, x.CircuitId });
        e.HasOne(x => x.Service).WithMany().OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.Circuit).WithMany().OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => x.CircuitId);
    }
}
