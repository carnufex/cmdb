#!/usr/bin/env bash
# Kör samma kontroller som en CI-pipeline: bygg, test och lint för api och web.
# Användning: scripts/verify.sh [--images] [--skip-web] [--skip-integration]
set -euo pipefail

IMAGES=0; WEB=1; INTEGRATION=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --images)           IMAGES=1 ;;
    --skip-web)         WEB=0 ;;
    --skip-integration) INTEGRATION=0 ;;
    *) echo "Okänt argument: $1"; exit 1 ;;
  esac; shift
done
cd "$(dirname "$0")/.."

step() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }

step "api: format"
dotnet format Cmdb.slnx --verify-no-changes

step "api: build"
dotnet build Cmdb.slnx -c Release

step "api: test"
if [[ $INTEGRATION -eq 1 ]]; then
  dotnet test --solution Cmdb.slnx -c Release --no-build
else
  dotnet test --project tests/Cmdb.Api.Tests -c Release --no-build
fi

if [[ $WEB -eq 1 ]]; then
  pushd src/web >/dev/null
  step "web: install"
  npm ci --no-audit --no-fund
  step "web: format"
  npx prettier --check "src/**/*.{ts,html,scss}"
  step "web: contrast"
  node scripts/check-contrast.mjs
  step "web: build"
  npx ng build
  step "web: test"
  npx ng test --watch=false
  popd >/dev/null
fi

if [[ $IMAGES -eq 1 ]]; then
  step "images"
  docker build -f src/api/Dockerfile -t cmdb-api:verify .
  docker build -t cmdb-web:verify src/web
fi

step "OK"
