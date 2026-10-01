# ADR-0017: Klassningar och regler som en modul vid sidan av kärnmodellen

**Status:** Föreslagen · **Datum:** 2026-10-01 · **Issue:** #175

## Kontext
Mycket information om nätet passar inte i den exakta datamodellen (site, utrustning, kabel, krets och tjänst), men avgör ändå hur nätet får byggas. Några exempel:

- Kritikalitetsnivåer (1–5), säkerhetsklass och redundanskrav.
- Krav på reservkraft och skalskydd för en byggnad.

Klassningar hänger dessutom ihop. En switch med nivå 5 i ett hus gör racket, rummet och huset till nivå 5, och kablarna som går in måste uppfylla nivåns krav. Det ska synas när man planerar. Om en ny nivå 5-switch hamnar på en site som inte klarar nivån ska planen säga vad som behöver förstärkas eller föreslå en annan site.

I dag finns kritikalitet bara som ett fritt attribut på tjänster (`criticality: critical`), och påverkansanalysen och driftagenten läser det direkt.

## Alternativ
- **A. Mer i kärnmodellen.** Kolumner eller attribut per typ för varje klassning. Det är enkelt i början, men varje ny klassning blir en migrering eller en schemaändring, och ärvning och regler hamnar spridda i koden.
- **B. En klassnings- och regelmodul.**
  - **Scheman** versioneras i katalogen (`catalog/classifications/*.json`, precis som utrustningstyperna): vilka nivåer som finns, vilka objekttyper de gäller och hur de ärvs.
  - **Tilldelningar** lagras i en egen tabell (objekt, schema, nivå, källa).
  - **Härledda nivåer** räknas fram i grafmotorn genom ärvning: inneslutning (utrustning, rack, rum, byggnad, site) och beroende (det som bär en klassad sites tjänster).
  - **Regler** är krav per nivå och utvärderas mot produktion och mot en plans vy. Avvikelser blir varningar i planen, risker i driftläget och förslag till åtgärd.
- **C. En extern policymotor** (till exempel OPA/Rego). Den är kraftfull, men lägger till en körmiljö och ett språk, och reglerna behöver grafens härledda data ändå.

## Rekommendation
**B.** Det håller kärnmodellen liten (ADR-0001 och ADR-0002) och gör klassningar till data i stället för kod, på samma sätt som typkatalogen. Ärvningen bygger på grafen som redan finns i minnet. Planerna (ADR-0005) får regelutfallet i sin förhandsvisning utan något nytt flöde.

## Konsekvenser (vid B)
- **Prestanda:** härledda nivåer räknas i grafmotorn och följer med i deltan (#119 och #121). Prestandabudgeten gäller.
- **Tjänsternas `criticality`** flyttas in i modulen som ett schema. Påverkansanalysen, prioritetsreglerna och driftagenten läser därefter klassningen.
- **Regler är data:** krav som "nivå 5 kräver två inkommande kablar via skilda trasér" uttrycks i katalogen med ett litet antal regeltyper (antal, oberoende vägar, attributkrav och nivå på beroenden). Fritt kodade regler används inte.
- **Ledet "skilda trasér"** blir exakt först med kanalisationen (#92 och ADR-0014). Fram till dess räknas oberoende på kablar och siter.
- **Behörighet:** tilldelningar och regelutfall följer behörighetsomfången som allt annat.
