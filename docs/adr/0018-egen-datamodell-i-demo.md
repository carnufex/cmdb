# ADR-0018: Egen datamodell i ett separat repo med cmdb som uppströms

**Status:** Accepterad · **Datum:** 2026-10-08 (beslutat i #206)

## Kontext
cmdb ska kunna visas på en arbetsplats med organisationens egna utrustningsmodeller, typer och data. CLAUDE.md regel 1 förbjuder riktig data och riktiga datamodeller i det här repot, och agenterna som arbetar här ska inte se riktig data. Innan den externa katalogen och importen byggs (Fas 8) behövs ett beslut om var datan ligger och hur den organisationens version hålls i takt med det här repot.

## Alternativ
- **A. Privat repo eller volym plus en instans på arbetsplatsen.** Katalog och importfiler ligger inom arbetsplatsens nät, och demon körs där.
- **B. Anonymiserad export i homelabbet.** Agenterna kan hjälpa till, men anonymiseringen måste godkännas och kan ändå avslöja nätets struktur.
- **C. Bara modellerna, syntetisk data.** Riktiga modeller med ett genererat nät. Minst risk, men visar inte den egna datan.
- **D. Ett separat repo som bygger vidare på cmdb.** Den organisationens katalog, data och eventuella anpassningar ligger i ett eget repo, med det här repot som uppströms.

## Beslut
**D**, beslutat i #206. Riktiga modeller och riktig data hamnar i ett separat repo som utgår från det här. cmdb förblir syntetiskt och är uppströms.

Repot skapas som ett eget repo på arbetsplatsens GitHub, med cmdb som `upstream`-remote, och inte som en GitHub-fork. En fork av ett privat repo kan inte flyttas till en annan organisation och följer det här repots synlighet.

## Konsekvenser
- **Skillnader mellan organisationer är data eller konfiguration, aldrig kod.**
  - Det gäller katalog, sitetyper, kategorier, attributscheman och klassningar.
  - Varje kodändring i det andra repot blir en framtida merge-konflikt mot uppströms. Därför byggs det som behövs för att byta modell här, generellt och mot syntetiska exempel.
- **Den externa katalogen (#207)** läses från `CMDB_CATALOG_PATH`. Det andra repot lägger sin katalog där i stället för att ändra `catalog/`.
- **Roller i stället för typnamn (#208).**
  - Sitetyper och utrustningskategorier är katalogdata med roller.
  - Koden frågar efter roller, så en organisation kan döpa sina typer fritt.
- **Attributscheman (#211)** gör även siters, kablars och tjänsters fält till katalogdata.
- **Katalogverktyg och import (#209, #210)** byggs och testas här mot syntetiska exempel och körs i det andra repot mot riktig data.
- **Datageneratorn** kan byggas mot en egen katalog (#219), så att det andra repot kan visa ett genererat nät innan riktig data importeras.
- **Agenterna i det här repot ser aldrig riktig data.** Fel som bara syns mot riktig data återskapas här med syntetiska exempel.
- **Uppdateringar flödar en väg:** från cmdb till det andra repot (`git fetch upstream` och merge). Förbättringar som hittas där och är generella förs tillbaka hit som issues, med syntetisk data.
