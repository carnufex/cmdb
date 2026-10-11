# Röstagenterna i ElevenLabs

Tre agenter (ADR-0015, ADR-0016, #151). Här finns allt de består av: prompter, runbooks och inställningar. Skriptet `deploy.py` gör ElevenLabs likt repot.

- **Service desk (Saga)** svarar alla samtal: "Hej, det här är Saga på service desk, hur kan jag hjälpa dig?" Alla agenter svarar på det språk uppringaren talar (svenska eller engelska), byter utan att nämna det och är rakt på sak.
  - Passertaggar som slutat fungera löser hon själv, efter verifiering.
  - IT (lösenord, konto, dator, utrustning) lämnas över till **IT-självhjälpen**.
  - Nätet (CMDB, fiber, stationer, larm) lämnas över till **NOC-agenten** (Driftagenten).
  - Allt annat: "Det kan jag tyvärr inte hjälpa dig med. Vill du att en människa ringer upp dig? Det är cirka X minuters kö."
- **Överlämningen** sker med `transfer_to_agent`, bara från växeln. Transkriptet och samtals-id:t följer med, så en verifiering gäller i hela samtalet. IT-självhjälpen och NOC kan inte lämna tillbaka: med en väg tillbaka studsade ett samtal mellan växeln och NOC tills talsyntesen föll (#154). Gäller samtalet något annat erbjuder de uppringning.
- **Varje agent har sin egen MCP-ingång** med bara sina verktyg: `/voice/servicedesk/mcp`, `/voice/it/mcp` och `/voice/mcp` (NOC).
- **Utgående risksamtal** går direkt till NOC-agenten.

| Fil | Innehåll |
|---|---|
| `servicedesk.md` | Växelns prompt: passertaggar, överlämning och uppringning |
| `it.md` | IT-självhjälpens prompt: lösenord och utrustning |
| `prompt.md` | NOC-agentens prompt: arbetsgång, verifiering och regler |
| `runbooks/*.md` | Kunskapsbasen: länk nere, strömavbrott och fiberbrott (syntetiska) |
| `deploy.py` | Skapar eller uppdaterar rösten, hemligheten, MCP-servrarna, kunskapsbasen och de tre agenterna med överlämningar |
| `agent.json` | Id:n för det som skapats (inte hemliga) |
| `e2e.mjs` | Helflödestest mot den riktiga agenten i textläge: `inbound`, `refusal` och `outbound` |
| `tests/*.json` | NOC-agentens agenttester i ElevenLabs |
| `tests/servicedesk/*.json` | Växelns routningstester: IT, nätet, passertagg och övrigt |
| `run_tests.py` | Kör agenttesterna (`servicedesk` för växeln), avslutar med kod 1 vid fel |

Varje testkörning kostar ElevenLabs-credits. Kör bara det som täcker ändringen, en gång.

## Inställningar
- **Röst:** "Sanna Hartfield" (svensk, Stockholm), TTS-modell `eleven_v4_turbo`, språk `sv` med `en` som extra språk (#147): `language_detection` byter till engelska när uppringaren talar engelska.
- **LLM:** `claude-haiku-4-5`, temperatur 0,2. `deepseek-v41-flash` provades i #147 (0,89 s mot 1,09 s till svar) men valdes bort.
- **MCP:** `https://cmdb.rosenvall.se/voice/mcp`.
  - Varje ingång har sin hemlighet i arbetsytan (`cmdb-voice-secret-noc|servicedesk|it`, värdena från Bitwarden `CMDB_VOICE_SECRET_NOC|_SERVICEDESK|_IT`, #181).
  - Samtals-id:t skickas i `X-Conversation-Id` (`system__conversation_id`) och agentens id i `X-Agent-Id` (`system__agent_id`).
  - Verktygen godkänns automatiskt. Behörigheten ligger i CMDB:n, inte i agenten.
- **Kostnadstak:** högst 2 samtidiga samtal och 60 samtal per dygn. Den publika testlänken kräver ingen inloggning.
- **Guardrails:**
  - `focus` och `prompt_injection` är inbyggda och kostar ingen mätbar tid.
  - En egen guardrail, *inga nätdetaljer före verifiering*, bedömer svaret medan det talas (strömmande). Ett brott lägger på samtalet. Bedömningsmodellen är `gemini-3.5-flash`: `gemini-3.1-flash-lite` lade på legitima samtal (allmänna ord som "tjänster" och stationskoden, #147).
  - Blockerande guardrails höll tillbaka varje svar cirka 2,5 s (3,5 s median mot 0,94 s utan, #145) och används inte.
- **Ärendenummer:** läses aldrig upp. `create_incident` skickar numret med SMS till uppringaren, och agenten säger "Jag smsar dig ärendenumret efter samtalet. Kan jag hjälpa dig med något mer?".
- **Utgående samtal (#137):** webbens "Ring ansvarig" skickar risken som dynamiska variabler (`risk_id`, `risk_title`, `responsible_name`, `responsible_employee_id`) och ett eget första meddelande.
- **Utvärdering efter varje samtal:**
  - verifiering före detaljer
  - bekräftad station
  - prioritet från servern
  - ignorerade instruktioner i data

## Köra
```bash
ELEVENLABS_API_KEY=… CMDB_VOICE_SECRET_NOC=… CMDB_VOICE_SECRET_SERVICEDESK=… CMDB_VOICE_SECRET_IT=… python agent/deploy.py
```

Testa samtalet på `https://elevenlabs.io/app/talk-to?agent_id=<agent_id i agent.json>`. Koden till verifieringen syns i CMDB-webbens panel **Driftagent**, under SMS-utkorg. Uppringare och scenario finns i [docs/demo-scenarier.md](../docs/demo-scenarier.md).

## Tester: guardrails får inte döda funktionen
Kör båda efter varje ändring av prompten eller av guardrails:

```bash
ELEVENLABS_API_KEY=… python agent/run_tests.py                  # repliker: legitim hjälp passerar, det skyddade blockeras
ELEVENLABS_API_KEY=… CMDB_TOKEN=… node agent/e2e.mjs inbound     # anmälan, verifiering, påverkan och P1-ärende
ELEVENLABS_API_KEY=… CMDB_TOKEN=… node agent/e2e.mjs refusal     # overifierad uppringare och injektion: inget lämnas ut
ELEVENLABS_API_KEY=… CMDB_TOKEN=… node agent/e2e.mjs outbound    # risksamtal: verifiering, risk_details, ärende på kabeln
```

- **Agenttesterna** prövar enskilda repliker. Två visar att normal hjälp går igenom (anmälan tas emot och stationen bekräftas), och tre att skyddet håller (inga detaljer före verifiering, ingen kod eller prioritet på beställning, utgående samtal verifierar först).
- **Helflödestesterna** kör de riktiga verktygen mot demon. De misslyckas om ett flöde inte når sitt ärende eller om koden upprepas.
- `CMDB_TOKEN` är en token som ser hela nätet, till exempel agentkontot `cmdb-agent-demo`, som i `scripts/perf.sh`.
