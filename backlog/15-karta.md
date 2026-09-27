---
title: Karta: OpenLayers, Lantmäteriet och vektorplattor per omfång
labels: type:feature,area:frontend,area:backend,fas-2
milestone: Fas 2 – Linser
---
## Bakgrund
Se ADR-0004.

## Acceptanskriterier
- [ ] Angular-kartkomponent som kapslar in OpenLayers
- [ ] EPSG:3006 med Lantmäteriets WMTS som bakgrund (nedtonat lager, API-nyckel via konfiguration). Välj en produkt som inte är på väg att utgå
- [ ] Vektorplattor från API:t (`ST_AsMVT`) för siter och kablar, stil efter livscykel
- [ ] Markering i kartan synkas med övriga linser
- [ ] Inga anrop till utländska karttjänster
