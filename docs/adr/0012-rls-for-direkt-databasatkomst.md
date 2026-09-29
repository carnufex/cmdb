# ADR-0012: Var Postgres RLS gäller

**Status:** Föreslagen · **Datum:** 2026-09-29 · **Beslut:** #96 · **Ändrar:** punkten om RLS i ADR-0007

## Kontext
ADR-0007 säger att behörighetsomfång tillämpas som synlighetsmasker i grafmotorn, med Postgres RLS som sista spärr. Steg 1 i #22 tillämpar omfången på alla läsytor i API:t, i grafmotorn och i MCP, med negativa tester per yta och demoanvändare.

Steg 2 införde RLS med FORCE på `site`, `equipment`, `cable`, `circuit` och `service`. I demon gick snabbsök från p95 47 ms till 27 755 ms och kartplattor från 46 ms till 7 054 ms. Under RLS får Postgres bara flytta in leakproof-villkor i indexsökningar före policyn. `LIKE`, trigramlikhet och PostGIS-operatorerna är inte leakproof, så frågorna faller tillbaka på sekventiella genomsökningar oavsett policyns innehåll och oavsett användarens omfång. RLS är avstängt igen (migrationen `DisableRowLevelSecurity`).

## Alternativ
- **A. Ingen RLS.** API:t och grafmotorn är enda spärren. Enklast och snabbast. Ingen spärr för den som läser databasen direkt.
- **B. RLS för begränsade användare.** Obegränsade användare och systemarbete kör med `BYPASSRLS`. Begränsade användare får sekundlånga sökningar och kartplattor. LEAKPROOF-omslag kring operatorerna mildrar det, men kräver superanvändare, är svåra att underhålla och kan läcka data via felmeddelanden.
- **C. RLS för direkt databasåtkomst.** API:t kör som en roll med `BYPASSRLS` och tillämpar omfången i applikationen och grafmotorn (steg 1). RLS gäller för roller som läser databasen utan att gå via API:t: integrationer, rapporter, export och läsrepliker.

## Förslag
**C.** Avsikten i ADR-0007 är att ingen konsument får mer än sitt omfång. Utanför API:t säkerställer RLS det. Innanför API:t gör en enda kodväg per yta det, tillsammans med en anslutningspool utan omfång som nekar allt och negativa tester för varje yta. Prestandabudgeten gäller API:t och påverkas inte.

## Konsekvenser
- En roll med `BYPASSRLS` för API:t och en roll per direktkonsument, båda som managed roles i CloudNativePG (homelabbets `database.yaml`).
- Policyerna från steg 2 återinförs, men gäller bara rollerna utan `BYPASSRLS`. Direktkonsumenter får samma omfångsnycklar via `cmdb.scopes`, satt i rollens standardinställningar (`ALTER ROLE … SET`).
- API:ts omfångsfilter och tester är den primära spärren och behåller sin täckning.
