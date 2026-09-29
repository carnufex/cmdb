-- Spike #84: two layouts for bitemporal history, on a copy of the full-scale database (cmdb_spike).
--
--   A  "same table":  a_<table> holds current and closed versions; current rows have sys_to IS NULL and every
--                     index the current-state queries use is partial on that.
--   B  "separate":    <table> stays as it is (current state only, plus sys_from); closed versions go to
--                     b_<table>_history, partitioned by month of sys_to.
--
-- Closed versions are copies of random current rows closed at random times in the last five years. In A they are
-- written in random order with the current rows, the worst case for heap locality (after years of updates the
-- current rows of busy tables are scattered among old versions).
--
-- psql -v site_hist=... -v eq_hist=... -v cable_hist=... -v conn_hist=... -f setup.sql

\timing on
SET maintenance_work_mem = '1GB';
SET work_mem = '256MB';

-- Rerunnable: start from the current tables as the application has them.
ALTER TABLE site DROP COLUMN IF EXISTS sys_from;
ALTER TABLE equipment DROP COLUMN IF EXISTS sys_from;
ALTER TABLE cable DROP COLUMN IF EXISTS sys_from;
ALTER TABLE connection DROP COLUMN IF EXISTS sys_from;

CREATE OR REPLACE FUNCTION spike_versions(t regclass, n bigint)
RETURNS SETOF record LANGUAGE plpgsql AS $$
BEGIN
    -- n closed versions of random rows of t: (id, sys_from, sys_to) to join back on.
    RETURN QUERY EXECUTE format($q$
        WITH ids AS (SELECT array_agg(id) AS a FROM %s)
        SELECT (a[1 + floor(random() * cardinality(a))::int])::bigint,
               now() - make_interval(secs => r * 157680000) - make_interval(secs => random() * 31536000),
               now() - make_interval(secs => r * 157680000)
        FROM ids, LATERAL (SELECT g, random() AS r FROM generate_series(1, %s) g) s
        $q$, t, n);
END $$;

-- ---------------------------------------------------------------- A: same table, partial indexes
DROP TABLE IF EXISTS a_site, a_equipment, a_cable, a_connection;

CREATE TABLE a_site AS
SELECT * FROM (
    SELECT s.*, s.valid_from AS sys_from, NULL::timestamptz AS sys_to FROM site s
    UNION ALL
    SELECT s.*, v.sys_from, v.sys_to FROM spike_versions('site', :site_hist) AS v(id bigint, sys_from timestamptz, sys_to timestamptz)
    JOIN site s USING (id)
) x ORDER BY random();

CREATE TABLE a_equipment AS
SELECT * FROM (
    SELECT e.*, e.valid_from AS sys_from, NULL::timestamptz AS sys_to FROM equipment e
    UNION ALL
    SELECT e.*, v.sys_from, v.sys_to FROM spike_versions('equipment', :eq_hist) AS v(id bigint, sys_from timestamptz, sys_to timestamptz)
    JOIN equipment e USING (id)
) x ORDER BY random();

CREATE TABLE a_cable AS
SELECT * FROM (
    SELECT c.*, c.valid_from AS sys_from, NULL::timestamptz AS sys_to FROM cable c
    UNION ALL
    SELECT c.*, v.sys_from, v.sys_to FROM spike_versions('cable', :cable_hist) AS v(id bigint, sys_from timestamptz, sys_to timestamptz)
    JOIN cable c USING (id)
) x ORDER BY random();

CREATE TABLE a_connection AS
SELECT * FROM (
    SELECT c.*, c.valid_from AS sys_from, NULL::timestamptz AS sys_to FROM connection c
    UNION ALL
    SELECT c.*, v.sys_from, v.sys_to FROM spike_versions('connection', :conn_hist) AS v(id bigint, sys_from timestamptz, sys_to timestamptz)
    JOIN connection c USING (id)
) x ORDER BY random();

ALTER TABLE a_site ADD PRIMARY KEY (id, sys_from);
CREATE INDEX ON a_site USING gist (geom) WHERE sys_to IS NULL;
CREATE INDEX ON a_site USING gin (code gin_trgm_ops) WHERE sys_to IS NULL;
CREATE INDEX ON a_site USING gin (name gin_trgm_ops) WHERE sys_to IS NULL;
CREATE INDEX ON a_site (code text_pattern_ops) WHERE sys_to IS NULL;
CREATE INDEX ON a_site (id) WHERE sys_to IS NULL;
-- Time travel needs the geometry of every version.
CREATE INDEX a_site_geom_all ON a_site USING gist (geom, tstzrange(sys_from, sys_to));

ALTER TABLE a_equipment ADD PRIMARY KEY (id, sys_from);
CREATE INDEX ON a_equipment USING gin (name gin_trgm_ops) WHERE sys_to IS NULL;
CREATE INDEX ON a_equipment (lower(name) text_pattern_ops) WHERE sys_to IS NULL;
CREATE INDEX ON a_equipment (site_id) WHERE sys_to IS NULL;
CREATE INDEX ON a_equipment (id) WHERE sys_to IS NULL;

ALTER TABLE a_cable ADD PRIMARY KEY (id, sys_from);
CREATE INDEX ON a_cable USING gist (geom) WHERE sys_to IS NULL;
CREATE INDEX ON a_cable USING gin (code gin_trgm_ops) WHERE sys_to IS NULL;
CREATE INDEX ON a_cable (id) WHERE sys_to IS NULL;

ALTER TABLE a_connection ADD PRIMARY KEY (id, sys_from);
CREATE UNIQUE INDEX ON a_connection (a_terminal_id, b_terminal_id) WHERE sys_to IS NULL AND valid_to IS NULL;
CREATE INDEX ON a_connection (b_terminal_id) WHERE sys_to IS NULL;
CREATE INDEX ON a_connection (id) WHERE sys_to IS NULL;

-- ---------------------------------------------------------------- B: separate history, partitioned by month
DROP TABLE IF EXISTS b_site_history, b_equipment_history, b_cable_history, b_connection_history;

ALTER TABLE site ADD COLUMN IF NOT EXISTS sys_from timestamptz;
UPDATE site SET sys_from = valid_from WHERE sys_from IS NULL;
ALTER TABLE equipment ADD COLUMN IF NOT EXISTS sys_from timestamptz;
UPDATE equipment SET sys_from = valid_from WHERE sys_from IS NULL;
ALTER TABLE cable ADD COLUMN IF NOT EXISTS sys_from timestamptz;
UPDATE cable SET sys_from = valid_from WHERE sys_from IS NULL;
ALTER TABLE connection ADD COLUMN IF NOT EXISTS sys_from timestamptz;
UPDATE connection SET sys_from = valid_from WHERE sys_from IS NULL;
-- Compact after the rewrite, so B's current tables are as dense as in steady state.
VACUUM FULL ANALYZE site, equipment, cable, connection;

DO $$
DECLARE t text; m date;
BEGIN
    FOREACH t IN ARRAY ARRAY['site', 'equipment', 'cable', 'connection'] LOOP
        EXECUTE format('CREATE TABLE b_%s_history (LIKE %s, sys_to timestamptz NOT NULL) PARTITION BY RANGE (sys_to)', t, t);
        FOR m IN SELECT generate_series(date_trunc('month', now() - interval '6 years'), date_trunc('month', now()), interval '1 month')::date LOOP
            EXECUTE format('CREATE TABLE b_%s_history_%s PARTITION OF b_%s_history FOR VALUES FROM (%L) TO (%L)',
                t, to_char(m, 'YYYYMM'), t, m, (m + interval '1 month')::date);
        END LOOP;
    END LOOP;
END $$;

INSERT INTO b_site_history
SELECT s.*, v.sys_to FROM spike_versions('site', :site_hist) AS v(id bigint, sys_from timestamptz, sys_to timestamptz)
JOIN site s USING (id);
UPDATE b_site_history SET sys_from = sys_to - make_interval(secs => random() * 31536000);
INSERT INTO b_equipment_history
SELECT e.*, v.sys_to FROM spike_versions('equipment', :eq_hist) AS v(id bigint, sys_from timestamptz, sys_to timestamptz)
JOIN equipment e USING (id);
INSERT INTO b_cable_history
SELECT c.*, v.sys_to FROM spike_versions('cable', :cable_hist) AS v(id bigint, sys_from timestamptz, sys_to timestamptz)
JOIN cable c USING (id);
UPDATE b_cable_history SET sys_from = sys_to - make_interval(secs => random() * 31536000);
INSERT INTO b_connection_history
SELECT c.*, v.sys_to FROM spike_versions('connection', :conn_hist) AS v(id bigint, sys_from timestamptz, sys_to timestamptz)
JOIN connection c USING (id);

CREATE INDEX ON b_site_history (id, sys_to);
CREATE INDEX ON b_site_history USING gist (geom, tstzrange(sys_from, sys_to));
CREATE INDEX ON b_equipment_history (id, sys_to);
CREATE INDEX ON b_cable_history (id, sys_to);
CREATE INDEX ON b_cable_history USING gist (geom, tstzrange(sys_from, sys_to));
CREATE INDEX ON b_connection_history (id, sys_to);

VACUUM (ANALYZE) a_site, a_equipment, a_cable, a_connection,
    b_site_history, b_equipment_history, b_cable_history, b_connection_history;

SELECT 'VACUUM FULL ANALYZE ' || c.oid::regclass FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
WHERE i.inhparent IN ('b_site_history'::regclass, 'b_cable_history'::regclass) \gexec
ANALYZE b_site_history, b_cable_history;

-- Search terms: substrings of real names and codes.
DROP TABLE IF EXISTS spike_terms;
CREATE TABLE spike_terms AS
SELECT row_number() OVER () AS i, term FROM (
    SELECT substr(name, 1 + floor(random() * 4)::int, 6) AS term FROM equipment ORDER BY random() LIMIT 500
) x;
