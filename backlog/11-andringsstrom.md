---
title: Ändringsström: outbox till grafmotorn
labels: type:feature,area:backend,fas-1
milestone: Fas 1 – Motor
---
## Acceptanskriterier
- [ ] Outbox-tabell skrivs i samma transaktion som ändringen
- [ ] LISTEN/NOTIFY väcker grafmotorn, som applicerar ändringar i ordning
- [ ] Flera API-instanser hålls konsistenta (testat med två instanser)
- [ ] Gränssnitt som tillåter byte till Kafka senare
