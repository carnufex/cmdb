# Import av ett befintligt nät (#210)

Ett nät som redan finns i en annan organisations källsystem läses in i produktion med ett utbytesformat: en mapp med en CSV-fil per slags objekt. Formatet är detsamma oavsett källsystem. En adapter (#217) eller ett exportskript skriver mappen, och cmdb läser den. Riktig data och riktiga modeller ligger aldrig i det här repot (ADR-0018). Formatet byggs och testas här mot syntetiska exempel i [`docs/exempel/import/`](exempel/import/).

```bash
# Kontrollera utan att skriva
dotnet run --project src/datagen -- import --from <mapp> --source <källsystem> --dry-run
# Importera
dotnet run --project src/datagen -- import --from <mapp> --source <källsystem>
# Skriv det syntetiska nätet i samma format (exempel, mätning)
dotnet run --project src/datagen -- export --to <mapp> --scale full --seed 1
```

Katalogen är den i `CMDB_CATALOG_PATH`, eller den inbäddade (se [domanmodell.md](domanmodell.md#extern-katalog)). Anslutningen tas från `ConnectionStrings__Cmdb` eller `--connection`.

## Filerna

Alla filer är valfria. En fil som saknas lämnar de objekten orörda. Kolumnerna har namn, så ordningen spelar ingen roll. Avgränsare är komma eller semikolon, och värden med avgränsare, citattecken eller radbrytning citeras med `"`.

Objekten hänvisar till varandra med sitt id i källsystemet (`id`-kolumnerna), aldrig med cmdb:s id. Ett id är unikt per fil och källsystem.

| Fil | Kolumner |
|---|---|
| `sites.csv` | `id`, `code`, `name`, `siteType`, position som `x`/`y` (SWEREF 99 TM) eller `lat`/`lon` (WGS 84), `lifecycle` |
| `locations.csv` | `id`, `site`, `parent`, `kind` (`building`, `room`, `rack`, `position`), `name`, `rackUnits` |
| `equipment.csv` | `id`, `site`, `location` eller `parent` + `slot` (kort), `name`, `type` (modellens nyckel i katalogen), `lifecycle`, `rackPosition` |
| `ports.csv` | `equipment`, `name`: portar som källsystemet känner till. Portarna skapas ur katalogens portmall, och en port som inte finns i mallen rapporteras. |
| `cables.csv` | `id`, `code`, `cableType`, `a`, `b` (siter), `lifecycle`, `route` (valfri WKT `LINESTRING` i SWEREF 99 TM, annars rakt mellan siterna). Ledarna följer av kabeltypen. |
| `connections.csv` | Två terminaler (`a…` och `b…`), `kind` (`patch`, `splice`, `termination`, `internal`), `lifecycle` |
| `circuits.csv` | `id`, `code`, `layer` (`physical`, `transmission`, `logical`), `lifecycle` |
| `circuit-hops.csv` | `circuit`, `seq` (från 0), en terminal, `channel` (valfri, till exempel `vlan:100` eller `wavelength:3`) |
| `circuit-dependencies.csv` | `circuit`, `carrier`: kretsen går på bärarkretsen |
| `services.csv` | `id`, `code`, `name`, `serviceType`, `lifecycle` |
| `service-circuits.csv` | `service`, `circuit` |

- **Terminaler** anges som en port, `Equipment` + `Port` (till exempel `aEquipment`, `aPort`), eller som en ände av en ledare, `Cable` + `Conductor` (från 1) + `Side` (`A` eller `B`). I `circuit-hops.csv` är kolumnerna `equipment`, `port`, `cable`, `conductor` och `side`.
- **En krets** går från sin första hopp till sin sista. En ny krets måste ha sin väg i `circuit-hops.csv`.
- **Attribut:** kolumnen `attributes` tar ett JSON-objekt. Kolumner som heter som ett fält i typens attributschema (#211) blir attribut, tolkade efter fältets typ, och läggs ovanpå. Allt kontrolleras mot schemat.
- **`lifecycle`** är `planned`, `under_construction`, `in_service`, `decommissioning` eller `removed`. Tom betyder `in_service`.

## Kontroll först

Varje rad kontrolleras innan något skrivs. Med ett enda fel skrivs ingenting, och alla fel rapporteras med fil, rad, objekt och vad som är fel:

```
equipment.csv:6 ex-e5: okänd modell 'acme-ax-99' i katalogen
ports.csv:6 ex-e3: porten 99 finns inte i modellens portmall (acme-odf-24)
connections.csv:3 : kabeln ex-c1 har 12 ledare, inte 13
```

Kontrollerna:
- Typer, sitetyper och kabeltyper finns i katalogen. Kort sitter i en slot som tar deras kategori, och annan utrustning i en location.
- Hänvisningar finns, i filerna eller redan i databasen för samma källsystem. Locations, utrustning och parent ligger i samma site. Inga cirklar.
- Portar och ledare finns i modellens portmall och i kabeltypen.
- Koder är unika och används inte av ett objekt från ett annat källsystem.
- Positioner ligger i kartan, och attribut passar typens schema.

## Omkörning och ändringar

Objekten matchas på källsystem (`--source`) och id. En import kan därför köras igen, och en ny export från källan uppdaterar det som ändrats:

- **Nya objekt** skapas. Nya utrustningar får sina portar och nya kablar sina ledare ur katalogen.
- **Ändrade objekt** uppdateras: namn, kod, livscykel, attribut, position, plats i rack och placering. `rackPosition` som saknas fylls i genom stapling (#173).
- **Länkar** (`circuit-dependencies.csv`, `service-circuits.csv`) mellan objekt från källsystemet följer filen helt när den finns. Länkar som filen inte längre har tas bort. En krets väg byts ut när den skiljer sig från filens.
- **Kopplingar** matchas på sina två terminaler.
- **Objekt som bara finns i databasen** räknas i rapporten men tas inte bort. Att avveckla dem är en plan (#216).
- **Går inte i en import:** byte av modell på utrustning, byte av kabeltyp och flytt av kabeländar. De rapporteras som fel och görs i en plan.

Allt skrivs i en transaktion med `COPY` till temporära tabeller och mängdbaserade satser. Objekten får `source_system`, `external_id` och `last_confirmed_at`. Efteråt läggs en `reload` i ändringsströmmen, så att grafen laddas om, och omfångens synlighet räknas om.

## Behörighet

Importen är systemarbete som datageneratorn. Den körs som ett eget steg med en egen databasroll och aldrig genom API:t, så ingen användares omfång kringgås. Den ser och skriver allt (`cmdb.scopes = *`). Kör den som ett Job med samma image och katalog som API:t, se [drift.md](drift.md#import-av-ett-befintligt-nat).

## Mätning

Det syntetiska nätet i full skala (seed 1) exporterat och importerat till en tom databas, lokalt i Postgres 17 med PostGIS:

| Steg | Rader | Första körningen | Omkörning (inget ändrat) |
|---|---|---|---|
| Läsa och kontrollera filerna (296 MB) | 10 M | 24 s | 21 s |
| Siter | 40 000 | 3 s | 2 s |
| Locations | 121 602 | 7 s | 3 s |
| Utrustning och portar | 224 707 och 5,0 M | 136 s | 17 s |
| Kablar och ledare | 42 771 och 1,3 M | 69 s | 2 s |
| Kopplingar | 3,0 M | 104 s | 46 s |
| Kretsar och hopp | 117 025 och 1,1 M | 64 s | 25 s |
| Beroenden, tjänster och tjänstekretsar | 262 724 | 8 s | 3 s |
| **Totalt** | | **7,3 min** | **2,1 min** |

Grafen laddar det importerade nätet som det genererade: 7,6 M terminaler och 4,3 M kanter på 17 s. Främmande nycklar kontrolleras per rad under importen, till skillnad från datageneratorns snabbväg, eftersom importen skriver i en databas som redan används.
