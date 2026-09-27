# Arkitektur

## Översikt

```
 Webbläsare (Angular, OpenLayers, Sigma.js)
        │  OIDC (Authentik)
        ▼
 API-poddar (.NET 10, FastEndpoints)
   ├── Grafmotor i minnet  ◄── ändringsström (outbox → LISTEN/NOTIFY; senare Kafka)
   ├── Behörighet (omfång → synlighetsmasker)
   └── Kommandon → operationslogg + tillstånd (samma transaktion)
        ▼
 PostgreSQL + PostGIS (sanningskälla, RLS som sista spärr)
```

## Grafmotor i minnet

- Hela grafen (~5–10 M noder/kanter) laddas i varje API-podd i kompakt form: heltals-id:n och adjacency i CSR-format i sammanhängande arrayer.
- Start från ögonblicksbild på några sekunder, därefter inkrementella uppdateringar från ändringsströmmen.
- Planer är tunna lager ovanpå basgrafen. Att visa en plan är basgraf + delta.
- Alla traverseringar (spårning, påverkan, grannskap) sker i minnet. Databasen används för detaljer, skrivningar och geografiska frågor.

## Skrivningar, spårbarhet och reproducerbarhet

- Alla ändringar är kommandon som valideras och skrivs som oföränderliga operationer (vem, när, varför, vad) **i samma transaktion** som tillståndstabellerna uppdateras.
- Tillståndstabellerna är bitemporala. Operationsloggen kan hashkedjas för manipulationsskydd.
- Ingen ren event sourcing: tillstånd och logg lever sida vid sida för enklare schemaändringar och felsökning.
- Läsningar loggas: fråga, omfång och hash av resultatet. Tillsammans med den bitemporala modellen kan man återskapa vad en användare såg vid en viss tidpunkt.

## Behörighet

Datan antas vara högt klassad, och en CMDB är en aggregeringsmaskin. Behörighet gäller därför även navigering och sammanställning.

**Omfång** kombinerar flera dimensioner:

- Geografi (en eller flera polygoner)
- Objektklasser
- Attribut (t.ex. koordinater dolda)
- Planer och projekt
- Tid (slutdatum, kräver förnyelse)

Grundregeln är att allt är nekat tills något uttryckligen beviljas. Omfång beviljas med motivering och godkännande av en andra person. Integrationer är konsumenter med egna omfång, precis som människor.

**Genomförande:**

1. Grafmotorn förberäknar synlighetsmasker (bitmängder) per omfångsprofil. Kontrollen blir en bitoperation per nod.
2. Traverseringar som passerar en dold nod visar en neutral platshållare och stannar vid omfångets gräns.
3. Kartplattor genereras på servern per omfång (`ST_AsMVT`). Klienten får aldrig mer data än den får se, och inget sparas i webbläsarens lagring.
4. Postgres Row Level Security som sista spärr.
5. Objekt som korsar en polygongräns: modellen stöder både hel visning och klippning (konfigurerbart).

## Databas

- **EF Core code-first (ADR-0009).** `CmdbDbContext` i `src/database` är den enda källan till schemat. Ändringar görs i modellen och blir en genererad migrering i `src/database/Migrations`, som granskas i PR. Ett test failar om modellen ändrats utan migrering.
- Postgres-specifika delar uttrycks i modellen: PostGIS (NetTopologySuite), enums med naturlig ordning, check constraints, partiella index, `NULLS NOT DISTINCT` och genererade kolumner. SQL som EF inte kan uttrycka (till exempel RLS) läggs i en migrering med `migrationBuilder.Sql(...)`.
- `terminal` är supertyp för `port` och `conductor_end`. Subtyperna refererar `(id, kind)`, så en terminal kan bara vara en sak.
- `connection` lagras en gång per par med `a_terminal_id < b_terminal_id`.
- Alla objekt har livscykel, giltighetstid (`valid_from`/`valid_to`) och provenance (`source_system`, `external_id`, `last_confirmed_at`).
- Bulkvägar (datageneratorns `COPY`, grafmotorns laddning) använder Npgsql direkt mot tabellerna som EF äger, men ändrar aldrig schemat.

## Geografi

- Lagring i SWEREF 99 TM (EPSG:3006), så att längder och avstånd blir korrekta.
- Kartmotor: OpenLayers. Den stöder godtyckliga projektioner, WMTS, ArcGIS REST och vektorplattor.
- Bakgrundskarta från svenska källor (Lantmäteriets WMTS). Inga utländska kartlager. Kartan kapslas in i en egen komponent så att motorn går att byta.

## Drift

- Containrar byggs OpenShift-kompatibla: icke-root, godtyckligt UID, skrivbara kataloger via volymer.
- Postgres via operator (CloudNativePG/Crunchy PGO) i produktion. **Ingen distribuerad databas**, se ADR-0003.
- Designat för isolerad miljö: inga externa CDN:er, egenhostade typsnitt och kartresurser, få och välkända beroenden.
- Autentisering via ren OIDC. Authentik i POC:en, organisationens IdP i produktion.
