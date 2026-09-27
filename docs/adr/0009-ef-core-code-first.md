# ADR-0009: EF Core code-first för schema och migreringar

**Status:** Accepterad · **Datum:** 2026-09-27

## Kontext
Schemat bär geometri, JSONB, supertyper (terminal), bitemporalitet och senare RLS. Teamet använder EF Core i andra appar och känner igen mönstret. Schemaändringar ska vara spårbara: ingen ska kunna ändra databasen direkt utan att det syns i repot.

## Beslut
- **EF Core code-first.** `CmdbDbContext` i `Cmdb.Database` är den enda källan till schemat. Varje ändring är en migrering som genereras med `dotnet ef migrations add` och granskas i PR.
- Postgres-specifika delar uttrycks i modellen: PostGIS via NetTopologySuite, Postgres-enums, check constraints, partiella index, sammansatta främmande nycklar och genererade kolumner.
- Ett test failar om modellen ändrats utan migrering (`HasPendingModelChanges`).
- Migreringar körs med `Database.MigrateAsync()`, vid start i dev och som separat steg (`--migrate`) i drift. EF Core låser migreringshistoriken så att parallella poddar inte krockar.
- **Bulkvägar går förbi EF men inte förbi schemat.** Datageneratorn (binär `COPY`) och grafmotorns laddning använder Npgsql direkt mot tabellerna som EF äger. De skapar eller ändrar aldrig schema.

## Alternativ
- **Handskrivna SQL-migreringar med egen migrator** (tidigare förslag). Full kontroll över SQL, men ett okänt mönster för teamet, och modell och schema kan glida isär utan att något test märker det.
- **DbUp / Evolve.** Samma nackdel som ovan.

## Konsekvenser
- SQL som EF inte kan uttrycka (till exempel RLS-policyer i #22) läggs i migreringar med `migrationBuilder.Sql(...)`, så att även den blir spårbar.
- CRUD-slices använder `CmdbDbContext`. Prestandakritiska läsningar får använda Npgsql eller `SqlQuery` där budgeten kräver det.
