-- Front panel grid and rack height from the type catalog, so the panel can be drawn from the database alone.
ALTER TABLE equipment_type
    ADD COLUMN panel jsonb NOT NULL DEFAULT '{"rows": 1, "columns": 1}',
    ADD COLUMN rack_units smallint CHECK (rack_units > 0);
