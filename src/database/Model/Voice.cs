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
