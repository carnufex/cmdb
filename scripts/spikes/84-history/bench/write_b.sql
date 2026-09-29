\set id random(1, 224707)
BEGIN;
INSERT INTO b_equipment_history SELECT e.*, clock_timestamp() FROM equipment e WHERE id = :id;
UPDATE equipment SET name = name, sys_from = clock_timestamp() WHERE id = :id;
INSERT INTO spike_operation (actor, command) VALUES ('bench', jsonb_build_object('op', 'rename', 'id', :id));
COMMIT;
