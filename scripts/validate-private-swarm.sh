#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image_reference="${SMARTTV_VIEWER_IMAGE:-}"
if [[ ! "$image_reference" =~ ^[^[:space:]]+@sha256:[0-9a-f]{64}$ ]]; then
  echo 'SMARTTV_VIEWER_IMAGE must be a digest-pinned image reference' >&2
  exit 1
fi

docker stack config -c "$repo_root/deploy/private-swarm.stack.yml" >/dev/null
echo 'Private Swarm stack syntax and image pin passed'
