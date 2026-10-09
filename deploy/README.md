# Private SmartTVRelay Viewer service

This package prepares SmartTVRelay as the tuner source for DreadTV's private
`smarttv-hdhomerun` channel. It does not deploy a service or publish an image.
The Viewer serves HLS and process liveness only on a private Swarm overlay;
DreadTV handles Keycloak roles and same-origin playback for viewers.

## Build and local validation

After committing the source, run `scripts/build-viewer-image.sh`. It archives
`HEAD`, builds an image locally, and verifies that its OCI revision label is
the full commit SHA. The script does not push an image. The runtime image has
.NET 10, `ffmpeg`, and `ffprobe`, runs as the built-in non-root `app` user, and
starts with a private umask. `/healthz` is liveness only: HTTP 200 does not
claim a tuner is reachable, an RF signal exists, or playback is healthy.

For a local container smoke, supply a reachable `Tuner__BaseUrl` and bind the
service only to loopback. The playlist request may take up to 45 seconds while
the tuner starts:

```bash
docker run --rm --name smarttvrelay-viewer-smoke \
  --publish 127.0.0.1:5189:5189 \
  --read-only --tmpfs /tmp:rw,size=512m \
  --env Tuner__BaseUrl=http://192.168.0.66 \
  smarttvrelay-viewer:local-<full-source-sha>
curl --fail http://127.0.0.1:5189/healthz
```

The IP is an example from this lab, not a package default to rely on in
another environment. Check `/api/channels`, then one known channel playlist
and segment. Stop the container and confirm its FFmpeg process and ephemeral
work directory are gone. Fixture-backed tests do not require the tuner.

## Swarm deployment contract

`private-swarm.stack.yml` is a separate one-replica service. It does not join
Traefik, publish a port, mount the Docker socket, or carry secrets. It joins
the verified non-attachable `dreadtv-internal` overlay under the alias
`smarttvrelay-internal` and the existing LAN-egress overlay for the HDHomeRun.
The service uses a bounded tmpfs for media, a read-only root filesystem, a
non-root process, and a stop-first update so an image change does not briefly
run two tuner owners. DreadTV's playback URL is
`http://smarttvrelay-internal:5189/hls/2.1/index.m3u8` on its private backend.

Before an authorized deployment, verify both network identities and the
chosen node's tuner reachability. Set `SMARTTV_VIEWER_IMAGE` to an immutable
`name@sha256:...` reference whose revision label matches the exact reviewed
commit; set `SMARTTV_TUNER_BASE_URL`, `SMARTTV_NODE_HOSTNAME`, and both network
names to verified non-secret values. Run `scripts/validate-private-swarm.sh`
to require a digest-pinned image and validate the rendered stack before handing
it to Portainer. Keep the prior image digest for rollback. Do not activate DreadTV's
channel config until the private service is healthy and exact-image HLS smoke
has passed. Publishing the image, changing live configuration, and deployment
are separate approval gates.

## Network exposure note

The Viewer has no authentication of its own. Any task attached to the shared
`dreadtv-egress` overlay (and any task on `dreadtv-internal`) can reach
`smarttvrelay-internal:5189`, including `/api/channels`, HLS, and `/healthz`.
Only attach trusted services to those overlays; DreadTV is the sole intended
viewer-facing boundary.

## Rollback procedure

Keep the previous immutable image digest (`name@sha256:...`) from the last
good deploy before every update.

1. Preferred, redeploy the previous digest through the stack. Set
   `SMARTTV_VIEWER_IMAGE` to the prior `name@sha256:...`, run
   `scripts/validate-private-swarm.sh`, and update the Portainer stack with
   that value. The one-replica, stop-first policy means the old tuner owner
   stops before the older image starts.
2. Fast path, on a manager node: `docker service rollback <stack>_smarttvrelay-viewer`
   reverts the service to its previous spec (uses `rollback_config`, stop-first).
   Afterwards set the stack's `SMARTTV_VIEWER_IMAGE` back to the same prior
   digest so the next stack update does not re-apply the bad one.
3. First deploy (no previous digest exists): remove the stack
   (`docker stack rm <stack>`, or delete it in Portainer) and leave DreadTV's
   `smarttv-hdhomerun` channel config inactive. Nothing else depends on the
   service, and removal frees the tuner immediately.

After any rollback, confirm `/healthz` and one exact-image HLS playlist before
re-activating DreadTV's channel config.

## Base image pinning and open items

`Dockerfile` pins both base images by tag and digest (resolved read-only with
`docker buildx imagetools inspect` on 2026-10-08; these are multi-arch index
digests). Refresh them deliberately to pick up base-image security updates.
Still open (needs an authorized image publish): confirm the container
`HEALTHCHECK` reaches healthy and render the stack with a published
digest-pinned image.
