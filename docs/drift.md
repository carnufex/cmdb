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
- **Migreringar måste tåla att den förra versionen fortfarande kör** under utrullningen och vid en rollback. Lägg till först och ta bort i en senare release (expand/contract). En destruktiv migrering kräver ett issue med `type:decision`.
- Databasen säkerhetskopieras inte. All data är syntetisk och genereras om.

### Ladda eller ladda om data

```bash
scripts/load-demo-data.sh                 # full skala, seed 1
scripts/load-demo-data.sh --scale small   # snabbt, för att prova
```

Skriptet kör datagen som ett Job i namnrymden `cmdb` med imagen `cmdb-datagen` från samma tagg som `cmdb-api` kör, så schemat alltid stämmer. Det skriver över all data (`--reset`) och startar om API:t efteråt. Full skala tar runt fem minuter. Behövs det efter en deploy? Bara om migreringen kräver omladdning eller datagen har ändrats.

Använd inte `kubectl port-forward` för att ladda full skala: tunneln tappar anslutningen mitt i en stor `COPY`.

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
- Inloggningen studsar: redirect-URI:n måste finnas i blueprinten `apps-cmdb.yaml` i homelab-repot.

## Hemligheter

| Bitwarden-post | Används till |
|---|---|
| `CMDB_DB_PASSWORD` | Databasägaren och API:ts anslutningssträng |
| `CMDB_DEMO_PASSWORD` | Demoanvändarna i Authentik |
| `REGISTRY_PASSWORD` | Push lokalt och pull i klustret |

Hemligheter checkas aldrig in, varken här eller i homelab-repot.
