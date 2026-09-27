using System.Text.Json;

namespace Cmdb.Catalog;

/// <summary>
/// An equipment model as defined in <c>catalog/equipment-types/*.json</c>. A new model is a catalog entry,
/// not a migration. See docs/domanmodell.md#typkatalogen.
/// </summary>
public sealed record EquipmentType(
    string Key,
    string Manufacturer,
    string Model,
    string Category,
    int? RackUnits,
    Panel Panel,
    IReadOnlyList<PortTemplate> Ports,
    IReadOnlyList<SlotTemplate>? Slots,
    JsonElement Attributes)
{
    public IReadOnlyList<SlotTemplate> SlotList => Slots ?? [];
}

/// <summary>The front panel as a grid of port cells.</summary>
public sealed record Panel(int Rows, int Columns);

/// <summary>
/// One port, or a numbered run of ports when <see cref="Range"/> is set. <c>{n}</c> in the name is the port
/// number and <c>{slot}</c> the slot the equipment sits in (for cards).
/// </summary>
public sealed record PortTemplate(
    string Name,
    string Type,
    string? Group,
    IReadOnlyList<int> At,
    IReadOnlyList<int>? Range = null,
    PortLayout Layout = PortLayout.Row);

/// <summary>How a run of ports is laid out from its first cell.</summary>
public enum PortLayout
{
    /// <summary>Left to right on one row.</summary>
    Row,

    /// <summary>Top to bottom in one column.</summary>
    Column,

    /// <summary>Two rows, odd numbers on top: 1 above 2, 3 above 4, as on most switches.</summary>
    Zigzag,
}

public sealed record SlotTemplate(string Name, IReadOnlyList<string> Accepts);

/// <summary>A concrete port generated from a template.</summary>
public sealed record Port(string Name, string Type, string? Group, int Position, int Row, int Column);

/// <summary>A cable model from <c>catalog/cable-types.json</c>. Medium is fiber, copper, coax or power.</summary>
public sealed record CableType(string Key, string Name, string Medium, int ConductorCount, string? ColorCode = null);
