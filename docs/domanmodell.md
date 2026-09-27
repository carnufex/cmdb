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
5. Konflikter detekteras på **resursnivå** (terminal, ledare, slot, kanal) via reservationer, inte bara på fältnivå.

## Tid och historik

Bitemporal modell:

- **Giltighetstid**: när något var sant i verkligheten.
- **Registreringstid**: när det dokumenterades.

Frågor som stöds: "hur såg nätet ut datum D?" och "vad trodde vi att nätet såg ut som datum D?".
