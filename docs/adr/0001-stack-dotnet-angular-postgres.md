# ADR-0001: .NET, Angular och PostgreSQL

**Status:** Accepterad · **Datum:** 2026-09-27

## Kontext
Prestanda är avgörande. Rust diskuterades. Organisationen förvaltar .NET, Angular och Postgres.

## Beslut
.NET 10 (FastEndpoints, vertical slices), Angular och PostgreSQL + PostGIS.

## Alternativ
- **Rust.** Högre råprestanda men svag förvaltningsbarhet i organisationen. Flaskhalsen i en CMDB ligger sällan i språket utan i datamodell, frågemönster och rendering.
- **.NET.** Tillräcklig prestanda med en grafmotor i minnet, välkänd stack och enkel överlämning.

## Konsekvenser
Prestandan måste komma från arkitekturen (ADR-0002) och disciplin kring budgetar, inte från språkval.
