# Adaptrar och `cmdb sync` (#217)

En adapter läser ett källsystem och skriver vad det vet i utbytesformatet ([import.md](import.md#filerna)). Allt annat gör avstämningen i API:t ([avstamning.md](avstamning.md)): matchning, källprioritet, avvikelser, planer och behörighet. En adapter är därför tunn, och de flesta är under 150 rader.

```bash
cmdb sync                                # vilka adaptrar som finns
cmdb sync acme-monitor --dry-run         # läs källan och stäm av, utan planer
cmdb sync acme-monitor                   # läs källan och stäm av
cmdb sync acme-monitor --out ./export    # skriv bara filerna, för felsökning
cmdb reconciliations                     # tidigare körningar
cmdb reconciliation 12                   # en körnings rapport
```

Felkoden är 0 när avstämningen gick igenom, 3 om kontot saknar behörighet, och 4 om källan inte gick att läsa eller filerna hade fel. Adapterns varningar skrivs till stderr och räknas i rapporten.

## Konto och omfång

Varje integration kör med ett eget tjänstekonto (ADR-0021):
- Kontot är medlem i gruppen `cmdb-integration`, som bara får stämma av, och i integrationens egen grupp, till exempel `cmdb-integration-acme-monitor`, som ger dess omfång.
- Omfånget är ett vanligt omfång i `access_scope`, med område, sitetyper och dolda attribut. Det beviljas med motivering och godkännande av en andra person. Det behöver se avstämningens planer (`plans`), annars kan den betrodda planen inte föras in.
- Kontot är aldrig medlem i `cmdb-full`.
- Inloggning som för andra tjänstekonton: `CMDB_CLIENT_ID`, `CMDB_USERNAME` och `CMDB_PASSWORD` (client credentials), och `CMDB_URL`.

## Inställningar och hemligheter

- **Inställningar** läses från miljön, `CMDB_SYNC_<ADAPTER>_<NYCKEL>` (till exempel `CMDB_SYNC_ACME_MONITOR_URL`). Annars läses de från JSON-filen i `--config` eller `CMDB_SYNC_CONFIG`. Tabeller, som källans modellnamn mot katalogens nycklar, ligger i filen.
- **Hemligheter** läses bara från miljön, så att de kan komma från en Secret eller en ExternalSecret. De står aldrig i konfigurationsfilen och aldrig i repot.
- **Källsystemets namn** i cmdb är adapterns, eller `--source`. Det är namnet som `source-priority.json` och `source-matching.json` använder.

## Skriva en ny adapter

Riktiga källsystem får adaptrar i organisationens egen fork, aldrig här (ADR-0018). Mönstret är detsamma:

1. **Läs källans API-dokumentation.** Ta reda på hur man autentiserar, hur sidor fungerar (sidnummer, markör eller nästa-länk), vilka objekt som finns och vilka fält som identifierar dem.
2. **Bestäm vad källan rapporterar.** Välj de filer i utbytesformatet som källan faktiskt vet något om. Ett övervakningssystem känner ofta till siter och utrustning, men inte kablar. En fil som inte skrivs lämnar de objekten orörda.
3. **Välj id:n.** Använd källans egna stabila id:n i `id`-kolumnerna och i hänvisningarna mellan filerna. Matchningen mot cmdb sker på koder och serienummer enligt `source-matching.json`, inte i adaptern.
4. **Skriv klassen** i `src/cli/Sync/Adapters/` (i forken). `cmdb sync` hittar alla klasser som implementerar `IAdapter`, så ingen annan kod behöver ändras.

   ```csharp
   public sealed class MinAdapter : IAdapter
   {
       public string Name => "min-kalla";
       public string Source => "min-kalla";
       public string Description => "Vad källan är";

       public async Task ReadAsync(AdapterContext context, ExchangeWriter output, CancellationToken ct)
       {
           var url = new Uri(context.Setting("url").TrimEnd('/') + "/");
           var token = context.Secret("token");
           var models = context.Map("models");
           await foreach (var item in AdapterContext.PagedAsync((page, c) => PageAsync(context, url, page, token, c), ct))
           {
               // Mappa källans fält till utbytesformatet. Hoppa över det som inte går, med en varning.
               if (!models.TryGetValue(Text(item, "model"), out var type))
               {
                   context.Warn($"{Text(item, "id")}: okänd modell");
                   continue;
               }
               output.Equipment(Text(item, "id"), Text(item, "site"), Text(item, "name"), type, location: Text(item, "rack"));
           }
       }
   }
   ```

5. **Mappa namn med konfiguration, inte kod.** Källans modellnamn, sitetyper och tillstånd mappas mot katalogens nycklar och livscykler med tabeller i konfigurationsfilen (`context.Map`). Då kan en ny modell läggas till utan en ny version.
6. **Hoppa hellre över än gissa.** Saknas något som behövs, eller pekar en rad på något okänt, anropa `context.Warn` och hoppa över raden. Varningarna syns i körningens utdata.
7. **Testa mot ett fejkat API.** Se `SyncTests` och referensadaptern `AcmeMonitorAdapter`: en minimal webbapp i testet svarar som källan med sidor och token, och testet kör `cmdb sync` mot ett riktigt API med integrationens konto och omfång.
8. **Prova på riktigt med `--out`** och sedan `--dry-run`, innan körningen schemaläggs.

### Hjälp i `AdapterContext`

| | |
|---|---|
| `Setting(nyckel)`, `OptionalSetting(nyckel)` | En inställning från miljön eller filen |
| `Secret(nyckel)` | En hemlighet, bara från miljön |
| `Map(nyckel)` | En tabell från konfigurationsfilen |
| `GetJsonAsync(url, token, ct)` | GET med JSON-svar. 429 och 5xx försöks igen några gånger, och `Retry-After` följs. |
| `PagedAsync(sida, ct)` | Läser alla sidor: sidnummer, markör eller nästa-länk. Stannar om en sida pekar tillbaka. |
| `Warn(text)` | Något som utelämnades eller gissades |

`ExchangeWriter` har metoder för siter, locations, utrustning, kablar och tjänster, och `Row` för övriga filer med kolumnerna i [import.md](import.md#filerna).

## Referensadaptern `acme-monitor`

ACME Monitor är ett syntetiskt övervakningssystem. Adaptern visar mönstret för en REST-källa:

| Anrop | Svar |
|---|---|
| `GET {url}/api/v1/sites?page=N` | `{"items":[{"id","code","name","type","lat","lon","state"}],"nextPage":N\|null}` |
| `GET {url}/api/v1/devices?page=N` | `{"items":[{"id","site","rack","hostname","model","serial","firmware","state"}],"nextPage":N\|null}` |

- Token i `CMDB_SYNC_ACME_MONITOR_TOKEN`.
- Inställningen `url`, och tabellerna `models` (källans modell mot katalogens nyckel), `siteTypes` och `states` (källans tillstånd mot livscykel; `active`, `planned`, `installing`, `retiring` och `retired` har standardvärden).
- Siter rapporteras med kod och position. Enheter rapporteras med serienummer och firmware som attribut, i ett rack per site.
- I demon matchas siterna på kod och utrustningen på serienummer mot det som acme-nms har importerat. Källprioriteten litar inte på ACME Monitor för attribut, så skillnader där blir avvikelser.

## Schemaläggning

En adapter körs som ett CronJob per integration med imagen `cmdb-cli`, se [drift.md](drift.md#avstämning-mot-källsystem).
