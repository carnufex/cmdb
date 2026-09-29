# ADR-0014: Kanalisation: trasé, dukt, subdukt och beläggning

**Status:** Föreslagen · **Datum:** 2026-09-30 · **Beslut:** #103 · **Issue:** #92

## Kontext
En kabel går i dag direkt mellan två siter som en LineString. I verkligheten ligger kablar i subdukter i multidukter, som ligger i trasé mellan brunnar och noder. Kanalisationen är en stor del av dokumentationen: vad ligger i vilket rör, var finns ledig kapacitet, och vad drabbas vid en grävskada på en sträcka. Svartfiber kan i dag bara uttryckas som en tjänst. Våglängder (WDM) finns redan som kanaler och transmissionskretsar och berörs inte.

## Beslut (föreslaget)
1. **Brunnar och skarvpunkter är siter.** Brunnar blir en ny sitetyp, `manhole`. De har redan position, livscykel, platser och omfång, och en skarvbox i en brunn är utrustning på den siten.
2. **Trasé** (`route_segment`) är en sträcka mellan två siter. Den har en LineString i EPSG:3006, ett anläggningssätt (`trench`, `plough`, `aerial`, `existing`), en ägare, livscykel och proveniens. Den är ingen terminal och ligger inte i signalgrafen.
3. **Dukttyper är en katalog** (`catalog/duct-types/<key>.json`), precis som kabeltyper och utrustningstyper. En typ har en ytterdimension och en **subduktmall** (antal, innerdiameter, färgkod), till exempel `acme-md-7x16` och `acme-md-24x7`. En ny storlek är en katalogpost, inte en migrering. Katalogen valideras och synkas i migreringssteget.
4. **Dukt** (`duct`) är en instans av en dukttyp längs en ordnad följd av trasésträckor (`duct_segment`). **Subdukter** (`subduct`) genereras från mallen när dukten skapas, som portar från portmallen. En dukt kan ligga i en annan dukts subdukt (rör i rör) genom en valfri referens till den subdukten.
5. **Beläggning** per subdukt: `empty`, `reserved` (via reservationer, #25), `cable` eller `blown_fibre`. En kabels **väg** är en ordnad följd av subdukter (`cable_path`). Har kabeln en väg härleds dess LineString från trasésträckorna vid ändring. Utan väg ligger kabeln fritt med sin egen geometri, till exempel en luftledning eller en äldre kabel utan dokumenterad dukt.
6. **Ledaranvändning** per ledare: `dark`, `lit`, `dark_fibre` (uthyrd, med kund eller avtal) och `spare`. `lit` härleds, eftersom ledaren ingår i en aktiv krets. De övriga är uttryckliga.
7. **Påverkan per trasésträcka** läggs i grafmotorn som ett härlett index, sträcka → kablar, som laddas med grafen och följer ändringsflödet (#11). En grävskada blir påverkan av alla kablar på sträckan, med samma kod som för en kabel (#10).
8. **Omfång:** trasé och dukter syns när deras geometri skär omfångets område, som kablar i läget `clip` (#22). Beläggning med kablar utanför omfånget visas som *upptagen* utan kabelns namn.

## Alternativ
- **Dukter som kablar av en särskild typ.** Återanvänder kabelvägen men blandar ihop rör och ledare. Subdukter som "ledare" och kablar i ledare blir svårbegripligt.
- **Dukter och subdukter som terminaler i grafen.** Kanalisationen bär ingen signal. Att lägga den i signalgrafen gör spårning och påverkan långsammare utan nytta. Påverkan per sträcka behöver bara indexet i punkt 7.
- **Brunnar som egen objekttyp.** Ger en egen tabell, egen sök och eget omfång för något som redan är en plats.

## Konsekvenser
- Nya tabeller: `route_segment`, `duct_type`, `duct`, `duct_segment`, `subduct` och `cable_path`, samt en kolumn för användning på `conductor`. Nya sitetyper och en ny katalog.
- Datageneratorn lägger stamnät och aggregering i gemensam trasé med multidukter. Fullskala växer med uppskattningsvis 100 000 trasésträckor och 1 M subdukter. Prestandabudgeten mäts om.
- Nya ytor: ett kartlager för trasé, en tvärsnittsvy för multidukter (samma mönster som frontpanelen), påverkan per sträcka, och en fråga om ledig kapacitet i avancerad sökning. Allt finns i API:t och MCP.
