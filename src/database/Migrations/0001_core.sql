-- Core physical model: sites, locations, equipment, terminals, cables, connections.
-- Geometry is stored in SWEREF 99 TM (EPSG:3006). See docs/domanmodell.md.
--
-- Every object carries lifecycle, valid time (full bitemporality arrives in phase 3) and
-- provenance. The columns are written out per table rather than inherited so each table
-- stays a plain, indexable relation.

CREATE EXTENSION IF NOT EXISTS postgis;

CREATE TYPE lifecycle_state AS ENUM (
    'planned', 'under_construction', 'in_service', 'decommissioning', 'removed'
);

CREATE TYPE terminal_kind AS ENUM ('port', 'conductor_end');

CREATE TYPE connection_kind AS ENUM ('patch', 'splice', 'termination', 'internal');

CREATE TYPE cable_medium AS ENUM ('fiber', 'copper', 'coax', 'power');

CREATE TABLE site (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code               text NOT NULL UNIQUE,
    name               text NOT NULL,
    site_type          text NOT NULL,
    geom               geometry(Geometry, 3006) NOT NULL
                       CHECK (GeometryType(geom) IN ('POINT', 'POLYGON', 'MULTIPOLYGON')),
    attributes         jsonb NOT NULL DEFAULT '{}',
    lifecycle          lifecycle_state NOT NULL DEFAULT 'planned',
    valid_from         timestamptz NOT NULL DEFAULT now(),
    valid_to           timestamptz,
    source_system      text,
    external_id        text,
    last_confirmed_at  timestamptz,
    CHECK (valid_to IS NULL OR valid_to > valid_from)
);
CREATE INDEX site_geom_idx ON site USING gist (geom);
CREATE UNIQUE INDEX site_external_idx ON site (source_system, external_id) WHERE external_id IS NOT NULL;

CREATE TABLE location (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    site_id            bigint NOT NULL REFERENCES site (id),
    parent_id          bigint REFERENCES location (id),
    kind               text NOT NULL CHECK (kind IN ('building', 'room', 'rack', 'position')),
    name               text NOT NULL,
    rack_units         smallint CHECK (rack_units > 0),
    attributes         jsonb NOT NULL DEFAULT '{}',
    lifecycle          lifecycle_state NOT NULL DEFAULT 'planned',
    valid_from         timestamptz NOT NULL DEFAULT now(),
    valid_to           timestamptz,
    source_system      text,
    external_id        text,
    last_confirmed_at  timestamptz,
    CHECK (parent_id IS DISTINCT FROM id),
    CHECK (valid_to IS NULL OR valid_to > valid_from),
    UNIQUE NULLS NOT DISTINCT (site_id, parent_id, name)
);
CREATE INDEX location_parent_idx ON location (parent_id);

CREATE TABLE equipment_type (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    key                text NOT NULL UNIQUE,
    manufacturer       text NOT NULL,
    model              text NOT NULL,
    category           text NOT NULL,
    attribute_schema   jsonb NOT NULL DEFAULT '{}',
    port_template      jsonb NOT NULL DEFAULT '[]' CHECK (jsonb_typeof(port_template) = 'array'),
    slot_template      jsonb NOT NULL DEFAULT '[]' CHECK (jsonb_typeof(slot_template) = 'array'),
    UNIQUE (manufacturer, model)
);

CREATE TABLE equipment (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    equipment_type_id  bigint NOT NULL REFERENCES equipment_type (id),
    site_id            bigint NOT NULL REFERENCES site (id),
    location_id        bigint REFERENCES location (id),
    parent_id          bigint REFERENCES equipment (id),
    slot               text,
    name               text NOT NULL,
    attributes         jsonb NOT NULL DEFAULT '{}',
    lifecycle          lifecycle_state NOT NULL DEFAULT 'planned',
    valid_from         timestamptz NOT NULL DEFAULT now(),
    valid_to           timestamptz,
    source_system      text,
    external_id        text,
    last_confirmed_at  timestamptz,
    -- Mounted either in a location or in a slot of another piece of equipment.
    CHECK ((location_id IS NOT NULL) <> (parent_id IS NOT NULL)),
    CHECK ((parent_id IS NULL) = (slot IS NULL)),
    CHECK (parent_id IS DISTINCT FROM id),
    CHECK (valid_to IS NULL OR valid_to > valid_from)
);
CREATE INDEX equipment_site_idx ON equipment (site_id);
CREATE INDEX equipment_location_idx ON equipment (location_id);
CREATE INDEX equipment_type_idx ON equipment (equipment_type_id);
CREATE UNIQUE INDEX equipment_slot_idx ON equipment (parent_id, slot) WHERE parent_id IS NOT NULL;

-- Terminal: anything a connection can attach to. Subtypes reference (id, kind),
-- so a terminal is exactly one of port or conductor end.
CREATE TABLE terminal (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind               terminal_kind NOT NULL,
    UNIQUE (id, kind)
);

CREATE TABLE port (
    terminal_id        bigint PRIMARY KEY,
    kind               terminal_kind NOT NULL DEFAULT 'port' CHECK (kind = 'port'),
    equipment_id       bigint NOT NULL REFERENCES equipment (id),
    name               text NOT NULL,
    port_type          text NOT NULL,
    port_group         text,
    position           integer NOT NULL,
    FOREIGN KEY (terminal_id, kind) REFERENCES terminal (id, kind),
    UNIQUE (equipment_id, name)
);

CREATE TABLE cable_type (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    key                text NOT NULL UNIQUE,
    name               text NOT NULL,
    medium             cable_medium NOT NULL,
    conductor_count    integer NOT NULL CHECK (conductor_count > 0),
    color_code         text
);

CREATE TABLE cable (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    cable_type_id      bigint NOT NULL REFERENCES cable_type (id),
    code               text NOT NULL UNIQUE,
    a_site_id          bigint NOT NULL REFERENCES site (id),
    b_site_id          bigint NOT NULL REFERENCES site (id),
    geom               geometry(LineString, 3006) NOT NULL,
    length_m           double precision GENERATED ALWAYS AS (ST_Length(geom)) STORED,
    attributes         jsonb NOT NULL DEFAULT '{}',
    lifecycle          lifecycle_state NOT NULL DEFAULT 'planned',
    valid_from         timestamptz NOT NULL DEFAULT now(),
    valid_to           timestamptz,
    source_system      text,
    external_id        text,
    last_confirmed_at  timestamptz,
    CHECK (a_site_id <> b_site_id),
    CHECK (valid_to IS NULL OR valid_to > valid_from)
);
CREATE INDEX cable_geom_idx ON cable USING gist (geom);
CREATE INDEX cable_a_site_idx ON cable (a_site_id);
CREATE INDEX cable_b_site_idx ON cable (b_site_id);

CREATE TABLE conductor (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    cable_id           bigint NOT NULL REFERENCES cable (id),
    number             integer NOT NULL CHECK (number > 0),
    color              text,
    UNIQUE (cable_id, number)
);

CREATE TABLE conductor_end (
    terminal_id        bigint PRIMARY KEY,
    kind               terminal_kind NOT NULL DEFAULT 'conductor_end' CHECK (kind = 'conductor_end'),
    conductor_id       bigint NOT NULL REFERENCES conductor (id),
    side               char(1) NOT NULL CHECK (side IN ('A', 'B')),
    FOREIGN KEY (terminal_id, kind) REFERENCES terminal (id, kind),
    UNIQUE (conductor_id, side)
);

-- Undirected edge between two terminals, stored with a < b so each pair has one row.
CREATE TABLE connection (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    a_terminal_id      bigint NOT NULL REFERENCES terminal (id),
    b_terminal_id      bigint NOT NULL REFERENCES terminal (id),
    kind               connection_kind NOT NULL,
    lifecycle          lifecycle_state NOT NULL DEFAULT 'planned',
    valid_from         timestamptz NOT NULL DEFAULT now(),
    valid_to           timestamptz,
    source_system      text,
    external_id        text,
    last_confirmed_at  timestamptz,
    CHECK (a_terminal_id < b_terminal_id),
    CHECK (valid_to IS NULL OR valid_to > valid_from)
);
CREATE UNIQUE INDEX connection_current_pair_idx ON connection (a_terminal_id, b_terminal_id) WHERE valid_to IS NULL;
CREATE INDEX connection_b_idx ON connection (b_terminal_id);
