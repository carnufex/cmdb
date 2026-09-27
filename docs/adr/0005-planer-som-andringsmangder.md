# ADR-0005: Planer som ändringsmängder med beroenden

**Status:** Accepterad · **Datum:** 2026-09-27

## Kontext
Projektering sker i systemet. Projekt har etapper och kan bygga på flera andra projekt. Konflikter upptäcks idag ofta först i fält.

## Beslut
Planer är ändringsmängder (operationer) med en beroendegraf (DAG). Konflikter detekteras på resursnivå via reservationer. Se regler i [domänmodellen](../domanmodell.md#planer).

## Alternativ
- **Flagga på objekt (planerad/byggd).** Klarar inte parallella projekt eller jämförelser.
- **Fri git-sammanslagning.** För komplex konfliktlösning för projektörer.
- **Ändringsmängder med konfliktfria beroenden.** Kraftfullt och begripligt.

## Konsekvenser
Grafmotorn stöder vyer som basgraf + delta. Ombyggnad av beroende planer vid införande måste vara deterministisk.
