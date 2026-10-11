# UX-principer

## Känsla

Mörk, informationstät och lugn, i samma familj som Linear. Tunna linjer (1 px), ytor som separeras med ljushet i stället för skuggor, och färg som bara används för att betyda något.

**Men:** allt bygger på design-tokens med ett mörkt standardtema **och** ett ljust tema. Projektorer och dagsljus i fält gör enbart mörkt tema oanvändbart. Testa demon på den projektor som faktiskt ska användas.

## Begriplighet (#261)

Systemet är avancerat, men det ska gå att förstå utan manual. Sex principer gäller för allt nytt:

1. **Se vad saker är.** Ikon och svenskt typnamn på varje objekt, i träd, sök, brödsmulor, paneler och kartans meny (#249).
2. **Inget tomt läge utan ingång.** Varje verktyg visar något användbart innan något är valt: innehållsträdet visar hela nätet (#250), en tom planlista förklarar vad en plan är och har *Ny plan* (#252), och en tom tabell säger var ändringar läggs till.
3. **Det som behöver plats får plats.** Verktyg kan ta halva eller hela skärmen, och kartan behöver inte alltid synas (#251). Planer, massredigering, fiberritning, rackvy, rumsvy och tjänstevy är arbetsytor.
4. **Rita det som går att rita.** Frontpanel, rack, rum, tjänstens väg och fiberritningen är bilder (#214, #254, #255, #256, #257), i samma anda.
5. **Gör saker där man tittar.** Högerklick i kartan (#258), urval med åtgärder (#253) och kablar som ritas i kartan längs kanalisationen (#259). Samma åtgärder finns i kommandopaletten.
6. **Namn efter vad man gör**, inte efter tekniken: *Massredigering* i stället för *Kalkylark*, *Lös konflikter* i stället för en statuskod.

## Skärmstorlek

Minsta stödda storlek är **1280×720 CSS-pixlar**, vilket motsvarar 1080p med 150 % skalning (#72). Där ska kartan, ett verktyg (avancerad sökning eller prestanda) och objektpanelen kunna vara öppna samtidigt, utan radbrytning i verktygsfältet och utan sidscroll. Paneler och verktyg scrollar internt. Under 1440 px bredd blir objektpanelen och verktygen smalare, så att kartan behåller ungefär 500 px. Större skärmar ger kartan all extra yta.

Ett verktyg har tre storlekar (#251), som växlas med knapparna i verktygets rubrik, med Alt+. eller från kommandopaletten:

- **Panel:** ovanpå kartans vänsterkant, som förut.
- **Halva skärmen:** verktyget bredvid kartan, som får minst 500 px. Planer och massredigering startar här.
- **Arbetsyta:** verktyget tar hela ytan under verktygsfältet. Kartan döljs men behåller sitt läge och ritas inte medan den är dold. *Visa i kartan* går tillbaka till halva skärmen med det öppna objektet markerat och kartan på det.

Varje verktyg anger vilka storlekar det klarar. Valet sparas per verktyg på enheten, och verktyget och storleken står i adressen (`?tool=plans&size=workspace`), så att en länk öppnar samma vy. Objektpanelen ligger till höger i alla tre. Under 1440 px döljs användarnamnet (det står på *Logga ut*), *Prestanda* blir en ikon och sökfältet krymper, så att verktygsfältet ryms på 1280 px även med en aktiv plan.

## Färg betyder status

| Status | Färg |
|---|---|
| Planerad | violett |
| Under byggnation | blå |
| I drift | lugn grön |
| Konflikt / fel | varm korall (också enda färgen för primär åtgärd) |

Status visas alltid med **prick och text**, aldrig bara med färg (WCAG 2.1 AA).

## Ikoner

Varje objekttyp har en ikon, så att man ser vad något är utan att läsa (#249). Ikonerna är inline-SVG på 16 px med tunn linje i `currentColor`, i samma stil som appens övriga ikoner, och finns i `src/web/src/app/shell/icons.ts` (`<cmdb-icon name="rack" />`).

- **Uppsättningen:** hela nätet (`network`), område (`area`), site (`site`), byggnad, rum, rack och position, utrustning, kort, port, kabel, ledare, trasésträcka (`route-segment`), dukt, krets, tjänst och plan, plus `action` för åtgärder i paletten.
- **Per sitetyp och kategori:** katalogen anger ikonen (`icon` i `site-types.json` och `equipment-categories.json`), annars gäller rollens (se [domanmodell.md](domanmodell.md#sitetyper-kategorier-och-roller)). Webben läser dem från `GET /api/catalog/kinds`, så en annan katalog får sina ikoner utan kodändring. Nav, aggregeringsnoder, skåp, radiotorn, skarvpunkter och brunnar har egna ikoner, liksom switchar, routrar, kort, radio, antenner, transmission, ODF:er, patchpaneler och kraft.
- **Var:** innehållsträdet, snabbsöket och kommandopaletten, panelrubriker, brödsmulor, hover-kort och objektlänkar. Kartans punkter har sin egen stil per sitetyp och inga ikoner.
- **Alltid bredvid text.** Ikonen är dekorativ (`aria-hidden`); typen står i texten eller i radens tillgängliga namn. Status visas fortfarande med prick och text, aldrig med ikonen.
- **Typnamn på svenska.** Innehållsträdet visar platsens typ som *Byggnad*, *Rum*, *Rack*, aldrig nyckeln.

## Få klick

- **Allt är en länk.** Varje objektreferens öppnas i en sidopanel. Panelerna staplas som en brödsmulestig och varje vy har en URL.
- **Förhandsvisning vid hovring** med det viktigaste, så att man ofta slipper klicka.
- **Inga modala kedjor.** Redigering sker direkt i panelen.
- **Kommandopaletten (Ctrl+K) gör saker**, inte bara söker: "ny site från mall", "patcha 1–24", "spåra tjänst". Tom palett visar åtgärderna för det öppna objektet, text ger matchande åtgärder före objektträffar och `>` först ger bara åtgärder. Åtgärder registreras i `CommandRegistry`, och en panel kan lägga till tillfälliga åtgärder för det som är markerat, till exempel "Spåra från port bh1" (#21).
- **Tangentbord först** för återkommande flöden.
- **Högerklick i kartan (#258)** visar vad som går att göra just där. Ligger flera objekt under pekaren väljer man först vilket.
  - *Tom yta:* Ny site här, Ny kabel härifrån, Mät avstånd (klicka där mätningen slutar; Escape eller nästa klick rensar) och Kopiera koordinat (SWEREF 99 TM).
  - *Site:* Öppna, Innehåll, Rackvy, Påverkan om siten faller, Grannskap, Ny kabel härifrån och Lägg till i urvalet.
  - *Kabel:* Öppna, Sätt in en site här (där man klickade på kabeln), Kabelbrott här (fiberritningen med avstånd från punkten), Fiberritning, Reservera fiber och Lägg till i urvalet.
  - *Trasésträcka:* Öppna, Påverkan vid grävning och Fiberritning.
  - Ändringar visas bara för den som får ändra (cmdb-full) och hamnar i den aktiva planen via planpanelens formulär, som kontrollerar dem som vanligt. Utan aktiv plan står det *· ny plan* vid åtgärden, och menyn frågar efter planens namn i samma steg, utan modal.
  - Menytangenten eller Shift+F10 öppnar menyn mitt i kartan för det öppna objektet. Pilar flyttar, Enter kör, Escape stänger. Samma åtgärder (utom de som behöver en punkt i kartan) finns i kommandopaletten för det öppna objektet.

## Massprovisionering

- **Mallar på alla nivåer.** En sitemall innehåller byggnad, rack, utrustning och intern kabeldragning.
- **Mönsteroperationer.** Markera portintervall som i ett kalkylark och dra till en kabel. Förskjutning och steglängd kan anges.
- **Rita på kartan.** En kabel mellan två siter skapar ledarna automatiskt och föreslår terminering.
- **Massredigering.** Valfritt urval blir ett redigerbart rutnät. Klistra in från Excel och fyll nedåt.
- **Urval som grund för åtgärder.** Lasso på kartan eller filter, och sedan en åtgärd på hela urvalet.

Allt massarbete sker i en plan: förhandsvisning av skillnader, konfliktkontroll och ångra genom att kasta planen.

Så är det byggt (#26):

- **Sitemallar:** planpanelens *Från mall* lägger en hel site (utrustning i rack och intern kabeldragning) vid kartans mittpunkt, i ett steg.
- **Mönster:** Shift-klicka ett portintervall i frontpanelen och välj *Skarva*: kabel, första ledare, sida och steg. Porterna följer panelens ordning.
- **Rita kabel:** *Ny kabel*, sedan *Välj siterna i kartan*, och klicka på två siter (befintliga eller planerade). Därefter föreslås terminering på lediga ODF-portar i båda ändar, och *Terminera* lägger till skarvarna.
- **Rita kabel med brytpunkter (#259):** *Ny kabel härifrån* i högerklicksmenyn på en site, eller *Rita vägen i kartan* i planens *Ny kabel*, startar ritningen. Kanalisationen tänds i kartan.
  - Ett klick på en trasésträcka följer den till nästa korsning, och kabeln läggs i ett tomt rör. Ett klick på en site går dit, längs sträckan om en finns, och avslutar. Ett klick i kartan blir en brytpunkt. Siter och sträckor snäpper inom några pixlar.
  - En rad över kartan visar längden löpande med andelen i befintlig kanalisation och andelen som är ny sträcka, varje del med sitt rör (*rör ledigt, 5 av 21*) och varningar. En sträcka utan lediga rör är röd och har *Dra bredvid*, som gräver en ny sträcka längs samma väg. Nya delsträckor är *grävda* eller i *luft*, och där kabeln inte följer någon sträcka föreslås en ny trasésträcka i planen.
  - Följda sträckor ritas heldragna i planerad färg, nya streckade och fulla röda. Brytpunkterna är inringade.
  - Backsteg tar bort senaste steget och Esc avbryter. Under *N steg* kan en brytpunkt flyttas (*Flytta*, sedan ett klick där den ska ligga) eller tas bort.
  - Kabeltyp väljs ur katalogen, med en fiberkabel förvald. *Lägg till kabel* lägger den i planen med sin väg och föreslår terminering i båda ändar som förut. I planens ändringstabell har en ritad kabel *Rita om*, som öppnar ritningen igen och sparar i samma ändring.
- **Allt eller inget:** en mall, ett mönster och en terminering läggs till helt eller inte alls, och varje operation kontrolleras som vanligt.
- **Urval (#253):** en lasso i kartan eller avancerad sökning (*Använd som urval*) ger ett urval av siter och kablar, som visas markerat i kartan och som en rad överst i den: *12 siter, 4 kablar markerade*. Raden har åtgärderna:
  - **Massredigera** öppnar massredigeringen med urvalets siter.
  - **Påverkan om urvalet faller** visar kretsarna och tjänsterna som påverkas om allt i urvalet faller samtidigt, som påverkan för ett enskilt objekt.
  - **Exportera (CSV)** laddar ned urvalet som semikolonseparerad fil för Excel, med kod, namn, sort, livscykel, position och attribut. Bara det omfånget visar kommer med: objekt utanför det, dolda attribut och dolda positioner lämnas ute.
  - **Rensa** tömmer urvalet.
  - *Lasso* (eller L) ritar en lasso: klicka runt siterna och dubbelklicka för att stänga. En lasso ersätter urvalet, med Shift lägger den till och med Alt tar den bort. Kablar som ligger helt inom lasson kommer med. Escape avbryter. Åtgärderna finns också i kommandopaletten när ett urval finns.
- **Massredigering (#27, tidigare *Kalkylark*):** *Massredigering* i verktygsfältet visar urvalets siter som ett redigerbart rutnät, med siterna eller utrustningen på dem.
  - Kolumnerna är namn, livscykel och attributen i modellernas scheman.
  - Klistra in ett block från Excel i markerad cell. Shift-klicka för att markera nedåt och fyll med Ctrl+D.
  - *Lägg i planen* skapar namnbyten, livscykel- och attributändringar i den aktiva planen, allt eller inget. Attributen kontrolleras mot modellens schema, inklusive format som ipv4. Ett fel pekas ut på raden.

## Linser

| Lins | Svarar på |
|---|---|
| Karta | Var? |
| Innehållsträd | Vad sitter i vad? |
| Frontpanel | Vilka portar, vad är kopplat? |
| Rackvy | Vad sitter var i racket, och vad är ledigt? |
| Rumsvy | Var i rummet står racket, och hur fullt är det? |
| Spårschema | Vilken väg tar tjänsten? |
| Tjänstevy | Hur är tjänsten uppbyggd, steg för steg, och har den en annan väg? |
| Grannskapsgraf | Vad finns runt omkring? Expandera och backa. |

**Frontpanelen** (#18) visar utrustningens portar med status. Har katalogen en bild av modellen (#214) ritas bilden med portarna som klickbara ytor ovanpå. Annars ritas portarna som ett rutnät. Status är en färgton över porten på bilden och förklaras alltid med prick och text under panelen. En ledig port har bara en kontur. Klick markerar, Shift-klick markerar ett intervall och pilarna går till närmaste port i pilens riktning, på bilden räknat från portytornas mitt. Har modellen portar på baksidan växlar *Framsida | Baksida* över panelen mellan sidorna, och panelen vänder sig själv när en port på andra sidan markeras, till exempel från trädet.

**Rackvyn** (#255) ritar ett rack med utrustningens bilder på sina enheter, med enhetsnummer på skenorna. En modell utan bild ritas som en neutral front med namn, modell och portrutnät. *Framsida | Baksida* vänder hela racket. Portarna har samma färgton som i frontpanelen och förklaras med prick och text under racket. Klick på en utrustning öppnar den, klick på en port markerar porten, och en kort hovring visar utrustningens kort. Lediga enheter är streckade, och i en aktiv plan ger ett klick på en ledig enhet *Ny utrustning* i planen med site, rack och position ifyllda. Planens ändringar syns: ny utrustning streckad i planerad färg, det planen tar bort överstruket. Siten visar en liten bild av varje rack som öppnar rackvyn som arbetsyta (#251), och racket står i adressen (`?tool=rack&rack=<id>`). Bildtiden mäts i prestandapanelen när rackvyn är öppen, med samma mål som frontpanelen (33 ms per bild).

**Rumsvyn** (#256) visar ett rum uppifrån: racken som rutor i sina rader med en gång mellan raderna, och rack utan plats i en egen rad sist. Den tjocka kanten är rackets framsida, så att fronterna som möts över en gång syns. Varje rack visar namn, lediga enheter (*34 av 42 U lediga*), antal enheter och en stapel för hur fullt det är, plus den högsta kritikaliteten i racket med prick och nivå. En röd ram betyder schemats kritiska nivå eller högre (#180). Klick på ett rack öppnar rackvyn. *Visa kablar mellan rack* ritar kopplingarna mellan racken som linjer med antalet. I en plan är nya rack streckade i planerad färg och rack som placeras om inramade i samma färg. Sitepanelen listar sitens rum per byggnad med antal rack, lediga enheter och en liten bild som öppnar rumsvyn som arbetsyta (`?tool=room&room=<id>`).

**Fiberritningen** (#254) är underlaget till en tekniker vid ett kabelbrott: kablarna i en kabel, en trasésträcka, en site eller ett urval, fiber för fiber. Den öppnas med *Fiberritning* i kabelns, sträckans och sitens panel och i urvalsraden, som arbetsyta (#251), och adressen (`?tool=fibre&fibre=cables:12`) öppnar samma ritning för den som får se den.

- **Per kabel:** typ, antal ledare, längd och siterna i ändarna. Per fiber: nummer med färg enligt kabeltypens färgkod (IEC 60304), vad den skarvas eller termineras till i A- och B-änden (en annan kabels fiber eller en port i en ODF), och vad den bär: kretsar, eller mörk, uthyrd mörk fiber eller reserv, och om den är reserverad.
- **Påverkade tjänster** listas bredvid, med kritikalitet (#176, en prick och nivån).
- **Brottpunkt:** *Välj brottpunkt i kartan* (för en kabel) lägger kartan bredvid ritningen och tar emot ett klick på kabeln. Ritningen visar avståndet längs kabelns sträckning från punkten till närmaste skarv eller ODF åt båda hållen, att jämföra med en OTDR-mätning.
- **Skriv ut** ger ritningen ensam på A3 liggande i ljust tema, och kan sparas som PDF ur utskriftsdialogen. **Spara som SVG** laddar ned den som bild.
- **I en plan** ritas planens vy: ett planerat ingrepp syns före och efter.
- **Behörighet:** det som ligger utanför omfånget visas som *dold* och kablar utanför det räknas bara, som i spårningen.

**Tjänstevyn** (#257) ritar en tjänsts hela väg från vänster till höger, utan kartan. Den öppnas med *Tjänstevy* i tjänstepanelen och i spårningen av en tjänst, som arbetsyta (#251). Adressen (`?tool=service&service=<id>`) öppnar samma vy.

- **Per site** visas utrustningen med modellens bild (#214), där de portar tjänsten använder lyser och resten är nedtonat. En modell utan bild ritas som en neutral front med modellnamnet. En site med bara skarvade ledare är en skarvpunkt.
- **Mellan siterna** ritas kablarna med kod, kabeltyp, längd och ledarnummer, plus ledarens färg enligt IEC 60304.
- **Lagren** syns samtidigt: transmissions- och logiska kretsar är band ovanför det fysiska lagret, över de siter de spänner.
- **Redundanta vägar** (flera fysiska kretsar) ritas som parallella rader, *Väg 1*, *Väg 2* …
- **Klick** öppnar objektet i panelstacken. En vald kabel eller utrustning säger dessutom om tjänsten har en annan väg, eller faller (#10).
- *Visa i kartan* går till halva skärmen med vägen ritad och spårningen bredvid.
- Hopp utanför omfånget visas som *Dold*, som i spårningen. I en plan ritas planens vy med planerade objekt streckade violetta.
- Data kommer från `GET /api/services/{id}/drawing`: spårningen plus modellbilder, portarnas platser och kablarnas typ och längd. Bildtiden mäts i prestandapanelen när tjänstevyn har visats (mål 33 ms per bild). En tjänst med 58 hopp ritas om på under 1 ms vid val.

**Innehållsträdet** (verktyget *Innehåll*, #17, #250) visar hela nätet med **Sverige** som rot, eller omfångets namn (till exempel *Region Nord*) för den som bara ser en del: sitetyp → site → plats → utrustning → kort → port. En sitetyp med fler än 500 siter delas i intervall efter kod (*SKP-00001 – SKP-00500*), så att ingen nivå får fler än 500 barn. Roten och varje grupp visar antalet siter, räknat inom omfånget. Varje nivå hämtas när den fälls ut, och raderna virtualiseras (Angular CDK), så hela nätet går att bläddra i och även ett nav med tusentals portar är smidigt. När en site eller utrustning öppnas fälls vägen dit ut från roten, och raden markeras och scrollas fram. *Expandera siten* fäller ut allt i den öppna siten, aldrig hela nätet. Grupperna är sitetyper och inte områden, eftersom omfångens områden bara täcker delar av nätet och kan överlappa. Tangentbordet fungerar som i ett vanligt träd: pilar, Home/End och Enter. Enter på en kopplad port öppnar spårningen från porten.

**Grannskapsgrafen** (`/graf`, växla med *Karta | Grannskap* uppe till höger, #20) utgår från den site som är öppen, eller från `?g=`. Kanter är kablar (fysiskt lager) och transmissions- och logiska kretsar mellan siter, sammanslagna per site och lager. *Expandera en nivå* hämtar grannarna till upp till 40 oexpanderade siter åt gången. Dubbelklick expanderar en enskild site, *Backa* tar tillbaka senaste expansionen och *Fokusera på vald* börjar om från den öppna siten. Lager och sitetyper filtreras med kryssrutor. Färg är status, storlek är sitetyp. Grafen ritas med Sigma (WebGL) och startar från kartpositionerna innan ForceAtlas2 ordnar den.

## Avancerad sökning

Snabbsöket (Ctrl+K) hittar objekt på namn och kod. För frågor som *alla radiositer med en radio på 3500 MHz* eller *siter med minst tre antenner* finns **Avancerad sökning** i verktygsfältet:

- Villkor på site (typ, livscykel), på utrustning (kategori, modell, attribut och minsta antal) och på tjänster som passerar siten. Flera utrustningsvillkor kombineras med OCH.
- Attribut och tillåtna värden kommer från typkatalogen, så nya modeller blir sökbara utan kodändring.
- Träffarna markeras i kartan i ett eget lager, på alla zoomnivåer, och övriga nätet tonas ned. En rad i listan öppnar siten i panelstacken.
- Färdiga exempel visar vad sökningen klarar och används i demon.

## Planer

- **Planer** i verktygsfältet öppnar planernas arbetsyta (#252), som standard på halva skärmen bredvid kartan (#251). Längst ned listas planerna du kan se, med status som prick och text: utkast (planerad), införd (i drift), avbruten (borttagen) och flaggad (konflikt). *Ny plan* är en kort rad: ett namn och *Skapa och visa*. Beskrivning och planer den bygger på ligger under en utfällning. Finns inga planer förklarar listan vad en plan är, med *Ny plan* som primär knapp.
- **Att välja en plan växlar hela appen till planens vy:** produktion, plus planerna den bygger på, plus planen. Planen står i adressen (`?plan=`), så en länk öppnar samma vy. En violett etikett i verktygsfältet visar vilken plan som gäller, och × går tillbaka till produktion.
- **Arbetsytan för en plan (#252)** svarar på vad planen gör, var, vad som påverkas, vad som hindrar den och vad nästa steg är:
  - **Översikt:** namn, status, vem som skapade den (och om en agent föreslog den), vilka planer den bygger på och antal ändringar per slag: ny, ändrad, borttagen, flyttad och kopplad. Ett klick på ett slag filtrerar tabellen. Det finns alltid ett tydligt nästa steg som primär knapp: *Lös konflikter*, *Lägg till ändringar*, *För in i produktion* eller, för en införd plan, *Visa produktion*.
  - **Att åtgärda** står först när planen är flaggad eller när ändringar inte längre passar produktionen eller krockar med andras anspråk. Därefter kommer klassning och krav (#179) med förslag på åtgärder.
  - **Ändringar** är en tabell med filter per slag och *Att åtgärda*, sökning och sortering (att åtgärda först, i ordning, efter slag, efter objekt). En rad visar slag, objektet med ikon, ändringen med skälet om den inte passar, och status med prick och text. Klick öppnar objektet och markerar dess siter i kartan bredvid. Tabellen visar 50 rader åt gången (*Visa fler*), så att växlingen till planen håller budgeten. Ändringar från planer den bygger på ligger utfällbara under tabellen.
  - **Påverkan:** kretsarna genom det planen kopplar, kopplar ifrån, tar bort, flyttar, kapar eller ändrar livscykel på, kretsarna som åker på dem och kretsarna vars väg eller länkar planen ändrar. För varje krets visas om vägen är *hel*, *bruten* eller *finns inte* före och efter planen, plus antalet tjänster. Tjänster som ändras visas med *hel*, *försvagad* (några av dess kretsar bryts) eller *bruten*. *Före* och *efter* spårar kretsen i produktion respektive i planen. Spårningen i produktion heter *Spårning … i produktion*.
  - **Reservationer** listar planens reservationer med *Släpp*.
  - **Lägg till** (bara för utkast) har formulären för nytt i planen och vägförslag. Ett förifyllt utkast, till exempel från rackvyn eller kartans högerklick, öppnar den fliken.
- **I planvyn** räknas spårning och påverkan i planen, och siterna planen rör markeras i kartan.
- **Ändringar görs där objekten finns.** Välj en port i utrustningspanelen:
  - *Koppla bort i planen* bryter en koppling som finns i planens vy.
  - *Koppla i planen…* väljer första änden. Välj sedan en port på valfri utrustning och *Koppla … hit*.
- **Nytt i planen (#107):** *Ny site* placeras vid kartans mittpunkt. *Ny utrustning* och *Ny kabel* anges med sitekod, befintlig eller planerad. Planerade objekt ritas streckat violetta i kartan, och länkarna till dem öppnar inget, eftersom de inte finns i produktion än. Deras portar och fibrer kopplas som andra i planen och syns i spårningen som *(planerad)*.
- *För in i produktion* och *Avbryt planen* kräver en bekräftelse. Planer som bygger på planen och som påverkas flaggas, och det står i panelen vilka det gäller.
- Detaljpanelerna (site, utrustning och kabel) visar produktion. Det som skiljer i planen syns i planpanelen, i spårning och i påverkan.
- **Reservationer och konflikter (#25):**
  - *Reservera för planen* finns på en port i utrustningspanelen, och *Reservera fibern för planen* med ledarnummer finns i kabelpanelen.
  - Planpanelen listar planens reservationer, och de går att släppa.
  - Anspråk visas där resursen finns: *Reserverad av …* och *Önskas av …*.
  - En konflikt är korall, som all annan konflikt: på porten i frontpanelen, på planen i listan och på operationen, med skälet och om den stoppar införandet.
