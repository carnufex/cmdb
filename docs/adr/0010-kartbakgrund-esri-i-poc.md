# ADR-0010: Kartbakgrund från Esri i POC:en

**Status:** Accepterad · **Datum:** 2026-09-27 · Ersätter bakgrundsdelen av ADR-0004

## Kontext
ADR-0004 angav Lantmäteriets WMTS som bakgrundskarta. Lantmäteriets tjänst för topografisk webbkarta beställs via Geotorget och är avgiftsbelagd för oss, och den gamla nyckelfria tjänsten är avstängd. POC:en ska inte kosta pengar. Beslut av Christopher, se #45.

## Beslut
- Bakgrunden är Esris nyckelfria Canvas-baskartor (`World_Dark_Gray_Base` och `World_Light_Gray_Base` från `services.arcgisonline.com`) och följer appens tema. Attribueringen visas enligt Esris villkor.
- Plattorna är i Web Mercator och projiceras om i webbläsaren till SWEREF 99 TM. Lagring och alla beräkningar sker fortfarande i EPSG:3006 (ADR-0004 gäller i övrigt).
- Bakgrunden styrs av `MAP_BASEMAP` (`esri` som standard, `none`). Med `none` gör klienten inga anrop till externa karttjänster.

## Avvägningar
- **Utländskt kartlager.** Det bryter mot den ursprungliga principen "inga utländska kartlager". Esri ser bara vilket kartutsnitt som visas, aldrig nätdata eller token, och datan i POC:en är syntetisk. I en miljö med klassad data ska `MAP_BASEMAP=none` användas, eller en svensk eller egenhostad källa.
- **Omprojicering.** Web Mercator-raster som visas i SWEREF blir något mjukare och kostar lite rendering. Det räcker för en nedtonad bakgrund.

## Konsekvenser
- Byte till Lantmäteriet eller en egenhostad WMTS i SWEREF är ett nytt lager i kartkomponenten (`map-basemap.ts`) och påverkar inget annat.
