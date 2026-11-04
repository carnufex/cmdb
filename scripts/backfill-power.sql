-- Gives existing equipment the power draw (powerDrawW) that datagen now generates, so a demo database need not be reloaded.
-- Same numbers and formula as src/datagen/Attributes.cs. Safe to run again.
UPDATE equipment e
SET attributes = e.attributes || jsonb_build_object('powerDrawW', d.watts + (e.id % 7) - 3)
FROM equipment_type t
JOIN (VALUES
  ('acme-ar-10', 180),
  ('acme-cr-8', 1200),
  ('acme-ax-24', 30),
  ('acme-ax-48', 55),
  ('acme-ax-48p', 90),
  ('acme-cx-32', 320),
  ('acme-ix-8', 25),
  ('acme-bb-6', 140),
  ('acme-mw-1', 35),
  ('acme-rr-2', 280),
  ('acme-rr-4', 520),
  ('acme-lc-16e1', 18),
  ('acme-lc-24x', 60),
  ('acme-lc-4c', 110),
  ('acme-ot-40', 400),
  ('acme-ot-8', 150),
  ('acme-sdh-63', 120),
  ('acme-pdu-12', 10),
  ('acme-rect-48', 45)
) AS d(key, watts) ON d.key = t.key
WHERE t.id = e.equipment_type_id
  AND NOT e.attributes ? 'powerDrawW';
