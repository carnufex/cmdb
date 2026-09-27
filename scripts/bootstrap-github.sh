#!/usr/bin/env bash
# Skapar GitHub-repot, etiketter, milstolpar och issues från backlog/.
# Användning: scripts/bootstrap-github.sh --public|--private [--name cmdb] [--dry-run]
set -euo pipefail

NAME="cmdb"; VIS=""; DRY=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --public)  VIS="public" ;;
    --private) VIS="private" ;;
    --name)    NAME="$2"; shift ;;
    --dry-run) DRY=1 ;;
    *) echo "Okänt argument: $1"; exit 1 ;;
  esac; shift
done
[[ -z "$VIS" ]] && { echo "Ange uttryckligen --public eller --private."; exit 1; }

command -v gh >/dev/null || { echo "Installera GitHub CLI: https://cli.github.com"; exit 1; }
gh auth status >/dev/null || { echo "Kör 'gh auth login' först."; exit 1; }
cd "$(dirname "$0")/.."

run() { if [[ $DRY -eq 1 ]]; then echo "[dry-run] $*"; else "$@"; fi; }

# 1. Git och repo
if [[ ! -d .git ]]; then
  run git init -b main
  run git add -A
  run git commit -m "docs: initial plan, ADRs, backlog and local environment"
fi
if ! git remote get-url origin >/dev/null 2>&1; then
  run gh repo create "$NAME" "--$VIS" --source . --remote origin --push \
    --description "POC: modern, snabb och säker CMDB för en rikstäckande telekomanläggning (syntetisk data)"
else
  run git push -u origin main
fi
REPO="$(gh repo view --json nameWithOwner -q .nameWithOwner 2>/dev/null || echo "OWNER/$NAME")"

# 2. Etiketter
label() { run gh label create "$1" --color "$2" --description "$3" --force -R "$REPO"; }
label "type:feature"  "665eff" "Ny funktion"
label "type:task"     "8b8fa3" "Tekniskt arbete"
label "type:spike"    "c5a3ff" "Tidsboxad undersökning"
label "type:bug"      "ff3a63" "Fel"
label "type:decision" "ffb86b" "Arkitektur-/designbeslut"
for a in backend frontend data infra security docs; do label "area:$a" "0067ff" "Område: $a"; done
for f in 0 1 2 3 4; do label "fas-$f" "2c2b52" "Fas $f enligt planen"; done
label "status:ready"       "3fb950" "Redo att påbörjas"
label "status:in-progress" "0067ff" "Pågår"
label "status:blocked"     "ff3a63" "Blockerad"
label "needs-human"        "ffb86b" "Kräver mänskligt beslut eller granskning"

# 3. Milstolpar
existing_ms="$(gh api "repos/$REPO/milestones?state=all&per_page=100" -q '.[].title' 2>/dev/null || true)"
for ms in "Fas 0 – Grund" "Fas 1 – Motor" "Fas 2 – Linser" "Fas 3 – Differentiering" "Fas 4 – Demo"; do
  grep -qxF "$ms" <<<"$existing_ms" || run gh api "repos/$REPO/milestones" -f title="$ms" >/dev/null
done

# 4. Issues (i nummerordning så att #{{NN}}-referenser kan lösas)
declare -A NUM
for f in backlog/[0-9][0-9]-*.md; do
  key="$(basename "$f" | cut -c1-2)"
  title="$(sed -n 's/^title: //p' "$f" | head -1)"
  labels="$(sed -n 's/^labels: //p' "$f" | head -1)"
  milestone="$(sed -n 's/^milestone: //p' "$f" | head -1)"
  body="$(awk 'c>=2{print} /^---$/{c++}' "$f")"
  for k in "${!NUM[@]}"; do body="${body//\#\{\{$k\}\}/#${NUM[$k]}}"; done

  found="$(gh issue list -R "$REPO" --state all --search "\"$title\" in:title" --json number,title \
          -q ".[] | select(.title==\"$title\") | .number" 2>/dev/null | head -1 || true)"
  if [[ -n "$found" ]]; then
    echo "Finns redan: #$found $title"; NUM[$key]="$found"; continue
  fi
  if [[ $DRY -eq 1 ]]; then
    echo "[dry-run] issue: $title [$labels] ($milestone)"; NUM[$key]="$key"; continue
  fi
  url="$(gh issue create -R "$REPO" --title "$title" --body "$body" --label "$labels" --milestone "$milestone")"
  NUM[$key]="${url##*/}"
  echo "Skapad: #${NUM[$key]} $title"
done

echo "Klart: https://github.com/$REPO"
