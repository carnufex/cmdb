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
- Kategorier: `switch`, `router`, `card`, `radio`, `antenna`, `transmission`, `odf`, `patch`, `power`.
- **Sitemallar** (`catalog/site-templates/<key>.json`, #26) beskriver en sitetyp med utrustning (modell, namnmönster med `{code}`, rack) och intern kabeldragning (port till port). De valideras mot utrustningstyperna vid laddning och används i planer.
- Katalogen valideras vid laddning (unika namn, portar inom panelen, inga överlapp, giltigt schema, kända kategorier) och synkas till `equipment_type` i samma steg som migreringarna. Om en portmall ändras genereras inte portarna om på befintlig utrustning.

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
