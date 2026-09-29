# ADR-0007: Omfångsbaserad behörighet

**Status:** Accepterad · **Datum:** 2026-09-27

## Kontext
Högt klassad data med få fullt behöriga användare och många med geografiskt eller objektmässigt begränsad åtkomst. Integrationer får partitioner av datan.

## Beslut
Egen omfångsmodell (polygon × objektklass × attribut × plan × tid) som tillämpas som synlighetsmasker i grafmotorn, med Postgres RLS som sista spärr. (RLS gäller direkt databasåtkomst, se ADR-0012.)

## Alternativ
- **RBAC.** För grovt.
- **OpenFGA / Zanzibar.** Bra för relationer, men passar dåligt för polygoner och aggregering.
- **OPA / Cedar.** Flexibelt men långsamt per nod vid traverseringar.

## Konsekvenser
Behörighetsmodellen blir en central del av grafmotorn och måste ha mycket hög testtäckning.
