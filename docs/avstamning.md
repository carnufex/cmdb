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

Siter, locations, utrustning (även kort), kablar, kretsar och tjänster, och det som ligger mellan dem: kopplingar, kretsarnas vägar (hopp), beroenden mellan kretsar och tjänsternas kretsar (#230). Portar jämförs inte: de följer utrustningstypen, och filen räknas bara i rapporten (`notReconciled`). Kanaler på hopp (`vlan:100`) läses men jämförs inte.

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
3. **Locations** känns igen på källa och id, och annars på site, typ (`kind`) och namn, där siten är den som källans site-id leder till.
4. **Inget av det:** objektet är nytt.

Kopplingar och hopp pekar på terminaler, och de matchas som i importen: en port på källans utrustning (utrustningens id och portnamnet) eller en ledarände i källans kabel (kabelns id, ledarnummer och sida). Utrustning och kablar översätts genom det som matchades i körningen och det som källan rapporterat förut, inom anroparens omfång.

## Skillnader

Varje attribut som källan rapporterar jämförs med objektet. Vem som äger attributet avgörs av `source-priority.json` (se [domanmodell.md](domanmodell.md#ursprung-per-attribut-215-adr-0019)), bland källorna som rapporterar objektet.

- **Källan äger attributet, och det finns en operation för det:** skillnaden blir en operation, `rename` (namn), `set_lifecycle` eller `set_attributes`.
- **Annars blir det en avvikelse** i rapporten, med källans värde, cmdb:s värde och ett skäl:
  - `owned-by-other`: en högre prioriterad källa rapporterar samma objekt.
  - `not-allowed`: regeln för attributet nämner inte källan.
  - `no-operation`: ingen planoperation ändrar attributet ännu (kod, typ, position, placering, kabelns sträckning).
- **Nytt i källan** blir `create_site`, `create_location`, `create_equipment`, `create_cable`, `create_service` och `create_circuit` med källans id, så att nästa körning känner igen objektet. Objekt som skapas i samma plan pekar på varandra: en location i en ny location, utrustning på en ny site, ett kort i ny utrustning. Utrustning läggs i det rack på siten som har källans namn på sin location, och ett kort (`parent` och `slot`) i sin förälders slot. En ny krets skapas med den väg som dess hopp leder till, och utan hopp som går att hitta väntar den (`no-path`). Det som pekar på något som varken finns eller skapas blir en avvikelse (`unknown-site`, `unknown-parent`, `no-slot`, `code-taken`).
- **Kopplingar:** en kopplingsfil är hela sanningen om kopplingarna mellan källans objekt. En ny koppling blir `connect`. En koppling mellan två av källans terminaler som inte längre finns i filen blir `disconnect`, alltid i planen för granskning. En koppling med annan typ (`patch`, `splice` …) är en avvikelse (`no-operation`). En rad vars terminal inte går att hitta blir en avvikelse (`unknown-terminal`), till exempel en port på utrustning som skapas i samma körning: den stäms av i nästa körning, när planen är införd.
- **Kretsar:** en väg som skiljer sig blir `set_circuit_path`. Beroenden blir `link_circuit` och tjänsternas kretsar `link_service`, att lägga till eller ta bort, med samma regel som kopplingarna: filen är hela sanningen om länkarna mellan källans objekt.
- **Ägarskap för länkarna:** `source-priority.json` tar även `connection` (`kind`), `circuit` (`path`, `carriers`) och `service` (`circuits`), så att en källa kan nekas eller betros med dem som med attribut. En operation som pekar på ett objekt som planen för granskning skapar granskas alltid med det.
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

- **`counts`** per objekttyp, och för kopplingar (`connection`): rapporterade, matchade, kopplade genom en regel, nya, ändrade, oförändrade, avvikelser, saknade och utanför omfånget.
- **`reasons`** räknar alla avvikelser per skäl, och **`deviations`** listar de första 1 000.
- **`reviewPlanId`** och **`appliedPlanId`** är planerna, **`errors`** felen i filerna och **`elapsedMs`** tiden.

## Mätning

Det syntetiska nätet i full skala (seed 1) exporterat och importerat som `acme-nms`, och sedan stämt av med siter, locations, utrustning, kablar och tjänster (373 159 objekt, 120 MB), lokalt i Postgres 17 med PostGIS:

| Körning | Tid |
|---|---|
| Provkörning, inget ändrat | 35 s |
| Inget ändrat (bekräftar 373 159 källposter) | 64 s |
| 1 216 ändrade serienummer och 2 247 ändrade namn på utrustning | 72 s |

I den sista körningen fördes serienumren in direkt i den betrodda planen, och namnen lades som 2 247 `rename` i planen för granskning. Skillnaden mellan provkörningen och körningen är att källposterna bekräftas.
