---
title: Datagenerator: deterministiskt syntetiskt nät i full skala
labels: type:feature,area:data,fas-0
milestone: Fas 0 – Grund
---
## Mål
Ett realistiskt men uppenbart syntetiskt nät för att bevisa prestanda.

## Acceptanskriterier
- [ ] .NET-konsolapp med `--seed` och `--scale small|medium|full`
- [ ] Samma seed ger alltid identiskt nät
- [ ] Full skala: ~40 000 siter, ~200 000 utrustningar, ~35 000 kablar, miljontals ledare och kopplingar
- [ ] Topologin följer **inte** något verkligt nät eller verklig infrastruktur. Syntetiska stamnät, ringar och grenar
- [ ] Tjänster och circuits genereras över lagren
- [ ] Bulkinsättning (COPY). Full skala laddas på några minuter
- [ ] Fiktiva namn genomgående
