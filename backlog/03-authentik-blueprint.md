---
title: Authentik-blueprint: OIDC-klient och demoanvändare
labels: type:task,area:security,area:infra,fas-0
milestone: Fas 0 – Grund
---
## Mål
Authentik konfigureras helt från kod (se ADR-0008).

## Acceptanskriterier
- [ ] Blueprint i `infra/authentik/blueprints` skapar OIDC-provider och applikation för cmdb (PKCE för web)
- [ ] Tre demoanvändare med grupper som senare mappas till omfång: fullt behörig, regional (polygon), projektbegränsad
- [ ] API validerar JWT och exponerar användarens grupper
- [ ] Web loggar in via OIDC och visar inloggad användare
