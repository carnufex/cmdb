\set x random(300000, 800000)
\set y random(6200000, 7500000)
SELECT (SELECT sum(ST_NPoints(geom)) FROM a_site WHERE geom && ST_MakeEnvelope(:x, :y, :x + 40000, :y + 40000, 3006) AND sys_to IS NULL),
       (SELECT sum(ST_NPoints(geom)) FROM a_cable WHERE geom && ST_MakeEnvelope(:x, :y, :x + 40000, :y + 40000, 3006) AND sys_to IS NULL);
