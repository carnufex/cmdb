---
title: Databasschema v1: plats, utrustning, terminaler, kablar
labels: type:feature,area:data,area:backend,fas-0,status:ready
milestone: Fas 0 – Grund
---
## Bakgrund
Se [domänmodellen](docs/domanmodell.md).

## Acceptanskriterier
- [ ] Tabeller: site, location, equipment_type, equipment, port, cable_type, cable, conductor, terminal, connection
- [ ] Terminal som supertyp för port och ledarände
- [ ] Geometri i EPSG:3006 med spatiala index
- [ ] JSONB-attribut på equipment, validerade mot typens JSON Schema i applikationen
- [ ] Livscykelkolumn och giltighetstid förberedda (full bitemporalitet kommer i fas 3)
- [ ] Provenance-kolumner (source_system, external_id, last_confirmed_at)
- [ ] Migreringar versionerade, integrationstester mot PostGIS
