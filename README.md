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
scripts/init-env.sh         # skapar .env med slumpade hemligheter och skriver ut demolösenordet
docker compose up -d --build
```

Öppna http://localhost:8480. Du skickas till Authentik och loggar in som någon av demoanvändarna, alla med lösenordet `CMDB_DEMO_PASSWORD` från `.env`:

| Användare | Grupp | Tänkt omfång |
|---|---|---|
| `demo-full` | `cmdb-full` | Hela nätet |
| `demo-region` | `cmdb-region-nord` | En region (polygon) |
| `demo-projekt` | `cmdb-projekt-a` | Ett projekt |

Authentik konfigureras helt av [blueprinten](infra/authentik/blueprints/cmdb.yaml). Administratören är `akadmin` med `AUTHENTIK_BOOTSTRAP_PASSWORD`.

| Tjänst | Adress |
|---|---|
| Webb | http://localhost:8480 |
| API | http://localhost:8481 (`/health`, `/health/ready`, allt annat under `/api`) |
| Authentik | http://localhost:9000 |
| PostGIS | localhost:15432 |

Portarna styrs av `CMDB_*_PORT` och `AUTHENTIK_PORT` i `.env`. API:t kör migreringarna vid start i compose (`Database__MigrateOnStartup`). I drift körs de som ett separat steg: `dotnet Cmdb.Api.dll --migrate`.

### Utveckling med hot reload

Kör databasen och Authentik i Docker och apparna lokalt:

```bash
docker compose up -d db authentik-server authentik-worker
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
- [Arkitekturbeslut (ADR)](docs/adr/)

## Arbetssätt

Allt arbete sker mot GitHub-issues, både för människor och agenter. Se [CLAUDE.md](CLAUDE.md) för agentflödet och [CONTRIBUTING.md](CONTRIBUTING.md) för konventioner.

## Stack

.NET 10 (FastEndpoints, vertical slices) · Angular · PostgreSQL + PostGIS · OpenLayers · Authentik (OIDC) · OpenShift-kompatibla containrar
