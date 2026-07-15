#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

csharp_matches="$(rg --line-number \
  --glob '*.cs' \
  '\bExcludeFrom(?:Code)?Coverage(?:Attribute)?\b' \
  src || true)"

frontend_matches="$(rg --line-number --ignore-case \
  --glob '*.{js,jsx,ts,tsx,vue}' \
  '\b(?:istanbul|v8|c8)[[:space:]]+ignore\b|\bnode:coverage[[:space:]]+(?:ignore|disable)\b' \
  frontend/src || true)"

if [[ -n "$csharp_matches" || -n "$frontend_matches" ]]; then
  echo "Production source coverage suppression is forbidden:"
  if [[ -n "$csharp_matches" ]]; then
    echo "$csharp_matches"
  fi
  if [[ -n "$frontend_matches" ]]; then
    echo "$frontend_matches"
  fi
  exit 1
fi

echo "Coverage-integrity scan passed."
