# ADR-0020: Avstämning i API:t, under integrationens omfång, till planer

**Status:** Accepterad · **Datum:** 2026-10-09 · **Beslut:** #229 · **Issue:** #216

## Kontext
Ett 40-tal källsystem ska läsas in genom tunna adaptrar som skriver utbytesformatet från #210 (analys: integrationer). Det som skiljer en källa från cmdb ska bli en plan (ADR-0005), inte skrivas tyst. Bara attribut som källan äger enligt källprioriteten (ADR-0019) ska ändras. En betrodd källa och ett betrott attribut ska kunna föras in automatiskt, med samma kontroller som andra planer. Integrationer är konsumenter med egna omfång (arkitektur.md).

Importen (#210) är en förstagångsladdning. Den körs som systemarbete med direkt databasåtkomst och ser allt. Avstämningen ska köras om och om igen, av många integrationer, och därför behövs ett beslut om var den körs och med vilken behörighet.

## Alternativ
- **A. I API:t, under anroparens omfång.** `POST /api/reconciliations` tar emot utbytesformatet från en integration som har ett eget konto och omfång. Det som integrationen inte ser kan den varken matcha, ändra eller få rapporterat. Skillnaderna läggs som planer genom samma skrivvägar som andra planer, och införandet går genom samma kontroller.
- **B. Som jobb bredvid importen, med systemroll.** Jobbet läser och skriver allt och lägger planer direkt i databasen, och planerna förs in via API:t. Det är enklast att skala, men behörigheten kringgås i jämförelsen och i rapporten (CLAUDE.md regel 5).
- **C. Ingen gemensam avstämning.** Varje adapter anropar planernas API själv. Då skrivs jämförelse, matchning och prioritet 40 gånger.

## Beslut
**A.**
- **Var den körs.** Avstämningen körs i API:t med anroparens omfång. Objekt utanför omfånget matchas inte och nämns inte. En rad som pekar dit räknas bara som utanför omfånget. `cmdb sync` (#217) kör adaptern och skickar filerna med integrationens konto.
- **Vad som jämförs.** Siter, utrustning, kablar och tjänster, det vill säga objekten som har planoperationer. Locations, kretsar, kopplingar och portar läses men räknas bara i rapporten tills de har egna operationer (eget issue).
- **Matchning.**
  - Först på källa och externt id: objektets eget, eller en källpost (ADR-0019) från samma källa.
  - Sedan på regler i katalogen, `source-matching.json`: per objektslag en lista av nycklar som `attributes.serialNumber` eller `name`. Den första regeln som ger exakt en träff kopplar ihop objektet med källan.
  - Två träffar är en avvikelse, inte en gissning.
- **Skillnader per attribut.**
  - Äger källan attributet enligt `source-priority.json`, och det finns en planoperation för det (namn, livscykel, attribut), blir skillnaden en operation.
  - Övriga skillnader blir avvikelser i rapporten: attribut som en annan källa äger, eller som saknar operation (position, kod, typ, placering).
- **Nytt** i källan blir `create_*`-operationer. Det som **försvunnit** ur källan markeras på källposten (`missing_since`) och rapporteras. Inget tas bort automatiskt.
- **Bekräftelse** skriver källposten och `last_confirmed_at` direkt, utan plan. Det är metadata om källan, inte nätet.
- **Automatiskt införande.**
  - En regel i källprioriteten kan ange `autoApply`, de källor vars ändringar av attributet förs in utan granskning.
  - Operationerna läggs i en egen plan, som förs in direkt genom samma väg som när en människa för in en plan. Den har samma konfliktkontroll, klassningskrav och historik.
  - Stoppar en kontroll planen blir den kvar som utkast. Resten läggs i en plan för granskning.
- **Rapport per körning** sparas (`reconciliation`): räknare, avvikelser, fel, tid och planerna. Den visas i appen och via MCP (#217).

## Konsekvenser
- **Skala.**
  - Utbytesformatet laddas till temporära tabeller med `COPY`, och jämförelsen görs mängdbaserat i SQL, som i importen.
  - 200 000 utrustningar ska stämmas av på minuter. Det mäts i #216.
  - Uppladdningen begränsas till utbytesformatets filer i ett zip-arkiv.
- **Kod.** Läsning och kontroll av utbytesformatet flyttas från datageneratorn till ett delat bibliotek (`Cmdb.Exchange`), så att import och avstämning läser filerna på samma sätt.
- **Behörighet.**
  - Avstämningen kräver rollen `cmdb-full` som andra planskrivningar.
  - Automatiskt införande kräver dessutom att källan är betrodd för attributet i katalogen. Katalogen ändras bara genom en utrullning.
  - En agent (`createdVia = mcp`) får fortfarande aldrig föra in en plan (#64). Integrationer är konton, inte agenter.
- **Importen** finns kvar för förstagångsladdning av en tom databas, eller av en källa som ingen annan källa överlappar.
