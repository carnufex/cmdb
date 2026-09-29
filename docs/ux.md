# UX-principer

## Känsla

Mörk, informationstät och lugn, i samma familj som Linear. Tunna linjer (1 px), ytor som separeras med ljushet i stället för skuggor, och färg som bara används för att betyda något.

**Men:** allt bygger på design-tokens med ett mörkt standardtema **och** ett ljust tema. Projektorer och dagsljus i fält gör enbart mörkt tema oanvändbart. Testa demon på den projektor som faktiskt ska användas.

## Skärmstorlek

Minsta stödda storlek är **1280×720 CSS-pixlar**, vilket motsvarar 1080p med 150 % skalning (#72). Där ska kartan, ett verktyg (avancerad sökning eller prestanda) och objektpanelen kunna vara öppna samtidigt, utan radbrytning i verktygsfältet och utan sidscroll. Paneler och verktyg scrollar internt. Under 1440 px bredd blir objektpanelen och verktygen smalare, så att kartan behåller ungefär 500 px. Större skärmar ger kartan all extra yta.

## Färg betyder status

| Status | Färg |
|---|---|
| Planerad | violett |
| Under byggnation | blå |
| I drift | lugn grön |
| Konflikt / fel | varm korall (också enda färgen för primär åtgärd) |

Status visas alltid med **prick och text**, aldrig bara med färg (WCAG 2.1 AA).

## Få klick

- **Allt är en länk.** Varje objektreferens öppnas i en sidopanel. Panelerna staplas som en brödsmulestig och varje vy har en URL.
- **Förhandsvisning vid hovring** med det viktigaste, så att man ofta slipper klicka.
- **Inga modala kedjor.** Redigering sker direkt i panelen.
- **Kommandopaletten (Ctrl+K) gör saker**, inte bara söker: "ny site från mall", "patcha 1–24", "spåra tjänst". Tom palett visar åtgärderna för det öppna objektet, text ger matchande åtgärder före objektträffar och `>` först ger bara åtgärder. Åtgärder registreras i `CommandRegistry`, och en panel kan lägga till tillfälliga åtgärder för det som är markerat, till exempel "Spåra från port bh1" (#21).
- **Tangentbord först** för återkommande flöden.

## Massprovisionering

- **Mallar på alla nivåer.** En sitemall innehåller byggnad, rack, utrustning och intern kabeldragning.
- **Mönsteroperationer.** Markera portintervall som i ett kalkylark och dra till en kabel. Förskjutning och steglängd kan anges.
- **Rita på kartan.** En kabel mellan två siter skapar ledarna automatiskt och föreslår terminering.
- **Kalkylarksläge.** Valfritt urval blir ett redigerbart rutnät. Klistra in från Excel och fyll nedåt.
- **Urval som grund för åtgärder.** Lasso på kartan eller filter, och sedan en åtgärd på hela urvalet.

Allt massarbete sker i en plan: förhandsvisning av skillnader, konfliktkontroll och ångra genom att kasta planen.

## Linser

| Lins | Svarar på |
|---|---|
| Karta | Var? |
| Innehållsträd | Vad sitter i vad? |
| Frontpanel | Vilka portar, vad är kopplat? |
| Spårschema | Vilken väg tar tjänsten? |
| Grannskapsgraf | Vad finns runt omkring? Expandera och backa. |

## Avancerad sökning

Snabbsöket (Ctrl+K) hittar objekt på namn och kod. För frågor som *alla radiositer med en radio på 3500 MHz* eller *siter med minst tre antenner* finns **Avancerad sökning** i verktygsfältet:

- Villkor på site (typ, livscykel), på utrustning (kategori, modell, attribut och minsta antal) och på tjänster som passerar siten. Flera utrustningsvillkor kombineras med OCH.
- Attribut och tillåtna värden kommer från typkatalogen, så nya modeller blir sökbara utan kodändring.
- Träffarna markeras i kartan i ett eget lager, på alla zoomnivåer, och övriga nätet tonas ned. En rad i listan öppnar siten i panelstacken.
- Färdiga exempel visar vad sökningen klarar och används i demon.
