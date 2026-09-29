\set s random(1, 40000)
SELECT * FROM equipment WHERE site_id = :s;
