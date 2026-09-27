using Cmdb.Database;
using Cmdb.Database.Model;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cmdb.Api.Features.Preferences;

public sealed record PreferencesResponse(string Theme);

public sealed record UpdatePreferencesRequest(string Theme);

public sealed class UpdatePreferencesValidator : Validator<UpdatePreferencesRequest>
{
    public UpdatePreferencesValidator() => RuleFor(r => r.Theme).Must(t => t is "dark" or "light").WithMessage("Theme is dark or light.");
}

/// <summary>The signed-in user's preferences. Defaults apply until something is saved.</summary>
public sealed class GetPreferencesEndpoint(CmdbDbContext db) : EndpointWithoutRequest<PreferencesResponse>
{
    public override void Configure() => Get("/me/preferences");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var subject = User.FindFirst("sub")?.Value ?? "";
        var theme = await db.UserPreferences.Where(p => p.Subject == subject).Select(p => p.Theme).SingleOrDefaultAsync(ct);
        await Send.OkAsync(new PreferencesResponse(theme ?? "dark"), ct);
    }
}

public sealed class UpdatePreferencesEndpoint(CmdbDbContext db) : Endpoint<UpdatePreferencesRequest, PreferencesResponse>
{
    public override void Configure() => Put("/me/preferences");

    public override async Task HandleAsync(UpdatePreferencesRequest req, CancellationToken ct)
    {
        var subject = User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(subject))
        {
            ThrowError("The token has no subject.");
        }

        var preference = await db.UserPreferences.SingleOrDefaultAsync(p => p.Subject == subject, ct);
        if (preference is null)
        {
            preference = new UserPreference { Subject = subject };
            db.UserPreferences.Add(preference);
        }
        preference.Theme = req.Theme;
        preference.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(new PreferencesResponse(preference.Theme), ct);
    }
}
