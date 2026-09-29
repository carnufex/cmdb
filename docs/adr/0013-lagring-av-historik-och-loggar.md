# ADR-0013: Lagring av historik, operationslogg och läslogg

**Status:** Föreslagen · **Datum:** 2026-09-29 · **Beslut:** #101 · **Preciserar:** ADR-0006

## Kontext
ADR-0006 säger att en oföränderlig operationslogg skrivs i samma transaktion som bitemporala tillståndstabeller och att läsningar loggas. Utredningen #84 ([docs/utredningar/84-datatillvaxt.md](../utredningar/84-datatillvaxt.md)) mätte lagringen på full skala med 5 och 10 års syntetisk historik:

- **Historik i samma tabell som nuläget** (partiella index på aktuella rader) gör snabbsöket 1,5 gånger långsammare, med p95 75 ms mot budgeten 50 ms. Kartplattor blir 2,5–3 gånger långsammare och grafens läsning av kopplingar 3 gånger, eftersom aktuella rader sprids ut bland gamla versioner.
- **Historik i separata tabeller** lämnar nulägesfrågorna helt opåverkade. Tidsresan för en kartruta tar p95 6–16 ms.
- Skrivkostnaden är 0,05–0,25 ms per ändring i båda fallen.
- **Läsloggen** tar 175 byte per anrop, 64 GB per år vid en miljon anrop per dygn. Historik och operationslogg tar cirka 270 byte per ändring, 1 GB per år vid 10 000 ändringar per dygn.

## Beslut (föreslaget)
1. **Nulägestabellerna** (`site`, `equipment`, `cable`, `connection` och så vidare) innehåller bara gällande versioner. De får `sys_from`, registreringstiden för versionen. Giltighetstiden finns redan (`valid_from`, `valid_to`).
2. **Historiken** ligger i en tabell per objekttabell (`<tabell>_history`) med samma kolumner plus `sys_to`, partitionerad per månad på `sys_to`. En ändring kopierar den gamla versionen dit i samma transaktion som uppdateringen. Tabellerna skrivs bara av kommandolagret, aldrig uppdateras.
3. **Operationsloggen** (`operation`) har en rad per kommando: vem, när, varför, kommandot som jsonb och påverkade nycklar, med valbar hashkedja. Den är partitionerad per månad. En massoperation är ett kommando.
4. **Läsloggen** (`read_log`) har en rad per läsande anrop: aktör, yta, fråga, omfång, resultathash och antal rader, aldrig resultatet. Den är partitionerad per dygn och skrivs buffrat i batch utanför förfrågans transaktion. 90 dagar ligger i Postgres, därefter exporteras partitionerna till arkiv och tas bort.
5. **`last_confirmed_at`** versioneras inte och indexeras inte, så bekräftelser från integrationer blir HOT-uppdateringar.
6. **Tidsresa** läser nuläget (`sys_from <= t`) plus historikversionerna som gällde vid `t`, där partitionerna före `t` hoppas över. Grafen per datum laddas på samma sätt och cachas per datum, utan ögonblicksbilder till en början.

## Alternativ
- **Historik i samma tabell med partiella index.** Tidsresan blir enklare och 5 gånger snabbare, men varje nulägesfråga betalar för historiken, och snabbsöket spräcker budgeten.
- **Temporala tillägg** (till exempel `temporal_tables`). De ger samma layout som beslutet men kräver ett tillägg som CloudNativePG-imagen saknar, och de ger mindre kontroll över partitionering.
- **Läslogg i samma databas utan retention.** Den växer 60 gånger snabbare än historiken och tränger undan nuläget i cachen.

## Konsekvenser
- Nulägesfrågorna och grafladdningen behöver inte ändras.
- Kommandolagret (#23) skriver versioner, operationer och ändringsflödet (#11) i samma transaktion.
- Partitioner skapas i förväg av en bakgrundstjänst, som också kör retentionen för läsloggen.
- Snabbsöket är fortfarande den närmaste flaskhalsen, men av ett annat skäl: nätets tillväxt (#100).
