---
name: cmdb-delivery
description: >-
  Take a cmdb issue from finished code to running on the demo site
  https://cmdb.rosenvall.se: verify, open the PR, squash-merge, build and push
  images, bump the homelab GitOps repo and confirm the rollout. Use when an issue
  is implemented and ready to ship, when asked to deploy/release/ship cmdb, when
  (re)loading demo data, or when the demo environment misbehaves after a deploy.
---

# cmdb delivery

The human-readable version is `docs/drift.md`. This skill is the runbook; follow
it in order and do not skip verification. GitHub Actions is dead: every "CI" step
runs on the workstation.

## Invariants

- The demo only ever runs code that is on `main` and went through a PR. Never
  `--deploy` from a branch, never hand-edit image refs in the homelab repo.
- Images are `registry.rosenvall.se/carnufex/cmdb-{api,web,datagen}:sha-<commit>`
  and pinned in the homelab by `tag@digest`. `scripts/deploy.sh` does both.
- Migrations run in the `migrate` init container on every rollout and must be
  backward compatible with the previous release (expand/contract). A destructive
  migration needs a `type:decision` issue first.
- Synthetic data only, also in the demo. The database has no backup by design.
- Christopher and Codex work in the same repos in parallel. Work in a git worktree
  (`git worktree add ../cmdb-<nr> <nr>-slug`) if the main checkout has changes you
  did not make, never commit or stash them, stage explicit paths, and
  `git fetch` before every push.

## 1. Before the PR

1. All acceptance criteria in the issue are met, or the rest is split into new
   issues linked with "Upptäckt i #<nr>".
2. Run `scripts/verify.sh` (add `--images` if a Dockerfile, nginx config or
   dependency changed). It must end with `==> OK`. Paste the summary in the PR.
3. Try the change in the local stack (`docker compose up -d --build`, then
   http://localhost:8480) for anything user-visible, and take screenshots.
4. `docs/` and ADRs updated if behaviour or the model changed.

## 2. PR and merge

```bash
git fetch origin && git rebase origin/main
git push -u origin <nr>-slug
gh pr create --base main --title "<type>(<scope>): <summary> (#<nr>)" --body-file <file>
```

Body follows `.github/pull_request_template.md` (Swedish): `Closes #<nr>`,
Vad och varför, Verifiering, Prestanda. Then:

```bash
gh pr merge <pr> --squash --delete-branch
git switch main && git pull --ff-only
```

Merge only when verify is green. A change that cannot be deployed right now must
not be merged, because `main` is what the demo is supposed to run.

## 3. Deploy

From a clean, up-to-date `main` in Git Bash:

```bash
scripts/deploy.sh --deploy --wait
```

It refuses a dirty tree, a branch other than `main` and a `main` that differs
from `origin/main`. It builds three images for `linux/amd64`, pushes them,
rewrites `kubernetes/applications/cmdb/app.yaml` in the homelab repo
(`HOMELAB_REPO`, default `~/source/repos/Rosenvalls-Homelab`), commits
`cmdb: deploy sha-<commit>`, pushes, refreshes the ArgoCD app, waits for both
rollouts and checks that the site answers.

Needs once per machine: `docker login registry.rosenvall.se` (user `homelab`,
password = Bitwarden `REGISTRY_PASSWORD`) and `KUBECONFIG` pointing at the cluster.

To test an image without deploying it: `scripts/deploy.sh --allow-branch`.

## 4. Verify on the demo and close the loop

1. Open https://cmdb.rosenvall.se, sign in (demo users: README, password in
   Bitwarden `CMDB_DEMO_PASSWORD`) and exercise what the issue changed.
2. `kubectl -n cmdb logs deploy/cmdb-api -c migrate` shows the migrations applied
   when the schema changed.
3. Comment on the issue: deployed tag, what you checked, performance numbers when
   relevant. The PR's `Closes #<nr>` closes it; remove `status:in-progress`.

## Demo data

`scripts/load-demo-data.sh [--scale full|medium|small] [--seed N]` runs the
datagen image with the same tag as the running API as a Job in namespace `cmdb`,
wipes and reloads the network, then restarts the API. Full scale takes about five
minutes. Needed after a migration that requires reloading or a datagen change.
Never load full scale over `kubectl port-forward`; the tunnel drops mid-`COPY`.

## Rollback

`git revert` the `cmdb: deploy sha-…` commit in the homelab repo and push. ArgoCD
rolls back the images. Migrations are not reverted, hence the expand/contract rule.

## When it breaks

```bash
kubectl -n argocd get application cmdb
kubectl -n cmdb get pods,externalsecret,cluster
kubectl -n cmdb logs deploy/cmdb-api -c migrate
kubectl -n cmdb logs deploy/cmdb-api
kubectl -n cmdb describe pod -l app.kubernetes.io/name=cmdb-api
```

| Symptom | Usual cause |
|---|---|
| `ImagePullBackOff` | tag not pushed, or `cmdb-registry` pull secret missing |
| `Init:CrashLoopBackOff` | migration failed; read the `migrate` log, fix forward in a new PR |
| `ExternalSecret` `SecretSyncError` | `cmdb` missing in the ClusterSecretStore conditions, or Bitwarden entry missing |
| Login loops back | redirect URI missing in blueprint `apps-cmdb.yaml` (homelab) |
| 404 on cmdb.rosenvall.se | HTTPRoute missing or not accepted by `gateway/external` |

Infrastructure changes (new env var, resources, database parameters) are made in
the homelab repo under `kubernetes/applications/cmdb/` as their own commit; follow
the homelab `gitops-app-onboarding` and `cluster-diagnostics` skills there.
