# ADR-0025: SSO mot Lots i en dold ram

**Status:** Accepterad (begärd av Christopher i #342) · **Datum:** 2026-11-04 · **Beslut:** #342 · **Genomförs i:** #342

## Kontext
Chatten loggade in i Lots med en popup (ADR-0023), eftersom Authentiks flödessidor svarar `X-Frame-Options: DENY`. Christopher vill ha SSO: den som är inloggad i cmdb ska inte behöva göra något mer. Lots tar bara en token utfärdad för Lots (Lots ADR 0051), och Authentik saknar tokenbyte, så cmdb:s token kan inte användas.

## Beslut
- **En dold ram.** Webben kör inloggningen mot klienten `lots` med `prompt=none` i en dold ram. Authentik svarar `login_required` direkt när sessionen saknas, och då visas knappen med popupen som tidigare.
- **Gatewayen öppnar en enda sida.** HTTPRoute för Authentik tar bort `X-Frame-Options` och sätter `Content-Security-Policy: frame-ancestors https://cmdb.rosenvall.se` på `/if/flow/default-provider-authorization-implicit-consent/`. Inloggnings- och övriga flödessidor går fortfarande inte att rama in.
- **Kontot kopplas på samma sätt.** Säger Lots att kontot behöver kopplas öppnar webben leverantörens adress i en dold ram med `prompt=none`, bara om adressen går till den leverantör vi loggar in hos. Går det inte visas länken till Lots.

## Alternativ
- **En omdirigering av hela sidan.** Ingen ändring i gatewayen, men kartan laddas om när chatten öppnas. Förkastat.
- **Lots godtar cmdb:s token.** Då kan cmdb:s server agera som vem som helst i Lots, vilket Lots ADR 0051 utesluter. Förkastat.
- **Refresh-token i webbläsaren.** Kräver en första inloggning ändå, och token skulle överleva i lagring. Förkastat.

## Konsekvenser
- Sidan för det implicita flödet går att rama in från cmdb. Den har inget att klicka på, och utan session kommer man aldrig dit med `prompt=none`.
- Kopplingen sker utan klick. Personen har redan en session och ett implicit samtycke hos samma leverantör, och kontot ger inte mer än personens egen behörighet.
- Ändras flödet eller adressen på demon stannar SSO, men chatten fungerar med popupen.
