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

## Scenario 2: grävkonflikt (proaktivt, #137)
**Markentreprenad Exempel AB** planerar schaktning för en fjärrvärmeledning om två dagar. Arbetet är ett område på 25 meter runt mitten av den kabel som bär flest kritiska tjänster. `GET /api/risks` hittar korsningen med PostGIS, och påverkansanalysen på kabeln visar tjänsterna. Ansvarig är Kim Lindqvist (1001).

## Scenario 3: gammalt reservkraftbatteri (proaktivt, #137)
Ett batteri (`acme-bat-100`) på Lingonåsen installerades 2013. Livslängden är 8 år, och risken visar vad stationen bär om reservkraften inte räcker.

Falsk redundans ("Mobilnät Lingonåsen norr", vars båda vägar går via Lingonåsen och Nav 4) listas också som risk.

## Demoflöde (utgående, #137)
1. Öppna panelen **Driftagent** i webben. Under **Risker** syns grävkonflikten, den falska redundansen och batteriet.
2. Klicka **Ring Kim Lindqvist** på grävkonflikten. ElevenLabs-widgeten öppnas med risken som dynamiska variabler, och agenten ringer upp: "Jag ringer om en risk i nätet som du ansvarar för. Innan jag berättar mer behöver jag verifiera dig."
3. Verifiera med 1001 och koden. Agenten anropar `risk_details`, förklarar grävningen och vilka tjänster kabeln bär, och föreslår åtgärden.
4. Säg "ja, skapa ett ärende". Ärendet skapas på kabeln och syns i panelen.

Telefoni (Twilio) används inte i demon. Samtalet går i webben.

# Demoscenarier i webben (#261)

Scenarierna visar att systemet går att använda utan manual, med samma syntetiska data (full skala, seed 1).

## Scenario 4: kabelbrott → fiberritning → tekniker
Kabeln i grävkonflikten (scenario 2) är `K-000001` mellan `HUB-001` och `HUB-046`, 79 km. Den bär flest kritiska tjänster.

1. Sök `K-000001` med Ctrl+K och öppna kabeln. Panelen visar typ, ledare och längd. *Påverkan om kabeln bryts* visar de kritiska tjänsterna.
2. Högerklicka på kabeln i kartan där brottet antas ligga och välj **Kabelbrott här** (#258). Fiberritningen öppnas som arbetsyta med brottpunkten satt.
3. Ritningen visar varje fiber med färg och nummer, vad den skarvas eller termineras till i båda ändar och vad den bär. Brottpunkten ger avståndet längs kabeln till närmaste skarv eller ODF åt båda hållen, att jämföra med en OTDR-mätning (#254).
4. **Skriv ut** ger ritningen på A3 i ljust tema, och **Spara som SVG** ger en fil att skicka till teknikern. Adressen öppnar samma ritning för den som får se den.
5. **Tjänstevy** från en av de påverkade tjänsterna (#257) visar om tjänsten har en annan väg. Välj kabeln i ritningen, så står det direkt om tjänsten faller.

## Scenario 5: planera en ny kabel i kartan
Lingonåsen (`AGG-1191`) ska få en ny fiberkabel till en grannsite längs befintlig kanalisation.

1. Öppna **Planer** och skapa planen *Ny kabel från Lingonåsen* med raden *Ny plan* (#252). Hela appen visar nu planens vy.
2. Högerklicka på Lingonåsen i kartan och välj **Ny kabel härifrån** (#258, #259). Kanalisationen tänds och raden *Ny kabel från AGG-1191* visas över kartan.
3. Klicka på trasésträckan `TR-012200` mot `SKP-005570`. Kabeln följer sträckan och får ett tomt rör (raden visar hur många rör som är lediga). Längden räknas löpande med andelen i befintlig kanalisation.
4. Klicka bortom skåpet för en brytpunkt och sedan på en site utan trasé dit. Den sista biten blir en ny grävd sträcka, och raden visar andelen som är ny. Välj **luft** före klicket för en luftledning.
5. **Lägg till kabel** lägger kabeln i planen med fiberkabel förvald. Termineringen på lediga ODF-portar i båda ändar föreslås, och **Terminera** lägger till skarvarna.
6. Planens arbetsyta visar ändringarna som tabell, med antal per slag. **Påverkan** visar att inga kretsar bryts, och **Rita om** på kabeln öppnar ritningen igen om vägen ska ändras.
7. **För in i produktion** gör kabeln, dess väg genom rören och den nya trasésträckan (planerad) verkliga.


## Scenario 6: planera ett nytt fibernät i Lingonåsen öst (#296)
Öster om Lingonåsen finns ett område utan nät: byar med villor och radhus i kluster, några flerbostadshus med 8–40 lägenheter vid Centrumvägen, företag längs Industrivägen och en mast på åsen. Anslutningspunkterna (`AP-LO-0001` …, källa `demo-adresser`) och projektet **Lingonåsen öst** läggs in av datageneratorn, deterministiskt för en given seed (i full skala, seed 1: drygt 200 punkter med omkring 400 enheter). Projektet har inga etapper än. Lingonåsen och de befintliga siterna närmast områdets västkant är där det nya nätet kopplas in.

1. Öppna **Projekt** och välj *Lingonåsen öst* (#293). Området ritas som en streckad kontur, och *Nästa steg* säger att den första etappen saknas. **Visa området** zoomar dit; från gatunivå syns anslutningspunkterna som fyrkanter i statusfärg (#292).
2. Klicka på ett flerbostadshus: panelen visar typ, antal lägenheter och att punkten inte är ansluten än. `find_service_points` ger samma sak för en agent, med antal per status.
3. **Lägg till etapp** skapar *Lingonåsen öst, etapp 1*. **Visa** öppnar den som plan.
4. **Föreslå noder** med taket 64 enheter (#294): förslagen visas som tabell och markeras i kartan, ett per by och fler där flerbostadshusen står tätt. **Lägg i planen** på ett förslag lägger ett accesskåp från mall i etapp 1. Dra sedan en kabel från Lingonåsen till noden längs kanalisationen och nya sträckor (scenario 5). Förslag på fördelningskablar kommer i #295.
5. Tillbaka i **Projekt** visar etappen sina ändringar, meter ny kabel och kostnad ur materiallistan (#291), och *Nästa steg* blir att granska och föra in etappen.
