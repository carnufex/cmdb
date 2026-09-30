#!/usr/bin/env bash
# Bygger och publicerar cmdb-api och cmdb-web till registry.rosenvall.se och
# (med --deploy) pekar homelabbet på de nya digesterna så att ArgoCD synkar ut dem.
#
# Användning: scripts/deploy.sh [--deploy] [--allow-branch] [--wait]
#   --deploy        uppdatera kubernetes/applications/cmdb/app.yaml i homelab-repot och pusha
#   --allow-branch  tillåt annan branch än main (bara för att testa en image, aldrig med --deploy)
#   --wait          vänta tills utrullningen i klustret är klar (kräver kubectl och KUBECONFIG)
#
# Homelab-repot hittas via HOMELAB_REPO (standard ~/source/repos/Rosenvalls-Homelab).
# Se docs/drift.md.
set -euo pipefail

REGISTRY=registry.rosenvall.se/carnufex
DEPLOY=0; ALLOW_BRANCH=0; WAIT=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --deploy)       DEPLOY=1 ;;
    --allow-branch) ALLOW_BRANCH=1 ;;
    --wait)         WAIT=1 ;;
    *) echo "Okänt argument: $1"; exit 1 ;;
  esac; shift
done
cd "$(dirname "$0")/.."

step() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
die()  { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }

step "kontroller"
[[ -z "$(git status --porcelain)" ]] || die "Arbetskatalogen är inte ren. Committa eller stasha först."
# HEAD must be exactly origin/main: a main checkout or a detached worktree
# (git switch --detach origin/main) when the main checkout is busy.
git fetch -q origin main
if [[ "$(git rev-parse HEAD)" != "$(git rev-parse origin/main)" ]]; then
  [[ $ALLOW_BRANCH -eq 1 && $DEPLOY -eq 0 ]] \
    || die "Deploy sker bara från origin/main (HEAD är $(git rev-parse --short HEAD)). Kör git pull, eller git switch --detach origin/main."
fi
sha=$(git rev-parse --short=7 HEAD)
tag="sha-$sha"
echo "commit $sha, tagg $tag"

build_push() { # <name> <dockerfile> <context>
  local image="$REGISTRY/$1"
  step "bygg $1"
  docker build --platform linux/amd64 -f "$2" -t "$image:$tag" -t "$image:latest" "$3"
  step "pusha $1"
  docker push -q "$image:$tag"
  docker push -q "$image:latest"
}
build_push cmdb-api src/api/Dockerfile .
build_push cmdb-web src/web/Dockerfile src/web
# Not deployed; scripts/load-demo-data.sh runs it as a Job with the same tag as the API.
build_push cmdb-datagen src/datagen/Dockerfile .

digest() { docker inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$REGISTRY/$1:$tag" \
  | grep "^$REGISTRY/$1@" | head -n1 | cut -d@ -f2; }
api_ref="$REGISTRY/cmdb-api:$tag@$(digest cmdb-api)"
web_ref="$REGISTRY/cmdb-web:$tag@$(digest cmdb-web)"
[[ "$api_ref" == *@sha256:* && "$web_ref" == *@sha256:* ]] || die "Kunde inte läsa digester efter push."
echo "$api_ref"
echo "$web_ref"

if [[ $DEPLOY -eq 0 ]]; then
  step "klart (ingen deploy, lägg till --deploy för att rulla ut)"
  exit 0
fi

homelab="${HOMELAB_REPO:-$HOME/source/repos/Rosenvalls-Homelab}"
manifest="$homelab/kubernetes/applications/cmdb/app.yaml"
[[ -f "$manifest" ]] || die "Hittar inte $manifest. Sätt HOMELAB_REPO."

step "uppdatera homelab"
git -C "$homelab" diff --quiet -- kubernetes/applications/cmdb \
  || die "Ocommittade ändringar i homelab under kubernetes/applications/cmdb."
hl_branch=$(git -C "$homelab" rev-parse --abbrev-ref HEAD)
git -C "$homelab" pull -q --rebase --autostash origin "$hl_branch"
# The deployment being replaced, for the change log's range (#82).
previous=$(grep -oE "cmdb-api:sha-[0-9a-f]+" "$manifest" | head -1 | sed 's/.*sha-//' || true)
sed -i -E \
  -e "s#image: $REGISTRY/cmdb-api[:@][^[:space:]]*#image: $api_ref#" \
  -e "s#image: $REGISTRY/cmdb-web[:@][^[:space:]]*#image: $web_ref#" \
  "$manifest"
if git -C "$homelab" diff --quiet -- "$manifest"; then
  echo "Homelab pekar redan på $tag."
else
  git -C "$homelab" add -- "$manifest"
  git -C "$homelab" commit -q -m "cmdb: deploy $tag" -m "carnufex/cmdb@$(git rev-parse HEAD)"
  git -C "$homelab" push -q origin "$hl_branch"
  echo "Pushat till homelab ($hl_branch). ArgoCD synkar appen cmdb."
fi

if [[ $WAIT -eq 1 ]]; then
  step "vänta på utrullning"
  kubectl -n argocd annotate application cmdb argocd.argoproj.io/refresh=normal --overwrite >/dev/null
  for d in cmdb-api cmdb-web; do
    for _ in $(seq 60); do
      current=$(kubectl -n cmdb get deploy "$d" -o jsonpath='{.spec.template.spec.containers[0].image}')
      [[ "$current" == *"$tag"* ]] && break
      sleep 5
    done
    [[ "$current" == *"$tag"* ]] || die "$d fick aldrig $tag. Kolla ArgoCD-appen cmdb."
    kubectl -n cmdb rollout status "deploy/$d" --timeout=5m
  done
  code=$(curl -s -o /dev/null -w '%{http_code}' https://cmdb.rosenvall.se/config.json)
  [[ "$code" == 200 ]] || die "https://cmdb.rosenvall.se/config.json svarade $code."
  echo "https://cmdb.rosenvall.se kör $tag."
fi

# Underlaget till händelseloggen (#82) för det som just rullades ut: skriv posterna med prompten och lägg dem i
# changelog/entries.json i en PR.
if [[ -n "${previous:-}" && "$previous" != "$sha" ]]; then
  mkdir -p artifacts/changelog
  if scripts/changelog.sh "$previous" "$sha" > "artifacts/changelog/$tag.md" 2>/dev/null; then
    echo "Underlag till händelseloggen: artifacts/changelog/$tag.md (sha-$previous … $tag)"
  fi
fi
