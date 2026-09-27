# ADR-0011: Agentic-first: MCP-server i API:t, agenter som vanliga konsumenter

**Status:** Föreslagen · **Datum:** 2026-09-27

## Kontext
CMDB:n ska vara *agentic-first*: agenter (Claude, Codex, egna automationer) ska kunna läsa, fråga och föreslå ändringar lika lätt som en människa klickar i UI:t. Samtidigt gäller behörighetsmodellen (ADR-0007), spårbarheten (ADR-0006) och planerna (ADR-0005) även för agenter. En agent med för breda rättigheter är samma risk som en integration med för breda rättigheter.

Läge i september 2026: Model Context Protocol (MCP) är standarden som Claude, Codex och de flesta agentramverk talar. Det officiella C#-SDK:t (`ModelContextProtocol.AspNetCore` 2.2) stöder Streamable HTTP. Homelabbets Authentik stöder `client_credentials` och device code, men inte dynamisk klientregistrering.

## Beslut
1. **MCP-servern ligger i API:t** på `/mcp` (Streamable HTTP). Den anropar samma slices som REST-API:t och går därmed igenom samma autentisering, samma behörighetsomfång (#22) och samma `Server-Timing`. Det blir ingen separat tjänst med egen dataväg.
2. **Uppgiftsorienterade verktyg, inte tabell-CRUD.** Agenten får samma operationer som en människa använder:
   - `search`
   - `get_object`
   - `find_sites` (avancerad sökning)
   - `impact`
   - `trace` (när #9 finns)
   - `neighbourhood`
   - `describe_catalog`

   Svaren är kompakta, har stabila referenser (`site:1268`) och innehåller länkar till UI:t (`https://cmdb.rosenvall.se/?p=site:1268`) så att en människa kan följa vad agenten såg.
3. **Resurser och prompter:** typkatalogen, domänmodellen och prestandabudgeten exponeras som MCP-resurser. Vanliga uppdrag, till exempel "vad händer om kabel X kapas", blir MCP-prompter.
4. **Agenter skriver aldrig direkt i produktion.** Skrivande verktyg arbetar bara i planer (ADR-0005): agenten föreslår en ändringsmängd och en människa granskar och för in den. Verktygen är skrivskyddade tills planer finns (#24).
5. **Agenter är identifierade konsumenter** med eget omfång, precis som integrationer (ADR-0007):
   - **Obevakade agenter:** tjänstekonto i Authentik och `client_credentials`.
   - **Interaktiva agenter** (Claude Code, Codex): MCP:s OAuth-flöde mot en förregistrerad publik klient (`cmdb-mcp`), eftersom Authentik saknar dynamisk registrering.

   Varje anrop loggas med agentens identitet i operationsloggen (#23).
6. **För agenter utan MCP** publiceras OpenAPI (`/api/openapi.json`) och en `llms.txt` som beskriver API:t, verktygen och datamodellen.
7. **Skydd:** gränser för storlek och antal träffar per svar, rate limit per klient och en spärr mot massuttag (en CMDB är en aggregeringsmaskin, se arkitekturen).

## Alternativ
- **Bara REST/OpenAPI.** Fungerar för alla, men varje agent måste själv lista ut flöden och kontext. Ger sämre träffsäkerhet och mer tokens.
- **Fristående MCP-server som anropar REST-API:t.** Enkel att bygga, men blir ännu en tjänst att drifta och en extra hop, och behörigheten måste hållas i synk.
- **Direkt databasåtkomst (SQL) för agenter.** Kraftfullt men kringgår behörighet, planer och operationslogg. Oförenligt med ADR-0006 och ADR-0007.
- **GraphQL.** Bra för flexibla frågor, men agentekosystemet samlas kring MCP, och behörighet per nod är svårare att garantera.

## Konsekvenser
- MCP-verktygen blir en del av API:ts kontrakt och får integrationstester precis som endpoints.
- Frågan i punkt 5 om Claude Code och Codex kan använda en förregistrerad klient mot Authentik utan dynamisk registrering verifieras i första implementationsissuet. Om det inte går används tokens från tjänstekonton även interaktivt.
