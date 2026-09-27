using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmdb.Database.Model;

/// <summary>Per-user settings kept server side, never in browser storage (docs/arkitektur.md#behörighet).</summary>
public class UserPreference
{
    /// <summary>The OIDC subject (<c>sub</c>) of the user.</summary>
    public required string Subject { get; set; }

    /// <summary>dark or light.</summary>
    public string Theme { get; set; } = "dark";

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class UserPreferenceConfiguration : IEntityTypeConfiguration<UserPreference>
{
    public void Configure(EntityTypeBuilder<UserPreference> e)
    {
        e.ToTable("user_preference", t => t.HasCheckConstraint("ck_user_preference_theme", "theme IN ('dark', 'light')"));
        e.HasKey(x => x.Subject);
        e.Property(x => x.Theme).HasDefaultValue("dark").HasSentinel("");
        e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
    }
}
