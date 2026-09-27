---
title: Operationslogg, bitemporal historik och tidsresa
labels: type:feature,area:backend,area:data,fas-3
milestone: Fas 3 – Differentiering
---
## Bakgrund
Se ADR-0006.

## Acceptanskriterier
- [ ] Alla skrivningar är kommandon som ger operationer (vem, när, varför, vad) i samma transaktion som tillståndet
- [ ] Bitemporala tillståndstabeller (giltighetstid och registreringstid)
- [ ] API och UI: visa nätet per datum, i läget "verklighet" eller "vad vi visste"
- [ ] Valbar hashkedjning av loggen
- [ ] Läslogg: fråga, omfång och resultathash
