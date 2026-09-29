\set u random(1, 50)
INSERT INTO spike_read_log (actor, surface, request, scopes, result_hash, rows)
VALUES ('agent-' || :u, 'search', jsonb_build_object('q', 'RAD-00' || :u, 'limit', 10), ARRAY['hela-natet'],
        sha256(('result' || :u || clock_timestamp())::bytea), 10);
