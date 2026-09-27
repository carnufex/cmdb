#!/usr/bin/env bash
# Skapar .env från .env.example och fyller tomma hemligheter med slumpvärden. Skriver aldrig över en befintlig .env.
set -euo pipefail
cd "$(dirname "$0")/.."
[[ -f .env ]] && { echo ".env finns redan, lämnas orörd."; exit 0; }

secret() { openssl rand -base64 36 | tr -d '\n/+=' | cut -c1-40; }
while IFS= read -r line; do
  if [[ "$line" =~ ^([A-Z_]+_(PASSWORD|SECRET_KEY))=$ ]]; then
    echo "${BASH_REMATCH[1]}=$(secret)"
  else
    echo "$line"
  fi
done < .env.example > .env
echo "Skapade .env. Demoanvändarnas lösenord finns i Bitwarden (CMDB_DEMO_PASSWORD)."
