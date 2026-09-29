# Utredning #84: datatillväxt med historik, operationslogg och läslogg

**Datum:** 2026-09-29 · **Issue:** #84 · **Leder till:** ADR-0013 (*Föreslagen*), beslut i #101

## Fråga
Blir lösningen ohållbar efter några år, när nätet växer 25–50 % per år och varje ändring och läsning loggas (ADR-0006)? Vad blir flaskhalsen, och hur långt bort är den?

## Kort svar
- **Historiken är ingen flaskhals, om den ligger i egna tabeller.** Med nuläget och historiken i samma tabell blir snabbsöket 50 % långsammare redan efter fem års trolig ändringstakt. Snabbsöket har redan liten marginal, så det räcker för att spräcka budgeten. Med historiken i separata tabeller, partitionerade per månad, påverkas nulägesfrågorna inte alls. Då växer historiken med 1 GB per år vid trolig takt och 10 GB per år vid hög.
- **Läsloggen blir störst.** Den är 175 byte per anrop, alltså 64 GB per år vid en miljon anrop per dygn. Den behöver en egen partitionerad tabell med retention och arkiv.
- **Snabbsöket är den närmaste flaskhalsen, och den kommer från att nätet växer, inte från loggningen.** I demon ligger p95 redan på 47–50 ms mot budgeten 50 ms (#59). Vid dubbla datamängden, alltså efter 2–3 år med 25–50 % tillväxt per år, behövs ett eget sökindex (#100).
- **Grafen i minnet klarar sig i 5–7 år.** Vid 50 % tillväxt per år tar den 5,6 GB efter fem år och 35 GB efter tio. Den bör omprövas (ADR-0002/0003) kring år 5–7 vid hög tillväxt.

## Antaganden

| Takt | Ändringar per dygn | Läsningar per dygn |
|---|---|---|
| Låg | 1 000 | 10 000 |
| Trolig | 10 000 | 1 000 000 (agenter via MCP) |
| Hög | 100 000 | 10 000 000 |

**Fördelning av ändringar:**

| Tabell | Andel |
|---|---|
| Kopplingar (`connection`) | 50 % |
| Portar och övrigt | 25 % |
| Utrustning | 15 % |
| Siter | 5 % |
| Kablar | 5 % |

Varje ändring ger en historikrad (stängd version), en operationsrad per kommando och en rad i ändringsflödet, som rensas efter 7 dagar (#78). Bekräftelser från integrationer (`last_confirmed_at`) skapar inga versioner.

## Metod
Mätningen gjordes på en kopia av databasen i full skala (40 000 siter, 225 000 utrustningar, 3,0 M kopplingar, 7,6 M terminaler) med två scheman för historiken:

- **A, samma tabell:** nuläge och stängda versioner i samma tabell. Nuläget har `sys_to IS NULL`, och alla index som nulägesfrågorna använder är partiella på det. Raderna skrevs i slumpordning, vilket är värsta fallet efter år av uppdateringar, när aktuella rader ligger utspridda bland gamla versioner.
- **B, separat historik:** nulägestabellerna är oförändrade (plus `sys_from`). Stängda versioner ligger i `b_<tabell>_history`, partitionerad per månad på `sys_to`.

Scenario 1 motsvarar fem år med trolig takt: 18,25 M versioner, varav 9,1 M kopplingar, 2,7 M utrustningar, 0,9 M siter och 0,9 M kablar. Scenario 2 har dubbla mängden, vilket motsvarar tio år med trolig takt eller ett år med hög.

Varje fråga kördes med pgbench, 4 klienter i 15 s, och värdena är p50/p95 i ms. Skripten ligger i `scripts/spikes/84-history/` och körs om med `setup.sql` och `bench/run.sh`.

- Sökfrågan är `ILIKE '%term%'` på utrustningsnamn. Det är trigramsökningens värsta fall, inte appens hela snabbsök, så det är kvoten som ska jämföras.
- Kartfrågan läser siter och kablar i en ruta på 40 × 40 km.

## Resultat

### Nulägesfrågor
B ger samma siffror som dagens tabeller, eftersom de inte ändras.

| Fråga | Nuläge / B | A, 5 år | A, 10 år |
|---|---|---|---|
| Sök, trigram på utrustningsnamn | 43,0 / 48,0 | 65,9 / 74,9 | 64,5 / 75,3 |
| Kartplatta, siter och kablar | 0,25 / 0,44 | 0,43 / 1,14 | 0,48 / 1,35 |
| Sitedetalj, utrustning på en site | 0,06 / 0,09 | 0,08 / 0,12 | 0,08 / 0,12 |
| Kodprefix, site | 0,07 / 0,10 | 0,07 / 0,11 | – |
| Grafladdning, läsning av alla kopplingar | 48–57 ms | 144–188 ms | 138–195 ms |

- **A:** sökningen blir 1,5 gånger långsammare, kartplattan 2,5–3 gånger och grafens läsning av kopplingar 3 gånger. Orsaken är att bitmap- och sekventiella läsningar besöker fler sidor när de aktuella raderna är utspridda. De partiella indexen hjälper indexstorleken, men inte heapen.
- **B:** siffrorna är oförändrade, oavsett hur mycket historik som finns.

### Tidsresa
Frågan räknar siter i en ruta på 40 km vid ett slumpat datum de senaste fem åren.

| | 5 år | 10 år |
|---|---|---|
| A (ett GiST-index på `(geom, tstzrange)`) | 0,41 / 1,18 | 0,94 / 3,07 |
| B (nuläge `UNION ALL` partitioner efter datumet) | 2,72 / 6,20 | 4,04 / 15,50 |

B är långsammare men ligger väl inom budgeten för en kartvy. Grafen per datum kräver en full laddning i båda alternativen: nuläget plus versionerna som gällde vid datumet. I dag tar en laddning 9,0 s från databasen och 0,3 s från ögonblicksbild.

### Skrivningar
Varje skrivning uppdaterar en utrustning och skriver en operationsrad, i en transaktion.

| | p50 / p95 |
|---|---|
| Utan historik | 0,73 / 0,94 |
| A (stäng version, ny version) | 0,80 / 1,19 |
| B (kopiera till historik, uppdatera nuläget) | 0,76 / 1,15 |

Historiken kostar ungefär 0,05–0,25 ms per skrivning i båda alternativen.

### Storlek per rad, med index

| | Byte |
|---|---|
| Historikversion, koppling | 125 |
| Historikversion, utrustning | 223 |
| Historikversion, site | 262 |
| Historikversion, kabel | 366 |
| Operationsrad | 116 |
| Läslogg: aktör, yta, fråga, omfång, resultathash, antal | 175 |

- Med fördelningen ovan blir det ungefär **270 byte per ändring**, historik och operationsrad tillsammans.
- En läsloggrad skriven synkront tar 0,7 / 0,9 ms, och databasen klarar cirka 5 000 per sekund med fyra klienter. Buffrad och skriven i batch blir kostnaden per anrop försumbar.

## Tillväxt per år

| | Låg | Trolig | Hög |
|---|---|---|---|
| Historik och operationslogg | 0,1 GB | 1 GB | 10 GB |
| Läslogg utan retention | 0,6 GB | 64 GB | 640 GB |

Nuläget är 3,0 GB i dag, och grafen tar 605 MB i minnet.

| År | 25 % per år | 50 % per år |
|---|---|---|
| 5 | 9 GB, graf 1,8 GB | 23 GB, graf 4,6 GB |
| 10 | 28 GB, graf 5,6 GB | 173 GB, graf 35 GB |

Vid 50 % i tio år tar grafladdningen från databasen cirka 8 minuter och från ögonblicksbild 17 s.

## Hypoteserna

1. **Läsloggen blir störst först.** *Stämmer.* Vid trolig takt är den 60 gånger större än historiken. Den ska ligga i en egen tabell, partitionerad per dygn, och skrivas buffrat i batch utanför förfrågans transaktion. 90 dagar ligger kvar i Postgres, och äldre partitioner exporteras till arkiv och tas bort. Loggen sparar fråga, omfång och resultathash, inte resultatet.
2. **Historik i samma tabell gör nulägesfrågorna långsammare.** *Stämmer, och det är den verkliga arkitekturfällan.* Alternativ A spräcker snabbsökets budget. B har ingen sådan effekt.
3. **Bekräftelser får inte skapa versioner.** *Stämmer.* `last_confirmed_at` ska inte vara indexerad, så att uppdateringarna blir HOT och inte blåser upp index. Den versioneras inte.
4. **Massoperationer loggas som ett kommando.** *Stämmer för operationsloggen.* "Patcha 1–24" blir en operationsrad. Historiken får fortfarande en version per ändrad rad, för att tidsresan ska fungera. Det ingår i siffrorna ovan.
5. **Tidsresa kräver ögonblicksbilder.** *Behövs inte till en början.* Kartan per datum är en fråga i millisekunder. Grafen per datum är en laddning som tar lika lång tid som en vanlig laddning, i dag 9 s. Den cachas per datum och visas som ett tydligt läge med laddningsindikator. Dagliga ögonblicksbilder blir aktuella först när laddningen tar minuter (#81).
6. **Nuläget klarar sig i en Postgres-primär, men snabbsöket kan behöva ett eget index vid 5–10 gånger data.** *Delvis.* Postgres klarar storleken, men snabbsöket når budgetgränsen redan vid ungefär dubbel datamängd, eftersom marginalen i demon redan är liten. Det har blivit #100.

## Flaskhalsar i ordning

| Flaskhals | Beror på | När | Åtgärd |
|---|---|---|---|
| Snabbsöket (p95 50 ms) | Nätets storlek | Vid ungefär dubbel data: 2–3 år (25 %), 1,5–2 år (50 %) | Eget sökindex i minnet eller i en separat tjänst (#100) |
| Läsloggen | Anrop per dygn | Direkt vid agenttrafik | Partition per dygn, batchskrivning, 90 dagars retention och arkiv (ADR-0013) |
| Grafen i minnet | Nätets storlek | Över 10 GB efter cirka 7 år vid 50 % per år | Ompröva ADR-0002/0003 kring år 5; mät med #81 |
| Historiken | Ändringstakten | Ingen gräns inom överskådlig tid med B | Separata tabeller partitionerade per månad (ADR-0013) |

## Beslut som behövs innan #23 byggs
Samlade i ADR-0013 (*Föreslagen*), beslut i #101:

1. Historiken läggs i separata tabeller per objekttabell, partitionerade per månad på `sys_to` (alternativ B). Nulägestabellerna är oförändrade.
2. Operationsloggen har en rad per kommando och partitioneras per månad.
3. Läsloggen läggs i en egen tabell, partitionerad per dygn och skriven buffrat i batch. 90 dagar ligger i Postgres, därefter arkiv.
4. `last_confirmed_at` versioneras inte och indexeras inte.
5. Tidsresan läser nuläget plus historiken. Inga ögonblicksbilder till en början.
