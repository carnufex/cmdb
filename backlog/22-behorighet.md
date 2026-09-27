---
title: Behörighetsomfång: modell, masker, RLS och platshållare
labels: type:feature,area:security,area:backend,fas-3
milestone: Fas 3 – Differentiering
---
## Bakgrund
Se ADR-0007 och [arkitekturen](docs/arkitektur.md#behörighet).

## Acceptanskriterier
- [ ] Omfång: polygon × objektklass × attribut × plan × tid, grundregel neka
- [ ] Omfång mappas från Authentik-grupper för de tre demoanvändarna
- [ ] Synlighetsmasker i grafmotorn. Spårning stannar vid gränsen och visar en platshållare
- [ ] Kartplattor, sök, paneler och export filtreras per omfång på servern
- [ ] Postgres RLS som sista spärr
- [ ] Konfigurerbar hantering av objekt som korsar polygongräns (hel visning eller klippning)
- [ ] Omfattande tester, inklusive negativa tester för varje yta
