# CLAUDE.md

Instruktioner för agenter (Claude Code) som arbetar i det här repot.

## Absoluta regler

1. **Syntetisk data, alltid.** Checka aldrig in riktig data, riktiga datamodeller eller namn på verkliga organisationer, system eller personer. Är du osäker: stanna och fråga i issuet.
2. **Allt arbete sker mot ett issue.** Finns inget issue skapar du ett först (`gh issue create`, använd rätt mall) och arbetar sedan mot det.
3. **Arkitekturbeslut tas inte tyst.** Avvikelser från en ADR eller nya beslut kräver en ny ADR med status *Föreslagen* och ett issue med etiketterna `type:decision` och `needs-human`.
4. **Prestandabudgeten i [docs/plan.md](docs/plan.md) är ett krav.** Kod som bryter mot den är inte klar.
5. **Behörighet får aldrig kringgås**, inte heller i test-, debug- eller exportvägar.

## Arbetsflöde

1. **Välj issue.** Helst ett med `status:ready` och ingen `needs-human`. Läs issuet, länkade issues, relevanta ADR:er och dokument i `docs/`.
2. **Ta issuet:** `gh issue edit <nr> --add-assignee @me --add-label status:in-progress --remove-label status:ready`.
3. **Kommentera en plan** i issuet innan du implementerar, om arbetet är större än en liten ändring: angreppssätt, berörda slices, testplan och öppna frågor.
4. **Branch:** `<nr>-kort-slug`, till exempel `12-trace-service`.
5. **Commits:** Conventional Commits med issuenummer, till exempel `feat(graph): trace across layers (#12)`.
6. **Dokumentera medan du arbetar.** Kommentera beslut, överraskningar och mätvärden i issuet. Issuet ska räcka för att förstå varför koden ser ut som den gör.
7. **Upptäckt arbete utanför scope** blir ett nytt issue, länkat till det aktuella med "Upptäckt i #<nr>". Utöka inte scope tyst.
8. **Blockerad?** Sätt `status:blocked` och beskriv vad som behövs. Behövs ett mänskligt beslut sätter du även `needs-human`.
9. **Pull request** med `Closes #<nr>`, en beskrivning av hur det verifierats, prestandasiffror där det är relevant och eventuella skärmdumpar.
10. **Uppdatera dokumentationen** i `docs/` när beteende eller modell ändras.

Slash-kommandon: `/work-issue <nr>` och `/new-issue <beskrivning>` (se `.claude/commands/`).

## Konventioner

- **Språk:** kod, identifierare och commit-meddelanden på engelska. Dokumentation, issues och PR-beskrivningar på svenska.
- **Backend:** .NET 10, FastEndpoints, vertical slices (en mapp per feature med endpoint, request/response, validering, handler och test). Inga generiska "services"-lager.
- **Frontend:** Angular med standalone-komponenter och signals. All styling via design-tokens. Status visas med prick och text.
- **Databas:** migreringar versionerade i repot. Geometri i EPSG:3006.
- **Tester:** enhetstester för domänlogik, integrationstester mot riktig Postgres (Testcontainers), benchmarks för grafmotorn.
- **Containrar:** OpenShift-kompatibla (icke-root, godtyckligt UID).

## Lokal miljö

```bash
scripts/init-env.sh
docker compose up -d
```
