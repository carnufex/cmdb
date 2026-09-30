#!/usr/bin/env bash
# Bygger cmdb-CLI:t (#86) som fristående binärer för Windows, Linux och macOS och laddar upp dem som en GitHub-release.
# GitHub Actions är avstängt, så det görs härifrån:
#
#   scripts/cli-release.sh 0.1.0            # bygg till artifacts/cli/0.1.0
#   scripts/cli-release.sh 0.1.0 --publish  # och skapa releasen cli-v0.1.0 med binärerna
set -euo pipefail
cd "$(dirname "$0")/.."

version="${1:?Ange version, till exempel 0.1.0}"
out="artifacts/cli/$version"
rm -rf "$out"; mkdir -p "$out"

for rid in win-x64 linux-x64 linux-arm64 osx-arm64 osx-x64; do
  echo "==> $rid"
  dotnet publish src/cli/Cmdb.Cli.csproj -c Release -r "$rid" --self-contained -p:Version="$version" -o "$out/$rid" --nologo -v quiet
  bin="$out/$rid/cmdb"; [[ "$rid" == win-* ]] && bin="$bin.exe"
  ext=""; [[ "$rid" == win-* ]] && ext=".exe"
  cp "$bin" "$out/cmdb-$rid$ext"
done
(cd "$out" && sha256sum cmdb-* > SHA256SUMS)
ls -la "$out"/cmdb-* "$out/SHA256SUMS"

if [[ "${2:-}" == "--publish" ]]; then
  gh release create "cli-v$version" "$out"/cmdb-* "$out/SHA256SUMS" \
    --title "cmdb CLI $version" \
    --notes "Kommandoradsverktyget för CMDB:n (#86). Se [docs/agenter.md](https://github.com/carnufex/cmdb/blob/main/docs/agenter.md#i-ett-skal-cmdb). All data i CMDB:n är syntetisk."
fi
