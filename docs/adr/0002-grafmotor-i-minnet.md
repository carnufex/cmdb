# ADR-0002: Grafmotor i minnet

**Status:** Accepterad · **Datum:** 2026-09-27

## Kontext
Spårning och påverkansanalys är traverseringar över miljontals noder. Rekursiva SQL-frågor ger för höga svarstider i full skala.

## Beslut
Varje API-podd håller hela grafen i minnet (CSR, heltals-id:n). Postgres är sanningskällan. Uppdateringar via ändringsström.

## Alternativ
- **Rekursiva CTE:er i Postgres.** Enkelt, men långsamt i djup och bredd.
- **Apache AGE / Neo4j.** Ny drift- och kompetensbörda, och sämre kontroll över behörighet per nod.
- **Grafmotor i minnet.** Snabbast, full kontroll, ~1–2 GB per podd.

## Konsekvenser
- Starttid och minne per podd måste mätas.
- Konsistens mellan databas och graf hanteras via outbox och versionsnummer.
- Planer och behörighet implementeras som lager och masker i motorn.
