#!/usr/bin/env bash
# E2e-sviten (#288): de viktigaste flödena i en riktig webbläsare mot en lokal stack.
# Startar en engångs-Postgres (Docker), laddar det lilla syntetiska nätet (seed 1) med datageneratorn, kör API:t mot en
# ersättare för OpenID-leverantören (src/web/e2e/idp.mjs) och webben på dev-servern, och kör Playwright.
# Användning: scripts/e2e.sh [--keep]   (--keep låter stacken stå kvar efter körningen, för felsökning)
set -euo pipefail

KEEP=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --keep) KEEP=1 ;;
    *) echo "Okänt argument: $1"; exit 1 ;;
  esac; shift
done
cd "$(dirname "$0")/.."
ROOT=$(pwd)

DB_PORT=${E2E_DB_PORT:-15499}
API_PORT=${E2E_API_PORT:-5199}
IDP_PORT=${E2E_IDP_PORT:-8790}
WEB_PORT=${E2E_WEB_PORT:-4310}
CONTAINER=cmdb-e2e-db
LOGS="$ROOT/artifacts/e2e"
mkdir -p "$LOGS"
CS="Host=127.0.0.1;Port=$DB_PORT;Database=cmdb;Username=cmdb;Password=e2e"
PIDS=()

step() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
wait_for() { # url, what
  for _ in $(seq 1 120); do
    curl -s -o /dev/null -w '%{http_code}' "$1" 2>/dev/null | grep -qE '^(200|204|302)$' && return 0
    sleep 1
  done
  echo "$2 svarar inte på $1"; return 1
}
cleanup() {
  if [[ $KEEP -eq 1 ]]; then
    echo "Stacken står kvar (--keep): webb :$WEB_PORT, API :$API_PORT, idp :$IDP_PORT, Postgres :$DB_PORT."
    return
  fi
  for pid in "${PIDS[@]:-}"; do [[ -n "$pid" ]] && kill "$pid" 2>/dev/null || true; done
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
}
trap cleanup EXIT

step "postgres"
docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
docker run -d --name "$CONTAINER" -e POSTGRES_USER=cmdb -e POSTGRES_PASSWORD=e2e -e POSTGRES_DB=cmdb \
  -p "127.0.0.1:$DB_PORT:5432" postgis/postgis:17-3.5 >/dev/null
for _ in $(seq 1 60); do docker exec "$CONTAINER" pg_isready -U cmdb -d cmdb >/dev/null 2>&1 && break; sleep 1; done
sleep 2

step "data: litet syntetiskt nät (seed 1)"
dotnet build src/datagen -c Release -v quiet >/dev/null
ConnectionStrings__Cmdb="$CS" dotnet run --project src/datagen -c Release --no-build -- --scale small --seed 1 --reset > "$LOGS/datagen.log" 2>&1

step "idp"
E2E_IDP_PORT=$IDP_PORT node src/web/e2e/idp.mjs > "$LOGS/idp.log" 2>&1 &
PIDS+=($!)
wait_for "http://127.0.0.1:$IDP_PORT/jwks" "idp"

step "api"
dotnet build src/api -c Release -v quiet >/dev/null
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://127.0.0.1:$API_PORT" ConnectionStrings__Cmdb="$CS" \
  Auth__Authority="http://127.0.0.1:$IDP_PORT/application/o/cmdb/" Auth__RequireHttpsMetadata=false \
  dotnet run --project src/api -c Release --no-build --no-launch-profile > "$LOGS/api.log" 2>&1 &
PIDS+=($!)
wait_for "http://127.0.0.1:$API_PORT/health" "API"

step "web"
pushd src/web >/dev/null
[[ -d node_modules ]] || npm ci --no-audit --no-fund
npx ng serve --port "$WEB_PORT" --proxy-config e2e/proxy.json > "$LOGS/web.log" 2>&1 &
PIDS+=($!)
popd >/dev/null
wait_for "http://localhost:$WEB_PORT/" "webben"

step "playwright"
pushd src/web >/dev/null
started=$(date +%s)
E2E_BASE_URL="http://localhost:$WEB_PORT" E2E_IDP="http://127.0.0.1:$IDP_PORT/application/o/cmdb/" \
  npx playwright test -c e2e/playwright.config.ts
popd >/dev/null
echo "Flödena tog $(( $(date +%s) - started )) s."
