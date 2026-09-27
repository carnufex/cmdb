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
docker compose up -d        # PostGIS + Authentik (api/web läggs till i backloggen)
```

| Tjänst | Adress |
|---|---|
| Authentik | http://localhost:9000 |
| PostGIS | localhost:5432 |

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
