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

## Prestandabudget

| Interaktion | Mål (p95, full skala) |
|---|---|
| Snabbsök | < 50 ms |
| Öppna objekt med närmaste grannar | < 50 ms |
| Spåra tjänst ände till ände | < 50 ms |
| Påverkansanalys för en kabelsträcka | < 200 ms |
| Kartplatta per omfång | < 100 ms |
| Växla vy mellan produktion och plan | < 100 ms |

Budgeten kan mätas direkt i appen: **Prestanda** i verktygsfältet kör interaktionerna mot det laddade nätet och visar p50 och p95 för servern (`Server-Timing`) och webbläsaren. Status bedöms på serverns p95.

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
