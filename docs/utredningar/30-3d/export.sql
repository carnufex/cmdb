-- Exports the synthetic demo network for the spike #30 prototype as one JSON document.
WITH hubs AS (SELECT site_type AS key FROM site GROUP BY 1 HAVING count(*) < 500),
sites AS (
  SELECT json_agg(json_build_array(round(ST_X(ST_PointOnSurface(geom)))::int, round(ST_Y(ST_PointOnSurface(geom)))::int,
                                   (site_type IN (SELECT key FROM hubs))::int)) AS v
  FROM site WHERE lifecycle <> 'removed'),
cables AS (
  SELECT json_agg(ST_AsGeoJSON(ST_Simplify(geom, 150), 0)::json -> 'coordinates') AS v
  FROM cable WHERE lifecycle <> 'removed'),
ends AS (
  SELECT h.circuit_id, (array_agg(h.terminal_id ORDER BY h.seq))[1] AS a, (array_agg(h.terminal_id ORDER BY h.seq DESC))[1] AS b
  FROM circuit_hop h JOIN circuit c ON c.id = h.circuit_id AND c.layer IN ('transmission', 'logical') GROUP BY 1),
pairs AS (
  SELECT c.layer, least(ea.site_id, eb.site_id) AS s1, greatest(ea.site_id, eb.site_id) AS s2, count(*) AS n
  FROM ends e JOIN circuit c ON c.id = e.circuit_id
  JOIN port pa ON pa.terminal_id = e.a JOIN equipment ea ON ea.id = pa.equipment_id
  JOIN port pb ON pb.terminal_id = e.b JOIN equipment eb ON eb.id = pb.equipment_id
  WHERE ea.site_id <> eb.site_id GROUP BY 1, 2, 3),
links AS (
  SELECT p.layer, json_agg(json_build_array(round(ST_X(ST_PointOnSurface(a.geom)))::int, round(ST_Y(ST_PointOnSurface(a.geom)))::int,
                                            round(ST_X(ST_PointOnSurface(b.geom)))::int, round(ST_Y(ST_PointOnSurface(b.geom)))::int, p.n)) AS v
  FROM pairs p JOIN site a ON a.id = p.s1 JOIN site b ON b.id = p.s2 GROUP BY 1)
SELECT json_build_object(
  'sites', (SELECT v FROM sites),
  'cables', (SELECT v FROM cables),
  'links', json_build_object(
    'transmission', coalesce((SELECT v FROM links WHERE layer = 'transmission'), '[]'),
    'logical', coalesce((SELECT v FROM links WHERE layer = 'logical'), '[]')));
