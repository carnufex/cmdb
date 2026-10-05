namespace Cmdb.Database;

/// <summary>
/// Where equipment sits in its rack (#173): its lowest rack unit. Equipment without one is stacked from the bottom in
/// id order, each taking its model's height; equipment in a slot of other equipment, or without a height, has none.
/// Used by the migration that added positions and by the data generator after a load.
/// </summary>
public static class RackStacking
{
    public const int DefaultRackUnits = 42;

    public const string Backfill = """
        UPDATE equipment e SET rack_position = s.pos
        FROM (
            SELECT e.id, 1 + coalesce(sum(t.rack_units) OVER (PARTITION BY e.location_id ORDER BY e.id
                                                            ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS pos
            FROM equipment e
            JOIN equipment_type t ON t.id = e.equipment_type_id
            JOIN location l ON l.id = e.location_id AND l.kind = 'rack'
            WHERE e.parent_id IS NULL AND t.rack_units IS NOT NULL
        ) s
        WHERE s.id = e.id AND e.rack_position IS NULL AND s.pos <= 32767
        """;
}
