# AGENTS.md

## Purpose
SmartTVRelay ingests live/recorded TV streams, gathers evidence about current broadcast state, and eventually relays the stream with safe user-controlled content substitution.

Observation Core is the shared evidence/fusion layer.

## Prime safety rule
**When evidence is unknown, disputed, stale, conflicting, or below policy threshold, preserve the original broadcast.**

False replacement of real programming is a more severe failure than failing to suppress a commercial.

## Architecture boundaries
Detectors/observers:
- inspect bounded media inputs
- emit observations/evidence
- expose confidence/provenance
- never switch the stream directly

Fusion:
- uses Observation Core
- reconciles evidence
- does not own application action policy

Policy:
- decides replacement eligibility
- defaults to original programming
- remains separate from detectors and fusion

Relay:
- executes already-authorized switching
- must handle failure by returning/preserving original programming

## Evidence priority
Prefer cheap/deterministic evidence before expensive inference:
1. explicit stream/ad markers (for example SCTE signaling when present)
2. known commercial fingerprints
3. deterministic visual/audio features
4. captions/OCR
5. optional VLM fallback

Do not introduce a VLM dependency merely because image analysis is involved.

## Change discipline
- Work only the assigned issue.
- Keep issues small enough for independent review.
- Do not build neighboring layers early.
- Do not make one detector the source of truth.
- Do not bypass Observation Core with detector-specific decision branches.
- Avoid unrelated refactors.

## Required validation
Before completion:
1. build/tests succeed
2. fixture/replay tests pass when relevant
3. detector changes report measurable behavior against fixtures
4. uncertain cases preserve original programming
5. timing/correlation/provenance remains traceable
6. no detector directly invokes relay substitution

## Fixtures and metrics
Prefer replayable recorded/synthetic fixtures over manual observation.
For detector work report, where applicable:
- precision
- recall
- boundary timing error
- false replacement authorizations
- execution cost/latency

False replacement must be surfaced separately and prominently.

## Hardware/live-stream work
Do not make tests depend exclusively on live HDHomeRun hardware.
Every hardware-facing feature should have a recorded/fixture-backed path where practical.

## Fan-out guidance
Good parallel agent lanes include:
- independent detector implementations
- fixture/report tooling
- media metadata/SCTE inspection
- relay components with stable interfaces

Avoid parallel agents modifying the same stream state machine or central contracts.

## PR evidence
Include:
- issue reference
- layer touched (source/detector/fusion/policy/relay)
- fixtures/tests used
- measured results when applicable
- commands run
- failure/fail-open behavior
- intentionally deferred work
