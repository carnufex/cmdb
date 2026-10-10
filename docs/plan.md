# Plan

## Vision

En CMDB för en rikstäckande telekomanläggning som är snabbare, enklare och säkrare än något användarna har arbetat i tidigare, och som gör planering, spårbarhet och behörighet till kärnfunktioner i stället för tillägg.

## Principer

1. **Prestanda är en funktion.** Varje interaktion har en budget (se nedan). Brott mot budgeten är en bugg.
2. **Flera linser, en markering.** Karta, träd, frontpanel, spårschema och graf visar alltid samma markerade objekt.
3. **Få klick.** Allt är en länk. Inga kedjor av modala dialoger. Massoperationer är normalfallet.
4. **Säkerhet i navigeringen.** Behörighet gäller varje traversering, varje kartplatta och varje export, aldrig bara listvyer.
5. **Allt är spårbart och reproducerbart.** Vem, när, varför och vad användaren såg.
6. **Syntetisk data, verklig skala.** Prestanda bevisas i full nationell storlek.

## Skala att designa för

| Objekt | Storleksordning |
|---|---|
| Siter | ~40 000 |
| Kablar (koppar, fiber, el m.fl.) | ~30 000–40 000 |
| Ledare (fibrer, par, ledare) | 1–2 miljoner |
| Utrustningar | ~200 000 |
| Portar | 2–6 miljoner |
| Kopplingar | miljontals |

Totalt cirka 5–10 miljoner noder och lika många kanter. Det ryms i minnet (~1–2 GB kompakt), och det är grunden för arkitekturen.

### Tillväxt: 2× och 4× full skala (#81, #119, #121, #123)

Vid 25–50 % tillväxt per år är nätet dubbelt så stort om 2–3 år och fyra gånger så stort om 3–6 år. Datageneratorn tar `--scale 2x` och `--scale 4x` (80 000 respektive 160 000 siter). Uppmätt 2026-09-30 på en utvecklingsmaskin (32 kärnor), så tiderna är lägre än i demon:

**Grafmotorn** (`dotnet run -c Release --project tests/Cmdb.Graph.Benchmarks -- scale full 2x 4x`, utan databas). Kompakteringen gäller en installation: ny utrustning med 24 patchade portar och en ny krets över dem som rider på en befintlig krets och bär en tjänst. Det är det dyraste fallet, eftersom både terminalarrayerna och kretsdelen byggs om. Ett delta med bara kopplingar kompakteras på 0,20–0,44 s med +78 till +314 MB. Topp = hur mycket den hanterade heapen växer under arbetet:

| Skala | Terminaler | Kanter | Bygga | Minne i vila | Snapshot | Läsa snapshot | Delta 2 terminaler | Delta 100 terminaler | Delta installation | Kompaktering av delta | Topp | Ombyggnad från rader | Topp |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| full | 7,6 M | 4,3 M | 1,4 s | 265 MB | 234 MB | 0,1 s | < 0,01 ms | 0,06 ms | 0,05 ms | 0,39 s | +295 MB | 1,2 s | +956 MB |
| 2× | 15,2 M | 8,6 M | 2,1 s | 529 MB | 467 MB | 0,2 s | < 0,01 ms | 0,05 ms | 0,05 ms | 0,57 s | +590 MB | 2,4 s | +1 778 MB |
| 4× | 30,5 M | 17,3 M | 4,5 s | 1 060 MB | 935 MB | 0,4 s | < 0,01 ms | 0,07 ms | 0,05 ms | 0,89 s | +1 180 MB | 4,9 s | +3 417 MB |

**Borttag och flytt som delta (#123)**, samma körning med en sats där en utrustning och en kabel tas bort med sina kopplingar och en utrustning flyttas till en annan site. Uppmätt 2026-10-08 på en mindre maskin (4 kärnor), så tiderna är högre än i tabellen ovan. Samma maskin gav 0,79 / 1,01 / 1,91 s för att kompaktera installationen och 3,9 / 7,2 / 15,3 s för ombyggnaden från rader i full / 2× / 4×:

| Skala | Delta borttag och flytt | Kompaktering av delta | Topp | Ombyggnad från rader | Topp |
|---|---|---|---|---|---|
| full | 1,2 ms | 1,3 s | +392 MB | 2,6 s | +699 MB |
| 2× | 0,9 ms | 1,3 s | +783 MB | 6,2 s | +1 397 MB |
| 4× | 1,0 ms | 2,7 s | +1 567 MB | 12,8 s | +2 795 MB |

Kompakteringen numrerar om arrayerna när något har tagits bort, så den kostar mer än en installation som bara läggs sist, men den tar runt en femtedel av ombyggnadens tid och toppen är ungefär halva ombyggnadens (cirka 1,5 gånger grafens storlek).

**API:t** (docker compose mot en databas i respektive skala; minne = containerns `memory.current`/`memory.peak`, med GC:ns marginal):

| Skala | Laddning från databasen | Datageneratorns laddning | Minne i vila | Sats som delta (inkl. läsning) | Sats som ombyggnad | Topp vid ombyggnad | `scripts/perf.sh` |
|---|---|---|---|---|---|---|---|
| full | 8,3 s | – | 1,0 GB | 14 ms | 1,3 s | 1,6 GB | inom budget |
| 2× | 16,2 s | 2,1 min | 1,8 GB | 10 ms | 2,7 s | 3,1 GB | inom budget, kartplatta p95 28 ms |
| 4× | 32,3 s | 4,6 min | 3,9 GB | 10 ms | 5,4 s | 5,7 GB | inom budget, kartplatta p95 58 ms, snabbsök p95 19 ms |

Slutsatser:

- **Frågorna skalar.** Hela budgeten håller i 4×. Kartplattan är raden som växer (fler objekt per ruta).
- **Snabbsöket behöver inget eget index (#100).** Sökningen i två omgångar (prefix först, delsträng bara vid behov) växer knappt med datan. Lokalt är p50 7,9 / 9,1 / 9,8 ms och p95 18,9 / 15,4 / 18,9 ms i full / 2× / 4×. I demon är p95 19–22 ms i full skala. Med samma tillväxt ger det ungefär 25–30 ms vid 4×, långt under budgeten 50 ms. Ett sökindex i minnet eller en separat söktjänst blir aktuellt först om snabbsökets p95 i demon passerar 35 ms. Efter en omladdning av demon 2026-10-10 steg p95 till 42 ms (#245). Orsaken var att GIN-trigramindexen fyllts rad för rad under bulkladdningen och blivit 2–5 gånger större än nybyggda: en trigramskanning tog 26 ms i stället för 4 ms. Datageneratorn och importen bygger nu om GIN-indexen efter en bulkskrivning (7,9 s i full skala), och p95 är tillbaka på 19 ms.
- **Ändringar skalar.** Satser som flyttar kopplingar, ändrar objekt, lägger till, tar bort, flyttar eller bygger om utrustning och kablar, eller ändrar kretsar blir ett delta (#81, #119, #121, #123, se [arkitektur.md](arkitektur.md)): millisekunder oavsett nätets storlek. Deltat kompakteras in i arrayerna vart tionde minut eller vid 50 000 noder. Utan borttag tar det under en sekund även i 4× och toppen är högst 1,1 gånger grafens storlek. Med borttag numreras arrayerna om, och toppen blir cirka 1,5 gånger grafens storlek.
- **Ombyggnad från rader är en reservväg.** Den sker bara när en sats rader inte passar deltat (till exempel en borttagen terminal som en krets fortfarande går genom). Tiden och toppen växer linjärt, och toppen är ungefär tre gånger grafens storlek. Podgränsen bestäms ändå av laddningen vid start och av den ovanliga ombyggnaden, tills API-tabellen ovan har mätts om. I full skala räcker 2 GiB (topp 1,6 GB). I 2× behövs cirka 4 GiB och i 4× cirka 7 GiB.

### Kanalisation i full skala (#235)

Uppmätt 2026-10-10 lokalt (docker compose, utvecklingsmaskin), seed 1:

| | Antal | COPY |
|---|---|---|
| Brunnar (siter) | 2 331 | – |
| Trasésträckor | 42 940 | 0,2 s |
| Dukter | 50 138 | 0,1 s |
| Rör | 218 024 | 0,3 s |
| Kabelvägar | 47 431 | 0,1 s |

Generatorn bygger kanalisationen på ungefär 0,2 s av totalt 2,0 s, och hela laddningen tar 65,5 s. `scripts/perf.sh --target local` håller budgeten med kanalisationen laddad: snabbsök p95 23 ms och kartplatta p95 16 ms. Kanalisationen läses ännu inte av några frågor; plattor och påverkan per sträcka mäts i #236 och #237.

## Prestandabudget

| Interaktion | Mål (p95, full skala) |
|---|---|
| Snabbsök | < 50 ms |
| Öppna objekt med närmaste grannar | < 50 ms |
| Spåra tjänst ände till ände | < 50 ms |
| Påverkansanalys för en kabelsträcka | < 200 ms |
| Påverkan av grävning på en trasésträcka (#237) | < 200 ms |
| Kartplatta per omfång | < 100 ms |
| Kanalisationsplatta per omfång (#236) | < 100 ms |
| Växla vy mellan produktion och plan | < 100 ms |

Budgeten kan mätas direkt i appen: **Prestanda** i verktygsfältet kör interaktionerna mot det laddade nätet och visar p50 och p95 för servern (`Server-Timing`) och webbläsaren. Status bedöms på serverns p95. Är kartan öppen mäts också kartans bildtid, och är grannskapsgrafen öppen mäts grafens bildtid när den har expanderats från ett nav till minst 2 000 siter (#89). Visas en frontpanel med bild mäts dess bildtid medan ett portintervall flyttas en port per bild (#214). Målet för alla tre är p95 under 33 ms (30 fps), men de ingår inte i budgeten.

Samma rader mäts automatiskt med k6: `scripts/perf.sh` (se [drift.md](drift.md#prestandamätning)) skriver en rapport och avslutas med fel om någon rad är över budget.

## Demoscenarier (prioritetsordning)

1. **Navigering.** Ctrl+K, hitta en switch, borra ner till en port och följ en tjänst till slututrustningen medan alla linser följer med.
2. **Spårning.** En tjänst från ände till ände, samtidigt i kartan och som kretsschema.
3. **Påverkan.** Klicka på en kabelsträcka och se vilka tjänster som drabbas.
4. **Behörighet.** Tre användare, tre olika nät. Spårningen stannar vid omfångets gräns.
5. **Planer.** Två projekt reserverar samma fiber och konflikten syns direkt.
6. **Tidsresa.** Hur nätet såg ut ett visst datum, och vad vi då trodde att det såg ut som.
7. **Prestanda.** Allt ovan på data i full skala med svarstiderna synliga.
8. **Massprovisionering.** Ny standardsite från mall och patchning av 48 portar med en dragning.

Om tiden blir knapp räcker scenario 1–4. Övriga visas som förberedda i datamodellen.

## Faser

| Fas | Innehåll | Scenarier |
|---|---|---|
| 0 – Grund | Skelett, lokal miljö, schema, typkatalog, datagenerator, CI | – |
| 1 – Motor | Grafmotor i minnet, spårning, påverkan, sök, ändringsström, benchmarks | 7 |
| 2 – Linser | Appskal, karta, panelstack, träd, frontpanel, spårschema, graf, kommandopalett | 1–3 |
| 3 – Differentiering | Behörighetsomfång, operationslogg, bitemporalitet, planer, konflikter, massprovisionering | 4–6, 8 |
| 4 – Demo | Manus, prestandapanel, driftsättning bakom Authentik | alla |
| 5 – Provisionering | Dokumentera nätet i planer: rita i kartan, sätt in en site i en befintlig kabel, massimport, kabelväg, ta bort och flytta, rack (#166) | – |
| 6 – Klassning och regler | Klassningar (t.ex. kritikalitet 1–5) med ärvning och regler som syns i planer (ADR-0017, #175) | – |
| 7 – Röstagenter: härdning | En hemlighet per röstingång, NetworkPolicy, SMS-leverantör (#181–#183) | – |
| 8 – Demo på egen datamodell | Katalog från extern mapp, sitetyper och kategorier med roller, attributscheman, katalog ur en export, datageneratorn mot en egen katalog (#206–#211, #219) | – |
| 9 – Integrationer | Proveniens, avstämning mot källsystem till planer, adapterramverk (#215–#217, #230) | – |
| 10 – Kanalisation | Trasé, dukter och rör, kablars väg, påverkan per trasésträcka, ledaranvändning och ledig kapacitet (ADR-0014, #92) | 3 |

Backloggen finns som issues på GitHub (källa: [`backlog/`](../backlog/)).

## Medvetet utanför POC:en

- Integrationer mot management-system. Förbereds via provenance per attribut och ändringsström.
- Det civila lagret (kanalisation, rör, schakt). Kan läggas till utan att kärnan ändras.
- 3D-översikt. Spike om tid finns.
- AI-funktioner (naturligt språk, avvikelsedetektion). Nästa steg.
