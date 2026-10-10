using System.Text.Json;
using System.Text.Json.Serialization;

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

/// <summary>The front panel as a grid of port cells, and optionally as images with the ports placed on them (#214).</summary>
public sealed record Panel(
    int Rows,
    int Columns,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PanelImages? Images = null);

/// <summary>A picture of the front and, when ports sit there too, the back.</summary>
public sealed record PanelImages(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PanelImage? Front = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PanelImage? Back = null)
{
    public PanelImage? Side(string side) => side switch
    {
        PortImage.FrontSide => Front,
        PortImage.BackSide => Back,
        _ => null,
    };
}

/// <summary>
/// An image file in <c>equipment-images/</c> of the catalog. Width and height are the coordinate system the ports are
/// placed in; the image is drawn to fill it.
/// </summary>
public sealed record PanelImage(string File, int Width, int Height);

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
    PortLayout Layout = PortLayout.Row,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PortImage? Image = null);

/// <summary>
/// Where a port, or the first port of a run, sits on the panel image (#214): its top left corner and size in image
/// coordinates. The rest of a run follows the template's layout, one <see cref="Step"/> per row or column.
/// </summary>
public sealed record PortImage(string Side, IReadOnlyList<int> At, IReadOnlyList<int> Size, IReadOnlyList<int>? Step = null)
{
    public const string FrontSide = "front";
    public const string BackSide = "back";
}

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

/// <summary>A concrete port generated from a template; <see cref="Box"/> is its area on the panel image, if any.</summary>
public sealed record Port(string Name, string Type, string? Group, int Position, int Row, int Column, PortBox? Box = null);

/// <summary>A port's area on one side's panel image, in image coordinates.</summary>
public sealed record PortBox(string Side, int X, int Y, int Width, int Height)
{
    public bool Overlaps(PortBox other) =>
        Side == other.Side && X < other.X + other.Width && other.X < X + Width && Y < other.Y + other.Height && other.Y < Y + Height;
}

/// <summary>A cable model from <c>catalog/cable-types.json</c>. Medium is fiber, copper, coax or power.</summary>
/// <param name="Attributes">Optional JSON Schema for the cable's attributes (#211); without one they are free.</param>
public sealed record CableType(string Key, string Name, string Medium, int ConductorCount, string? ColorCode = null, JsonElement? Attributes = null);

/// <summary>
/// A duct model from <c>catalog/duct-types/&lt;key&gt;.json</c> (ADR-0014): its outer size and the template its subducts are
/// generated from, as ports are from a port template. A new size is a catalog entry, not a migration.
/// </summary>
public sealed record DuctType(string Key, string Name, string Manufacturer, string Model, int OuterDiameterMm, SubductTemplate Subducts);

/// <param name="ColorCode">The colour sequence the tubes follow, e.g. "IEC 60304"; tube N gets the N-th colour.</param>
public sealed record SubductTemplate(int Count, int InnerDiameterMm, string? ColorCode = null);
