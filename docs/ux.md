# UX-principer

## Känsla

Mörk, informationstät och lugn, i samma familj som Linear. Tunna linjer (1 px), ytor som separeras med ljushet i stället för skuggor, och färg som bara används för att betyda något.

**Men:** allt bygger på design-tokens med ett mörkt standardtema **och** ett ljust tema. Projektorer och dagsljus i fält gör enbart mörkt tema oanvändbart. Testa demon på den projektor som faktiskt ska användas.

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
- **Kommandopaletten (Ctrl+K) gör saker**, inte bara söker: "ny site från mall", "patcha 1–24", "spåra tjänst".
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
