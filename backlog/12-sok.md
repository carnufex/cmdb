---
title: Snabbsök över alla objekt
labels: type:feature,area:backend,fas-1
milestone: Fas 1 – Motor
---
## Acceptanskriterier
- [ ] Sök på namn, id och attribut över alla objekttyper (pg_trgm eller index i minnet, beslut dokumenteras)
- [ ] Rankning: exakta träffar först, sedan objekttyp och närhet
- [ ] p95 < 50 ms i full skala
- [ ] Förberett för omfångsfiltrering
