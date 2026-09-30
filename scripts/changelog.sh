#!/usr/bin/env bash
# Bygger underlaget till händelseloggen (#82) för ett intervall av utrullningar: för varje squash-commit issuet
# (titel och acceptanskriterier), PR:en (beskrivning) och vilka delar av koden som berörts. Prompten i
# docs/changelog-prompt.md läggs först, så utdata kan ges direkt till en modell:
#
#   scripts/changelog.sh <från-sha> <till-sha>              # prompt + underlag på stdout
#   scripts/changelog.sh <från-sha> <till-sha> --generate   # med claude -p om det finns, JSON på stdout
#
# Resultatet granskas och läggs i changelog/entries.json i en PR.
set -euo pipefail
cd "$(dirname "$0")/.."

[[ $# -ge 2 ]] || { echo "Användning: $0 <från-sha> <till-sha> [--generate]" >&2; exit 1; }
from="${1#sha-}"; to="${2#sha-}"; generate="${3:-}"

bundle() {
  cat docs/changelog-prompt.md
  echo
  echo "## Underlag: sha-$from … sha-$to"
  git log --first-parent --reverse --format='%H%x09%s' "$from..$to" | while IFS=$'\t' read -r commit subject; do
    echo
    echo "### $subject"
    echo "Commit: ${commit:0:7}"
    for issue in $(grep -oE '#[0-9]+' <<<"$subject" | tr -d '#' | sort -u); do
      kind=$(gh api "repos/{owner}/{repo}/issues/$issue" --jq 'if .pull_request then "pr" else "issue" end' 2>/dev/null || echo "")
      if [[ "$kind" == "issue" ]]; then
        echo
        echo "#### Issue #$issue: $(gh issue view "$issue" --json title --jq .title)"
        gh issue view "$issue" --json body --jq .body | sed -n '/Acceptanskriterier/,/^## /p' | head -40
      elif [[ "$kind" == "pr" ]]; then
        echo
        echo "#### Pull request #$issue"
        gh pr view "$issue" --json body --jq .body | head -60
      fi
    done
    echo
    echo "#### Berörda delar"
    git show --stat --format= "$commit" | awk '{print $1}' | grep -E '^(src|docs|catalog|scripts)/' \
      | sed -E 's#^src/web/src/app/([^/]+).*#web/\1#; s#^(src/[^/]+/(Features/)?[^/]+).*#\1#; s#^(docs|catalog|scripts)/.*#\1#' \
      | sort | uniq -c | sort -rn | head -12
  done
}

if [[ "$generate" == "--generate" ]]; then
  command -v claude >/dev/null || { echo "claude finns inte; kör utan --generate och ge underlaget till agenten." >&2; exit 1; }
  bundle | claude -p
else
  bundle
fi
