\set id random(1, 224707)
BEGIN;
WITH old AS (UPDATE a_equipment SET sys_to = clock_timestamp() WHERE id = :id AND sys_to IS NULL RETURNING *)
INSERT INTO a_equipment SELECT r.* FROM old, jsonb_populate_record(NULL::a_equipment, to_jsonb(old) || jsonb_build_object('sys_from', clock_timestamp(), 'sys_to', NULL)) r;
INSERT INTO spike_operation (actor, command) VALUES ('bench', jsonb_build_object('op', 'rename', 'id', :id));
COMMIT;
