using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmdb.Database.Model;

/// <summary>
/// Someone who may call the operations agent (ADR-0015, #134): a synthetic employee with a phone number for
/// one-time codes and the identity provider groups a verified call gets, which decide its access scopes.
/// </summary>
public class VoiceCaller
{
    public required string EmployeeId { get; set; }
    public required string Name { get; set; }
    public required string Phone { get; set; }

    /// <summary>technician, contractor or noc; for the agent's tone and the ticket, not for access.</summary>
    public required string Role { get; set; }
    public string[] Groups { get; set; } = [];
}

/// <summary>The stubbed SMS gateway: messages that would have been sent, shown in the web app's agent panel.</summary>
public class VoiceSms
{
    public long Id { get; set; }
    public required string ToPhone { get; set; }
    public required string EmployeeId { get; set; }
    public required string Body { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A one-time code sent during a call: stored hashed, valid 5 minutes, 3 attempts.</summary>
public class VoiceChallenge
{
    public long Id { get; set; }
    public required string ConversationId { get; set; }
    public required string EmployeeId { get; set; }
    public required string CodeHash { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>A verified call: until it expires, the call carries the caller's groups and so their access scopes.</summary>
public class VoiceSession
{
    public required string ConversationId { get; set; }
    public required string EmployeeId { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Every tool call on the voice channel, verified or not (ADR-0015).</summary>
public class VoiceToolCall
{
    public long Id { get; set; }
    public required string ConversationId { get; set; }
    public string? EmployeeId { get; set; }
    public required string Tool { get; set; }
    public required string Outcome { get; set; }

    /// <summary>What the call was about ("site:12", "cable:4"), so the map can follow a call live (#156).</summary>
    public string? Reference { get; set; }
    public int Milliseconds { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A fault report (#135), enriched when it is created with what the network knows: affected services, redundancy,
/// equipment status. The priority is set by rules, not by the agent.
/// </summary>
public class Incident
{
    public long Id { get; set; }
    public long SiteId { get; set; }

    /// <summary>What failed, as a reference ("site:12", "equipment:34", "port:56").</summary>
    public required string Reference { get; set; }
    public required string Description { get; set; }
    public string Observations { get; set; } = "";

    /// <summary>P1, P2 or P3.</summary>
    public required string Priority { get; set; }
    public string Status { get; set; } = "open";
    public required string ConversationId { get; set; }
    public required string ReportedBy { get; set; }
    public JsonDocument Enrichment { get; set; } = JsonDocument.Parse("{}");
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When and by whom the incident was resolved in the agent panel (#161).</summary>
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
}

/// <summary>
/// A request to the service desk or IT self-service agents (ADR-0016, #151): a broken access tag, a password reset, an
/// equipment order, or a callback from a person. Everything but a callback needs a verified caller.
/// </summary>
public class ServiceRequest
{
    public long Id { get; set; }

    /// <summary>tag, password, equipment or callback.</summary>
    public required string Kind { get; set; }
    public string Status { get; set; } = "open";

    /// <summary>The verified caller; null for a callback from someone who did not verify.</summary>
    public string? EmployeeId { get; set; }
    public required string CallerName { get; set; }
    public required string Phone { get; set; }
    public required string ConversationId { get; set; }

    /// <summary>What was asked for, in one sentence.</summary>
    public required string Summary { get; set; }
    public JsonDocument Details { get; set; } = JsonDocument.Parse("{}");
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ServiceRequestConfiguration : IEntityTypeConfiguration<ServiceRequest>
{
    public void Configure(EntityTypeBuilder<ServiceRequest> e)
    {
        e.ToTable("service_request", t =>
        {
            t.HasCheckConstraint("ck_service_request_kind", "kind IN ('tag', 'password', 'equipment', 'callback')");
            t.HasCheckConstraint("ck_service_request_status", "status IN ('open', 'done')");
        });
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.Property(x => x.Status).HasDefaultValue("open").HasSentinel(null!);
        e.Property(x => x.Details).HasColumnType("jsonb");
        e.HasIndex(x => new { x.Kind, x.Status });
        e.HasIndex(x => x.CreatedAt);
    }
}

internal sealed class VoiceCallerConfiguration : IEntityTypeConfiguration<VoiceCaller>
{
    public void Configure(EntityTypeBuilder<VoiceCaller> e)
    {
        e.ToTable("voice_caller", t => t.HasCheckConstraint("ck_voice_caller_role", "role IN ('technician', 'contractor', 'noc')"));
        e.HasKey(x => x.EmployeeId);
    }
}

internal sealed class VoiceSmsConfiguration : IEntityTypeConfiguration<VoiceSms>
{
    public void Configure(EntityTypeBuilder<VoiceSms> e)
    {
        e.ToTable("voice_sms");
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.HasIndex(x => x.CreatedAt);
    }
}

internal sealed class VoiceChallengeConfiguration : IEntityTypeConfiguration<VoiceChallenge>
{
    public void Configure(EntityTypeBuilder<VoiceChallenge> e)
    {
        e.ToTable("voice_challenge");
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.HasIndex(x => new { x.ConversationId, x.EmployeeId });
    }
}

internal sealed class VoiceSessionConfiguration : IEntityTypeConfiguration<VoiceSession>
{
    public void Configure(EntityTypeBuilder<VoiceSession> e)
    {
        e.ToTable("voice_session");
        e.HasKey(x => x.ConversationId);
    }
}

internal sealed class VoiceToolCallConfiguration : IEntityTypeConfiguration<VoiceToolCall>
{
    public void Configure(EntityTypeBuilder<VoiceToolCall> e)
    {
        e.ToTable("voice_tool_call");
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.HasIndex(x => x.ConversationId);
    }
}

internal sealed class IncidentConfiguration : IEntityTypeConfiguration<Incident>
{
    public void Configure(EntityTypeBuilder<Incident> e)
    {
        e.ToTable("incident", t =>
        {
            t.HasCheckConstraint("ck_incident_priority", "priority IN ('P1', 'P2', 'P3')");
            t.HasCheckConstraint("ck_incident_status", "status IN ('open', 'closed')");
        });
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.Property(x => x.Status).HasDefaultValue("open").HasSentinel(null!);
        e.Property(x => x.Observations).HasDefaultValue("").HasSentinel(null!);
        e.Property(x => x.Enrichment).HasColumnType("jsonb");
        e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => x.SiteId);
        e.HasIndex(x => x.CreatedAt);
    }
}

/// <summary>
/// Planned work in the field (#137): digging, construction, track work. Where it intersects a cable route, the
/// proactive agent finds a risk and calls the person responsible for the network there.
/// </summary>
public class PlannedWork
{
    public long Id { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public required string Contractor { get; set; }

    /// <summary>The employee responsible for the network where the work is (a voice caller), who gets the call.</summary>
    public required string ResponsibleEmployeeId { get; set; }

    /// <summary>Where the work is, in EPSG:3006.</summary>
    public required NetTopologySuite.Geometries.Polygon Area { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
}

internal sealed class PlannedWorkConfiguration : IEntityTypeConfiguration<PlannedWork>
{
    public void Configure(EntityTypeBuilder<PlannedWork> e)
    {
        e.ToTable("planned_work", t => t.HasCheckConstraint("ck_planned_work_time", "ends_at > starts_at"));
        e.Property(x => x.Area).HasColumnType("geometry(Polygon,3006)");
        e.Property(x => x.Description).HasDefaultValue("").HasSentinel(null!);
        e.HasIndex(x => x.Area).HasMethod("gist");
    }
}
