# ADR-0021: Integrationskonton med egen roll och eget omfång

**Status:** Föreslagen · **Datum:** 2026-10-10 · **Beslut:** #248 · **Issue:** #217

## Kontext
Integrationer är konsumenter med egna omfång (arkitektur.md), och avstämningen körs under integrationens omfång (ADR-0020). I dag ger grupperna både roll och omfång: `cmdb-full` är rollen som får skriva planer, och samma grupp ger omfånget *Hela nätet*. Ett tjänstekonto för en integration som ska få stämma av kan därför bara få `cmdb-full`, och då ser och skriver det hela nätet, oavsett vilket omfång det får i övrigt. ADR-0020 säger att avstämningen kräver `cmdb-full`.

`cmdb sync` (#217) kör en adapter som ett CronJob med ett tjänstekonto per integration. Kontot behöver kunna stämma av, och inget annat, inom det omfång som integrationen har beviljats.

## Alternativ
- **A. En egen roll, `cmdb-integration`, och omfånget från en egen grupp.** Rollen ger bara avstämningen (`POST` och `GET /api/reconciliations`). Läsning kräver ingen roll, bara ett omfång, som för alla. Omfånget kommer från integrationens grupp, till exempel `cmdb-integration-acme-monitor`, som ett omfång i `access_scope` med område, sitetyper och dolda attribut som för en person. Avstämningen tillåter `cmdb-full` eller `cmdb-integration`. Övriga skrivningar kräver fortfarande `cmdb-full`.
- **B. Skilj roller från omfång för alla.** `cmdb-full` slutar ge *Hela nätet*, och alla, också människor, får sitt omfång från en egen grupp. Renast, men ändrar behörigheten för alla användare och demons inloggningar.
- **C. Integrationer får `cmdb-full`.** Inget nytt, men integrationen ser och skriver hela nätet, vilket strider mot att integrationer har egna omfång.

## Beslut (förslag)
**A.**
- Rollen `cmdb-integration` tillåts på avstämningens endpoints. Den ger inte rätt att skriva eller föra in planer direkt. Avstämningen för fortfarande in den betrodda planen genom samma väg och kontroller som en människa (ADR-0020), och bara det källan är betrodd med i katalogen.
- Varje integration har en egen grupp och ett eget omfång. Omfånget beviljas med motivering och godkännande av en andra person, som andra omfång. Omfånget behöver se avstämningens egna planer (`plans`).
- Demon får ett syntetiskt omfång för referensadaptern: *Övervakning Nord*, gruppen `cmdb-integration-acme-monitor`, samma område som Region Nord.

## Konsekvenser
- ADR-0020 ändras på en punkt: avstämningen kräver `cmdb-full` eller `cmdb-integration`.
- Ett integrationskonto i identitetstjänsten är medlem i `cmdb-integration` och sin egen omfångsgrupp, aldrig i `cmdb-full`.
- En integration med ett begränsat omfång kan inte matcha, ändra eller få rapporterat något utanför det, som för en person.
- Integrationen läser det dess omfång visar, som alla inloggade. Agentkonton (`cmdb-agents`) berörs inte.
