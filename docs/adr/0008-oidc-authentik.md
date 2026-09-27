# ADR-0008: OIDC med Authentik i POC:en

**Status:** Accepterad · **Datum:** 2026-09-27

## Beslut
Authentik som IdP i utveckling och demo. Applikationen använder enbart standard-OIDC, så att byte till organisationens IdP blir konfiguration. Demoanvändare och klienter skapas via Authentik blueprints (versionerade i `infra/authentik`).
