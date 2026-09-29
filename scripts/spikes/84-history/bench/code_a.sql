\set n random(1, 20000)
SELECT id, code FROM a_site WHERE code LIKE 'RAD-' || lpad(:n::text, 5, '0') || '%' AND sys_to IS NULL LIMIT 20;
