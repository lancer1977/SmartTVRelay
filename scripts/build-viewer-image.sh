#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_commit="$(git -C "$repo_root" rev-parse HEAD)"
[[ "$source_commit" =~ ^[0-9a-f]{40}$ ]] || { echo 'Expected a full Git SHA' >&2; exit 1; }
image_tag="${SMARTTV_LOCAL_IMAGE_TAG:-smarttvrelay-viewer:local-$source_commit}"

# git archive makes the input exactly the committed source; untracked media,
# worktrees, local credentials, and generated output never enter the build.
git -C "$repo_root" archive --format=tar HEAD |
  docker buildx build --load --file Dockerfile \
    --build-arg "VCS_REF=$source_commit" --tag "$image_tag" -

image_revision="$(docker image inspect "$image_tag" --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}')"
[[ "$image_revision" == "$source_commit" ]] || {
  echo 'Image revision does not match the archived Git commit' >&2
  exit 1
}
printf 'Built %s from %s\n' "$image_tag" "$source_commit"
