# Utredning #30: 3D-översikt av nätet

**Datum:** 2026-10-11 · **Issue:** #30 · **Leder till:** rekommendation att avstå

## Fråga
Tillför en 3D-översikt något utöver wow-effekt, och vad kostar den?

## Kort svar
- **Avstå från en 3D-översikt i appen.** Den enda 3D-vy som visar något kartan inte kan är lagren staplade på varandra: kablarna på marken, transmission och logiskt som plan ovanför. I prototypen syns stamnätets ring och hur tjänsterna strålar ut från aggregeringen, men bara som helhetsintryck. Ingen fråga från användarna (var, vad sitter i vad, vilken väg, vad påverkas) blir lättare att besvara.
- **Tekniken är inget hinder.** Demons hela nät (42 331 siter, 42 771 kablar, 38 408 logiska förbindelser) ritas i 144 bilder per sekund vid rotation, skärmens tak, med första bilden efter en halv sekund. Det som kostar är allt annat: ett andra ritbibliotek, en vy som är svår att läsa och använda med tangentbord, och ännu en lins att hålla i takt med kartan.
- **Det värdefulla finns redan i 2D.** Tjänstevyn (#257) visar lagren ovanför varandra för en tjänst, och spårningen (#19) går genom lagren. Vill man se lagren för hela nätet räcker ett kartlager med transmissions- och logiska förbindelser som linjer mellan siterna, utan ny teknik.

## Prototypen

`30-3d/index.html` med `30-3d/export.sql`: deck.gl 9 i en `OrbitView` direkt i SWEREF 99 TM-meter, alltså utan kartprojektion. Kablarna förenklas med `ST_Simplify` (150 m). Kretsarnas ändsiter slås ihop per sitepar och lager; linjens bredd är antalet kretsar. Lodräta trådar binder varje lager till sina siter på marken.

Så körs den: kör `export.sql` mot demon (`psql -qAt < export.sql > data.json`, 7 s) och servera mappen lokalt.

| Mått | Värde |
|---|---|
| Data | 4,5 MB JSON, 1,6 MB gzip |
| Hämtning och tolkning | 35 ms (lokalt) |
| Första bild | 518 ms |
| Bildfrekvens vid rotation | 144 fps (skärmens tak, stationär dator med dedikerat grafikkort) |
| deck.gl | 1,25 MB minifierat, 366 kB gzip för hela paketet; en avskalad build med kärna och tre lager blir ungefär hälften |
| three.js (alternativet) | 671 kB minifierat, 167 kB gzip, utan färdiga lager för linjer och punkter i stor mängd |

Vad prototypen visar:

- **Stamnätet** (95 transmissionsförbindelser) blir en tydlig ring ovanför marken. Det är vackert och begripligt.
- **Det logiska lagret** (38 408 förbindelser) blir stjärnor från varje aggregeringsnod. Tätt och mest dekorativt; enskilda tjänster går inte att följa utan att filtrera bort nästan allt.
- **Marken** (siter och kablar) skyms av lagren ovanför när man ser snett, och avstånd och riktningar förvrängs av perspektivet. Kartan svarar bättre på *var*.

## Kostnad mot nytta

| | Bedömning |
|---|---|
| Nytta för planering och drift | Låg. Inget av flödena i [demo-scenarier.md](../demo-scenarier.md) eller planeringsflödet (#260) blir kortare. |
| Begriplighet (#261) | Sämre än kartan: skymda objekt, perspektiv, ingen skala, svårt att peka på rätt sak. |
| Tillgänglighet | En 3D-scen går inte att navigera med tangentbord eller skärmläsare utan ett parallellt gränssnitt. |
| Prestandabudget | Klarar sig, men lägger till 4,5 MB data per öppning och ett eget bibliotek; den initiala bunten (nu ~900 kB av 1 MB) måste skyddas med lazy loading. |
| ADR-0004 (utbytbar kartmotor) | En andra motor vid sidan av OpenLayers, med egen stil, urval och synk med panelerna. |
| Underhåll | Varje ny lins ska följa planer, omfång, urval och tidsresa (#23). Det är där kostnaden ligger, inte i första versionen. |
| Wow-effekt | Hög i en demo. Prototypen räcker för det och behöver inte ligga i appen. |

## Rekommendation
- Bygg ingen 3D-översikt. Stäng #30 med den här utredningen.
- Prototypen ligger kvar här för demosyfte och som mätunderlag om frågan kommer tillbaka.
- Om översikten av lagren behövs i appen: ett 2D-kartlager med transmissions- och logiska förbindelser mellan siterna (samma sammanslagning per sitepar som i prototypen, med vektorplattor). Det blir ett eget issue först när någon frågar efter det.
