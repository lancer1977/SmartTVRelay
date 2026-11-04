---
title: Mapped-Frontier Build Packet - SmartTVRelay finish line
status: draft
owner: lancer1977
issue: https://github.com/lancer1977/SmartTVRelay/issues/131
created: 2026-11-01
updated: 2026-11-01
---

# Mapped-Frontier Build Packet: SmartTVRelay finish line

## Intent

SmartTVRelay: HDHomeRun tuner relay with LAN Viewer fusing broadcast-state evidence. This packet records the repository's finish-line scope from open issues as of 2026-11-01: what remains, what blocks it, and the merge policy. Non-goals: no code, secret, auth, workflow or infrastructure change is made by this packet.

## Acceptance criteria

- [ ] [#131](https://github.com/lancer1977/SmartTVRelay/issues/131) Epic: V1 closed with evidence recorded on the issue.
- [ ] [#134](https://github.com/lancer1977/SmartTVRelay/issues/134) Live-capture proof run closed with evidence recorded on the issue.
- [ ] [#139](https://github.com/lancer1977/SmartTVRelay/issues/139) Viewer misses short black bars closed with evidence recorded on the issue.
- [ ] [#141](https://github.com/lancer1977/SmartTVRelay/issues/141) Detector evidence gaps (captions, black-frame, OCR) closed with evidence recorded on the issue.
- [ ] [#133](https://github.com/lancer1977/SmartTVRelay/issues/133) Detector backlog for evaluation gate closed with evidence recorded on the issue.
- [ ] [#135](https://github.com/lancer1977/SmartTVRelay/issues/135) V1 QA checklist closed with evidence recorded on the issue.
- [ ] [#132](https://github.com/lancer1977/SmartTVRelay/issues/132) V1 boundary + tag closed with evidence recorded on the issue.
- [ ] `.mfd/project.yaml` and the merge policy below stay in sync.

## Current behavior

- Integration branch: `dev`; protected branch: `main`.
- Local checkout is on feat/106-lan-viewer (untouched).

## Behavior map

Not applicable: repository-level finish-line packet. Feature behavior maps belong in per-issue packets.

## UI proof

Not applicable: docs-only packet.

## Technical contract

Unchanged. Any change to auth, secrets, workflows, deploy or infrastructure paths escalates to the owner (see `escalate_paths` in `.mfd/project.yaml`).

## Verification map

| Requirement | Scenario | Validation | Expected evidence |
| --- | --- | --- | --- |
| unit tier | PR to `dev` | `dotnet test SmartTVRelay.slnx -c Release` | build |
| integration tier | PR to `dev` | `dotnet build SmartTVRelay.slnx -c Release` | build |
| automation tier | PR to `dev` | `dotnet run --project src/SmartTVRelay.Evaluation -c Release` | build |

## Per-project merge policy

- Required checks: `build`
- Integration branch: dev
- Protected branch: main

| Test tier | Command | Required | CI job name |
| --- | --- | --- | --- |
| Unit | `dotnet test SmartTVRelay.slnx -c Release` | Yes | build |
| Integration | `dotnet build SmartTVRelay.slnx -c Release` | Yes | build |
| Automation | `dotnet run --project src/SmartTVRelay.Evaluation -c Release` | Yes | build |

Checks observed in workflow files on `dev`:

- `build` from CI (ci.yml) (every pull_request; self-hosted pr-validation/smarttvrelay unless HOSTED_CI_ENABLED)

## Delivery map

| Order | GitHub issue / card | Requirement / scenario | Dependencies | Validation |
| --- | --- | --- | --- | --- |
| 1 | [#131](https://github.com/lancer1977/SmartTVRelay/issues/131) | Epic: V1 | Children open | Required checks |
| 2 | [#134](https://github.com/lancer1977/SmartTVRelay/issues/134) | Live-capture proof run | Meets narrowed V1 bar; waits on owner close | Required checks |
| 3 | [#139](https://github.com/lancer1977/SmartTVRelay/issues/139) | Viewer misses short black bars | Detector gap; test-first fix | Required checks |
| 4 | [#141](https://github.com/lancer1977/SmartTVRelay/issues/141) | Detector evidence gaps (captions, black-frame, OCR) | Scope: captions/OCR may be post-V1 | Required checks |
| 5 | [#133](https://github.com/lancer1977/SmartTVRelay/issues/133) | Detector backlog for evaluation gate | Depends on #139/#141 | Required checks |
| 6 | [#135](https://github.com/lancer1977/SmartTVRelay/issues/135) | V1 QA checklist | Needs screenshots at mobile/desktop | Required checks |
| 7 | [#132](https://github.com/lancer1977/SmartTVRelay/issues/132) | V1 boundary + tag | Needs green exact-head commit | Required checks |

### Rollout and rollback

Docs-only. Roll back by reverting the packet commit. Deploys, if any, are out of scope and need separate owner approval.

### Blockers

| Blocker | Owner | Unblock condition |
| --- | --- | --- |
| [#131](https://github.com/lancer1977/SmartTVRelay/issues/131): Children open | Owner | Children closed |
| [#134](https://github.com/lancer1977/SmartTVRelay/issues/134): Meets narrowed V1 bar; waits on owner close | Owner | Owner closes |
| [#139](https://github.com/lancer1977/SmartTVRelay/issues/139): Detector gap; test-first fix | Agent | Failing test then fix |
| [#141](https://github.com/lancer1977/SmartTVRelay/issues/141): Scope: captions/OCR may be post-V1 | Owner | Scope decided |
| [#133](https://github.com/lancer1977/SmartTVRelay/issues/133): Depends on #139/#141 | Agent | Coverage lines tested |
| [#135](https://github.com/lancer1977/SmartTVRelay/issues/135): Needs screenshots at mobile/desktop | Agent | Record filled |
| [#132](https://github.com/lancer1977/SmartTVRelay/issues/132): Needs green exact-head commit | Owner | Tag on green commit |

### Future requirements

- Secrets: None
- Infrastructure: SmartTVRelay Swarm service for DreadTV (DreadTV #549/#557)
- Hardware: HDHomeRun tuner, OTA antenna

### Open decisions

| Decision | Options | Recommendation |
| --- | --- | --- |
| Close #134 under narrowed V1 | Close / keep | Close (open) |
| #141 captions/OCR | V1 / defer | Defer captions/OCR post-V1 (open) |

## Approval

- Product owner: lancer1977
- Decision: Pending
- Date: Pending
- Approved commit or immutable revision: Pending
- Approval evidence: Pending
- Approval brief: Pending

## Deviations

| Date | Requirement / boundary | Decision | Approver | Follow-up |
| --- | --- | --- | --- | --- |
| None | None | None | None | None |

## Release controls and closeout evidence

- Target branch: `dev`
- Pull request: Pending
- Required reviewers: lancer1977
- CI and validation evidence: Pending

### Deployment contract

- Deployment applicability: Not applicable: docs-only packet; deployments need separate owner approval
- Target environments: None
- Deployment authority (separate from approval): lancer1977
- Source commit: Not applicable
- Immutable artifact / digest: Not applicable
- Rollout record: Not applicable
- Authenticated smoke proof: Not applicable
- Rollback target: Not applicable

### Release closeout record

- Live verification result: Not applicable
- Deferred drift / follow-up issue: None
