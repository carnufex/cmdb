-- Logical layer: channels, circuits across layers and the services they carry.
-- A circuit is an ordered path of terminals (circuit_hop) in one layer and may ride on
-- circuits in lower layers (circuit_dependency). Impact analysis walks
-- terminal -> circuit_hop -> circuit -> circuit_dependency (upwards) -> service_circuit.

CREATE TYPE circuit_layer AS ENUM ('physical', 'transmission', 'logical');

CREATE TYPE channel_kind AS ENUM ('wavelength', 'timeslot', 'vlan');

-- Capacity on a terminal: a wavelength on a line port, a timeslot, a VLAN.
CREATE TABLE channel (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    terminal_id        bigint NOT NULL REFERENCES terminal (id),
    kind               channel_kind NOT NULL,
    number             integer NOT NULL CHECK (number >= 0),
    UNIQUE (terminal_id, kind, number)
);

CREATE TABLE service (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code               text NOT NULL UNIQUE,
    name               text NOT NULL,
    service_type       text NOT NULL,
    attributes         jsonb NOT NULL DEFAULT '{}',
    lifecycle          lifecycle_state NOT NULL DEFAULT 'planned',
    valid_from         timestamptz NOT NULL DEFAULT now(),
    valid_to           timestamptz,
    source_system      text,
    external_id        text,
    last_confirmed_at  timestamptz,
    CHECK (valid_to IS NULL OR valid_to > valid_from)
);

CREATE TABLE circuit (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code               text NOT NULL UNIQUE,
    layer              circuit_layer NOT NULL,
    a_terminal_id      bigint NOT NULL REFERENCES terminal (id),
    b_terminal_id      bigint NOT NULL REFERENCES terminal (id),
    lifecycle          lifecycle_state NOT NULL DEFAULT 'planned',
    valid_from         timestamptz NOT NULL DEFAULT now(),
    valid_to           timestamptz,
    source_system      text,
    external_id        text,
    last_confirmed_at  timestamptz,
    CHECK (valid_to IS NULL OR valid_to > valid_from)
);
CREATE INDEX circuit_a_idx ON circuit (a_terminal_id);
CREATE INDEX circuit_b_idx ON circuit (b_terminal_id);

CREATE TABLE circuit_hop (
    circuit_id         bigint NOT NULL REFERENCES circuit (id),
    seq                integer NOT NULL CHECK (seq >= 0),
    terminal_id        bigint NOT NULL REFERENCES terminal (id),
    channel_id         bigint REFERENCES channel (id),
    PRIMARY KEY (circuit_id, seq)
);
CREATE INDEX circuit_hop_terminal_idx ON circuit_hop (terminal_id);

-- circuit rides on carrier (a logical circuit over physical ones, a wavelength over a fibre path).
CREATE TABLE circuit_dependency (
    circuit_id         bigint NOT NULL REFERENCES circuit (id),
    carrier_id         bigint NOT NULL REFERENCES circuit (id),
    PRIMARY KEY (circuit_id, carrier_id),
    CHECK (circuit_id <> carrier_id)
);
CREATE INDEX circuit_dependency_carrier_idx ON circuit_dependency (carrier_id);

CREATE TABLE service_circuit (
    service_id         bigint NOT NULL REFERENCES service (id),
    circuit_id         bigint NOT NULL REFERENCES circuit (id),
    PRIMARY KEY (service_id, circuit_id)
);
CREATE INDEX service_circuit_circuit_idx ON service_circuit (circuit_id);
