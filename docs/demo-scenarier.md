# Demoscenarier för driftagenten

Scenarierna läggs in av datageneratorn vid varje laddning (`scripts/load-demo-data.sh`). De kan också läggas på en redan laddad databas med `dotnet run --project src/datagen -- --scenarios`. Urvalet är deterministiskt för en given seed, och allt är syntetiskt (#132, epic #130).

## Stationen Lingonåsen
Det är den aggregeringsnod som flest fysiska kretsar passerar genom (i full skala, seed 1: `AGG-1191`). Den får namnet **Lingonåsen** och alias som en uppringare kan använda: "Lingonåsen station", "Lingonåsens nod", "LGÅ", "Lingon" och "aggregering 1191". `find_station` hittar den även när namnet hörs fel.

## Scenario 1: länk nere med falsk redundans (P1)
Tre tjänster går genom Lingonåsen:

| Tjänst | Kritisk | Vägar | Vid fel på Lingonåsen |
|---|---|---|---|
| Mobilnät Lingonåsen norr | ja | 2 | **Falsk redundans**: reservkretsen (`…-RESERV`) rider på en annan fysisk krets, men även den går via Lingonåsen. Båda vägarna faller. |
| Mobilnät Lingonåsen syd | ja | 2 | Reservvägen går utanför Lingonåsen och fungerar. |
| Företagsanslutning Lingonåsen | nej | 1 | Ingen reservväg. |

Transmissionstjänster (stamförbindelser) är också kritiska. `fault_impact` på Lingonåsen ger därför **P1**. Sammanfattningen säger rakt ut att en tjänst har reservväg på papperet, men att den också går via stationen.

Delad *kanalisation* kan inte visas förrän kanalisationen finns i modellen (#92). Falsk redundans visas därför här genom delad nod.

## Uppringare
Verifieringen sker med engångskod (ADR-0015). Koden syns i webbens panel **Driftagent** under SMS-utkorg.

| Anst.nr | Namn | Roll | Omfång |
|---|---|---|---|
| 1001 | Kim Lindqvist | tekniker | hela nätet |
| 2002 | Sam Nyberg | entreprenör | region Nord |
| 3003 | Robin Ekdahl | NOC | hela nätet |

Entreprenören ser bara det region Nord visar, samma som i webben. En station utanför regionen svarar som att den inte finns.

## Demoflöde (inkommande)
1. "Hej, det är ingen länk på Lingonåsen." Agenten anropar `find_station` och bekräftar: "Menar du Lingonåsen, aggregeringsnoden?"
2. Agenten ber om anställningsnummer, anropar `request_verification_code`, och koden syns i SMS-utkorgen. Uppringaren läser upp koden, och agenten anropar `verify_caller`.
3. `fault_impact` ger P1, kritiska tjänster nere och falsk redundans. Agenten förklarar det kort.
4. `create_incident` skapar ärendet `INC-…` med prioritet P1, och jouren larmas. Ärendet syns i panelen Driftagent med berikningen.

## Kvar att bygga
- Scenario 2 (grävkonflikt) och scenario 3 (åldrad utrustning och batteri) kommer med #137.
