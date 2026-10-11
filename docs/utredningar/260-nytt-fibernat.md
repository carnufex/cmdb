# Utredning #260: planera ett nytt fibernät i ett område

**Datum:** 2026-10-11 · **Issue:** #260 · **Leder till:** ADR-0021 (*Accepterad*, alternativ B i #290) och issues för flödet

## Fråga
Hur används verktyget för att planera ett nytt fibernät i ett område där vi inte har något i dag, från första skiss till byggbar plan?

## Kort svar
- **Det mesta av byggstenarna finns redan.** Planer med beroenden ger etapper (#24). Siter, utrustning och kablar läggs till i planen (#107, #167). En kabel kan ritas längs kanalisationen med nya sträckor där ingen finns (#259). En site kan sättas in i en kabel (#168), och vägförslag går över befintligt nät (#171). Klassningsreglerna säger vad en nod kräver (#179). Planens arbetsyta visar ändringar och påverkan (#252).
- **Det som saknas är området som helhet.** Var finns kunderna, hur många fibrer behövs till varje nod, vad kostar det och i vilken ordning byggs det. Det kräver tre nya saker:
  1. **Anslutningspunkter** (adresser eller kunder) som en egen objekttyp med position, typ och status, utan att vara siter.
  2. **Kostnad i katalogen**: pris per meter för kabel- och dukttyper och per enhet för utrustning och anläggningssätt (grävt, plöjt, luft).
  3. **Ett projekt** som håller ihop ett område och dess etapper (planer som bygger på varandra), med en materiallista och kalkyl per etapp.
- **Verktyget föreslår, planeraren bestämmer.** Förslag på nodplacering per kluster av anslutningspunkter, ett träd av fördelningskablar från noden längs vägar och befintlig kanalisation, och fiberantal dimensionerat ur katalogens kabeltyper med reserv. Allt hamnar i planen som vanliga ändringar som planeraren kan rita om (#259), flytta eller ta bort.
- Anslutningspunkter och projekt är nya begrepp i modellen. De föreslås i **ADR-0021**, med beslut i ett eget issue (`type:decision`, `needs-human`). Kostnad i katalogen är katalogdata som kabeltyperna och behöver inget nytt beslut.

## Flödet steg för steg

| Steg | Vad planeraren gör | Finns i dag | Saknas |
|---|---|---|---|
| 1. Avgränsa området | Ritar området i kartan och ger det ett namn | Lasso i kartan (#253), polygon i en plan saknas | Ett **projekt** med område (polygon) |
| 2. Anslutningspunkter | Läser in adresser eller kunder (CSV/GeoJSON) eller ritar dem | Import av siter och kablar till en plan (#170) | **Anslutningspunkt** som objekttyp, import och kartlager |
| 3. Anslutning till befintligt nät | Väljer var området kopplas in: en nod, ett skåp eller en kabel att sätta in en site i | Ny site, site i kabel (#168), vägförslag (#171), ledig kapacitet (#238) | Förslag på närmaste noder med lediga fibrer och rör |
| 4. Noder och skåp | Placerar fördelningsnoder och skåp, eller tar emot förslag per kluster | Ny site från mall (#26, #167) | **Förslag på nodplacering** per kluster |
| 5. Kablar | Drar stamkabel till noden och fördelningskablar ut, längs befintlig och ny trasé | Rita kabel med brytpunkter längs kanalisationen (#259) | **Trädtopologi**: förslag på fördelningskablar från noden till anslutningspunkterna |
| 6. Dimensionering | Fiberantal per kabel med reserv, ur katalogens kabeltyper | Kabeltyper med antal ledare | **Dimensionering**: anslutningspunkter nedströms + reserv → minsta kabeltyp som räcker |
| 7. Material och längder | Materiallista och kostnad | Längder från ritningen (#259) | **Kostnad i katalogen** och **materiallista per plan** |
| 8. Etapper | Bygger området i etapper som planer på varandra | Planer med beroenden (#24), flaggor när en etapp ändras | **Projektvy** över etapperna med material, status och nästa steg |

## Vad verktyget föreslår och vad planeraren ritar

- **Förslag:**
  - *Nodplacering:* anslutningspunkterna klustras (k-means på position med ett tak för antal per nod, till exempel 64 eller 128). Noden föreslås i klustrets tyngdpunkt, flyttad till närmaste väg eller befintliga brunn.
  - *Trädtopologi:* från noden följer fördelningskablarna befintlig kanalisation där den finns och vägnätet annars. I det syntetiska nätet saknas vägnät, så där används ett rutnät, och ett riktigt vägnät läses in senare.
  - *Dimensionering:* varje kabel får antalet anslutningspunkter nedströms plus reserven (till exempel 20 %), avrundat uppåt till närmaste kabeltyp i katalogen.
  - *Anslutning till befintligt nät:* närmaste noder med lediga fibrer (#238) och väg dit (#171).
- **Planeraren ritar och bestämmer:** områdets gräns, vilka förslag som tas, var noder faktiskt hamnar, kablar som ska gå en annan väg (*Rita om*, #259), anläggningssätt per sträcka och etappindelningen.
- **Allt förslag blir vanliga ändringar i planen.** Det finns inget separat "designläge". Planens arbetsyta (#252), konfliktkontrollen och klassningsreglerna (#179) gäller som för allt annat.

## Vad som behövs i modellen

1. **Anslutningspunkt** (`service_point`): position (EPSG:3006), typ (villa, flerbostadshus med antal lägenheter, företag, mast), status (*planerad*, *beställd*, *ansluten*), valfri adress och extern id. Den är ingen site, eftersom den inte har utrustning eller platser förrän den ansluts. När den ansluts kopplas den till den site eller port som betjänar den. Kräver ett beslut, se **ADR-0021**.
2. **Projekt** (`project`): namn, område (polygon), ansvarig och de planer som är dess etapper, i ordning. Planerna själva ändras inte, eftersom etapperna redan är planer med beroenden. Kräver ett beslut, se **ADR-0021**.
3. **Kostnad i katalogen:** `costPerMeter` på kabel- och dukttyper, `cost` på utrustningstyper och `costPerMeter` per anläggningssätt (grävt, plöjt, luft, befintlig). Det är katalogdata, valideras som övriga fält och kräver inget nytt beslut. Valutan står i katalogens rot.
4. **Materiallista per plan:** härleds ur planens ändringar och katalogen. Den lagras inte.

## Gränssnittet

```
┌ Projekt: Lingonåsen öst ────────────────────────────── [Halva] [Arbetsyta] × ┐
│ Område ritat · 412 anslutningspunkter · 3 etapper                            │
│                                                                              │
│ Etapp 1  Stamkabel och nod    ● Utkast   18 ändr.  7,4 km  412 000 kr  [Visa] │
│ Etapp 2  Fördelning norr      ● Utkast   46 ändr. 11,2 km  638 000 kr  [Visa] │
│ Etapp 3  Fördelning syd       ○ Ej påbörjad                     [Föreslå nät] │
│                                                                              │
│ Nästa steg: Etapp 1 har 2 konflikter → [Lös konflikter]                      │
└──────────────────────────────────────────────────────────────────────────────┘
```

```
Kartan, med området:                     Högerklick i området (#258):
  ╭──────────────────────────╮             Ny anslutningspunkt här
  │  · ·  ·    ·  ·    ·     │             Föreslå noder i området…
  │ ·  ◆N1 ·  ·   ·  ◆N2 ·   │             Föreslå fördelning från vald nod…
  │  ·  ┃ ·  ·  ·   ┃  ·     │             Materiallista för etappen
  │ ━━━━┻━━━━━━━━━━━┻━━━━ AGG│
  ╰──────────────────────────╯
  · anslutningspunkt  ◆ föreslagen nod  ━ stamkabel (ritad, #259)
```

```
Förslag på noder (arbetsyta, halva skärmen):
┌──────────────────────────────────────────────┐
│ Kluster   Anslutn.  Nod föreslagen vid  Fibrer│
│ N1        118       Brunn BR-001342     144   │
│ N2         96       Ny site (väg)        96   │
│ N3        198       Ny site (väg)       288   │
│ Tak per nod [128]  Reserv [20 %]  [Lägg i planen] │
└──────────────────────────────────────────────┘
```

- **Projektet** är ett nytt verktyg (arbetsyta, #251), öppnat från *Planer*. Etapperna är planer, och *Visa* växlar till etappens vy.
- **Anslutningspunkter** är ett kartlager med prick och status och en egen panel, sökbara som andra objekt.
- **Förslagen** visas som en tabell bredvid kartan och läggs i planen med en knapp. Därefter är de vanliga ändringar.
- **Materiallistan** är en flik i planens arbetsyta (#252): kabeltyper med längd, dukter, utrustning och anläggningssätt med pris, summerat och exporterbart som CSV.

## Syntetisk data
Datageneratorn får ett valfritt **greenfield-område**: en polygon utanför det befintliga nätet med anslutningspunkter (villor i kluster, några flerbostadshus och företag) och ett par befintliga noder i kanten med lediga fibrer. Allt är syntetiskt och deterministiskt per frö, och demon får ett projekt *Lingonåsen öst* utan etapper, redo att planera.

## Uppdelning
Issues, länkade hit:

1. #290 Beslut: anslutningspunkter och projekt i modellen (ADR-0021), `type:decision`, `needs-human`.
2. #291 Kostnad i katalogen och materiallista per plan (kräver inget beslut).
3. #292 Anslutningspunkter: objekttyp, import, kartlager och panel (efter #290).
4. #293 Projekt med område och etapper (efter #290).
5. #294 Förslag på nodplacering per kluster av anslutningspunkter (efter #290).
6. #295 Förslag på fördelningskablar och dimensionering av fiberantal (efter #290).
7. #296 Datageneratorn: greenfield-område med anslutningspunkter (efter #290).

## Avgränsningar
Inget vägnät läses in i det här steget: förslagen följer kanalisationen och ett rutnät. Ingen optimering av total kostnad. Förslagen är heuristiker som planeraren justerar.
