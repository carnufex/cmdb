#!/usr/bin/env bash
# Mäter prestandabudgeten (docs/plan.md) med k6 mot ett laddat nät och jämför serverns p95 med budgeten (#13).
# GitHub Actions är avstängt, så det här är det manuella jobbet: resultatet hamnar i artifacts/perf/ och kan
# postas som kommentar i ett issue.
#
# Användning: scripts/perf.sh [--target demo|local|<url>] [--duration 60s] [--rate 15] [--comment <issue>]
#   --target   demo (https://cmdb.rosenvall.se, standard), local (http://localhost:8480) eller en egen bas-URL
#   --duration hur länge anrop skickas (standard 60s)
#   --rate     anrop per sekund; under agentgränsen 20/s (standard 15)
#   --comment  posta sammanfattningen som kommentar i issuet
#
# Token: agentkontot cmdb-agent-demo (client credentials, docs/agenter.md). Lösenordet tas från CMDB_AGENT_TOKEN,
# annars från Bitwarden Secrets Manager med bws (BWS_ACCESS_TOKEN, eller hämtad från klustret med kubectl).
# Avslutas med kod 1 om någon rad är över budget eller anrop misslyckades.
set -euo pipefail

TARGET=demo; DURATION=60s; RATE=15; COMMENT=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --target)   TARGET="$2"; shift ;;
    --duration) DURATION="$2"; shift ;;
    --rate)     RATE="$2"; shift ;;
    --comment)  COMMENT="$2"; shift ;;
    *) echo "Okänt argument: $1"; exit 1 ;;
  esac; shift
done
cd "$(dirname "$0")/.."

K6_IMAGE=grafana/k6@sha256:e66db15b860113878fa74670e31f5e274830b7b6e42c8bff28b2f2d86a257603 # v2.3.0
BWS=${BWS:-bws}
[[ -x "$BWS" ]] || command -v "$BWS" >/dev/null || BWS="$HOME/AppData/Local/bws/bws.exe"
HOMELAB_PROJECT=e3b0b2ac-19f2-464d-a859-b39f00892f97

step() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
die()  { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }

case "$TARGET" in
  demo)  url=https://cmdb.rosenvall.se ;;
  local) url=http://host.docker.internal:8480 ;;
  *)     url="$TARGET" ;;
esac

step "token"
if [[ -z "${CMDB_AGENT_TOKEN:-}" ]]; then
  if [[ -z "${BWS_ACCESS_TOKEN:-}" ]]; then
    BWS_ACCESS_TOKEN=$(kubectl -n external-secrets get secret bitwarden-access-token -o jsonpath='{.data.token}' | base64 -d) \
      || die "Sätt CMDB_AGENT_TOKEN eller BWS_ACCESS_TOKEN (eller KUBECONFIG mot klustret)."
    export BWS_ACCESS_TOKEN
  fi
  CMDB_AGENT_TOKEN=$("$BWS" secret list "$HOMELAB_PROJECT" --server-url https://vault.bitwarden.eu \
    | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const x=JSON.parse(s).find(e=>e.key==="CMDB_AGENT_TOKEN");if(!x)process.exit(1);process.stdout.write(x.value)})') \
    || die "Hittade inte CMDB_AGENT_TOKEN i Bitwarden."
fi
token=$(curl -sf https://authentik.rosenvall.se/application/o/token/ \
  -d grant_type=client_credentials -d client_id=cmdb-agents -d username=cmdb-agent-demo \
  --data-urlencode "password=$CMDB_AGENT_TOKEN" -d "scope=openid profile email" \
  | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>process.stdout.write(JSON.parse(s).access_token))') \
  || die "Kunde inte hämta token från Authentik."
echo "token för cmdb-agent-demo hämtad"

out="artifacts/perf/$(date -u +%Y%m%dT%H%M%SZ)-${TARGET//[^a-z0-9]/-}"
mkdir -p "$out"
step "k6 mot $url ($RATE anrop/s i $DURATION)"
status=0
MSYS_NO_PATHCONV=1 docker run --rm -i \
  --add-host host.docker.internal:host-gateway \
  -v "$PWD/perf:/scripts:ro" -v "$PWD/$out:/out" \
  -e CMDB_URL="$url" -e CMDB_TOKEN="$token" -e RATE="$RATE" -e DURATION="$DURATION" \
  "$K6_IMAGE" run --quiet /scripts/budget.js || status=$?

[[ -f "$out/summary.md" ]] || die "k6 gav ingen sammanfattning (kod $status)."
echo "Resultat: $out/summary.md och summary.json"
if [[ -n "$COMMENT" ]]; then
  gh issue comment "$COMMENT" --body-file "$out/summary.md" >/dev/null
  echo "Kommenterat i #$COMMENT"
fi
# k6 exits 99 when a threshold fails: a budget regression.
[[ $status -eq 0 ]] || die "Över budget eller misslyckade anrop (k6 kod $status)."
