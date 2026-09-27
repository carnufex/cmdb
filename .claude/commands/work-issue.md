Arbeta med GitHub-issue #$ARGUMENTS enligt flödet i CLAUDE.md.

1. Läs issuet med `gh issue view $ARGUMENTS --comments`, inklusive länkade issues och relevanta ADR:er i `docs/adr/`.
2. Om issuet har `needs-human` eller saknar tydliga acceptanskriterier: kommentera vad som saknas och stanna.
3. Ta issuet: tilldela dig, sätt `status:in-progress` och ta bort `status:ready`.
4. Kommentera en kort plan i issuet: angreppssätt, berörda slices, testplan och öppna frågor.
5. Skapa branchen `$ARGUMENTS-<kort-slug>`.
6. Implementera med tester. Kommentera beslut och mätvärden i issuet under arbetets gång.
7. Skapa nya issues för upptäckt arbete utanför scope och länka dem.
8. Öppna en PR med `Closes #$ARGUMENTS`, verifieringsbeskrivning och prestandasiffror.
