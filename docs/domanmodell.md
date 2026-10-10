# Domänmodell

## Kärnidé: allt som kan kopplas ihop är en terminal

En port på en switch, ena änden av fiber 17 i en kabel och en antennanslutning är samma sak ur grafens perspektiv: en punkt som något kan anslutas till. Med en enda kanttyp (`Connection`) kan en signal följas genom hela nätet oavsett om vägen går via patch, skarv eller kabel.

## Översikt

```
PLATS
  Site            geometri (punkt/polygon, SWEREF 99 TM), typ, livscykel
  Location        hierarki inom site: byggnad → rum → rack → position

UTRUSTNING
  EquipmentType   katalog: tillverkare, modell, slottar, portmallar, attributschema (JSON Schema)
  Equipment       instans av typ; sitter i Location eller i en slot på annan Equipment
  Port            genereras från typens portmall → är en Terminal

KABEL
  CableType       koppar/fiber/el, antal ledare, färgkod
  Cable           sträcka mellan två siter/skarvpunkter, LineString
  Conductor       fiber/par/ledare nr N → två Terminals (A- och B-ände)

ANSLUTNING
  Terminal        supertyp: port eller ledarände
  Connection      Terminal ↔ Terminal; typ: patch | skarv | terminering | intern

LOGIK
  Channel         kapacitet på terminal/ledare: våglängd, tidslucka, VLAN
  Circuit         ordnad väg av hopp över Terminals/Channels, per lager
  Service         tjänst som bärs av en eller flera Circuits

TVÄRGÅENDE
  Lifecycle       tillståndsmaskin + giltighetstid på allt
  Plan            ändringsmängd med beroenden ovanpå produktion
  Reservation     plan eller tjänst som gör anspråk på en resurs
  Provenance      källsystem, externt id, senast bekräftad, per attribut
  Scope           behörighetsomfång (se arkitektur)
```

## Lager

Fysiskt (kablar, ledare, kopplingar) → transmission (kanaler, våglängder, tidsluckor) → logiskt/tjänst (circuits, services). En tjänst är en väg genom lagren, och spårning kan starta och sluta i vilket lager som helst.

## Typkatalogen

Utrustningstyper modelleras inte som tabeller. En `EquipmentType` har:

- **Attributschema** (JSON Schema) som validerar instansens JSONB-attribut.
- **Portmall** med namn, typ (RJ45, LC, SFP, E1, antenn, ström …), position på frontpanelen och grupp.
- **Slotmall** för kort och moduler.

När en utrustning skapas genereras portarna från mallen, och frontpanelen kan ritas direkt ur den. En ny modell är en katalogpost och inte en migrering.

### Format

En fil per modell i [`catalog/equipment-types/`](../catalog/equipment-types/). Filnamnet är `<key>.json`. Alla tillverkare är fiktiva (`Acme …`).

```json
{
  "key": "acme-ax-48",
  "manufacturer": "Acme Networks",
  "model": "AX-48",
  "category": "switch",
  "rackUnits": 1,
  "panel": { "rows": 2, "columns": 26 },
  "ports": [
    { "name": "ge-0/0/{n}", "range": [1, 48], "type": "RJ45", "group": "access", "at": [0, 0], "layout": "zigzag" },
    { "name": "xe-0/1/{n}", "range": [1, 4], "type": "SFP+", "group": "uplink", "at": [0, 24], "layout": "zigzag" }
  ],
  "slots": [ { "name": "1", "accepts": ["card"] } ],
  "attributes": { "type": "object", "properties": { "serialNumber": { "type": "string" } }, "additionalProperties": false }
}
```

- `ports` är mallar. `{n}` ersätts med numret i `range` och `{slot}` med sloten som ett kort sitter i. `at` är första cellen `[rad, kolumn]` på panelen, och `layout` är `row`, `column` eller `zigzag` (udda nummer överst, som på de flesta switchar).
- Portarna får positionsnummer 1…N i mallens ordning. Samma typ ger därför alltid samma numrering.
- `attributes` är ett JSON Schema (2020-12) för instansens JSONB-attribut. Det valideras när utrustning skapas.
- Kategorierna står i [`catalog/equipment-categories.json`](../catalog/equipment-categories.json) och sitetyperna i [`catalog/site-types.json`](../catalog/site-types.json), se nedan.
- **Sitemallar** (`catalog/site-templates/<key>.json`, #26) beskriver en sitetyp med utrustning (modell, namnmönster med `{code}`, rack) och intern kabeldragning (port till port). De valideras mot utrustningstyperna vid laddning och används i planer.
- Katalogen valideras vid laddning (unika namn, portar inom panelen, inga överlapp, giltigt schema, kända kategorier) och synkas till `equipment_type` i samma steg som migreringarna. Om en portmall ändras genereras inte portarna om på befintlig utrustning.

### Bild per modell (#214)

En modell kan ha en bild av framsidan och, om portar sitter där, av baksidan. Portarna placeras då på bilden som klickbara ytor. Utan bild ritas panelen som rutnätet ovan.

```json
"panel": {
  "rows": 2, "columns": 26,
  "images": { "front": { "file": "acme-ax-48-front.svg", "width": 960, "height": 88 } }
},
"ports": [
  { "name": "ge-0/0/{n}", "range": [1, 48], "type": "RJ45", "group": "access", "at": [0, 0], "layout": "zigzag",
    "image": { "side": "front", "at": [60, 18], "size": [26, 20], "step": [30, 26] } }
]
```

- **Bilderna** ligger i `equipment-images/` i katalogen och följer med den, inbyggda eller från `CMDB_CATALOG_PATH`. Filnamnet ska vara gemener och sluta på `.svg` eller `.png`. Mappar är inte tillåtna. `width` och `height` är koordinatsystemet som portarna placeras i, och bilden skalas till det.
- **Portens yta** är `at` (övre vänstra hörnet, `[x, y]`) och `size` på sidan `front` eller `back`. I ett intervall stegar porten med `step` (`[dx, dy]`) i samma `layout` som i rutnätet: en kolumn är ett steg åt höger, en rad ett steg nedåt. Rutnätet (`at` i celler) finns kvar och används när bilden inte kan ritas.
- **Validering:** har modellen bild ska varje port ha en yta på en sida som har bild. Ytan ska ligga inom bilden och får inte överlappa någon annan port på samma sida. Bildfilen ska finnas i katalogen.
- **API:** `GET /api/equipment/{id}` har bilderna i `panel.images` och portens yta i `box`. Bilden hämtas från `GET /api/catalog/images/{fil}`, som kräver inloggning som resten av API:t. SVG skickas med en Content-Security-Policy som inte kör skript.
- Den syntetiska katalogen har fiktiva Acme-bilder för AX-48, PP-24, ODF-96 och RECT-48. RECT-48 har ström in och ut på baksidan. Tillverkarnas egna bilder hör hemma i organisationens egen katalog, aldrig i det här repot.

### Sitetyper, kategorier och roller

Sitetyper och utrustningskategorier är katalogdata med nyckel, visningsnamn och **roller** (#208). Koden frågar efter roller, aldrig efter typnamn. En organisation kan därför kalla sina siter och kategorier vad den vill och ändå få planer, risker, klassningsförslag, ruttförslag, karta och graf som hittar nav och aggregeringsnoder.

```json
[
  { "key": "hub", "name": "Nav", "roles": ["hub"] },
  { "key": "radio", "name": "Radiosite", "roles": ["access"] }
]
```

| Roll | Gäller | Vad koden gör med den |
|---|---|---|
| `hub` | sitetyp | Syns på alla zoomnivåer, störst i karta och graf, mål för kabelförslag vid klassningskrav. |
| `aggregation` | sitetyp | Som `hub`; pekas ut först vid falsk redundans. |
| `access` | sitetyp | Kund- och radiositer. Standardtyp för nya siter i planer. |
| `splice-point` | sitetyp | Bara skarvar. Standardtyp när en site sätts in i en kabel. |
| `card` | kategori | Sitter i en slot, får använda `{slot}` i portnamn och skapas inte fristående eller i sitemallar. |
| `termination` | kategori | Terminerar fibrer (ODF): där mönster och ruttförslag landar ledare. |
| `power` | kategori | Reservkraft: riskvyn kollar batteriernas ålder. |

En typ kan ha flera roller eller ingen. Okända roller, dubbla nycklar och sitemallar med okänd sitetyp ger fel vid laddning. Avancerad sökning och MCP `describe_catalog` visar namn och roller, och webben hämtar dem från `GET /api/catalog/kinds`. Datageneratorn väljer också sitetyper efter roll (se [Syntetiskt nät mot en egen katalog](#syntetiskt-nät-mot-en-egen-katalog)).

### Attributscheman för siter, kablar och tjänster (#211)

Sitetyper, kabeltyper och tjänstetyper kan ha ett eget `attributes`, ett JSON Schema (2020-12) som för utrustningsmodeller. Utan schema är attributen fria. Fältens `title` är det namn webben och avancerad sökning visar, så en organisation kan döpa sina fält utan kodändring.

```json
{
  "key": "hub", "name": "Nav", "roles": ["hub"],
  "attributes": {
    "type": "object",
    "properties": {
      "backupHours": { "title": "Reservkraft (timmar)", "type": "integer", "minimum": 0 },
      "aliases": { "title": "Andra namn", "type": "array", "items": { "type": "string" } }
    }
  }
}
```

- **Tjänstetyper** står i [`catalog/service-types.json`](../catalog/service-types.json) med nyckel, namn och schema. Filen är valfri. Tjänstetyper som finns i databasen men inte i filen visas med sin nyckel och har fria attribut.
- **Validering vid skrivning:** `set_attributes` på site, utrustning och kabel, `create_site` och `create_cable` med `attributes`, och import till en plan kontrolleras mot typens schema innan de kommer in i planen. Värden är enkla (text, tal, sant/falskt) eller en lista av enkla värden.
- **Import:** en kolumn (CSV) eller egenskap (GeoJSON) med samma namn som ett fält i typens schema blir ett attribut, tolkat efter fältets typ. Andra kolumner ignoreras.
- **Visning:** site-, kabel- och tjänstepanelen visar attributen med schemats namn, i schemats ordning, och övriga attribut efter nyckel.
- **Sökning:** avancerad sökning och MCP `find_sites` tar `siteAttributes`, upp till fem villkor på sitens egna attribut med samma operatorer som för utrustning. `describe_catalog` och `GET /api/query/fields` listar fälten per site-, kabel- och tjänstetyp. Attribut som användarens omfång döljer går inte att söka på, varken på siten eller på utrustningen.

### Extern katalog

Den syntetiska katalogen i `catalog/` byggs in i API:t och datageneratorn och används som standard. En annan organisations katalog ligger utanför repot (#206, #207): sätt `CMDB_CATALOG_PATH` till en mapp med samma struktur, så läser API:t, datageneratorn och migreringssteget katalogen därifrån i stället.

```
<mapp>/
  cable-types.json            krävs
  site-types.json             krävs
  equipment-categories.json   krävs
  service-types.json          valfri
  source-priority.json        valfri, vilken källa som äger vilket attribut (#215)
  source-matching.json        valfri, hur objekt från en ny källa matchas mot cmdb (#216)
  equipment-types/<key>.json
  duct-types/<key>.json       valfri, dukttyper för kanalisationen (ADR-0014)
  equipment-images/<fil>      valfri, bilder som modellerna pekar ut (#214)
  classifications/<key>.json  criticality.json krävs
  site-templates/<key>.json
```

- Valideringen och synken till databasen är densamma oavsett källa. Fel anger katalogens mapp, filen och regeln, och processen startar inte.
- En satt men saknad mapp är ett fel, inte en tom katalog. Utan variabeln används den inbäddade katalogen.
- Katalogen läses en gång när processen startar. En ändrad katalog börjar gälla vid nästa utrullning, när migreringssteget synkar den.

#### Generera katalog ur en export (#209)

En organisation med hundratals modeller skriver inte katalogposterna för hand. Ofta finns modellerna bara i data: utrustning med en modellbeteckning och portar med namn. `catalog generate` gör en utrustningstyp per modell ur en export:

```bash
dotnet run --project src/datagen -- catalog generate --from <export> --out <katalogmapp> [--force]
```

- **Exporten** är importformatets `equipment.csv` och `ports.csv` ([import.md](import.md)), med modellbeteckningen i `model` (eller `type`). `equipment.csv` kan också ha `manufacturer`, `category`, `rackUnits` och, för kort, `parent` + `slot`. `ports.csv` kan ha `type` och `group`. Ett syntetiskt exempel finns i [`docs/exempel/katalog-export/`](exempel/katalog-export/).
- **Nyckel:** tillverkare och modell som gemener, siffror och bindestreck (`initech-sw-24g`).
- **Portmall:**
  - Portnamnen från alla enheter av modellen slås ihop.
  - Namn som bara skiljer sig i ett avslutande löpnummer, med samma typ och grupp, blir en mall per obruten följd: `ge-0/0/{n}` med `range` 1–24. Övriga blir enskilda portar.
  - Nollutfyllda nummer (`P01`) behålls som de är.
  - Har samma port olika typ i exporten används den vanligaste, och modellen får en varning.
- **Frontpanel:** mallarna läggs ut från vänster till höger på rader som är lika breda som den bredaste följden, högst 48 kolumner. Längre följder delas på flera rader, så varje port får en egen cell.
- **Kort och slotar** genereras bara när exporten anger `parent` och `slot`. Kortets slotnummer i portnamnen blir `{slot}`. Kortet får mappens kategori med rollen `card` (annars en ny, `kort`), och chassimodellen får de slotar exporten använder.
- **Kategorier** som saknas läggs till i `equipment-categories.json` utan roller, och modeller utan kategori hamnar i `ovrigt`. Sätt roller efteråt (#208).
- **Attributschema** genereras inte: attributen är fria tills någon skriver ett.
- **Kontroll:**
  - Varje typ kontrolleras som en handskriven innan den skrivs. Hela mappen laddas sedan som API:t gör.
  - Rapporten listar varningar och modeller som inte genererades, med orsak.
  - En befintlig fil skrivs bara över med `--force`.

#### Syntetiskt nät mot en egen katalog (#219)

Datageneratorn bygger ett syntetiskt nät mot den katalog den startas med, även en egen (`CMDB_CATALOG_PATH`), utan kodändring. Topologin är densamma: nav med stamnät, aggregeringsringar och accessgrenar.

- **Sitetyper efter roll:** nav, aggregeringsnod, access och skarvpunkt hämtas ur katalogens roller (#208). Radiositer får den första accesstypen och skåp den sista.
- **Kablar efter medium:** stamnätet får den största fiberkabeln, ringarna den minsta med minst 96 fibrer, accessgrenarna den minsta som räcker. Kopparkabel läggs bara om katalogen har en. När den största fiberkabeln är liten serverar en gren högst så många siter som kabeln har fibrer.
- **Den syntetiska katalogen** (alla dess modeller finns) ger hela nätet som tidigare: radiosektorer, våglängder och VLAN över routrar och switchar, med samma fingeravtryck för samma frö.
- **En annan katalog** ger ett generiskt nät:
  - Kablar termineras på modeller med rollen `termination`.
  - Varje site utom skarvpunkter får en aktiv modell, det vill säga en kategori utan rollerna card, termination och power, med minst två portar. Accessiter får den med minst portar, nav och aggregeringsnoder den med flest, och fler enheter när portarna tar slut.
  - Nav och aggregeringsnoder får en kraftmodell om katalogen har en.
  - Varje länk blir en fysisk krets genom ODF:erna. Varje accessite får en tjänst (katalogens första tjänstetyp, annars `ethernet`) på en logisk krets över grenen och aggregeringsnodens ring.
  - Kort genereras inte.
- **Attribut** för okända modeller och tjänster genereras ur typens JSON Schema: alla obligatoriska egenskaper, och valfria enkla egenskaper utan mönster, inom sina gränser.
- **Krav på katalogen:** minst en sitetyp per roll, en fiberkabel, en modell med rollen `termination` och en aktiv modell. Saknas något stannar generatorn med ett fel som säger vad.
- **Demoscenarierna** ([demo-scenarier.md](demo-scenarier.md)) hittar nav och aggregeringsnoder efter roll, men kräver tjänster av typen `mobile-backhaul` och hoppar annars över.

## Kanalisation (ADR-0014, #92)

Kablar ligger i subdukter i dukter, som ligger i trasé mellan siter. Kanalisationen bär ingen signal och ligger inte i grafen.

| Tabell | Vad |
|---|---|
| `route_segment` | Trasésträcka mellan två siter: LineString i EPSG:3006, anläggningssätt (`trench`, `plough`, `aerial`, `existing`), ägare, livscykel och proveniens. Längden räknas av databasen. |
| `duct_type` | Dukttyp, synkad från `catalog/duct-types/<key>.json`: ytterdiameter och subduktmall (antal, innerdiameter, färgkod). |
| `duct` | En dukt av en dukttyp. Ligger den i en annan dukts rör (rör i rör) pekar `parent_subduct_id` ut röret. |
| `duct_segment` | Duktens ordnade trasésträckor (`seq` från 0). |
| `subduct` | Rör nummer N i dukten, genererat ur mallen. `occupancy` är `empty`, `cable` eller `blown_fibre`. *Reserverad* lagras inte: det är en reservation av röret (`resource_kind = 'subduct'`, #25). |
| `cable_path` | Kabelns väg som ordnade subdukter från A-änden. Ett rör rymmer en kabel. Utan väg ligger kabeln fritt med sin egen geometri. |
| `conductor.usage` | `dark`, `dark_fibre` (uthyrd, tänds av kunden) eller `spare`; tom när inget sagts. *Tänd* lagras inte, eftersom en ledare i en aktiv krets är tänd. |

- **Brunnar** är siter av sitetypen `manhole` (Brunn), med position, livscykel, platser och omfång som andra siter.
- **Dukttyper** är katalogdata som kabeltyperna: en ny storlek är en fil, inte en migrering. Mappen `duct-types/` är valfri, också i en extern katalog. Fel anger filen och regeln, till exempel filnamn som inte matchar nyckeln, inga rör eller ett rör som är större än dukten.
- **Datageneratorn** (#235) lägger kablarna i kanalisation när katalogen har dukttyper:
  - **Gemensamma stråk:** stamnät och ringar dras i stråk längs ett rutnät med en brunn ungefär var tionde kilometer. Kablar åt samma håll delar trasé, så ett navs länkar och ringar lämnar det i samma stråk. Ett stråk som skulle gå över hav byts mot en egen trasé.
  - **Accesskablar:** en egen trasé längs sin sträckning, delad med andra kablar mellan samma två siter.
  - **Kablarnas geometri** är deras trasésträckors.
  - **Dukter:** stråken får multidukter med marginal. En enskild kabel får skyddsrör eller en multidukt, och en del accesstrasé får en mikrorörsbunt med blåsfiber.
  - **Resten av nätet** är oförändrat för samma frö (egen slumpkälla), utom de långa kablarnas sträckning.
- **I kartan** (#236): kryssrutan *Kanalisation* i teckenförklaringen visar trasén som ett eget lager under nätet (`GET /api/tiles/conduit/{z}/{x}/{y}`), så nätets plattor och deras budget är opåverkade. Stamtrasé (`route_segment.trunk`: en sträcka som bär en kabel med minst 96 fibrer, samma gräns som för kablarna) syns på alla zoomnivåer, med ett eget partiellt index, och övriga sträckor från detaljnivån (#243). Bredden visar antalet dukter, och luftledningar är streckade.
- **Sträckpanelen** (`GET /api/route-segments/{id}`) visar anläggningssätt, ändar, ägare och varje dukt som ett tvärsnitt ritat ur dukttypens mall. Rörens beläggning visas som prick och text: tom, reserverad (en aktiv reservation av röret), kabel eller blåsfiber. Kabeln i röret är en länk.
- **Kabelpanelen** visar kabelns väg genom kanalisationen (`GET /api/cables/{id}/path`): sträcka, dukt och rör från A-änden.
- **Omfång:** en sträcka syns när dess geometri skär något av anroparens områden (`scope_route_segment`, materialiserad med de andra omfångstabellerna), och i plattorna klipps den vid områdets kant. Dukter och rör följer sin sträcka. En kabel utanför omfånget visas som *upptagen* utan namn, och en sträcka utanför omfånget i en kabels väg visas som dold.
- **Påverkan per sträcka** (#237): en grävning på en trasésträcka kapar alla kablar i dess dukter (`GET /api/route-segments/{id}/impact`, MCP `impact` med `route-segment:<id>`, och *Om sträckan grävs av* i sträckpanelen). Påverkan räknas med samma kod som för en kabel, över alla kablarna tillsammans. Kablar utanför anroparens omfång räknas med för det de bär, så långt anroparen får se, och anges bara som antal.
  - Indexet sträcka → kablar hålls i minnet per grafversion. En ny graf (laddning eller ändringsflöde, #11) ger en ny version, så indexet följer grafen. Kanalisationen skrivs bara av laddningar i dag. När planer skriver den läggs tabellerna in i ändringsflödet.
  - Planerade arbeten i driftläget visar vilka sträckor grävområdet korsar. Ett klick på området öppnar den första sträckan.
  - MCP `get_object` tar `route-segment:<id>` och visar sträckan med dukter, rör och kablar.
- **Ledaranvändning** (#238):
  - **Tänd** härleds: ledaren ingår i en krets i drift.
  - **Angiven användning:** släckt, svartfiber (uthyrd, tänds av kunden) och reserv anges med planoperationen `set_conductor_usage` (kabel, ledarnummer, användning eller tomt för att ta bort). En tänd ledare kan inte få en angiven användning.
  - **Var den syns:** kabelpanelen visar varje ledare med användning som prick och text och kretsarna den ingår i (`GET /api/cables/{id}/conductors`). Ett formulär lägger operationen i aktiv plan. MCP `get_object` för en kabel har `conductorUsage`.
- **Ledig kapacitet** (#238): `GET /api/conduit/capacity?minFreeTubes=&minFreeFibres=`, MCP `find_capacity`, och *Ledig kapacitet* i avancerad sökning.
  - Sträckor med minst N tomma rör (inte reserverade).
  - Kablar med minst N lediga fibrer: ingen krets alls går på fibern (även planerade kretsar tar den), och den är inte angiven som svartfiber eller reserv. Fibrerna räknas i grafen i minnet.
  - Kanalisationslagret i kartan visar dukter och lediga rör per sträcka vid hovring.

## Livscykel

```
planerad → under byggnation → i drift → under avveckling → borttagen
```

Övergångar loggas. Livscykeln ersätter ad hoc-flaggor av typen "planerad: ja/nej/byggd".

## Planer

En plan är en ändringsmängd (operationer: skapa/ändra/ta bort per objekt och attribut) med explicita beroenden på andra planer.

```
Produktion
 ├── A: ny fiber mellan två orter
 ├── B: ny radiosite
 └── C: ny tjänst          (beror på A + B)
        └── C-etapp 2       (beror på C)
```

Regler:

1. Vyn "plan X" = produktion + X:s beroenden + X, applicerade i beroendeordning.
2. En plans beroenden måste vara konfliktfria sinsemellan. Konflikter löses där de uppstår.
3. När en plan förs in i produktion byggs beroende planer om. Nya krockar flaggas.
4. När en plan avbryts markeras alla planer som beror på den.
5. Konflikter detekteras på **resursnivå** (terminal, ledare, slot, kanal) via reservationer, inte bara på fältnivå. En plans kopplingar är anspråk på sina terminaler och ledare. En reservation håller resursen åt en plan eller tjänst, och en terminal tar en koppling av varje slag (#25).

### Klassningar (#176, ADR-0017)

En klassning är en nivå i ett schema, till exempel kritikalitet 1–5, på en site, utrustning, kabel eller tjänst. Schemat är data i katalogen och nivåerna är rader i `classification`, så en ny klassning är en fil och inte en migrering.

- **Schema** (`catalog/classifications/*.json`): nyckel, namn, vilka objekttyper det gäller, nivåerna med namn, vilken nivå som räknas som kritisk (`criticalFrom`) och ärvningsregeln (`max` genom inneslutning och beroende, som #177 räknar fram).
- **Tilldelning:** en nivå per schema och objekt, med källa (satt eller importerad), vem och när.
- **Sätta:**
  - Direkt i panelerna av den som har skrivbehörighet (`PUT /api/classifications`, inom omfånget).
  - I en plan som operationen `set_classification`, som MCP-verktyget `add_to_plan` också använder. Agenter sätter dem inte direkt.
- **Läsa:** `GET /api/classifications` och MCP-verktygen `describe_classifications` och `get_classification`.
- **Kritisk tjänst:** felanalysen, prioritetsreglerna och driftagenten läser nivån i stället för tjänstens fria attribut. En tjänst är kritisk från schemats `criticalFrom` (kritikalitet 5). Attributet `criticality` flyttades in i schemat av migreringen.

#### Härledd klassning (#177)

Ett objekt har också en härledd nivå: den högsta av dess egen nivå, nivåerna på utrustningen i det och nivåerna på tjänsterna som går genom det.

- **Inneslutning:** en switch på nivå 5 gör racket, rummet, byggnaden och siten till nivå 5.
- **Beroende:** det som bär en klassad tjänst måste klara tjänstens nivå. En kabel eller site tar därför den högsta nivån bland de tjänster som skulle drabbas om den föll (påverkansanalysen i grafen).
- **Förklaring:** varje härledd nivå säger vad den kommer från ("innehåller SW-1 (5)", "bär tjänst TJ-… (4)").
- **Beräkning:** på begäran ur grafens vy, `GET /api/classifications/derived?type=&id=&plan=`. Den följer därför deltan och planvyer av sig själv, och en plans egna `set_classification` räknas i dess vy. Tjänster utanför användarens omfång räknas inte. Panelerna visar den härledda raden, och sitepanelen nivån per rack. MCP-verktyget `get_classification` ger både satt och härledd nivå.

#### Krav per nivå (#178)

Ett schema kan ha regler: krav som gäller när ett objekts härledda nivå når en viss nivå. Reglerna är data i schemat med två typer.

- **`cables`:** antalet kablar vid en site mot ett minimum. `supporting` räknar bara kablar som själva bär minst sitens nivå, och `independent` räknar siterna i andra änden, så två kablar till samma granne räknas som en.
- **`attribute`:** ett numeriskt attribut på objektet självt ska vara minst ett värde.

Kritikalitet 4 och uppåt kräver två kablar till olika siter som bär nivån, och reservkraft i minst 4 timmar (`backupHours` på siten).

- **Utvärdering:** `GET /api/classifications/rules?type=site&id=&plan=` ger varje krav med uppfyllt eller inte, värdet, kablarna som räknas och vad som skulle uppfylla det. Mot en plans vy räknas planens nya, delade och borttagna kablar och dess attributändringar.
- **Risker:** siter som någon klassat (själva siten, eller utrustning i den) vars krav inte är uppfyllda blir risken *Klassningskrav* i driftläget och i driftagentens risklista, med åtgärdsförslag. Siter som bara är kritiska för att tjänster går genom dem lämnas åt felanalysen, annars drunknar resten. Högst 50 siter kontrolleras per körning.
- **MCP:** `get_classification` ger kraven tillsammans med satt och härledd nivå.

#### Klassning och krav i planer (#179)

När en plan läggs in ska det synas vad den gör med nivåerna. Lägger du en ny switch på kritikalitet 5 på en site som inte klarar det, säger planen det.

- **Planerade objekt kan klassas:** `set_classification` tar också en ny site eller ny utrustning i planen (negativt id). Formuläret för ny utrustning har en kritikalitetsväljare.
- **Rapport per plan:** `GET /api/plans/{id}/classification` och MCP-verktyget `check_plan_classification` listar siterna planen berör. För varje site som planen höjer till en högre härledd nivå, eller där den gör ett krav ouppfyllt, visas:
  - nivån före och efter, och vad som orsakar den (innehåller SW-NY, bär tjänst TJ-…),
  - kraven som inte uppfylls, med vad som skulle uppfylla dem,
  - åtgärdsutkast som operationer att lägga till i planen med ett klick (reservkraft på siten, kablar till närmaste nav och aggregeringsnoder),
  - alternativ: upp till tre närliggande siter (inom 30 km) som redan klarar nivån och har rackutrymme för utrustningen.
- **Det som planen själv bryter:** bara krav som var uppfyllda i produktion, eller inte gällde, och inte är det i planens vy räknas mot planen. Ett krav som redan saknades är inte planens fel.
- **Införande:** en plan som gör krav ouppfyllda förs bara in med ett undantag och en motivering (`POST /api/plans/{id}/apply?exception=…`, högst 500 tecken), som sparas på planen och visas. Annars svarar servern 409 med kraven. Webben frågar efter motiveringen.

#### Klassning i kartan (#180)

Kartans teckenförklaring har en väljare *Klassning* (av, eller från nivå 1–5). Då ritas siter och kablar från den nivån ovanpå ett nedtonat nät: nivån som siffra, rött från schemats kritiska nivå och bärnsten under den. Status är fortfarande prick och text i panelerna. Ett klick öppnar siten eller kabeln.

- **Underlag:** `GET /api/classifications/map?schema=&min=` ger siter (id, kod, namn, position, nivå) och kablar (id, kod, nivå, förenklad sträckning). Nivån är den härledda från #177, men räknad i bulk: varje klassad tjänst märker siterna och kablarna dess kretsar går genom, ner genom kretsarna de ligger på, och klassad utrustning märker sin site.
- **Behörighet:** bara siter, kablar och tjänster inom anroparens omfång. Ett omfång som döljer positioner får en tom karta.
- **Gräns:** högst 4 000 siter och 2 500 kablar, de högsta nivåerna först; `truncated` säger när det fanns fler.

### Rack och positioner (#173)

Utrustning i ett rack har en position, dess lägsta rackenhet (`equipment.rack_position`), och modellens höjd (`rackUnits` i katalogen) säger hur många enheter den tar. Ett rack är 42 U om inget annat anges.

- **I planer** kan ny utrustning få `rack`, `room` (rummet racket står i, skapas vid behov) och `position`. Servern kontrollerar att den ryms i racket och inte krockar med det som redan sitter där, i produktion eller i planen. Utrustning som planen tar bort frigör sina enheter. Utan position hamnar den överst.
- **Befintlig utrustning** staplas i sina rack från botten i id-ordning, av migreringen och av datageneratorn.
- **Siten** visar varje rack som en frontvy med utrustningen på sina enheter och antalet lediga.

### Förslag på väg för en ny förbindelse (#171)

För en förbindelse mellan två siter föreslår systemet vägar över befintliga kablar som fortfarande har lediga fibrer, eller en ny kabel där det inte finns någon väg.

- **Lediga fibrer:** en ledare är ledig när inget är skarvat, patchat eller terminerat i någon av dess ändar, i produktion eller i planens vy. Kablar i drift med minst så många lediga fibrer som förbindelsen behöver (1–96) är kanter i ett sitenät.
- **Väg:** kortaste väg (Dijkstra) viktad på kabellängd, ett tillägg per skarv (300 m) och en andel av kabelns lediga fibrer som tas. Upp till tre alternativ: det bästa, sedan det bästa utan den längsta kabeln i det förra.
- **Ny kabel:** finns ingen väg föreslås en ny fiberkabel (minsta typen i katalogen med tillräckligt många fibrer) mellan de siter på startsidan och målsidan som ligger närmast varandra, högst 100 km i fågelvägen, med de befintliga delarna före och efter. Sträckan räknas på kablar och siter; exakta trasévägar kommer med kanalisationen (#92).
- **API:** `GET /api/routes/suggest?from=&to=&fibres=&plan=` (sitekod eller id; `plan` räknar planens förbrukning som tagen) och `POST /api/plans/{id}/route` med `alternative`. MCP: `suggest_route` och `add_route_to_plan`.
- **I planen:** den nya kabeln (om någon) och en skarvoperation per fibre och site på vägen, de lägsta lediga ledarna på båda kablarna. Allt eller inget. Ändarna lämnas för terminering (`terminate_cable`).
- **Behörighet:** bara siter och kablar inom anroparens omfång räknas med. Agenter får föreslå och lägga i planer, inte föra in.

### Import till en plan (#170)

Siter och kablar kan importeras i bulk till en plan, från CSV eller GeoJSON. Exempel finns i [`docs/exempel/`](exempel/): GeoJSON-filen bygger på CSV-filen, och de importeras i den ordningen till samma plan.

- **CSV:** en rubrikrad, kommatecken eller semikolon som avgränsare.
  - Kolumner: `kind` (`site` eller `cable`), `code`, `name`, `template` eller `siteType`, och position som `x`/`y` i SWEREF 99 TM eller `lat`/`lon` i WGS 84.
  - Kablar anges med `a`, `b` (sitekoder) och `cableType`.
  - Kolumner med samma namn som ett fält i site- eller kabeltypens attributschema blir attribut (#211).
- **GeoJSON:** punkter blir siter och linjer kablar med sin sträckning, med samma namn som egenskaper. Koordinaterna är i SWEREF 99 TM, eller i WGS 84 när de ser ut så.
- **Kontroll av varje rad:**
  - Typ, mall och kabeltyp ska finnas.
  - Positionen ska ligga i kartan och i omfånget.
  - En kod får inte förekomma två gånger i filen och får inte redan användas.
  - Kabeländarna ska finnas.
  - Med ett fel läggs inget till. En provkörning (`dryRun`) kontrollerar och räknar utan att skriva.
- **Omkörning:** en site eller kabel som planen redan har hoppas över, så att en import kan köras igen.
- **Skrivning:** operationerna skrivs i bulk och planens vy byggs en gång. 10 000 siter med mall tar cirka 9 sekunder.
- **Planens vy för en stor plan** listar de första 200 operationerna per plan och alla med problem eller konflikter, och räknar resten (`counts`). Hela listan fås med `?all=true`. För 10 000 importerade siter tar vyn cirka 1,6 sekunder och 1,3 MB, mot över 10 minuter och 124 MB innan namnen, urvalet och anspråken optimerades (#170).

### Import till produktion (#210)

Ett befintligt nät från ett källsystem läses in i produktion med ett utbytesformat, en CSV per slags objekt, som matchas på källsystem och id i källan. Importen kan därför köras igen, och objekten får `source_system`, `external_id` och `last_confirmed_at`. Se [import.md](import.md).

### Ursprung per attribut (#215, ADR-0019)

Varje källa som rapporterar ett objekt får en **källpost** (`source_record`) per objekt och källa: källans id, när den senast bekräftade objektet och värdet per attribut som källan rapporterade det. Attributen är objektets fält (`name`, `lifecycle`, `position`, `placement` …) och dess egna attribut som `attributes.<nyckel>`. Importen skriver källposterna i samma transaktion som objekten.

- **Nuvarande värde jämförs vid läsning.** Objektets panel, API och MCP:s `get_object` visar under *Källor* vad varje källa sa, och om objektet ändrats i cmdb sedan dess. Ingen skrivväg behöver veta om källposterna, så de kan inte glida isär.
- **Vem äger vad** är katalogdata: `source-priority.json` i katalogen anger per objekttyp och attribut vilka källor som får skriva, i prioritetsordning. Den mest specifika regeln gäller (`attributes.serialNumber` före `attributes.*` före `*`, objekttyp före `*`). Utan regel äger den källa som bekräftade senast. Avstämningen (#216) använder samma regler, och `autoApply` anger källorna vars ändringar av attributet förs in utan granskning.
- **Behörighet:** källposterna maskas som objektets attribut. Dolda attribut och, när omfånget döljer koordinater, `position` och `route` visas inte. Länkar till andra objekt (`placement`, `ends`) och kabelns sträckning jämförs men visas aldrig som värden, eftersom de är interna id:n som kan peka på objekt utanför omfånget. Tabellen ingår inte i den direkta databasåtkomsten (ADR-0012).

### Avstämning mot ett källsystem (#216, ADR-0020)

En källas data i utbytesformatet jämförs med cmdb genom `POST /api/reconciliations`, under anroparens omfång. Objekten matchas på källa och id, och för en ny källa på reglerna i `source-matching.json`. Skillnader i attribut som källan äger blir operationer i en plan för granskning, och det källan är betrodd med (`autoApply`) förs in direkt i en egen plan. Övriga skillnader, nya objekt som inte kan skapas och det som saknas i källan rapporteras. Inget tas bort. Se [avstamning.md](avstamning.md).

### Ta bort en site, utrustning eller kabel (#172)

Operationen `remove` sätter livscykeln borttagen och avslutar varje koppling på objektets portar och ledarändar. Raden ligger kvar, och grafen ändras bara genom kopplingar. En site tar med sig sin utrustning och kablarna som slutar där. Ett objekt som bär en krets kan inte tas bort, eftersom tjänsterna då skulle brytas utan att någon beslutat det. Kretsarna måste flyttas först. Kartans planlager visar det som tas bort, och den kabel en kapning ersätter, överstruket.

### Flytta utrustning och kabeländar (#187)

Operationen `move` flyttar utrustning till ett annat rack eller en annan site, eller en kabels ena ände (A eller B) till en annan site. Målsiten kan vara en site som planen själv skapar.

- **Inom en site:** utrustningen byter rack (och rum) och enhet. Racket skapas om det saknas, och enheten kontrolleras som för ny utrustning (ryms, ingen krock, utrustningens egna enheter räknas som lediga).
- **Till en annan site:** utrustningens kopplingar följer inte med och tas bort. Kabeln hålls kvar i sin andra ände, och dess geometri följer änden till målsitens punkt.
- **Kabelände:** kopplingarna på ledarna i den änden tas bort. Kabeln får inte sluta på samma site i båda ändar.
- **Kretsar:** det som bär en krets kan inte flyttas, som vid borttag (#172): kretsarna måste flyttas först. Ett nekande kommer redan när operationen läggs till, och som problem i planens vy om en krets lagts till under tiden.
- **Förhandsvisning:** planens vy visar att kopplingarna försvinner, men utrustningen står fortfarande på sin gamla site där tills planen förts in (grafen håller siten per utrustning, #123). Klassningsrapporten och reglerna räknar dock med målsiten för kabeländar och tar med båda siterna för en utrustning som flyttas.
- **Gränssnitt:** *Flytta* i utrustnings- och kabelpanelerna, och MCP: `add_to_plan` med `kind: move`, `target`, `site`, `end` (kabel) eller `rack`/`position` (utrustning).

### Sätta in en site i en befintlig kabel (#168)

Operationen `split_cable` delar en kabel vid en site (befintlig eller planerad i samma plan, högst 2 km från kabeln och inte vid en ände):

- Kabeln ersätts av två: `<kod>-A` från A-änden till siten och `<kod>-B` från siten till B-änden, med samma ledarnummer och färger. Den gamla kabeln får livscykeln borttagen.
- Kopplingarna vid de gamla ändarna flyttas till de nya kablarnas yttre ändar.
- Varje ledare skarvas igenom i siten, utom de som termineras där. De blir lediga i siten. En ledare som bär en krets kan inte termineras.
- Kretsar längs kabeln får de nya ändarna med skarven emellan, så varje tjänst går som förut.
- Operationen räknas fram mot planens vy just före den. Den flyttar alltså även kopplingar som tidigare operationer i planen gjort, och en senare ändring i produktion syns som ett problem i förhandsvisningen.

## Tid och historik

Bitemporal modell:

- **Giltighetstid**: när något var sant i verkligheten.
- **Registreringstid**: när det dokumenterades.

Frågor som stöds: "hur såg nätet ut datum D?" och "vad trodde vi att nätet såg ut som datum D?".
