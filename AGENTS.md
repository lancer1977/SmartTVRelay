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

<!-- dev-forge:low-interruption:start version=5 -->
## Low-Interruption Execution

- Treat explicit outcome requests such as "fix," "build," "complete," and
  "finish" as continuing authorization for bounded work toward that outcome.
- Continue through diagnosis, implementation, tests, commits, pushes, review
  feedback, and CI repair without renewed confirmation.
- New defects discovered within the same task or pull request remain in scope
  only when they are P0/P1 or release-blocking, or while the accepted outcome
  is not yet complete. After acceptance criteria pass, P2-and-lower
  non-blocking findings become linked follow-ups unless the owner explicitly
  keeps them in the active slice.
- Progress updates are informational and do not pause execution.
- Do not request confirmation when the only realistic alternatives are the
  clearly supported action and inaction.
- Use MAID (Multiple-Choice Agentic Interface Development) only for a real
  operator fork. Lead with the recommended option, label it, make choices
  mutually exclusive, and describe what each choice causes.
- Use the current host's native picker when it is available in the current
  mode. Never invoke a picker that the current host or mode does not expose.
  A missing picker is not a blocker: at a genuine impasse, present two or three
  concise, lettered **A**/**B**/**C** choices instead and request the letter.
- Scale a checkpoint to what it costs the operator, not to how important the
  decision feels. A native picker is cheap enough for any real fork; a typed
  fallback remains reserved for a genuine impasse. Neither is a reason to ask
  for reassurance, re-ask a settled decision, or ask what the repository can
  answer.
- Do not offer a picker whose options are an action and its absence. Do not use
  "Done — continue" as a generic permission gate.
- A picker selects a path; it never authorizes merge, deploy, destructive work,
  secret or access changes, material cost, external communication, or credible
  downtime or data-loss risk. Use the applicable approval control for those
  protected actions.
- End every handback with selectable, genuine next steps when a real next-step
  fork remains. Use the native picker when available; otherwise use lettered
  choices. If no second genuine option exists, state the actual status plainly
  and stop rather than inventing a placeholder choice.
- This governs the form of a handback, not the frequency of checkpoints. Do not
  interrupt bounded work more often because of it. Do not add selectable
  options to a machine-readable output constrained by an external schema.
<!-- dev-forge:low-interruption:end -->
