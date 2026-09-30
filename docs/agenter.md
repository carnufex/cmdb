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

Inget verktyg ändrar produktion. Agenter föreslår ändringar som planer, och en människa granskar och för in dem i webben (#64). Det finns inget verktyg som för in eller avbryter en plan, och en token från en agentklient (`cmdb-agents`) nekas att föra in planer även via REST.

| Verktyg | Vad det svarar på |
|---|---|
| `search` | Objekt på kod, namn, id eller attributtext (samma som snabbsöket) |
| `get_object` | Allt UI:t visar för ett objekt. Tar `"site:1268"` eller en exakt kod, till exempel `RAD-000007`. |
| `find_sites` | Strukturerade frågor: sitetyp, livscykel, utrustning (kategori, modell, attribut, antal) och tjänster (samma som avancerad sökning) |
| `impact` | Vad ett avbrott på en kabel, utrustning eller site påverkar: kretsar och tjänster, med kedjan av kretsar som når varje tjänst |
| `trace` | Spåra en tjänst eller krets ned genom lagren, eller den fysiska vägen från en terminal genom patchar, skarvar och ledare |
| `neighbourhood` | Siter inom 1–3 kabelhopp från en site |
| `describe_catalog` | Sitetyper, livscykler, tjänstetyper, kategorier med attribut och alla modeller |
| `list_plans` | Planer du kan se, med status, antal ändringar, konflikter och vem som gjort dem |
| `create_plan` | Skapar ett utkast. Planen märks med agentens identitet och `createdVia: mcp`. |
| `add_to_plan` | Lägger till ändringar i ordning: koppla, koppla bort, livscykel, namnbyte, ny site, ny utrustning och ny kabel. Ett nytt objekt får en planerad referens (`target`, till exempel `site:-12`) som senare ändringar använder. Varje ändring kontrolleras mot planens vy, och svaret visar det som inte passar produktion och konflikter med andra planers anspråk eller reservationer. |
| `connect_ports` | Ett portintervall mot ett annat i frontpanelens ordning, till exempel "patcha port 1–24 på SW-1 mot ODF-3". Fungerar också mot planerad utrustning (`equipment:-12`). |
| `list_site_templates`, `create_site_from_template` | Sitemallar, och en hel site ur en mall i planen |
| `splice_ports_to_cable` | Mönsterpatchning: portar från en startport mot fibrer från en startfiber, med steg, på kabelns A- eller B-sida |
| `terminate_cable` | Terminerar en kabel i båda ändar på lediga ODF-portar, som planen föreslår |
| `preview_plan` | Planens ändringar mot produktion, inklusive de planer den bygger på, med problem, konflikter och om den är redo att föras in. `url` öppnar planen i webben. |

Skrivande verktyg kräver grupp `cmdb-full` eller `cmdb-agents` och följer omfånget: en agent kan inte röra terminaler eller objekt utanför sitt omfång.

Varje objekt i svaren har en stabil referens (`ref`, till exempel `site:1268`) och en `url` som öppnar samma objekt i UI:t, så att människan kan se det agenten såg. Listor är begränsade till 50 poster och anger när de är trunkerade.

### Resurser och prompter

- **Resurser:** `cmdb://docs/domanmodell`, `cmdb://docs/plan` och `cmdb://docs/arkitektur`. Det är samma dokument som i `docs/`, inbäddade vid bygget.
- **Händelselogg:** `cmdb://changelog` visar vad som ändrats för användare, med issuereferenser (#82).
- **Prompter:** `cable_cut_impact` (vad händer om en kabel kapas) och `find_sites_by_equipment` (översätt en fråga till `find_sites`).

## I ett skal: `cmdb`

Kodagenter i en terminal (Claude Code, Codex) och skript använder med fördel kommandoradsverktyget `cmdb` (#86). Det anropar samma REST-API som webben och MCP-verktygen, så omfång, planer och loggning gäller lika. Verktygsbeskrivningarna tar ingen plats i agentens kontext, och utdata går att kombinera med `grep`, `jq` och pipes.

**Installera:** ladda ned binären för din plattform från den senaste releasen `cli-v*` på [GitHub](https://github.com/carnufex/cmdb/releases) (Windows, Linux och macOS, fristående och utan .NET), döp den till `cmdb` och lägg den i `PATH`. Kontrollsummor finns i `SHA256SUMS`. Utvecklare kör `dotnet run --project src/cli -- <kommando>`.

**Logga in:**
- `cmdb login` öppnar inloggningen i webbläsaren (PKCE mot klienten `cmdb-mcp`) och sparar token lokalt. `cmdb logout` glömmer den.
- `CMDB_TOKEN` används som den är.
- För ett tjänstekonto används `CMDB_CLIENT_ID`, `CMDB_USERNAME` och `CMDB_PASSWORD` (client credentials).
- `CMDB_URL` (eller `--url`) väljer miljö. Standard är demon.

**Kommandon** (`cmdb help`):

| Kommando | Motsvarar |
|---|---|
| `cmdb search <text> [--limit N]` | `search` |
| `cmdb get <ref\|kod>` | `get_object` |
| `cmdb find-sites [--type T] [--lifecycle L] [--model KEY] [--category C] [--service-type S]` | `find_sites` |
| `cmdb impact <ref>` | `impact` |
| `cmdb trace <service:ID\|circuit:ID\|terminal:ID\|kod>` | `trace` |
| `cmdb neighbourhood <site> [--hops N]` | `neighbourhood` |
| `cmdb catalog` | `describe_catalog` |
| `cmdb plans`, `cmdb plan plan:ID` | `list_plans`, `preview_plan` |
| `cmdb whoami` | `/api/me` |

**Utdata och felkoder:**
- Utdata är kompakt text, en rad per objekt med referens, kod, namn och länk till webben. `--json` ger API:ts svar oförändrat.
- Felkoderna är 0 för ok, 1 för fel användning, 2 om något inte finns eller ligger utanför omfånget, 3 om man inte är inloggad eller behörig, och 4 för annat fel.

**Mätt mot demon** (2026-09-30, svarsstorlek i byte; ungefär en token per fyra byte):

| | MCP | CLI (text) |
|---|---|---|
| Verktygsbeskrivningar i kontexten | 14 701 (`tools/list`, varje session) | 1 326 (`cmdb help`, bara vid behov) |
| Siter med modellen acme-bb-6 (10 st) | 3 120 | 1 111 |
| Vad påverkar kabel K-000001 | 2 155 | 514 |
| Spåra tjänst TJ-0000001 | 11 140 (alla hopp) | 171 (kretsar per lager; hoppen med `--json`) |

Varje uppgift tog ett anrop med båda. CLI:t löser koder direkt (`cmdb trace TJ-0000001`), medan MCP-verktyget `trace` kräver en referens. Siffrorna är svarsstorlekar, inte uppmätta tokens i en Claude Code-session. För agenter i en terminal ger CLI:t ändå tydligt mindre kontext per uppgift. MCP är fortfarande rätt för chattklienter utan skal.

## Utan MCP

- **OpenAPI:** `https://cmdb.rosenvall.se/api/openapi.json`, öppen utan inloggning. Datan bakom kräver token.
- **`llms.txt`:** `https://cmdb.rosenvall.se/llms.txt` beskriver CMDB:n för språkmodeller: MCP-adress, inloggning, verktyg, REST, modellen i korthet och bra första frågor.

## För utvecklare

Ett verktyg bor i samma slice som endpointen det motsvarar, till exempel `Features/Query/QueryTools.cs`, och anropar samma statiska läsfunktion (`QuerySitesEndpoint.RunAsync`). Nya läsfunktioner ska få både en endpoint och ett verktyg, och verktyget ska testas med MCP-klienten i `tests/Cmdb.Api.IntegrationTests/Agents/`.
