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
 PostgreSQL + PostGIS (sanningskälla, RLS för direkt databasåtkomst)
```

## Grafmotor i minnet

- Hela grafen laddas i varje API-podd i kompakt form (`src/graph`, #8). Noderna är terminalerna, med täta heltalsindex. Kanterna är kopplingar plus en kant per ledare, lagrade i CSR-format i sammanhängande arrayer. Utrustning, siter, kablar, kretsar (hopp, beroenden) och tjänster ligger i parallella arrayer.
- Grafen är oföränderlig. Läsare tar den aktuella instansen en gång per operation, och en ny version byts in atomiskt.
- Vid start läses en ögonblicksbild (fil) om dess dataversion stämmer med databasen, annars laddas grafen med binär `COPY` och en ny ögonblicksbild skrivs. `/health/ready` svarar först när grafen finns, och `GET /api/graph` visar storlek, version och laddtid.
- **Planer (#24, ADR-0005)** är tunna lager ovanpå basgrafen.
  - Att visa en plan är basgraf plus delta. `Graph.WithChanges` ger en vy som delar alla basens arrayer och bara ersätter grannlistan för de noder planen kopplar eller kopplar bort. En plan kostar alltså minne i proportion till sin storlek, inte till nätets. En kopia per plan (605 MB) ryms inte i podden.
  - Vyn är produktion, plus planens utkast till beroenden i beroendeordning, plus planen själv. Den cachas per produktionsgraf och planinnehåll (`PlanViews`). Omfångsmasker delas med basen.
  - Operationer som inte längre passar produktion (terminalen finns inte, redan kopplad, inte kopplad) hoppas över i vyn och rapporteras per operation.
  - Spårning och påverkan tar `?plan=<id>`. `GET /api/plans/{id}/view` ger diffen mot produktion med de siter den rör. Den är budgetraden *Växla vy mellan produktion och plan*.
- Alla traverseringar (spårning, påverkan, grannskap) sker i minnet. Databasen används för detaljer, skrivningar och geografiska frågor.
- Påverkansanalys (`GET /api/{cables|equipment|sites}/{id}/impact`, `src/graph/GraphImpact.cs`, #10): objektets terminaler ger de direkt drabbade kretsarna, och bredden först uppåt via beroenden nås kretsarna som rider på dem och deras tjänster. Varje tjänst får den kortaste kedjan av kretsar som når den. Grafen har härledda index utrustning → portar, kabel → ledarändar och site → utrustning; de byggs vid laddning och lagras inte i ögonblicksbilden.
- Ändringsström (#11): radtriggers på tabellerna grafen byggs av skriver den ändrade *nyckeln* (utrustning, kabel, terminal eller krets) till outboxen `graph_change` i samma transaktion, och en satsvis trigger gör `NOTIFY graph_change`, som levereras vid commit. Varje API-instans följer outboxen själv (`GraphLoadingService`, `PostgresGraphChangeFeed`): väckt av NOTIFY, eller efter högst `Graph:PollSeconds` (5 s), läser den nycklarnas aktuella rader i en REPEATABLE READ-transaktion, lappar grafens rader (`GraphData.From`, `Replace`), bygger om grafen deterministiskt och byter den atomärt. Ändringar under ombyggnaden blir nästa sats. I full skala tar en sats cirka 1,3 s och minnestoppen är cirka 1,1 GiB.
  - Vattenstämpeln är `{databas-oid}:{xid}`. En läsning tar rader från transaktioner i `[vattenstämpel, xmin)`, alltså bara avslutade transaktioner, så en transaktion som committas sent hoppas aldrig över. En långvarig transaktion håller tillbaka senare ändringar tills den är klar.
  - Ögonblicksbildens version är vattenstämpeln. Vid start läses filen och motorn kommer ikapp därifrån; en fil från en annan databas ger full laddning. Bulkladdning (datageneratorn) sätter `cmdb.bulk = on`, som stänger av triggers, och skriver en `reload`-rad som ger full omladdning. `TRUNCATE` på `connection` gör detsamma.
  - Går de lappade raderna inte ihop (en senare ändring är committad men ännu inte under horisonten) laddas grafen om i sin helhet. Skrivningar syns i grafen efter ungefär en sekund, inte i samma anrop.
  - Outboxen rensas varje timme på rader äldre än `Graph:OutboxRetentionDays` (7 dagar), under ett advisory lock så att bara en instans rensar (#78). Högsta borttagna transaktion sparas i `graph_change_pruned`. En läsare vars vattenstämpel ligger där eller under, till exempel en gammal ögonblicksbild, laddar om i sin helhet i stället för att komma ikapp.
  - `IGraphChangeFeed` (position, läs sedan vattenstämpel, vänta) är gränsen mot en framtida Kafka-källa, där vattenstämpeln blir offset.
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

### Planer: tabeller och införande

- `plan` (utkast, införd eller avbruten, flagga med skäl, version), `plan_dependency` (DAG; en cykel nekas när beroenden sätts) och `plan_operation` (ordnade operationer med jsonb: `connect`, `disconnect`, `set_lifecycle`, `rename`). Ny utrustning och nya kablar i planer kommer i ett senare steg.
- **Införande** (`POST /api/plans/{id}/apply`) kräver att beroendena redan är införda och att alla operationer passar produktion. Operationerna körs i en transaktion. En koppling blir en rad i `connection`, och en bortkoppling stänger raden (`valid_to`, livscykel *borttagen*). Ändringsflödet tar dem till grafen. Därefter kontrolleras alla utkast som bygger på planen, direkt eller indirekt, mot produktion som den blir, och de vars operationer inte längre passar flaggas.
- **Avbrott** (`POST /api/plans/{id}/cancel`) flaggar alla utkast som bygger på planen. En ändring i en flaggad plan tar bort flaggan.
- **Behörighet:** skrivningar kräver `cmdb-full`, som övriga skrivningar. Operationer får bara röra terminaler och objekt inom användarens omfång. En plan syns för omfång med `*` eller planens id i `access_scope.plans`; *Hela nätet* har `*`.
- Datageneratorn lägger in tre syntetiska planer: en patchning på första navet, avveckling av en kabel och en andra etapp som bygger på den första.

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

**Så är det byggt (#22, steg 1):**

- `access_scope` (EF-modell) har område (MultiPolygon, EPSG:3006; tomt = hela nätet), sitetyper, dolda attribut, planer (för #24), gränsläge (`whole`/`clip`), giltig till och grupper. Motivering, beviljad av och godkänd av finns också, och en check constraint kräver två olika personer. Demoomfången är syntetiska och synkas vid migrering, som typkatalogen: *Hela nätet* för `cmdb-full` och `cmdb-agents`, *Region Nord* för `cmdb-region-nord` och *Projekt A* för `cmdb-projekt-a`. Projekt A omfattar ett område på västkusten, bara radiositer och skåp, och döljer serienummer.
- Vad varje omfång visar materialiseras i `scope_site`, `scope_cable`, `scope_circuit` och `scope_service`. Kablar följer gränsläget. Kretsar följer sina två ändar (port → utrustningens site, ledarände → kabelns site på den sidan) och tjänster sina kretsar. Utrustning och portar följer sin site. Omräkningen sker i en transaktion (4,5 s i full skala) vid migrering, vid start, var femte minut och efter ändringar från ändringsflödet. Nya siter, kablar och kretsar är dolda tills dess, vilket är säkert eftersom grundregeln är att neka.
- Varje förfrågan får sina omfång från token-grupperna (`UserScope`). Alla SQL-ytor filtrerar på omfångsnycklarna: sök, sitevy, utrustning, kabel, tjänst, krets, hovringskort, avancerad sökning, grannskap, grannskapsgraf och kartplattor. Ett objekt utanför omfånget ger 404, inte 403. Dolda attribut tas bort ur svaren.
- Grafmotorn får synlighetsmasker (bool per site, kabel, krets och tjänst), byggda ur samma tabeller och cachade per grafinstans och omfångskombination. Spårningen stannar vid gränsen (`TraceEnd.Boundary`) och visar en neutral platshållare utan id eller namn. Påverkan och spårning räknar kretsar och tjänster utanför omfånget (`hiddenServices`, `hidden`) utan att nämna dem.
- MCP-verktygen går genom samma kod med agentens token. `/api/me` visar gällande omfång, och webben visar dem i verktygsfältet.
- **Postgres RLS gäller direkt databasåtkomst** (ADR-0012, #96). API:t ansluter som tabellägare, som RLS inte gäller (ingen FORCE), och tillämpar omfången själv. Roller som läser databasen direkt (integrationer, rapporter, export) listas i `access_scope.db_roles`. Policyer på `site`, `location`, `equipment`, `port`, `cable`, `conductor`, `circuit`, `circuit_hop`, `service` och `service_circuit` begränsar dem till vad deras omfång visar (`cmdb_direct_scopes()` läser `current_user`, så en sessionsinställning kan inte vidga något). Vid start får de SELECT på just de tabellerna, typkatalogerna och omfångstabellerna (`DirectAccess.GrantAsync`), inget annat. Döljer omfånget attribut får rollen inte kolumnen `attributes`, och dolda koordinater tar bort `geom`. Demon har rollen `cmdb_rapport_nord` (Region Nord). Steg 2 med RLS för API:ts egna frågor drogs tillbaka: sök och kartplattor fick sekventiella genomsökningar eftersom deras villkor inte är leakproof.
- **Gränsläge (steg 3):** `whole` visar en kabel med en ände i området i sin helhet. `clip` visar dessutom kablar som bara passerar området, och skär all kabelgeometri vid gränsen i kartplattor och spårningens rutt (`ScopeSql.CableGeometry`), om inget annat av användarens omfång visar kabeln hel. Siter och utrustning utanför är dolda i båda lägena, och kretsar visas när någon av ändarna är synlig.
- **Dolda koordinater (steg 3):** `coordinates` bland dolda attribut tar bort alla positioner: tomma kartplattor, inget x/y på site, sökträffar, avancerad sökning och grannskapsgrafen, ingen rutt i spårningen och ingen sortering efter avstånd (den skulle avslöja positioner). Webben döljer då *Visa i kartan*, och grannskapsgrafen lägger ut noderna med krafter enbart.
- MCP-verktygen har negativa tester per demoanvändare (`ScopeEdgeTests`). Påverkan på ett objekt utanför omfånget svarar som för ett objekt som inte påverkar något, samma svar som för ett id som inte finns.

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
