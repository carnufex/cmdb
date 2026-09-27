# Bidra

Samma flöde gäller för människor och agenter. Se [CLAUDE.md](CLAUDE.md) för detaljer.

## Etiketter

| Etikett | Betydelse |
|---|---|
| `type:feature` / `type:task` / `type:spike` / `type:bug` / `type:decision` | Typ av arbete |
| `area:backend` / `area:frontend` / `area:data` / `area:infra` / `area:security` / `area:docs` | Område |
| `fas-0` … `fas-4` | Fas enligt planen |
| `status:ready` | Tillräckligt specificerat för att påbörjas |
| `status:in-progress` / `status:blocked` | Pågår / blockerat |
| `needs-human` | Kräver mänskligt beslut eller granskning |

## Definition of done

- Acceptanskriterierna i issuet är uppfyllda.
- Tester finns och går igenom med `scripts/verify.sh`.
- Prestandabudgeten hålls där den är relevant, med siffror i PR:en.
- Dokumentation och ADR:er är uppdaterade.
- Ingen riktig data och inga verkliga namn.
