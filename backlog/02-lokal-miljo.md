---
title: Lokal miljö: api och web i docker compose
labels: type:task,area:infra,fas-0
milestone: Fas 0 – Grund
---
## Mål
`docker compose up -d` startar hela stacken: PostGIS, Authentik, api och web.

## Acceptanskriterier
- [ ] api och web tillagda i `compose.yaml` med healthchecks
- [ ] Migreringar körs automatiskt vid start i utvecklingsläge
- [ ] Hot reload dokumenterat för lokal utveckling utanför Docker
- [ ] README: från klon till fungerande inloggning på under 5 minuter

Beror på #{{01}}.
