\set x random(300000, 800000)
\set y random(6200000, 7500000)
\set d random(30, 1800)
SELECT count(*) FROM (
    SELECT id FROM site WHERE geom && ST_MakeEnvelope(:x, :y, :x + 40000, :y + 40000, 3006) AND sys_from <= now() - make_interval(days => :d)
    UNION ALL
    SELECT id FROM b_site_history WHERE geom && ST_MakeEnvelope(:x, :y, :x + 40000, :y + 40000, 3006)
      AND sys_to > now() - make_interval(days => :d) AND sys_from <= now() - make_interval(days => :d)
) v;
