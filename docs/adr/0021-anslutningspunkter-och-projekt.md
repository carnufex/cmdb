# ADR-0021: Anslutningspunkter och projekt för planering av nytt nät

**Status:** Accepterad (alternativ B i #290) · **Datum:** 2026-10-11 · **Genomförs i:** #292–#296 · **Utredning:** [#260](../utredningar/260-nytt-fibernat.md)

## Kontext
Att planera ett nytt fibernät i ett område (#260) kräver två saker som modellen saknar:

- **Var kunderna finns.** Fibrer dimensioneras och noder placeras efter adresser och kunder, men de är inte siter. De har ingen utrustning, inga platser och ingen livscykel som nät förrän de ansluts. Att lägga dem som siter av en egen sitetyp skulle ge tusentals siter utan innehåll, i sök, träd, omfång och grafen.
- **Ett område som helhet.** En utbyggnad är flera etapper, det vill säga planer som bygger på varandra (ADR-0005). Det finns inget som håller ihop dem med området, ansvarig och den samlade kalkylen.

## Alternativ
- **A. Anslutningspunkter som siter av en egen sitetyp, och etapper som en namnkonvention på planer.** Ingen modelländring. Men siterna blir många och tomma, varje sitefråga måste filtrera bort dem, och etapperna hålls ihop bara av namn.
- **B. Två nya objekttyper vid sidan av kärnmodellen.**
  - **Anslutningspunkt** (`service_point`): position i EPSG:3006, typ (ur katalogen, till exempel villa, flerbostadshus med antal lägenheter, företag och mast), status (*planerad*, *beställd*, *ansluten*), adress och extern id, plus proveniens som andra objekt (ADR-0019). När den ansluts pekar den ut siten eller porten som betjänar den. Den ligger inte i grafen och har ingen livscykel som nät.
  - **Projekt** (`project`): namn, område (polygon), ansvarig och dess etapper som en ordnad lista av planer. Planerna ändras inte.
  - Båda följer omfången (ADR-0007): en anslutningspunkt som en site via sin position, och ett projekt via sitt område.
- **C. Anslutningspunkter i ett externt system** (till exempel en kund- eller adressdatabas), som bara läses vid planering. Inget lagras här, men dimensionering, kartlager och etapper blir beroende av ett system som demon inte har.

## Beslut
**B.** Det håller siterna och grafen fria från tusentals tomma objekt och gör etapperna till något man kan se och följa. Kärnmodellen och planerna (ADR-0005) ändras inte. Projektet är en vy ovanpå planer, och anslutningspunkterna är data som förslagen och dimensioneringen läser.

## Konsekvenser
- Nya tabeller via EF Core (ADR-0009): `service_point`, `service_point_type` (katalog), `project` och `project_plan`. Anslutningspunkter får sök, kartlager och panel.
- Planoperationer för anslutningspunkter (ny, flytta, ändra status, koppla till site eller port), med samma kontroller som andra operationer.
- Omfång: anslutningspunkter filtreras på position som siter. Ett projekt syns för den som ser dess område.
- Datageneratorn får ett greenfield-område med anslutningspunkter.
- Kostnad i katalogen (pris per meter och per enhet) är katalogdata och omfattas inte av beslutet.
