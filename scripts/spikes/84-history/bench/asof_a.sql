\set x random(300000, 800000)
\set y random(6200000, 7500000)
\set d random(30, 1800)
SELECT count(*) FROM a_site
WHERE geom && ST_MakeEnvelope(:x, :y, :x + 40000, :y + 40000, 3006)
  AND tstzrange(sys_from, sys_to) @> (now() - make_interval(days => :d));
