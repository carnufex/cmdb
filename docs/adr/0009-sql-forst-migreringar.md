# ADR-0009: SQL först, versionerade SQL-migreringar och Npgsql utan ORM

**Status:** Föreslagen · **Datum:** 2026-09-27

## Kontext
Schemat bär geometri, JSONB, supertyper (terminal), bitemporalitet och senare RLS. Grafmotorn laddas med bulkläsning och datageneratorn skriver med `COPY`. Arkitekturen kräver få och välkända beroenden.

## Beslut
- Schemat skrivs som rena SQL-filer, `NNNN_beskrivning.sql`, inbäddade i projektet `Cmdb.Database`.
- En liten egen migrator applicerar filerna i ordning, var och en i en egen transaktion, under ett advisory lock. Applicerade versioner och kontrollsummor sparas i `schema_migrations`. En ändrad, redan applicerad fil är ett fel.
- Dataåtkomst sker med Npgsql direkt (kommandon, binär `COPY`). Ingen ORM.

## Alternativ
- **EF Core med migreringar.** Bekvämt för CRUD, men döljer SQL där prestanda och PostGIS/RLS-detaljer avgör, och migreringarna genereras i stället för att granskas.
- **DbUp / Evolve.** Fungerar, men ger lite utöver ~100 rader egen kod och är ytterligare ett beroende.

## Konsekvenser
- Migreringar granskas som SQL i PR:er.
- Mappning från rader till objekt skrivs för hand där den behövs. Blir det för mycket kan Dapper läggas till utan att ändra beslutet om SQL först.
- Migreringar körs automatiskt i utvecklingsläge och som separat steg (`--migrate`) i drift.
