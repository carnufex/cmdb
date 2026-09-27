# ADR-0003: Ingen distribuerad databas

**Status:** Accepterad · **Datum:** 2026-09-27

## Kontext
Målmiljön är OpenShift. Distribuerade databaser (Citus, CockroachDB, YugabyteDB) övervägdes.

## Beslut
En Postgres-primär med synkrona repliker via operator. Lässkalning sker i grafmotorn.

## Motivering
- Datamängden är liten (tiotals GB inklusive historik).
- Reservationer och konfliktkontroll kräver strikt konsistens.
- Grafnavigering innebär många korsande joins, som blir nätverksanrop när datan är utspridd.
- Tyngre drift ger inget värde i den här skalan.

## Konsekvenser
Distribution sker i läsledet (poddar) och i händelseflödet (Kafka senare), inte i sanningskällan.
