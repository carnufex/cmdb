---
title: Planer: ändringsmängder med beroenden
labels: type:feature,area:backend,area:frontend,fas-3
milestone: Fas 3 – Differentiering
---
## Bakgrund
Se ADR-0005.

## Acceptanskriterier
- [ ] Skapa plan med noll eller flera beroenden (DAG, cykler förhindras)
- [ ] Vyn för en plan = produktion + beroenden + plan, i grafmotorn som basgraf + delta
- [ ] Växla mellan produktion och plan på under 100 ms
- [ ] Diff mot produktion i kartan och som lista
- [ ] Införande i produktion med ombyggnad av beroende planer. Avbruten plan markerar beroende planer
