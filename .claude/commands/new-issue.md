Skapa ett nytt GitHub-issue utifrån: $ARGUMENTS

1. Sök efter dubbletter: `gh issue list --search "<nyckelord>" --state all`.
2. Välj typ (feature/task/spike/bug/decision), område och fas enligt CONTRIBUTING.md.
3. Skriv på svenska med sektionerna Bakgrund, Mål, Acceptanskriterier (kryssrutor), Avgränsningar och Referenser (ADR:er, dokument, relaterade issues).
4. Sätt `status:ready` endast om acceptanskriterierna är tydliga nog att påbörja utan frågor. Kräver det ett beslut, sätt `needs-human`.
5. Skapa med `gh issue create --title ... --body-file ... --label ... --milestone ...` och rapportera länken.
