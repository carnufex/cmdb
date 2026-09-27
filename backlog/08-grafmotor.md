---
title: Grafmotor i minnet: laddning och datastruktur
labels: type:feature,area:backend,fas-1
milestone: Fas 1 – Motor
---
## Bakgrund
Se ADR-0002.

## Acceptanskriterier
- [ ] Kompakt representation: heltals-id:n, CSR-adjacency, nodtyp och livscykel som packade fält
- [ ] Laddning från databasen och från ögonblicksbild (fil). Starttid mätt i full skala
- [ ] Minnesanvändning mätt och dokumenterad i issuet
- [ ] Trådsäker läsning med versionsnummer
- [ ] BenchmarkDotNet-projekt
