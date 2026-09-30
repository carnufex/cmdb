# ADR-0016: Röstväxel med flera agenter

**Status:** Accepterad · **Datum:** 2026-09-30 (beslut av Christopher, #151) · **Bygger på:** ADR-0015

## Kontext
Röstkanalen hade en agent, Driftagenten, som bara hanterar nätet. Samtal om annat (lösenord, datorer, passertaggar) har ingen plats, och en tung NOC-prompt gör också enkla samtal långsammare. Vi vill att den som ringer möts av en lätt och snabb växel, som själv tar enkla ärenden och lämnar över resten till en specialist.

## Alternativ
- **A Flera agenter med överlämning** (ElevenLabs `transfer_to_agent`). Varje agent har sin egen prompt, sina egna verktyg och sin egen MCP-ingång.
- **B En agent med alla verktyg** och en längre prompt.
- **C Arbetsflöde i plattformen** (noder och villkor i ElevenLabs), där konfigurationen är svårare att versionera från repot.

## Beslut
**A.**

- **Service desk** svarar alla samtal. Hälsningen är på både svenska och engelska, och agenten har en kort prompt.
  - Passertaggar som slutat fungera hanteras direkt, efter verifiering.
  - IT-frågor (lösenord, konto, dator, beställa utrustning) lämnas över till **IT-självhjälp**.
  - Frågor om nätet (CMDB, fiber, stationer, larm) lämnas över till **NOC-agenten** (tidigare Driftagenten).
  - Allt annat avböjs, och agenten erbjuder att en människa ringer upp. Kötiden kommer från servern.
- **En MCP-ingång per agent** med bara den agentens verktyg: `/voice/mcp` för NOC, `/voice/servicedesk/mcp` och `/voice/it/mcp`.
  - En agent kan alltså inte anropa en annan agents verktyg, oavsett vad prompten säger.
  - Autentisering, verifiering och omfång är desamma som i ADR-0015.
- **Samtals-id:t följer med vid överlämning.** En verifiering gäller därför i hela samtalet, och uppringaren behöver inte verifiera sig på nytt hos specialisten.
- **Serviceärenden** (tagg, lösenord, utrustning, uppringning) sparas i en egen tabell med egna nummer (`SR-xxxxx`). Numren skickas med SMS och läses aldrig upp (#145).
- **Allt versioneras i repot.** `agent/deploy.py` skapar och uppdaterar alla agenter och deras överlämningar.

## Konsekvenser
- En överlämning kostar en kort paus och ett byte av prompt. Det är enkla samtal, som de flesta är, som vinner på en lätt växel.
- Varje agent testas för sig. Växelns routning har egna agenttester.
- IT-självhjälpen och passertaggarna är syntetiska: ingen koppling till något riktigt IT- eller passersystem.
