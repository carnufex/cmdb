namespace Cmdb.Database.Provenance;

/// <summary>
/// An object's attributes in the shape a source record keeps them (#215, ADR-0019): one key per attribute, with the
/// object's own attributes as <c>attributes.&lt;key&gt;</c>. The import records what a source reported with it, and the
/// API compares it with the object as it is now, so the two can never drift apart.
/// </summary>
public static class ReportedValues
{
    /// <summary>The object types that carry source records.</summary>
    public static readonly IReadOnlyList<string> ObjectTypes = ["site", "location", "equipment", "cable", "circuit", "service"];

    /// <summary>The attributes recorded per object type, besides <c>attributes.&lt;key&gt;</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Attributes = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["site"] = ["code", "name", "siteType", "lifecycle", "position"],
        ["location"] = ["name", "kind", "rackUnits", "lifecycle", "placement"],
        ["equipment"] = ["name", "type", "lifecycle", "placement"],
        ["cable"] = ["code", "type", "lifecycle", "ends", "route"],
        ["circuit"] = ["code", "layer", "lifecycle", "ends"],
        ["service"] = ["code", "name", "type", "lifecycle"],
    };

    /// <summary>The prefix of an object's own attributes.</summary>
    public const string AttributePrefix = "attributes.";

    /// <summary>A jsonb expression with the values of the object aliased <paramref name="alias"/> in a table of <paramref name="objectType"/>.</summary>
    public static string Sql(string objectType, string alias)
    {
        var a = alias;
        var own = $"(SELECT coalesce(jsonb_object_agg('{AttributePrefix}' || k, v), '{{}}') FROM jsonb_each({a}.attributes) AS own(k, v))";
        return objectType switch
        {
            "site" => $"""
                (jsonb_build_object('code', {a}.code, 'name', {a}.name, 'siteType', {a}.site_type, 'lifecycle', {a}.lifecycle::text,
                    'position', jsonb_build_array(ST_X({a}.geom), ST_Y({a}.geom))) || {own})
                """,
            "location" => $"""
                (jsonb_build_object('name', {a}.name, 'kind', {a}.kind, 'rackUnits', {a}.rack_units, 'lifecycle', {a}.lifecycle::text,
                    'placement', jsonb_build_object('site', {a}.site_id, 'parent', {a}.parent_id)) || {own})
                """,
            "equipment" => $"""
                (jsonb_build_object('name', {a}.name, 'type', (SELECT et.key FROM equipment_type et WHERE et.id = {a}.equipment_type_id),
                    'lifecycle', {a}.lifecycle::text,
                    'placement', jsonb_build_object('site', {a}.site_id, 'location', {a}.location_id, 'parent', {a}.parent_id,
                        'slot', {a}.slot, 'rackPosition', {a}.rack_position)) || {own})
                """,
            "cable" => $"""
                (jsonb_build_object('code', {a}.code, 'type', (SELECT ct.key FROM cable_type ct WHERE ct.id = {a}.cable_type_id),
                    'lifecycle', {a}.lifecycle::text, 'ends', jsonb_build_array({a}.a_site_id, {a}.b_site_id),
                    'route', md5(ST_AsBinary({a}.geom))) || {own})
                """,
            "circuit" => $"""
                jsonb_build_object('code', {a}.code, 'layer', {a}.layer::text, 'lifecycle', {a}.lifecycle::text,
                    'ends', jsonb_build_array({a}.a_terminal_id, {a}.b_terminal_id))
                """,
            "service" => $"""
                (jsonb_build_object('code', {a}.code, 'name', {a}.name, 'type', {a}.service_type, 'lifecycle', {a}.lifecycle::text) || {own})
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(objectType), objectType, "No source records for this object type."),
        };
    }

    /// <summary>The table an object type lives in.</summary>
    public static string Table(string objectType) =>
        ObjectTypes.Contains(objectType) ? objectType : throw new ArgumentOutOfRangeException(nameof(objectType), objectType, null);
}
