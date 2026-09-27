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
