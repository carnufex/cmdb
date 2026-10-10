# Drift och leveransflöde

Hur en ändring går från ett issue till demomiljön på <https://cmdb.rosenvall.se>. Flödet gäller både människor och agenter. Agenter följer skillen `cmdb-delivery` (`.claude/skills/cmdb-delivery/`), som är en körbar version av det här dokumentet.

## Miljöer

| Miljö | Adress | Data | Hur den uppdateras |
|---|---|---|---|
| Lokal | http://localhost:8480 (compose) eller :4200 (`ng serve`) | Syntetisk, valfri skala | `docker compose up -d --build` |
| Demo | https://cmdb.rosenvall.se | Syntetisk, full skala (seed 1) | `scripts/deploy.sh --deploy` från `main` |

Båda loggar in mot homelabbets Authentik. Demomiljön körs i klustret `hemma-k8s` som ArgoCD-appen `cmdb`, med manifesten i `Rosenvalls-Homelab/kubernetes/applications/cmdb/`.

## Flödet

```
issue ──► branch <nr>-slug ──► lokal utveckling ──► scripts/verify.sh ──► PR ──► squash-merge till main ──► scripts/deploy.sh --deploy --wait ──► verifiera på demo
```

1. **Issue och branch.** Som i [CLAUDE.md](../CLAUDE.md): ta issuet, planera i det och skapa `<nr>-kort-slug`.
2. **Utveckla lokalt.** Allt byggs och testas lokalt, antingen hela stacken i compose eller databasen i Docker och apparna med hot reload (se README).
3. **Verifiera.** `scripts/verify.sh` är CI, eftersom GitHub Actions inte kan köras. Ändringar i Dockerfiles, nginx eller beroenden kräver `scripts/verify.sh --images`.
4. **Pull request** mot `main` med `Closes #<nr>` och verifieringen enligt mallen.
5. **Merge.** Squash-merge när verifieringen är grön och PR:en är granskad. `main` är alltid det som ska köras i demomiljön, så merga inte något som inte kan driftsättas.
6. **Deploy direkt efter merge**, från en ren och uppdaterad `main`:

   ```bash
   git switch main && git pull
   scripts/deploy.sh --deploy --wait
   ```

   Skriptet bygger `cmdb-api`, `cmdb-web` och `cmdb-datagen` för `linux/amd64`, pushar dem till `registry.rosenvall.se/carnufex/` med taggen `sha-<commit>`, skriver in `tagg@digest` i homelabbets `app.yaml`, committar och pushar homelab-repot. ArgoCD synkar. `--wait` väntar tills båda deploymenterna har rullat ut och kontrollerar att sajten svarar.
7. **Verifiera på demo** det som issuet handlar om, och kommentera i issuet vilken tagg som rullades ut.

Varför så här: GitHub Actions är avstängt, så bygge och publicering sker lokalt. Att bara driftsätta från `main` gör att demomiljön alltid motsvarar en commit som gått igenom PR, och att taggen `sha-<commit>` pekar tillbaka på exakt den koden. Images pinnas med digest i homelab-repot, så det som körs är oföränderligt och syns i Git.

En image från en branch kan byggas för att testa den (`scripts/deploy.sh --allow-branch`), men den driftsätts aldrig till demo.

## Förutsättningar på arbetsstationen

- Docker, inloggad mot registret en gång: `docker login registry.rosenvall.se` (användare `homelab`, lösenord i Bitwarden Secrets Manager: `REGISTRY_PASSWORD`).
- Homelab-repot utcheckat. Skriptet letar i `~/source/repos/Rosenvalls-Homelab`, annars sätts `HOMELAB_REPO`.
- För `--wait`: `kubectl` med `KUBECONFIG` mot klustret och Git Bash på Windows.

## Databas och migreringar

- PostGIS körs med CloudNativePG (`cmdb-postgresql`, en instans, Longhorn). `postgis` och `pg_trgm` skapas när klustret initieras, eftersom postgis kräver superuser.
- Migreringarna och synken av typkatalogen körs i init-containern `migrate` (`dotnet Cmdb.Api.dll --migrate`) innan API:t startar. De körs alltså vid varje utrullning.
- **Egen katalog** (#207): montera en mapp med samma struktur som `catalog/` som volym och sätt `CMDB_CATALOG_PATH` på API:t, init-containern `migrate` och datageneratorn, alla till samma sökväg. Mappen behöver bara vara läsbar, så skrivskyddad montering och godtyckligt UID fungerar. Utan variabeln används den inbäddade syntetiska katalogen. Se [domanmodell.md](domanmodell.md#extern-katalog).

  ```yaml
  env:
    - name: CMDB_CATALOG_PATH
      value: /catalog
  volumeMounts:
    - name: catalog
      mountPath: /catalog
      readOnly: true
  volumes:
    - name: catalog
      configMap:          # eller en PVC eller git-sync-sidecar när katalogen är stor
        name: cmdb-catalog
  ```

  En ConfigMap kan inte ha undermappar; använd `items` med `path: equipment-types/<key>.json` per fil, eller en volym som har mappstrukturen.
- **Migreringar måste tåla att den förra versionen fortfarande kör** under utrullningen och vid en rollback. Lägg till först och ta bort i en senare release (expand/contract). En destruktiv migrering kräver ett issue med `type:decision`.
- Databasen säkerhetskopieras inte. All data är syntetisk och genereras om.

### Direkt databasåtkomst

Integrationer, rapporter och export som läser databasen utan API:t får en egen roll (ADR-0012):

1. Skapa rollen som managed role i CloudNativePG (`database.yaml` i homelabbet) med lösenordet i Bitwarden och en ExternalSecret, som `cmdb_rapport_nord`.
2. Lägg rollen i `db_roles` på de omfång den ska ha (i demon: `ScopeCatalog`).
3. Starta om API:t. Migreringssteget ger rollen SELECT på de tabeller som har radpolicyer och inget annat.

Rollen ser bara det dess omfång visar. Tas den bort ur `db_roles` ser den ingenting, även om behörigheterna ligger kvar. Så här provar du demorollen:

```bash
kubectl -n cmdb exec -it cmdb-postgresql-1 -- psql "host=localhost dbname=cmdb user=cmdb_rapport_nord password=$(kubectl -n cmdb get secret cmdb-rapport-nord -o jsonpath='{.data.password}' | base64 -d)" -c "SELECT count(*) FROM site"
```

### Ladda eller ladda om data

```bash
scripts/load-demo-data.sh                 # full skala, seed 1
scripts/load-demo-data.sh --scale small   # snabbt, för att prova
```

Skriptet kör datagen som ett Job i namnrymden `cmdb` med imagen `cmdb-datagen` från samma tagg som `cmdb-api` kör, så schemat alltid stämmer. Det skriver över all data (`--reset`) och startar om API:t efteråt. Full skala tar runt fem minuter. Behövs det efter en deploy? Bara om migreringen kräver omladdning eller datagen har ändrats.

Använd inte `kubectl port-forward` för att ladda full skala: tunneln tappar anslutningen mitt i en stor `COPY`.

### Import av ett befintligt nät

Ett nät i utbytesformatet ([import.md](import.md), #210) läses in med datagen-imagen som ett Job, precis som laddningen ovan men med argumenten:

```yaml
args: ["import", "--from", "/import", "--source", "<källsystem>"]
```

- **Mappen** monteras skrivskyddat på `/import`, och samma katalog som API:t monteras med `CMDB_CATALOG_PATH`. Riktig data ligger i den organisationens egna repo eller volym, aldrig här (ADR-0018).
- **Prova först med `--dry-run`.** Den kontrollerar varje rad och rapporterar fel per fil och rad utan att skriva.
- **Utan `--reset`:** importen skriver över ingenting. Den skapar och uppdaterar objekt från källsystemet och kan köras igen. Grafen laddas om av sig själv via ändringsströmmen, så API:t behöver inte startas om.

### Avstämning mot källsystem

Varje integration körs som ett CronJob med imagen `cmdb-cli` (#217, [adaptrar.md](adaptrar.md)). Imagen byggs och pushas av `scripts/deploy.sh` med samma tagg som API:t, och CronJobbet pinnas i homelab-repot som API:t. Exempel för referensadaptern:

```yaml
apiVersion: batch/v1
kind: CronJob
metadata:
  name: cmdb-sync-acme-monitor
  namespace: cmdb
spec:
  schedule: "15 * * * *"
  concurrencyPolicy: Forbid
  jobTemplate:
    spec:
      backoffLimit: 0
      template:
        spec:
          restartPolicy: Never
          securityContext:
            runAsNonRoot: true
          containers:
            - name: sync
              image: registry.rosenvall.se/carnufex/cmdb-cli:sha-<commit>@sha256:<digest>
              args: ["sync", "acme-monitor"]
              env:
                - name: CMDB_URL
                  value: http://cmdb-api.cmdb.svc:8080   # API:ts Service i klustret, som i homelab-manifestet
                - name: CMDB_SYNC_CONFIG
                  value: /config/acme-monitor.json
              envFrom:
                - secretRef:
                    name: cmdb-sync-acme-monitor   # CMDB_CLIENT_ID, CMDB_USERNAME, CMDB_PASSWORD, CMDB_SYNC_ACME_MONITOR_URL, CMDB_SYNC_ACME_MONITOR_TOKEN
              volumeMounts:
                - { name: config, mountPath: /config, readOnly: true }
              securityContext:
                allowPrivilegeEscalation: false
                capabilities: { drop: ["ALL"] }
          volumes:
            - name: config
              configMap: { name: cmdb-sync-acme-monitor }
```

- **Kontot** är integrationens eget, medlem i `cmdb-integration` och i integrationens omfångsgrupp, aldrig i `cmdb-full` (ADR-0021).
- **Anropet går inom klustret** till API:t, inte via Cloudflare, så att stora arkiv inte stoppas av gränsen för uppladdningar där.
- **Hemligheterna** kommer från en Secret eller ExternalSecret. Konfigurationen, med tabellerna för modeller och tillstånd, ligger i en ConfigMap.
- **Prova först** med `kubectl create job --from=cronjob/cmdb-sync-acme-monitor prova` och argumenten `["sync", "acme-monitor", "--dry-run"]`.
- **Resultatet** syns under *Avstämningar* i appen och med `cmdb reconciliations`. Ett Job som misslyckas har felkod 4 och felet i loggen.

## Prestandamätning

`scripts/perf.sh` mäter prestandabudgeten ([plan.md](plan.md#prestandabudget)) med k6 (#13), med stickprov ur det laddade nätet och serverns p95 (`Server-Timing`) mot budgeten. GitHub Actions är avstängt, så det här är det manuella jobbet.

```bash
scripts/perf.sh                          # mot demomiljön, 60 s, 15 anrop/s
scripts/perf.sh --target local           # mot docker compose på localhost:8480
scripts/perf.sh --comment 13             # posta rapporten i ett issue
```

- Anropen görs som agentkontot `cmdb-agent-demo`. Lösenordet hämtas ur Bitwarden (`CMDB_AGENT_TOKEN`) med `bws`, med åtkomsttoken från klustret om `BWS_ACCESS_TOKEN` saknas. Takten ligger under agentgränsen 20 anrop/s.
- Rapporten hamnar i `artifacts/perf/<tid>-<mål>/summary.md` och `summary.json` (ignoreras av git).
- En rad över budget, eller misslyckade anrop, markeras med ❌ och ger felkod. Kör mätningen efter varje driftsättning som rör API:t eller databasen, och kommentera rapporten i issuet.

## Händelselogg

Användarna ser nyheter under *Nyheter* i verktygsfältet (#82). Posterna ligger i `changelog/entries.json` och synkas till databasen i migreringssteget.

1. `scripts/deploy.sh --deploy` skriver underlaget för det som just rullades ut till `artifacts/changelog/<tagg>.md`. Underlaget är prompten i `docs/changelog-prompt.md` plus issue, PR och berörda delar per commit. Det kan också köras för hand: `scripts/changelog.sh <från-sha> <till-sha>`, och med `--generate` om `claude` finns.
2. Agenten som driftsätter skriver posterna med prompten och lägger dem i `changelog/entries.json` med `"published": false`.
3. En människa granskar texten i PR:en och sätter `"published": true`. Posten syns efter nästa utrullning och räknas som oläst för alla som inte öppnat *Nyheter* sedan dess.

Agenter läser samma logg som MCP-resursen `cmdb://changelog` eller via `GET /api/changelog`.

## Rollback

Återställ homelab-commiten `cmdb: deploy sha-…` med `git revert` och pusha. ArgoCD rullar tillbaka till förra digesten. Migreringar backas inte, vilket är skälet till regeln om bakåtkompatibla migreringar ovan.

## Felsökning

```bash
kubectl -n argocd get application cmdb                       # synkstatus
kubectl -n cmdb get pods,externalsecret,cluster              # poddar, hemligheter, databas
kubectl -n cmdb logs deploy/cmdb-api -c migrate              # migreringssteget
kubectl -n cmdb logs deploy/cmdb-api                         # API:t
```

- `ExternalSecret` i `SecretSyncError`: namnrymden saknas i `ClusterSecretStore` eller hemligheten saknas i Bitwarden.
- Poddar i `ImagePullBackOff`: kontrollera att `cmdb-registry` finns och att taggen pushades.
- Anslutningar som hänger eller nekas mellan poddar, mot databasen eller mot Authentik: namnrymden har default deny (CiliumNetworkPolicy, #182). Se vad som släpps i `kubectl -n cmdb get cnp` och vad som nekas med `hubble observe -n cmdb --verdict DROPPED`. Nya arbetslaster i namnrymden behöver en egen regel.
- Inloggningen studsar: redirect-URI:n måste finnas i blueprinten `apps-cmdb.yaml` i homelab-repot.

## Hemligheter

| Bitwarden-post | Används till |
|---|---|
| `CMDB_DB_PASSWORD` | Databasägaren och API:ts anslutningssträng |
| `CMDB_DEMO_PASSWORD` | Demoanvändarna i Authentik |
| `REGISTRY_PASSWORD` | Push lokalt och pull i klustret |

Hemligheter checkas aldrig in, varken här eller i homelab-repot.
