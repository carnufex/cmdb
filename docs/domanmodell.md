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
