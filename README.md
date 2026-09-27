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
cp .env.example .env        # fyll i hemligheter enligt kommentarerna
docker compose up -d --build
```

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

| Katalog | Innehåll |
|---|---|
| `src/api` | .NET 10-API, FastEndpoints, en mapp per feature under `Features/` |
| `src/web` | Angular-app (standalone-komponenter, signals) |
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
