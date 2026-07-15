#!/usr/bin/env bash
set -euo pipefail

if [[ $# -eq 0 ]]; then
  echo "Usage: eng/security/verify-image-hardening.sh <image> [...]" >&2
  exit 64
fi

for image in "$@"; do
  configured_user="$(docker image inspect --format '{{.Config.User}}' "$image")"
  case "$configured_user" in
    ""|root|0|0:*)
      echo "Image $image has an invalid runtime user: ${configured_user:-<empty>}" >&2
      exit 1
      ;;
  esac

  echo "Image $image uses non-root runtime user $configured_user."
done
