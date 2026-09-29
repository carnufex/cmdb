\set n random(1, 20000)
SELECT id, code FROM site WHERE code LIKE 'RAD-' || lpad(:n::text, 5, '0') || '%' LIMIT 20;
