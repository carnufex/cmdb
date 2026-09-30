# ADR-0015: Driftagentens röstkanal och verifiering av uppringare

**Status:** Accepterad · **Datum:** 2026-09-30 (A1 + B1, #131) · **Epic:** #130

## Kontext
Driftagenten tar emot felanmälningar på svenska via röst, felsöker mot CMDB:n, skapar ärenden och ringer ut om risker. Två saker är nya:

1. **En extern röst- och LLM-plattform** tar emot samtalet och anropar CMDB:ns verktyg. Samtalsljud, transkript och verktygssvar behandlas då hos en tredje part.
2. **Uppringare som inte är inloggade** ska kunna verifieras under samtalet innan de får se topologi eller påverkan, eller skapa ärenden. All inloggning sker annars med OIDC (ADR-0008), och vad en användare ser styrs av åtkomstomfång (ADR-0007) och RLS för direkt åtkomst (ADR-0012).

## Alternativ
- **A1 ElevenLabs Agents.** Färdig svensk röst och taligenkänning, MCP-klient, webbwidget och utgående samtal.
- **A2 Egen kedja** med taligenkänning, LLM och talsyntes.
- **A3 Chattagent först**, röst senare.
- **B1 Engångskod som ger ett samtalsbundet omfång.** Uppringaren verifieras med en kod som skickas till telefonnumret för hens anställningsnummer. Samtalet får då den användarens grupper, och därmed samma omfång, masker och filter som i webben.
- **B2 Egen rollkontroll i agentverktygen** (tekniker, entreprenör, NOC) vid sidan av omfången.

## Beslut
**A1 + B1**, beslutat i #131.

- **Egen ingång.** Agenten når CMDB:n på `/voice/mcp`, inte på `/mcp`.
  - Autentiseringen sker med en hemlighet i en header (`Voice:Secret`, i Bitwarden). Den kan bara användas mot röstverktygen.
  - ElevenLabs skickar samtals-id:t som header (`X-Conversation-Id`, den dynamiska variabeln `system__conversation_id`).
- **Utan verifiering** har samtalet inga grupper och därmed omfånget "ingenting".
  - Då finns bara offentliga verktyg: stationssök (kod, namn, typ, region och status, ingen topologi) och själva verifieringen.
  - Stationssöket är ett avsiktligt offentligt undantag. Det lämnar bara ut det som står på en skylt på en station.
- **Efter verifiering** bär samtalet användarens grupper tills sessionen går ut (30 minuter).
  - Alla känsliga verktyg går genom `ScopeRegistry.For` precis som webben.
  - Behörigheten ligger alltså i servern och aldrig i prompten.
  - En entreprenör med regionomfång ser bara sin region, även via röst.
- **Koden gäller i 5 minuter** och tillåter 3 försök per samtal.
  - Koden sparas som en hash. SMS stubbas med en utkorg i databasen, som webbens driftagentpanel visar.
  - En verifiering gäller bara det samtal den gjordes i.
- **Uppringarna är syntetiska**, versionerad data som synkas vid migrering, precis som åtkomstomfången.

## Konsekvenser
- Samtal och verktygssvar behandlas hos ElevenLabs.
  - Demon använder bara syntetisk data.
  - En skarp version kräver beslut om datalagring (EU-residens), säkerhetsskydd och avtal. Det tas upp i färdplanen.
- Hemligheten för `/voice/mcp` ger inget mer än ett overifierat samtal. Allt känsligt kräver en giltig kod för ett känt anställningsnummer.
- Agentens konfiguration (prompt, verktyg, ordlista och tester) ligger i repot under `agent/` och skapas med ett skript mot ElevenLabs API, så att den går att granska och ändra som kod.
- ElevenLabs-nyckeln delas med homelabbets övriga röstdemos (samma arbetsyta). Dagliga samtalsgränser sätts på agenten.
