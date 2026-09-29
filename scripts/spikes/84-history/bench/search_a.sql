\set i random(1, 500)
SET enable_seqscan = off; SET enable_indexscan = off;
SELECT id, name FROM a_equipment WHERE name ILIKE (SELECT '%' || term || '%' FROM spike_terms WHERE i = :i) AND sys_to IS NULL LIMIT 20;
