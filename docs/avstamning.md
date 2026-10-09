# Avstämning mot ett källsystem (#216, ADR-0020)

Importen ([import.md](import.md)) laddar ett nät första gången. Avstämningen jämför därefter en källas data med cmdb, gång på gång, och gör skillnaderna till planer i stället för att skriva dem tyst. Den tar samma utbytesformat som importen, i ett zip-arkiv, och körs i API:t med anroparens omfång: en integration med eget konto, eller en person.

```bash
# Provkörning: rapporten utan planer och utan att bekräfta något
curl -X POST https://<cmdb>/api/reconciliations -H "Authorization: Bearer <token>" \
  -F source=acme-nms -F dryRun=true -F file=@export.zip
# Körning
curl -X POST https://<cmdb>/api/reconciliations -H "Authorization: Bearer <token>" -F source=acme-nms -F file=@export.zip
# Tidigare körningar och en körnings rapport
curl https://<cmdb>/api/reconciliations
curl https://<cmdb>/api/reconciliations/<id>
```

`cmdb sync` (#217) kör en adapter och skickar filerna på det här sättet.

## Vad som stäms av

Siter, utrustning, kablar och tjänster, det vill säga objekten som har planoperationer. Locations, portar, kopplingar, kretsar och deras hopp, beroenden och tjänstekretsar läses och kontrolleras, men räknas bara i rapporten (`notReconciled`) tills de har egna operationer (#230).

Filerna kontrolleras som i importen. Med ett enda fel jämförs ingenting, och felen står i rapporten. Arkivet får bara innehålla utbytesformatets filer, var och en en gång, högst 256 MB packat och 2 GB uppackat.

## Matchning

1. **Källa och id.** Ett objekt som källan rapporterade förut känns igen på sitt id i källan: objektets eget (`source_system`, `external_id`) eller källposten från samma källa (#215).
2. **Regler i katalogen.** `source-matching.json` anger per objekttyp vilka nycklar som kopplar ihop ett objekt från en ny källa med ett som cmdb redan har:

   ```json
   [
     { "object": "equipment", "keys": ["attributes.serialNumber"] },
     { "object": "site", "keys": ["code"] }
   ]
   ```

   Reglerna prövas i ordning, och den första som hittar exakt ett objekt kopplar ihop det med källan. Två eller fler träffar är en avvikelse (`ambiguous`), aldrig en gissning. Nycklar är objektets fält med enkla värden (`code`, `name` …) eller egna attribut. Länkar och positioner (`placement`, `ends`, `route`, `position`) går inte att matcha på. Dolda attribut används inte för den som inte ser dem.
3. **Inget av det:** objektet är nytt.

## Skillnader

Varje attribut som källan rapporterar jämförs med objektet. Vem som äger attributet avgörs av `source-priority.json` (se [domanmodell.md](domanmodell.md#ursprung-per-attribut-215-adr-0019)), bland källorna som rapporterar objektet.

- **Källan äger attributet, och det finns en operation för det:** skillnaden blir en operation, `rename` (namn), `set_lifecycle` eller `set_attributes`.
- **Annars blir det en avvikelse** i rapporten, med källans värde, cmdb:s värde och ett skäl:
  - `owned-by-other`: en högre prioriterad källa rapporterar samma objekt.
  - `not-allowed`: regeln för attributet nämner inte källan.
  - `no-operation`: ingen planoperation ändrar attributet ännu (kod, typ, position, placering, kabelns sträckning).
- **Nytt i källan** blir `create_site`, `create_equipment` och `create_cable` med källans id, så att nästa körning känner igen objektet. Utrustning läggs i det rack på siten som har källans namn på sin location, och racket skapas i rummet med källans namn när det saknas. Det som inte kan skapas än (kort, tjänster, utrustning i en site som varken finns eller skapas) blir avvikelser.
- **Saknas i källan:** ett objekt som källan rapporterat förut, men inte i den här filen, markeras på källposten (`missing_since`) och rapporteras. Inget tas bort. Att avveckla det är en plan som en människa lägger.
- **Bekräftat:** källposten och `last_confirmed_at` skrivs direkt, utan plan. Det är uppgifter om källan, inte om nätet.

## Planer

- **Plan för granskning:** skillnaderna läggs i en plan, `Avstämning <källa> <tid>`. En ny körning avbryter källans föregående plan för granskning om den fortfarande är ett utkast, så att samma sak inte föreslås två gånger.
- **Automatiskt införande:** en regel i källprioriteten kan ange `autoApply`, källorna som är betrodda med attributet:

  ```json
  { "object": "equipment", "attribute": "attributes.*", "sources": ["acme-nms", "planering"], "autoApply": ["acme-nms"] }
  ```

  De ändringarna läggs i en egen plan, `Avstämning <källa> <tid> (betrodd)`, och förs in direkt, genom samma väg som när en människa för in en plan: konfliktkontroll, klassningskrav och historik. Stoppar en kontroll planen blir den kvar som utkast, och skälet står i rapporten (`autoApplyProblem`).

## Behörighet

- Avstämningen kräver rollen `cmdb-full`, som andra planskrivningar, och körs inom anroparens omfång. Objekt utanför omfånget matchas inte, ändras inte och nämns inte. En rad som pekar dit räknas bara (`outsideScope`).
- Automatiskt införande kräver dessutom att katalogen litar på källan för attributet. Katalogen ändras bara genom en utrullning.
- En agent får aldrig föra in en plan (#64). Integrationer är konton, inte agenter.
- Rapporterna listas för den som körde dem, och alla för den med obegränsat omfång.

## Rapporten

Varje körning sparas i `reconciliation` och returneras:

- **`counts`** per objekttyp: rapporterade, matchade, kopplade genom en regel, nya, ändrade, oförändrade, avvikelser, saknade och utanför omfånget.
- **`reasons`** räknar alla avvikelser per skäl, och **`deviations`** listar de första 1 000.
- **`reviewPlanId`** och **`appliedPlanId`** är planerna, **`errors`** felen i filerna och **`elapsedMs`** tiden.

## Mätning

Mätningen i full skala (224 707 utrustningar) läggs till här när den är gjord.
