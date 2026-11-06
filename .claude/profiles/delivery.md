# Delivery profile — cmdb

<!-- Read by the delivery plugin (`delivery@carnufex`, enabled in .claude/settings.json): new-issue, work-issue,
     plan-milestone, decision, ship. Facts about cmdb only; CLAUDE.md and CONTRIBUTING.md stay the source of the rules. -->

## Language

- Issues, PR descriptions and issue comments: **Swedish**
- Code, identifiers and commit messages: English

## Labels

- Type: `type:feature`, `type:task`, `type:spike`, `type:bug`, `type:decision`
- Area: `area:backend`, `area:frontend`, `area:data`, `area:infra`, `area:security`, `area:docs`
- Phase: `fas-0` … `fas-4` (the plan's phases, `docs/plan.md`)
- Status: `status:ready`, `status:in-progress`, `status:blocked`
- Human: `needs-human`, always with the section `## Vad behövs från dig`

## Status and claims

- Ready: `status:ready` and no `needs-human`
- Claim: `node <work-issue skill>/scripts/claim.mjs claim <n> --label status:in-progress --as "Claude (<person>): <title>" --branch <n>-<slug>`,
  then remove `status:ready` (`gh issue edit <n> --remove-label status:ready`)
- Blocked: `status:blocked` plus a comment with what unblocks it

## Milestones

- GitHub milestones, one per phase of `docs/plan.md`; the `fas-N` label matches the milestone.

## Issue template

Bakgrund · Mål · Acceptanskriterier (kryssrutor) · Avgränsningar · Hur vi märker att det går sönder (when it runs in
production) · Referenser (ADR:er, dokument, relaterade issues). Discovered work: "Upptäckt i #<nr>".

## Branches and commits

- Default branch `main`, changed only through PRs.
- Branch `<nr>-<kort-slug>`, e.g. `12-trace-service`; worktree `../cmdb-<nr>` when the main checkout has someone else's changes.
- Commits: Conventional Commits with the issue number, `feat(graph): trace across layers (#12)`.
- PR title `<type>(<scope>): <summary> (#<nr>)`; body `.github/pull_request_template.md` (Swedish: Vad och varför,
  Verifiering, Prestanda, checklist) plus a `3 am:` line.

## Tests policy

Unit tests for domain logic, integration tests against real PostgreSQL (Testcontainers), benchmarks for the graph engine.
The performance budget in `docs/plan.md` is a requirement: numbers in the PR when relevant.

## Verify

| Change touches | Run |
|---|---|
| anything | `scripts/verify.sh` — must end with `==> OK`; paste the summary in the PR |
| Dockerfile, nginx config, dependencies | `scripts/verify.sh --images` |
| web app | `scripts/e2e.sh` (main flows in a real browser against a throwaway stack, ~30 s warm) |
| anything user-visible | `docker compose up -d --build`, http://localhost:8480, screenshots |

## Review keys

- Issue heading: `## Review key` (in Swedish like the rest of the issue); written with the issue or, at the latest, as the
  first comment before work starts.
- Key files: `.review-keys/<nr>.json`; key-owned checks under `src/web/e2e/review-keys/<nr>-<slug>.spec.ts` (they also run
  in the whole e2e suite afterwards, as regression tests) or `tests/**/ReviewKeys/`.
- Run: `node <review-key skill>/scripts/keycheck.mjs run <nr> --out artifacts/review-key/<nr>` (`artifacts/` is git-ignored).
- UI keys run in the e2e stack: `bash scripts/e2e.sh -- review-keys/<nr>-<slug>.spec.ts`. While implementing, keep the
  stack up (`scripts/e2e.sh --keep -- …`) and run the spec against it (`E2E_BASE_URL=http://localhost:4310
  E2E_IDP=http://127.0.0.1:8790/application/o/cmdb/ npx playwright test -c e2e/playwright.config.ts <spec>` in `src/web`);
  the dev server reloads on save. Stop the kept stack (ports 4310, 5199, 8790 and container `cmdb-e2e-db`) before the
  key-reviewer runs the key, since it starts its own.
- A worktree needs its own `npm ci` in `src/web`; the main checkout's `node_modules` may be older than the branch.
- Usual kinds: `spec` (an e2e spec that measures the behaviour, or API integration tests), `parity` for API changes
  that must not change answers (recorded responses), `eval` for the performance budget in `docs/plan.md`.

## Demo

- When: every PR with a user-visible change.
- Captions: Swedish.
- Before/after: `E2E_SHOTS=<dir>` makes a review-key spec save its screenshots (run it on `main` and on the branch).
- Record the changed flow: Playwright `recordVideo` around the key spec's steps against the kept stack, 1280×800.

## Notify

- Slack: #pr-demos (C0CFQLUEP36), posted as the bot with the showcase plugin's post-slack.mjs

## Merge

- When `scripts/verify.sh` is green: `gh pr merge <pr> --squash --delete-branch`, then `git switch main && git pull --ff-only`.
- A change that cannot be deployed right now is not merged: `main` is what the demo runs.
- A destructive migration needs a `type:decision` issue first.

## Deploy

- By command, from a clean, up-to-date `main` in Git Bash: `scripts/deploy.sh --deploy --wait`. It refuses a dirty tree
  or any HEAD other than `origin/main`; from a busy checkout use a worktree with `git switch --detach origin/main`.
- It builds `cmdb-{api,web,datagen}` for linux/amd64, pushes `sha-<commit>` to the registry, pins them by digest in the
  homelab repo (`kubernetes/applications/cmdb/app.yaml`, `HOMELAB_REPO`), commits `cmdb: deploy sha-<commit>`, pushes,
  refreshes ArgoCD, waits for both rollouts and checks that the site answers.
- Never `--deploy` from a branch; never hand-edit image refs in the homelab repo. `scripts/deploy.sh --allow-branch`
  builds an image to test without deploying.
- Once per machine: `docker login` to the registry (Bitwarden `REGISTRY_PASSWORD`) and `KUBECONFIG` for the cluster.
- The human-readable version is `docs/drift.md`.

## Live

- https://cmdb.rosenvall.se — sign in as a demo user (README; password in Bitwarden `CMDB_DEMO_PASSWORD`) and exercise
  what the issue changed.
- Migrations: `kubectl -n cmdb logs deploy/cmdb-api -c migrate`.
- Close the loop: comment the deployed tag, what you checked and performance numbers on the issue; remove `status:in-progress`.
- Demo data: `scripts/load-demo-data.sh [--scale full|medium|small] [--seed N]` (a Job with the running API's datagen tag;
  wipes and reloads, ~5 min at full scale). Needed after a migration that requires a reload or a datagen change. Never load
  full scale over `kubectl port-forward`. Synthetic data only; the database has no backup by design.

## Rollback

`git revert` the `cmdb: deploy sha-…` commit in the homelab repo and push; ArgoCD rolls the images back. Migrations are not
reverted, hence expand/contract.

## When it breaks

```bash
kubectl -n argocd get application cmdb
kubectl -n cmdb get pods,externalsecret,cluster
kubectl -n cmdb logs deploy/cmdb-api -c migrate
kubectl -n cmdb logs deploy/cmdb-api
```

| Symptom | Usual cause |
|---|---|
| `ImagePullBackOff` | tag not pushed, or the `cmdb-registry` pull secret missing |
| `Init:CrashLoopBackOff` | migration failed: read the `migrate` log, fix forward in a new PR |
| `ExternalSecret` `SecretSyncError` | `cmdb` missing in the ClusterSecretStore conditions, or the Bitwarden entry missing |
| Login loops back | redirect URI missing in the identity provider blueprint for cmdb (homelab) |
| 404 on cmdb.rosenvall.se | HTTPRoute missing or not accepted by `gateway/external` |

Infrastructure changes (env vars, resources, database parameters) go in the homelab repo under
`kubernetes/applications/cmdb/` as their own commit (homelab's `gitops-app-onboarding` and `cluster-diagnostics`).

## Decisions

- ADRs: `docs/adr/NNNN-<slug>.md`, Swedish; a new or deviating decision starts as status *Föreslagen* with an issue
  labelled `type:decision` + `needs-human`.
- Needs-human section heading: `## Vad behövs från dig` (konkret fråga, alternativ med rekommendation, hur man svarar).
- Session start: check open `needs-human` issues for answers.
