# Driftagenten i ElevenLabs

Röstagenten för felanmälan (epic #130, ADR-0015). Här finns allt agenten består av: prompten, runbooks och inställningarna. Skriptet `deploy.py` gör ElevenLabs likt repot.

| Fil | Innehåll |
|---|---|
| `prompt.md` | Systemprompten (svenska): arbetsgång, verifiering och regler |
| `runbooks/*.md` | Kunskapsbasen: länk nere, strömavbrott och fiberbrott (syntetiska) |
| `deploy.py` | Skapar eller uppdaterar rösten, hemligheten, MCP-servern, kunskapsbasen och agenten |
| `agent.json` | Id:n för det som skapats (inte hemliga) |
| `e2e.mjs` | Helflödestest mot den riktiga agenten i textläge: `inbound`, `refusal` och `outbound` |
| `tests/*.json` | Agenttester i ElevenLabs, kopplade till agenten av `deploy.py` |
| `run_tests.py` | Kör agenttesterna, avslutar med kod 1 vid fel |

## Inställningar
- **Röst:** "Sanna Hartfield" (svensk, Stockholm), TTS-modell `eleven_v4_turbo`, språk `sv` med `en` som extra språk (#147): `language_detection` byter till engelska när uppringaren talar engelska.
- **LLM:** `claude-haiku-4-5`, temperatur 0,2. `deepseek-v41-flash` provades i #147 (0,89 s mot 1,09 s till svar) men valdes bort.
- **MCP:** `https://cmdb.rosenvall.se/voice/mcp`.
  - Hemligheten ligger som en hemlighet i arbetsytan (`cmdb-voice-secret`, värdet från Bitwarden `CMDB_VOICE_SECRET`).
  - Samtals-id:t skickas i `X-Conversation-Id` (`system__conversation_id`).
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
ELEVENLABS_API_KEY=… CMDB_VOICE_SECRET=… python agent/deploy.py
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
