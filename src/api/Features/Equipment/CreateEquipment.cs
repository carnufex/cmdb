using System.Text.Json;
using Cmdb.Catalog;
using Cmdb.Database;
using Cmdb.Database.Model;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using CatalogType = Cmdb.Catalog.EquipmentType;
using DbEquipment = Cmdb.Database.Model.Equipment;
using DbPort = Cmdb.Database.Model.Port;

namespace Cmdb.Api.Features.Equipment;

public sealed record CreateEquipmentRequest(
    string TypeKey,
    string Name,
    long? LocationId,
    long? ParentId,
    string? Slot,
    JsonElement? Attributes);

public sealed record CreatedPort(long TerminalId, string Name, int Position);

public sealed record CreateEquipmentResponse(long Id, long SiteId, IReadOnlyList<CreatedPort> Ports);

public sealed class CreateEquipmentValidator : Validator<CreateEquipmentRequest>
{
    public CreateEquipmentValidator()
    {
        RuleFor(r => r.TypeKey).NotEmpty();
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        RuleFor(r => r)
            .Must(r => (r.LocationId is not null) != (r.ParentId is not null))
            .WithName("placement")
            .WithMessage("Give either locationId or parentId and slot.");
        RuleFor(r => r.Slot).NotEmpty().When(r => r.ParentId is not null);
        RuleFor(r => r.Slot).Empty().When(r => r.ParentId is null);
    }
}

/// <summary>
/// Creates equipment in a location or in a slot of other equipment, and generates its terminals and ports
/// from the type's port template in the same save.
/// </summary>
public sealed class CreateEquipmentEndpoint(CmdbDbContext db, TypeCatalog catalog)
    : Endpoint<CreateEquipmentRequest, CreateEquipmentResponse>
{
    public override void Configure()
    {
        Post("/equipment");
        // Interim until access scopes (#22): only fully authorised users may write.
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(CreateEquipmentRequest req, CancellationToken ct)
    {
        var type = catalog.Find(req.TypeKey);
        if (type is null)
        {
            ThrowError(r => r.TypeKey, $"Unknown equipment type '{req.TypeKey}'.");
        }

        var attributes = req.Attributes is { ValueKind: not JsonValueKind.Null } a ? a : JsonDocument.Parse("{}").RootElement;
        foreach (var error in catalog.ValidateAttributes(type.Key, attributes))
        {
            AddError(r => r.Attributes, error);
        }
        ThrowIfAnyErrors();

        var siteId = req.LocationId is { } locationId
            ? await SiteOfLocationAsync(locationId, ct)
            : await SiteOfSlotAsync(req.ParentId!.Value, req.Slot!, type, ct);
        var typeId = await db.EquipmentTypes.Where(t => t.Key == type.Key).Select(t => (long?)t.Id).SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Type '{type.Key}' is not in the database; run the catalog sync.");

        var equipment = new DbEquipment
        {
            EquipmentTypeId = typeId,
            SiteId = siteId,
            LocationId = req.LocationId,
            ParentId = req.ParentId,
            Slot = req.Slot,
            Name = req.Name,
            Attributes = attributes.GetRawText(),
        };
        foreach (var port in PortExpansion.Expand(type, req.Slot))
        {
            equipment.Ports.Add(new DbPort
            {
                Terminal = new Terminal { Kind = TerminalKind.Port },
                Name = port.Name,
                PortType = port.Type,
                PortGroup = port.Group,
                Position = port.Position,
            });
        }
        db.Equipment.Add(equipment);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await Send.ResultAsync(TypedResults.Conflict(new { message = $"Slot '{req.Slot}' is already occupied." }));
            return;
        }

        var ports = equipment.Ports.OrderBy(p => p.Position).Select(p => new CreatedPort(p.TerminalId, p.Name, p.Position)).ToList();
        await Send.CreatedAtAsync($"/api/equipment/{equipment.Id}", null, new CreateEquipmentResponse(equipment.Id, siteId, ports), generateAbsoluteUrl: false, cancellation: ct);
    }

    private async Task<long> SiteOfLocationAsync(long locationId, CancellationToken ct)
    {
        var siteId = await db.Locations.Where(l => l.Id == locationId).Select(l => (long?)l.SiteId).SingleOrDefaultAsync(ct);
        if (siteId is null)
        {
            ThrowError(r => r.LocationId, $"Location {locationId} does not exist.");
        }
        return siteId.Value;
    }

    private async Task<long> SiteOfSlotAsync(long parentId, string slot, CatalogType type, CancellationToken ct)
    {
        var parent = await db.Equipment.Where(e => e.Id == parentId)
            .Select(e => new { e.SiteId, TypeKey = e.EquipmentType.Key })
            .SingleOrDefaultAsync(ct);
        if (parent is null)
        {
            ThrowError(r => r.ParentId, $"Equipment {parentId} does not exist.");
        }

        var slotTemplate = catalog.Find(parent.TypeKey)?.SlotList.FirstOrDefault(s => s.Name == slot);
        if (slotTemplate is null)
        {
            ThrowError(r => r.Slot, $"Equipment {parentId} has no slot '{slot}'.");
        }
        if (!slotTemplate.Accepts.Contains(type.Category))
        {
            ThrowError(r => r.Slot, $"Slot '{slot}' accepts {string.Join(", ", slotTemplate.Accepts)}, not {type.Category}.");
        }
        return parent.SiteId;
    }
}
