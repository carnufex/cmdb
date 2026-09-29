\set id random(1, 224707)
BEGIN;
UPDATE equipment SET name = name WHERE id = :id;
INSERT INTO spike_operation (actor, command) VALUES ('bench', jsonb_build_object('op', 'rename', 'id', :id));
COMMIT;
