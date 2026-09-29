# Agenter

CMDB:n är byggd för att agenter ska kunna läsa och fråga nätet lika lätt som en människa klickar i UI:t (ADR-0011). Agenter går genom samma API, inloggning och behörighet som webben och får ingen egen väg till datan.

## MCP

| | |
|---|---|
| Adress | `https://cmdb.rosenvall.se/mcp` (lokalt `http://localhost:8480/mcp`) |
| Transport | Streamable HTTP, stateless |
| Inloggning | Bearer-token från homelabbets Authentik, se nedan |

### Koppla in en agent

**Claude Code, interaktivt (din egen inloggning):**

```bash
claude mcp add --transport http cmdb https://cmdb.rosenvall.se/mcp --client-id cmdb-mcp --callback-port 33418
```

Vid första anropet öppnas webbläsaren för inloggning i Authentik med en cmdb-användare (`cmdb-demo-full`, `cmdb-demo-region` eller `cmdb-demo-projekt`, lösenordet `CMDB_DEMO_PASSWORD` i Bitwarden), eller med ett eget konto som ligger i någon av cmdb-grupperna. Authentik saknar dynamisk klientregistrering, så klienten `cmdb-mcp` är förregistrerad med callback på port 33418. Servern pekar själv ut Authentik via `/.well-known/oauth-protected-resource` (RFC 9728).

Lokalt byts adressen mot `http://localhost:8480/mcp`. Codex och andra klienter använder samma klient-id och callback.

**Obevakade agenter (tjänstekonto):**

```bash
TOKEN=$(curl -s https://authentik.rosenvall.se/application/o/token/   -d grant_type=client_credentials -d client_id=cmdb-agents   -d username=cmdb-agent-demo -d password="$CMDB_AGENT_TOKEN" -d "scope=openid profile email" | jq -r .access_token)
claude mcp add --transport http cmdb https://cmdb.rosenvall.se/mcp --header "Authorization: Bearer $TOKEN"
```

`CMDB_AGENT_TOKEN` är tjänstekontots app-lösenord i Bitwarden. Tokenet gäller en timme. Nya tjänstekonton läggs till i homelabbets blueprint `apps-cmdb.yaml` i gruppen `cmdb-agents`, med app-lösenordet i Bitwarden.

**Identitet och gränser:** API:t litar på tokens från cmdb-applikationerna i Authentik (`cmdb`, `cmdb-mcp` och `cmdb-agents`) men inte från andra applikationer i homelabbet. Varje MCP-anrop loggas med användare och klient (`azp`), och `/api/me` visar klienten. Agenter begränsas till 20 anrop per sekund (60 i skur) per klient och användare. Människor i webben begränsas inte.

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

## Utan MCP

- **OpenAPI:** `https://cmdb.rosenvall.se/api/openapi.json`, öppen utan inloggning. Datan bakom kräver token.
- **`llms.txt`:** `https://cmdb.rosenvall.se/llms.txt` beskriver CMDB:n för språkmodeller: MCP-adress, inloggning, verktyg, REST, modellen i korthet och bra första frågor.

## För utvecklare

Ett verktyg bor i samma slice som endpointen det motsvarar, till exempel `Features/Query/QueryTools.cs`, och anropar samma statiska läsfunktion (`QuerySitesEndpoint.RunAsync`). Nya läsfunktioner ska få både en endpoint och ett verktyg, och verktyget ska testas med MCP-klienten i `tests/Cmdb.Api.IntegrationTests/Agents/`.
