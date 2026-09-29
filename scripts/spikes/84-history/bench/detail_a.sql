\set s random(1, 40000)
SELECT * FROM a_equipment WHERE site_id = :s AND sys_to IS NULL;
