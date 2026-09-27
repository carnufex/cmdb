# ADR-0004: OpenLayers och SWEREF 99 TM

**Status:** Accepterad · **Datum:** 2026-09-27

## Kontext
Målorganisationen använder ArcGIS-lager och svenska kartkällor i SWEREF 99 TM. Utländska kartlager är inte tillåtna.

## Beslut
OpenLayers som kartmotor. Lagring i EPSG:3006. Bakgrundskarta från Lantmäteriets WMTS.

## Alternativ
- **MapLibre GL.** Modern WebGL-rendering men enbart Web Mercator, som är olämpligt för analys särskilt i norr.
- **ArcGIS Maps SDK.** Kompatibelt men licensierat och slutet.
- **OpenLayers.** Godtyckliga projektioner, WMTS, ArcGIS REST, vektorplattor och WebGL-lager.

## Konsekvenser
Kartan kapslas in i en Angular-komponent så att motorn går att byta.
