# ADR-0008: OIDC med homelabbets Authentik

**Status:** Accepterad · **Datum:** 2026-09-27 (reviderad samma dag, se #42)

## Beslut
Authentik är IdP i utveckling och demo. Vi använder den befintliga instansen i Rosenvalls homelab (`https://authentik.rosenvall.se`) och kör ingen egen. Klient (`cmdb-web`, publik med PKCE), grupper och syntetiska demoanvändare definieras i homelabbets GitOps-blueprint `apps-cmdb.yaml`. Demolösenordet kommer från Bitwarden via ExternalSecret.

Applikationen använder enbart standard-OIDC, så ett byte till organisationens IdP är konfiguration (`OIDC_AUTHORITY`, `Auth:Authority`).

## Alternativ
- **Egen Authentik i compose** (ursprunglig plan). Gav en andra IdP att drifta och en konfiguration som skiljer sig från demomiljön.

## Konsekvenser
- Lokal utveckling kräver nätåtkomst till `authentik.rosenvall.se`. Automatiska tester påverkas inte, eftersom de signerar egna tokens.
- Åtkomst till applikationen styrs av cmdb-grupperna i homelabbets Authentik.
