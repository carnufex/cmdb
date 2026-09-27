using Cmdb.Database;
using Cmdb.Database.Model;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cmdb.Api.Features.Objects;

/// <summary>Inline edit from the panel: only what is sent changes.</summary>
public sealed class UpdateObjectRequest
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Lifecycle { get; set; }
}

public sealed class UpdateObjectValidator : Validator<UpdateObjectRequest>
{
    public UpdateObjectValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200).When(r => r.Name is not null);
        RuleFor(r => r.Lifecycle)
            .Must(l => Enum.TryParse<LifecycleState>(l!.Replace("_", "", StringComparison.Ordinal), ignoreCase: true, out _))
            .When(r => r.Lifecycle is not null)
            .WithMessage("Unknown lifecycle.");
    }
}

/// <summary>
/// PATCH a site's or equipment's name and lifecycle. Writes go through EF; the operation log that records
/// who, when and why arrives with #23.
/// </summary>
public sealed class UpdateObjectEndpoint(CmdbDbContext db) : Endpoint<UpdateObjectRequest>
{
    public override void Configure()
    {
        Patch("/sites/{id}", "/equipment/{id}");
        // Interim until access scopes (#22): only fully authorised users may write.
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(UpdateObjectRequest req, CancellationToken ct)
    {
        var isSite = HttpContext.Request.Path.Value!.Contains("/sites/", StringComparison.Ordinal);
        Tracked? entity = isSite
            ? await db.Sites.SingleOrDefaultAsync(s => s.Id == req.Id, ct)
            : await db.Equipment.SingleOrDefaultAsync(e => e.Id == req.Id, ct);
        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (req.Name is not null)
        {
            switch (entity)
            {
                case Site site:
                    site.Name = req.Name;
                    break;
                case Database.Model.Equipment equipment:
                    equipment.Name = req.Name;
                    break;
                default:
                    break;
            }
        }
        if (req.Lifecycle is not null)
        {
            entity.Lifecycle = Enum.Parse<LifecycleState>(req.Lifecycle.Replace("_", "", StringComparison.Ordinal), ignoreCase: true);
        }
        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
