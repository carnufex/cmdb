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

### Tillväxt: 2× och 4× full skala (#81, #119, #121)

Vid 25–50 % tillväxt per år är nätet dubbelt så stort om 2–3 år och fyra gånger så stort om 3–6 år. Datageneratorn tar `--scale 2x` och `--scale 4x` (80 000 respektive 160 000 siter). Uppmätt 2026-09-30 på en utvecklingsmaskin (32 kärnor), så tiderna är lägre än i demon:

**Grafmotorn** (`dotnet run -c Release --project tests/Cmdb.Graph.Benchmarks -- scale full 2x 4x`, utan databas). Kompakteringen gäller en installation: ny utrustning med 24 patchade portar och en ny krets över dem som rider på en befintlig krets och bär en tjänst. Det är det dyraste fallet, eftersom både terminalarrayerna och kretsdelen byggs om. Ett delta med bara kopplingar kompakteras på 0,20–0,44 s med +78 till +314 MB. Topp = hur mycket den hanterade heapen växer under arbetet:

| Skala | Terminaler | Kanter | Bygga | Minne i vila | Snapshot | Läsa snapshot | Delta 2 terminaler | Delta 100 terminaler | Delta installation | Kompaktering av delta | Topp | Ombyggnad från rader | Topp |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| full | 7,6 M | 4,3 M | 1,4 s | 265 MB | 234 MB | 0,1 s | < 0,01 ms | 0,06 ms | 0,05 ms | 0,39 s | +295 MB | 1,2 s | +956 MB |
| 2× | 15,2 M | 8,6 M | 2,1 s | 529 MB | 467 MB | 0,2 s | < 0,01 ms | 0,05 ms | 0,05 ms | 0,57 s | +590 MB | 2,4 s | +1 778 MB |
| 4× | 30,5 M | 17,3 M | 4,5 s | 1 060 MB | 935 MB | 0,4 s | < 0,01 ms | 0,07 ms | 0,05 ms | 0,89 s | +1 180 MB | 4,9 s | +3 417 MB |

**API:t** (docker compose mot en databas i respektive skala; minne = containerns `memory.current`/`memory.peak`, med GC:ns marginal):

| Skala | Laddning från databasen | Datageneratorns laddning | Minne i vila | Sats som delta (inkl. läsning) | Sats som ombyggnad | Topp vid ombyggnad | `scripts/perf.sh` |
|---|---|---|---|---|---|---|---|
| full | 8,3 s | – | 1,0 GB | 14 ms | 1,3 s | 1,6 GB | inom budget |
| 2× | 16,2 s | 2,1 min | 1,8 GB | 10 ms | 2,7 s | 3,1 GB | inom budget, kartplatta p95 28 ms |
| 4× | 32,3 s | 4,6 min | 3,9 GB | 10 ms | 5,4 s | 5,7 GB | inom budget, kartplatta p95 58 ms, snabbsök p95 19 ms |

Slutsatser:

- **Frågorna skalar.** Hela budgeten håller i 4×. Kartplattan är raden som växer (fler objekt per ruta).
- **Snabbsöket behöver inget eget index (#100).** Sökningen i två omgångar (prefix först, delsträng bara vid behov) växer knappt med datan. Lokalt är p50 7,9 / 9,1 / 9,8 ms och p95 18,9 / 15,4 / 18,9 ms i full / 2× / 4×. I demon är p95 19–22 ms i full skala. Med samma tillväxt ger det ungefär 25–30 ms vid 4×, långt under budgeten 50 ms. Ett sökindex i minnet eller en separat söktjänst blir aktuellt först om snabbsökets p95 i demon passerar 35 ms.
- **Ändringar skalar.** Satser som flyttar kopplingar, ändrar objekt utan att ändra struktur, lägger till utrustning och kablar eller ändrar kretsar blir ett delta (#81, #119, #121, se [arkitektur.md](arkitektur.md)): millisekunder oavsett nätets storlek. Deltat kompakteras in i arrayerna vart tionde minut eller vid 50 000 noder. Det tar under en sekund även i 4×, och toppen är högst 1,1 gånger grafens storlek.
- **Borttagningar och flyttar skalar inte ännu.** Satser som tar bort, flyttar eller bygger om utrustning och kablar byggs om från rader. Tiden och toppen växer linjärt, och toppen är ungefär tre gånger grafens storlek. Inget flöde i appen gör sådana ändringar i dag (#123). Podgränsen bestäms därför av laddningen vid start och av den ovanliga ombyggnaden. I full skala räcker 2 GiB (topp 1,6 GB). I 2× behövs cirka 4 GiB och i 4× cirka 7 GiB.

## Prestandabudget

| Interaktion | Mål (p95, full skala) |
|---|---|
| Snabbsök | < 50 ms |
| Öppna objekt med närmaste grannar | < 50 ms |
| Spåra tjänst ände till ände | < 50 ms |
| Påverkansanalys för en kabelsträcka | < 200 ms |
| Kartplatta per omfång | < 100 ms |
| Växla vy mellan produktion och plan | < 100 ms |

Budgeten kan mätas direkt i appen: **Prestanda** i verktygsfältet kör interaktionerna mot det laddade nätet och visar p50 och p95 för servern (`Server-Timing`) och webbläsaren. Status bedöms på serverns p95. Är kartan öppen mäts också kartans bildtid, och är grannskapsgrafen öppen mäts grafens bildtid när den har expanderats från ett nav till minst 2 000 siter (#89). Målet för båda är p95 under 33 ms (30 fps), men de ingår inte i budgeten.

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

Backloggen finns som issues på GitHub (källa: [`backlog/`](../backlog/)).

## Medvetet utanför POC:en

- Integrationer mot management-system. Förbereds via provenance per attribut och ändringsström.
- Det civila lagret (kanalisation, rör, schakt). Kan läggas till utan att kärnan ändras.
- 3D-översikt. Spike om tid finns.
- AI-funktioner (naturligt språk, avvikelsedetektion). Nästa steg.
