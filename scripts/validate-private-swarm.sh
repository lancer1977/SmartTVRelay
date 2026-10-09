#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image_reference="${SMARTTV_VIEWER_IMAGE:-}"
if [[ ! "$image_reference" =~ ^[^[:space:]]+@sha256:[0-9a-f]{64}$ ]]; then
  echo 'SMARTTV_VIEWER_IMAGE must be a digest-pinned image reference' >&2
  exit 1
fi

docker stack config -c "${SMARTTV_STACK_FILE:-$repo_root/deploy/private-swarm.stack.yml}" >/dev/null
echo 'Private Swarm stack syntax and image pin passed'

# Structural guards: the Viewer is unauthenticated and must stay private. Assert on the
# rendered stack (what Swarm would receive) and on the raw file, so neither a compose
# interpolation nor a comment-adjacent edit can hide an exposure.
stack_file="${SMARTTV_STACK_FILE:-$repo_root/deploy/private-swarm.stack.yml}"
rendered="$(docker stack config -c "$stack_file")"
fail=0
check() { # label, extended-regex; searched in rendered and raw, ignoring comment-only lines
  local label="$1" pattern="$2" source name
  for name in rendered raw; do
    if [[ "$name" == rendered ]]; then source="$rendered"; else source="$(cat "$stack_file")"; fi
    if grep -Ev '^[[:space:]]*#' <<<"$source" | grep -Eiq "$pattern"; then
      echo "FAIL: stack must not contain $label ($name)" >&2
      fail=1
    fi
  done
}
check 'published ports'  '^[[:space:]]*ports:'
check 'traefik labels'   'traefik'
check 'docker.sock'      'docker\.sock'
check 'privileged mode'  '^[[:space:]]*privileged:[[:space:]]*(true|"true"|yes)|cap_add:|network_mode:[[:space:]]*host'
if (( fail )); then
  echo 'Private Swarm stack exposure guard FAILED' >&2
  exit 1
fi
echo 'Private Swarm stack exposure guard passed (no ports, traefik, docker.sock, privileged)'
