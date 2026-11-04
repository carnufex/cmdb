# ADR-0023: Chatten går via Lots, som personen hela vägen

**Status:** Accepterad (alternativ A i #331) · **Datum:** 2026-11-04 · **Beslut:** #331 · **Genomförs i:** #330

## Kontext
Den inloggade i cmdb ska kunna chatta med sin kontext i Lots och se svaren på kartan. Kravet är att personen pratar som sig själv: chatten får inte kunna visa mer än personens omfång, och både Lots audit och cmdb:s läslogg ska visa personen.

Kedjan har två sträckor med var sin identitet:

1. **Webbläsaren till Lots.** Lots tar bara emot tokens utfärdade för Lots (Lots ADR 0051), så cmdb:s token kan inte skickas vidare.
2. **Lots till cmdb.** Lots kontext `cmdb` anropar `/mcp` med det delade tjänstekontot `cmdb-agent-demo`. Cmdb ser då tjänstekontot och dess omfång. En användare med ett begränsat omfång skulle kunna få svar om objekt utanför det, vilket regel 5 i CLAUDE.md förbjuder.

Authentik saknar RFC 8693-token exchange. Lots har strategin `user-connected`: personen kopplar sitt eget konto en gång, och token sparas krypterad per person.

## Alternativ
- **A. Riktig Lots-token i webbläsaren och `user-connected` på kontexten `cmdb`, med en egen klient `cmdb-lots`.** Cmdb ser personen på allt. Den egna klienten gör att läsloggen visar att anropet kom via Lots. Varje person kopplar en gång, och kopplingen faller om den står oanvänd längre än refresh-tokenets livstid.
- **B. Behåll tjänstekontot och scopa bara kartan.** Inget nytt i produktion, men chattsvaret kan nämna objekt utanför omfånget.
- **C. En ny kontext med personlig koppling bredvid den gamla.** Inget ändras för dem som använder `cmdb` i dag, men två nästan likadana kontexter ska underhållas och routern måste välja mellan dem.

## Beslut
**A** (föreslaget).

- **Inloggning i Lots.** Webben loggar in i Lots-applikationen i Authentik med PKCE, i den session personen redan har. Det sker i en popup från ett klick, eftersom Authentiks sidor inte får ligga i en ram (`X-Frame-Options: DENY`). Token ligger bara i minnet. En Lots-session för någon annan än den som är inloggad i cmdb (`sub` skiljer sig) används inte.
- **Protokoll.** AG-UI, direkt från webbläsaren till Lots `POST /ag-ui` (Lots ADR 0067). Lots svarar bara origins på sin lista, utan credentials. Ingen proxy i cmdb, så Lots-token passerar inte vår server.
- **Kartan styrs av ett vanligt verktyg.** `show_on_map` är ett läsverktyg i MCP och går genom Lots policy och audit. Webben ser anropet i strömmen och kör samma fråga mot `/api/query/sites` med personens cmdb-token. Argumenten är en fråga och aldrig ett svar: bara kända fält skickas vidare, API:t validerar dem och svaret följer personens omfång.
- **Inga frontend-tools.** Webbläsaren kör inga verktyg åt agenten, och ingen körning väntar på en webbläsare.
- **Orter.** Nätet har inga ortsnamn, så `place` på `find_sites`, `show_on_map` och `/api/query/sites` slår upp en kommun i `catalog/places/` (SCB, CC0). Den som har dolda positioner får inte söka på plats, eftersom det skulle avslöja var en site ligger.

## Konsekvenser
- Alla som använder kontexten `cmdb` i Lots måste koppla sitt konto en gång, också utanför widgeten. Tills dess startar `/ag-ui` ingen körning utan ber om kopplingen.
- Schemalagda körningar och kanaler i kontexten `cmdb` använder personens koppling eller misslyckas stängt.
- Homelabbet ändras: en redirect-URI på klienten `lots`, en ny klient `cmdb-lots`, `Auth__TrustedApplications` i cmdb och profilen i Lots.
- Kommungränserna är förenklade. De räcker för "siterna i Gävle" men inte för att avgöra en gräns, och en site vid kusten kan hamna utanför alla kommuner.
- Att stänga chatten stoppar inte en körning i Lots. Körningen finns kvar i personens historik.
