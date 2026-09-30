# Driftagenten i ElevenLabs

Röstagenten för felanmälan (epic #130, ADR-0015). Här finns allt agenten består av: prompten, runbooks och inställningarna. Skriptet `deploy.py` gör ElevenLabs likt repot.

| Fil | Innehåll |
|---|---|
| `prompt.md` | Systemprompten (svenska): arbetsgång, verifiering och regler |
| `runbooks/*.md` | Kunskapsbasen: länk nere, strömavbrott och fiberbrott (syntetiska) |
| `deploy.py` | Skapar eller uppdaterar rösten, hemligheten, MCP-servern, kunskapsbasen och agenten |
| `agent.json` | Id:n för det som skapats (inte hemliga) |
| `e2e.mjs` | Helflödestest mot den riktiga agenten i textläge |

## Inställningar
- **Röst:** "Sanna Hartfield" (svensk, Stockholm), TTS-modell `eleven_v4_turbo`, språk `sv`.
- **LLM:** `claude-haiku-4-5`, temperatur 0,2.
- **MCP:** `https://cmdb.rosenvall.se/voice/mcp`.
  - Hemligheten ligger som en hemlighet i arbetsytan (`cmdb-voice-secret`, värdet från Bitwarden `CMDB_VOICE_SECRET`).
  - Samtals-id:t skickas i `X-Conversation-Id` (`system__conversation_id`).
  - Verktygen godkänns automatiskt. Behörigheten ligger i CMDB:n, inte i agenten.
- **Kostnadstak:** högst 2 samtidiga samtal och 60 samtal per dygn. Den publika testlänken kräver ingen inloggning.
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

Helflödestest i textläge. `CMDB_TOKEN` är en token som ser hela nätet, till exempel agentkontot `cmdb-agent-demo`, som i `scripts/perf.sh`:

```bash
ELEVENLABS_API_KEY=… CMDB_TOKEN=… node agent/e2e.mjs            # anmälan, verifiering, påverkan och P1-ärende
ELEVENLABS_API_KEY=… CMDB_TOKEN=… node agent/e2e.mjs refusal    # overifierad uppringare och injektion: inget lämnas ut
```
