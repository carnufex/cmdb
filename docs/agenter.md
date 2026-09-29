# Agenter

CMDB:n är byggd för att agenter ska kunna läsa och fråga nätet lika lätt som en människa klickar i UI:t (ADR-0011). Agenter går genom samma API, inloggning och behörighet som webben och får ingen egen väg till datan.

## MCP

| | |
|---|---|
| Adress | `https://cmdb.rosenvall.se/mcp` (lokalt `http://localhost:8480/mcp`) |
| Transport | Streamable HTTP, stateless |
| Inloggning | Bearer-token från homelabbets Authentik. Klienter och tjänstekonton beskrivs i #62. |

### Verktyg

Alla verktyg är skrivskyddade. Ändringar kommer att föreslås som planer som en människa för in (#64).

| Verktyg | Vad det svarar på |
|---|---|
| `search` | Objekt på kod, namn, id eller attributtext (samma som snabbsöket) |
| `get_object` | Allt UI:t visar för ett objekt. Tar `"site:1268"` eller en exakt kod, till exempel `RAD-000007`. |
| `find_sites` | Strukturerade frågor: sitetyp, livscykel, utrustning (kategori, modell, attribut, antal) och tjänster (samma som avancerad sökning) |
| `impact` | Vad ett kabelavbrott eller ett siteavbrott påverkar: kretsar och tjänster |
| `neighbourhood` | Siter inom 1–3 kabelhopp från en site |
| `describe_catalog` | Sitetyper, livscykler, tjänstetyper, kategorier med attribut och alla modeller |

Varje objekt i svaren har en stabil referens (`ref`, till exempel `site:1268`) och en `url` som öppnar samma objekt i UI:t, så att människan kan se det agenten såg. Listor är begränsade till 50 poster och anger när de är trunkerade.

### Resurser och prompter

- **Resurser:** `cmdb://docs/domanmodell`, `cmdb://docs/plan` och `cmdb://docs/arkitektur`. Det är samma dokument som i `docs/`, inbäddade vid bygget.
- **Prompter:** `cable_cut_impact` (vad händer om en kabel kapas) och `find_sites_by_equipment` (översätt en fråga till `find_sites`).

## För utvecklare

Ett verktyg bor i samma slice som endpointen det motsvarar, till exempel `Features/Query/QueryTools.cs`, och anropar samma statiska läsfunktion (`QuerySitesEndpoint.RunAsync`). Nya läsfunktioner ska få både en endpoint och ett verktyg, och verktyget ska testas med MCP-klienten i `tests/Cmdb.Api.IntegrationTests/Agents/`.
