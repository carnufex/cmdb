---
title: Solution-skelett: .NET 10-API och Angular-app
labels: type:task,area:backend,area:frontend,fas-0,status:ready
milestone: Fas 0 – Grund
---
## Bakgrund
Grunden som allt annat byggs på. Se ADR-0001.

## Acceptanskriterier
- [ ] `src/api`: .NET 10, FastEndpoints, vertical slices-struktur (`Features/<Feature>/...`) med ett hälsoendpoint som exempel
- [ ] `src/web`: Angular med standalone-komponenter och signals, tomt appskal
- [ ] `tests/`: enhetstestprojekt och integrationstestprojekt (Testcontainers + PostGIS)
- [ ] Dockerfile per app, OpenShift-kompatibel (icke-root, godtyckligt UID)
- [ ] README uppdaterad med byggkommandon
