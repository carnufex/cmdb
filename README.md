# cmdb

En proof of concept för en modern, snabb och säker CMDB för en rikstäckande telekomanläggning: siter, kablar, utrustning, portar och tjänster, med planering och projektering inbyggt.

> **All data i det här repot är syntetisk.** Nätet, organisationen och alla namn är påhittade. Riktig data eller riktiga datamodeller får aldrig checkas in.

## Varför

Befintliga CMDB-verktyg för telekom är ofta långsamma, klickintensiva och svåra att navigera. Den här POC:en visar vad som går att leverera med modern teknik:

- **Hastighet.** Spårning av en tjänst från port till slututrustning på millisekunder, i full nationell skala.
- **Helhet.** Samma objekt visas samtidigt i karta, innehållsträd, frontpanel, spårschema och grannskapsgraf.
- **Få klick.** Allt är en länk, kommandopalett, massprovisionering med mallar och mönster.
- **Planering som förstaklassfunktion.** Planer med beroenden, resursreservationer och konfliktdetektion.
- **Spårbarhet och säkerhet.** Oföränderlig operationslogg, bitemporal historik och omfångsbaserad behörighet ner på attributnivå.

## Kom igång

Krav: Docker med Compose v2.

```bash
scripts/init-env.sh         # skapar .env med slumpade hemligheter
docker compose up -d --build
```

Öppna http://localhost:8480. Inloggningen går mot homelabbets Authentik (`https://authentik.rosenvall.se`). Där finns tre syntetiska demoanvändare, alla med lösenordet i Bitwarden Secrets Manager (`CMDB_DEMO_PASSWORD`, projekt *homelab*):

| Användare | Grupp | Tänkt omfång |
|---|---|---|
| `cmdb-demo-full` | `cmdb-full` | Hela nätet |
| `cmdb-demo-region` | `cmdb-region-nord` | En region (polygon) |
| `cmdb-demo-projekt` | `cmdb-projekt-a` | Ett projekt |

Klient, grupper och användare definieras i blueprinten `apps-cmdb.yaml` i `Rosenvalls-Homelab` (`kubernetes/infrastructure/controllers/authentik-runtime/`). Endast medlemmar i cmdb-grupperna kan logga in. Egna konton ges åtkomst genom att läggas i en av grupperna.

| Tjänst | Adress |
|---|---|
| Webb | http://localhost:8480 |
| API | http://localhost:8481 (`/health`, `/health/ready`, allt annat under `/api`) |
| PostGIS | localhost:15432 |

Portarna styrs av `CMDB_*_PORT` i `.env`. API:t kör migreringarna vid start i compose (`Database__MigrateOnStartup`). I drift körs de som ett separat steg: `dotnet Cmdb.Api.dll --migrate`.

### Vad finns i appen

- **Karta** över hela nätet i SWEREF 99 TM. Nav och aggregering syns direkt, accessiter och små kablar från zoom 5. Färg betyder status.
- **Snabbsök** (Ctrl+K eller `/`) över siter, utrustning (även serienummer och IP), kablar, tjänster och kretsar. Ett val öppnar objektet och flyger kartan dit.
- **Objektpaneler** som staplas till höger med brödsmulor. Allt ligger i URL:en (`?p=site:42,equipment:7`), så bakåt och framåt i webbläsaren och delbara länkar fungerar. Varje referens är en länk med hover-kort.
  - **Site:** utrustning per rack, kablar till grannsiter och påverkan.
  - **Utrustning:** frontpanel ritad ur portmallen, vad varje port är kopplad till, kort i slotar och attribut.
  - **Kabel:** ändar, ledare i bruk, kretsar genom kabeln och vilka tjänster som berörs om den kapas.
  - **Tjänst och krets:** vägen hopp för hopp genom lagren.
- **Redigering direkt i panelen** för namn och livscykel på siter och utrustning (gruppen `cmdb-full`).
- **Mörkt och ljust tema**, sparat i användarprofilen.

### Utveckling med hot reload

Kör databasen i Docker och apparna lokalt:

```bash
docker compose up -d db
export PGPASSWORD=<CMDB_DB_PASSWORD från .env>
dotnet watch --project src/api          # http://localhost:5080, migrerar vid start
cd src/web && npx ng serve              # http://localhost:4200, proxar /api till 5080
```

## Syntetisk data

`src/datagen` genererar ett deterministiskt, påhittat nät (samma seed ger alltid samma nät) och laddar det med binär `COPY`:

```bash
export PGPASSWORD=<CMDB_DB_PASSWORD från .env>
dotnet run --project src/datagen -c Release -- --scale full --seed 1 --reset   # small | medium | full
dotnet run --project src/datagen -c Release -- --scale full --dry-run          # bara räkna och visa fingeravtryck
```

Topologin är ett maskat stamnät mellan nav, aggregeringsringar och accessgrenar inom en grov kontur av Sverige. Den följer inga verkliga nät och alla namn är koder. Full skala (seed 1) ger:

| Objekt | Antal |
|---|---|
| Siter | 40 000 |
| Utrustningar | 224 707 |
| Portar | 5 011 935 |
| Kablar | 42 771 |
| Ledare | 1 300 364 |
| Kopplingar | 3 010 766 |
| Kretsar (fysiska, transmission, logiska) | 117 025 |
| Tjänster | 65 681 |

Genereringen tar ungefär en sekund och laddningen under två minuter på en utvecklingsmaskin, inklusive kontroll av alla främmande nycklar.

## Bygga och testa

Krav: .NET SDK 10, Node 24.15+ och Docker (för integrationstester och containrar).

```bash
dotnet build Cmdb.slnx
dotnet test --solution Cmdb.slnx          # enhet + integration mot PostGIS (Testcontainers)

cd src/web
npm ci
npx ng serve                              # http://localhost:4200
npx ng test --watch=false

docker build -f src/api/Dockerfile -t cmdb-api .
docker build -t cmdb-web src/web
```

Innan en PR körs hela kedjan (format, bygg, test, lint och valfritt containerbygge) med samma skript som en framtida CI-runner ska använda:

```bash
scripts/verify.sh            # --images bygger även containrarna
```

### Schemaändringar

Schemat ägs av EF Core-modellen i `src/database` (ADR-0009). Ändra modellen och generera en migrering:

```bash
dotnet tool restore
dotnet ef migrations add <Namn> --project src/database --output-dir Migrations
```

Migreringarna körs vid start i utvecklingsläge och med `dotnet Cmdb.Api.dll --migrate` i drift. Gör aldrig ändringar direkt i databasen.

| Katalog | Innehåll |
|---|---|
| `src/api` | .NET 10-API, FastEndpoints, en mapp per feature under `Features/` |
| `src/web` | Angular-app (standalone-komponenter, signals) |
| `src/database` | EF Core-modell och migreringar |
| `src/catalog` | Typkatalogen: laddning, validering, portexpansion och synk |
| `tests/Cmdb.Api.Tests` | Enhetstester |
| `tests/Cmdb.Api.IntegrationTests` | Integrationstester mot riktig PostGIS |

Båda containrarna kör som icke-root och fungerar med godtyckligt UID (OpenShift). API:t lyssnar på 8080 och exponerar `/health` och `/health/ready`.

## Dokumentation

- [Plan](docs/plan.md): vision, demoscenarier, faser och avgränsningar
- [Domänmodell](docs/domanmodell.md)
- [Arkitektur](docs/arkitektur.md): grafmotor, säkerhet och drift
- [UX-principer](docs/ux.md)
- [Agenter och MCP](docs/agenter.md)
- [Drift och leveransflöde](docs/drift.md): demomiljön på https://cmdb.rosenvall.se, deploy, demodata och rollback
- [Arkitekturbeslut (ADR)](docs/adr/)

## Arbetssätt

Allt arbete sker mot GitHub-issues, både för människor och agenter. Se [CLAUDE.md](CLAUDE.md) för agentflödet och [CONTRIBUTING.md](CONTRIBUTING.md) för konventioner.

## Stack

.NET 10 (FastEndpoints, vertical slices) · Angular · PostgreSQL + PostGIS · OpenLayers · Authentik (OIDC) · OpenShift-kompatibla containrar
