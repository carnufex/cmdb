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

- Hela grafen laddas i varje API-podd i kompakt form (`src/graph`, #8). Noderna är terminalerna, med täta heltalsindex. Kanterna är kopplingar plus en kant per ledare, lagrade i CSR-format i sammanhängande arrayer. Utrustning, siter, kablar, kretsar (hopp, beroenden) och tjänster ligger i parallella arrayer.
- Grafen är oföränderlig. Läsare tar den aktuella instansen en gång per operation, och en ny version byts in atomiskt.
- Vid start läses en ögonblicksbild (fil) om dess dataversion stämmer med databasen, annars laddas grafen med binär `COPY` och en ny ögonblicksbild skrivs. `/health/ready` svarar först när grafen finns, och `GET /api/graph` visar storlek, version och laddtid.
- Planer är tunna lager ovanpå basgrafen, och att visa en plan är basgraf plus delta.
- Alla traverseringar (spårning, påverkan, grannskap) sker i minnet. Databasen används för detaljer, skrivningar och geografiska frågor.
- Spårning (`GET /api/trace`, `src/graph/GraphTrace.cs`, #9): från en terminal följs signalen åt båda hållen genom patch, skarv, terminering, intern koppling och ledare tills vägen når aktiv utrustning. Finns mer än en fortsättning stannar vägen och markeras som förgrening i stället för att en väg gissas; cykler och ett tak på 2 000 hopp stoppar också. En tjänst eller krets spåras nedåt via bärarna (logisk → transmission → fysisk). Bara namnen på de terminaler som ingår hämtas från databasen, i ett batchanrop.

Uppmätt i full skala (7,6 M terminaler, 4,3 M kanter, 117 000 kretsar):

| | |
|---|---|
| Laddning från databasen | 8,6 s |
| Laddning från ögonblicksbild | 0,3 s (fil 243 MB) |
| Grafens arrayer | 232 MB (container i stabilt läge omkring 320 MB) |
| Uppslag terminal-id → nod | 0,16 µs |

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
- Bakgrundskarta i POC:en: Esris nyckelfria Canvas-kartor, omprojicerade till SWEREF 99 TM (ADR-0010). Med `MAP_BASEMAP=none` görs inga externa kartanrop, och det är läget för miljöer med klassad data. Kartan kapslas in i en egen komponent så att motorn och bakgrunden går att byta.

## Drift

- Containrar byggs OpenShift-kompatibla: icke-root, godtyckligt UID, skrivbara kataloger via volymer.
- Postgres via operator (CloudNativePG/Crunchy PGO) i produktion. **Ingen distribuerad databas**, se ADR-0003.
- Designat för isolerad miljö: inga externa CDN:er, egenhostade typsnitt och kartresurser, få och välkända beroenden.
- Autentisering via ren OIDC. Authentik i POC:en, organisationens IdP i produktion.
- Demomiljön körs i homelabbets Kubernetes via ArgoCD, med migreringarna som init-container. Se [drift.md](drift.md).
