# ADR-0024: Ändringsbegäran i chatten: agenten föreslår, personen svarar ja eller nej

**Status:** Accepterad (begärd av Christopher i #335) · **Datum:** 2026-11-04 · **Beslut:** #335 · **Genomförs i:** #335

## Kontext
I chatten (ADR-0023) ska personen kunna säga "uppdatera namnet på siten AGG-0315 till X" och få frågan:

> Ändringsbegäran - skriv: byta namn på site AGG-0315 till X. Ska vi skriva det här? ja/nej

Samtidigt gäller ADR-0011: agenter föreslår och människor beslutar. Ingen agentklient får föra in en plan.

## Beslut
- **Verktyget `request_change`** skapar en utkastplan med ändringarna. Det skriver inget i nätet, och agenten har inget verktyg för att föra in planen.
- **Frågan ställs av webben, inte av modellen.** Webben ser verktygsanropet i AG-UI-strömmen, hämtar planen från cmdb med personens token och visar serverns beskrivning av varje ändring. Vad modellen skrev visas inte i dialogen.
- **Svaret går aldrig genom modellen.** Ja kör `POST /api/plans/{id}/apply` och Nej `POST /api/plans/{id}/cancel`, i webbläsaren med personens token. Skrivet "ja" eller "nej" tolkas också lokalt.
- **Bara personens eget utkast frågas om.** Pekar anropet på någon annans plan, eller på en plan som inte är ett utkast, visas ett fel i stället för en fråga.
- **Planens regler gäller.** Rollen `cmdb-full`, kontrollerna mot produktion och andra planer samt driftloggen är desamma som i webben. En ändring med problem går inte att godkänna.
- **Inget godkännande i Lots för utkast.** Profilen `cmdb` slutar kräva godkännande i Lots för `write`, eftersom de verktygen bara skriver utkast. Annars skulle personen behöva godkänna två gånger, och beslutet hör hemma där ändringen faktiskt görs.

## Alternativ
- **Godkännande i Lots** med AG-UI:s avbrott (interrupts, utkast i specen) och ett verktyg som skriver direkt. Enhetligt för alla verktyg, men agenten skulle kunna skriva i nätet, planens kontroller skulle inte gälla, och specen är inte klar. Förkastat.
- **Frontend tools** i AG-UI. Webbläsaren skulle köra verktyget, utanför Lots policy och revision (Lots ADR 0067). Förkastat.
- **Frågan i modellens text** och ett svar till modellen. En prompt injection skulle kunna ändra vad som står eller svara själv. Förkastat.

## Konsekvenser
- Varje besvarad fråga blir en plan i planlistan: införd vid ja, avbruten vid nej. Det är också historiken över vad som ändrats via chatten.
- Stängs chatten utan svar ligger utkastet kvar i planlistan och skriver inget.
- Skrivande handlingar utanför planerna, som att skapa ärenden, ingår inte. De tas i ett eget issue.
- Den som kan chatta men saknar `cmdb-full` får inget utkast: agenten får svaret att personen inte får föreslå ändringar.
