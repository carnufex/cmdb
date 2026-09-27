# ADR-0006: Operationslogg och bitemporal modell

**Status:** Accepterad · **Datum:** 2026-09-27

## Beslut
Oföränderlig operationslogg skrivs i samma transaktion som bitemporala tillståndstabeller. Läsningar loggas. Hashkedjning är valbar.

## Alternativ
- **Ren event sourcing.** Elegant men tung vid schemaändringar och felsökning.
- **Endast audit-triggers.** Saknar "varför" och bitemporalitet.

## Konsekvenser
Man kan svara på "hur såg nätet ut", "vad trodde vi" och "vad såg användare X" för godtyckligt datum.
