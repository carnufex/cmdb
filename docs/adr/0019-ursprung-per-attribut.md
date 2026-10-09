# ADR-0019: Ursprung per attribut som källposter per objekt och källa

**Status:** Föreslagen · **Datum:** 2026-10-09 (#215)

## Kontext
Objekten har i dag ursprung på objektnivå: `source_system`, `external_id` och `last_confirmed_at` (ADR-0009, `Tracked`). När ett 40-tal källsystem ska läsas in (Fas 9) äger olika system olika attribut på samma objekt. En nätverkshanterare äger kort och portar, övervakningen bekräftar att utrustningen lever och planeringen äger positionen. Samma utrustning finns dessutom i flera system med olika id:n. Avstämningen (#216) behöver kunna veta vem som sa vad och när, och vilken källa som får skriva vilket attribut.

## Alternativ
- **A. En rad per attribut** (`objekt, attribut, källa, bekräftad`). Ger direkt "vem äger det här värdet", men 5–10 gånger fler rader vid import i full skala. Det säger heller inte om värdet ändrats i cmdb efteråt, utan att alla skrivvägar uppdaterar raderna.
- **B. Ett jsonb-fält med ursprung på varje objekttabell.** Inga nya tabeller, men varje bekräftelse skriver om objektets rad, och externa id:n från flera källor kan inte ges ett unikt index.
- **C. En källpost per objekt och källa** (`source_record`): källa, externt id, när källan senast bekräftade objektet, och de värden källan rapporterade per attribut (jsonb).

## Beslut (förslag)
**C.**
- **Källposten.** En källa bekräftar alla sina attribut på ett objekt samtidigt, så en rad per objekt och källa räcker. `(objektslag, källa, externt id)` är unikt, så samma objekt kan ha id:n från flera källor.
- **Källans värden.** Raden sparar det källan rapporterade, inte en hänvisning till cmdb:s värde. Ett värde som ändrats i cmdb efter bekräftelsen syns därför direkt, utan att planer, redigering eller andra skrivvägar behöver märka ursprung. Avstämningen (#216) får också det den behöver för att visa källans värde mot cmdb:s.
- **Ursprungskällan.** `source_system`, `external_id` och `last_confirmed_at` på objektet finns kvar som den källa som skapade objektet. Importen matchar på dem.
- **Källprioritet** är katalogdata (`source-priority.json`): per objektslag och attribut (exakt, `attributes.*` eller `*`), vilka källor som får skriva och i vilken ordning. Ägaren till ett värde är den högst prioriterade källan som rapporterar det, och utan regel den senast bekräftade.

## Konsekvenser
- **Storlek.** En ny rad per objekt och källa. Vid import i full skala är det ungefär lika många rader som objekt utom portar och ledare, som saknar egna externa id:n. Geometrier (kabelvägar) sparas som hash, inte som hela linjen.
- **Läsning.** Paneler och MCP `get_object` läser objektets källposter via ett index på `(objektslag, objekt-id)`. Det är en extra fråga i samma batch.
- **Behörighet.** Källans värden visas med samma maskning som attributen (ADR-0007). Dolda attribut, och positioner när koordinater döljs, tas bort även här.
- **Historik.** Källposten är det senaste källan sa. Historiken över ändringar finns i operationsloggen (ADR-0006).
- **Skrivregler.** Att bara den ägande källan får ändra ett attribut genomförs av avstämningen (#216). Importen (#210) är en förstagångsladdning från en källa och skriver källposter men följer inte prioriteten.
