#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

pattern='(?i)\b(payment|billing|pricing|balance|refund|promo|redeem|affiliate|commission)\b|personal[-_ ]?quota|purchasable[-_ ]?quota'

search_roots=(src frontend/src deploy/config .github)
existing_roots=()
for root in "${search_roots[@]}"; do
  if [[ -e "$root" ]]; then
    existing_roots+=("$root")
  fi
done

if [[ ${#existing_roots[@]} -eq 0 ]]; then
  echo "No production/configuration roots exist yet."
  exit 0
fi

matches="$(rg --hidden --line-number --ignore-case \
  --glob '!**/node_modules/**' \
  --glob '!**/dist/**' \
  --glob '!**/bin/**' \
  --glob '!**/obj/**' \
  --glob '!**/*lock*' \
  --glob '!**/AGENTS.md' \
  "$pattern" "${existing_roots[@]}" || true)"

# Runtime startup validation intentionally contains the denied section names.
# Only individually marked guard lines are exempt; the containing source file
# remains in scope so an unmarked feature, route, model, or configuration key
# still fails this gate.
matches="$(printf '%s\n' "$matches" | rg -v 'poolai-forbidden-scope-guard' || true)"

if [[ -n "$matches" ]]; then
  echo "Forbidden commercial or personal-quota scope found:"
  echo "$matches"
  exit 1
fi

echo "Forbidden-scope scan passed."
