# Detector backlog and evidence priority (#6)

This note ranks the candidate commercial-detection observers listed in #6 by
cost/reliability, per AGENTS.md's evidence priority order, and selects an
MVP subset -- each with its own small, independently assignable
implementation issue. It does not implement any detector; that's #36-#43+.

## Evidence priority (from AGENTS.md, restated for reference)

1. explicit stream/ad markers (e.g. SCTE signaling, when present)
2. known commercial fingerprints
3. deterministic visual/audio features
4. captions/OCR
5. optional VLM fallback -- never introduced merely because image analysis
   is involved

## Candidate observers, ranked

| # | Observer | `SourceKind` (from #3) | Tier | Cost | Determinism | Availability |
|---|---|---|---|---|---|---|
| 1 | Explicit stream/ad markers | `explicit-marker` | 1 | Near-zero (read a flag/cue) | Fully deterministic | Only on sources that carry SCTE-35 or equivalent -- many consumer capture paths don't |
| 2 | Known commercial fingerprint | `commercial-fingerprint` | 2 | Cheap to run once built; the fingerprint database/matching pipeline itself is a substantial sub-project | Deterministic given a match | Universal, but needs a maintained fingerprint corpus |
| 3 | Black-frame transition | `black-frame` | 3 | Very cheap (per-frame luminance threshold) | Deterministic | Universal |
| 4 | Audio/loudness transition | `audio-loudness` | 3 | Very cheap (RMS/LUFS delta) | Deterministic | Universal, though loudness-normalization regulation has weakened this signal since its DVR-era heyday |
| 5 | Scene-cut frequency | `scene-cut` | 3 | Cheap (shot-boundary heuristic) | Deterministic but noisier -- higher false-positive rate standalone | Universal |
| 6 | Station-logo presence | `logo-presence` | 3 | Moderate (template/model match per network) | Deterministic given a trained/templated logo, but per-network setup cost | Needs a logo asset or model per station |
| 7 | Captions/OCR language | `caption-ocr` | 4 | Moderate (caption decode or OCR pipeline) | Deterministic decode, but weaker as a standalone commercial/program signal -- more useful for content classification | Depends on captions being present/decodable |
| 8 | Optional VLM fallback | `vlm-observer` | 5 | Highest (a model call per interval) | Non-deterministic | Last resort only, per AGENTS.md |

## MVP selection

Three detectors, chosen to be genuinely small (no ML/model dependency, no
fingerprint corpus to build or maintain), span two different priority tiers
so the fusion pipeline gets real multi-signal, multi-tier corroboration
(not three variations on the same evidence type), and each maps directly
onto an existing `SourceKind` from #3's pinned contract:

1. **Explicit stream/ad markers** (`explicit-marker`, tier 1) -- #58
2. **Black-frame transition** (`black-frame`, tier 3) -- #59
3. **Audio/loudness transition** (`audio-loudness`, tier 3) -- #60

Deferred past MVP, with reasons:
- **Known commercial fingerprint** -- the fingerprinting pipeline (hashing +
  a maintained corpus) is a project in its own right, not a "small"
  detector issue; revisit as its own epic once the MVP trio proves the
  fusion/policy/fixture pipeline end-to-end.
- **Scene-cut frequency** -- noisier standalone than black-frame/loudness;
  more valuable as a later corroborating signal once the pipeline already
  has confident detectors to compare it against.
- **Station-logo presence** -- needs a per-network logo asset/model before
  any code can be written; an infrastructure prerequisite, not a small
  detector issue.
- **Captions/OCR** -- needs a caption-decode or OCR pipeline; also tier 4,
  intentionally after the cheaper tier-1/3 detectors.
- **Optional VLM fallback** -- explicitly last-resort per AGENTS.md; only
  worth building once cheaper detectors are in place to compare against
  and calibrate its actual necessity.

## Bounded contract every detector issue shares

Every detector implementation issue (see #58-#60) is scoped to exactly:

- One function/class that emits `Observation<BroadcastState>` (from #3's
  `BroadcastObservations` constants) for a single live source, using its
  own fixed `SourceKind` from the table above.
- Input: whatever raw signal that specific detector needs (a marker flag, a
  frame buffer, an audio buffer) -- never another detector's output, and
  never `BroadcastReplacementPolicy` or `ObservationFusion<T>` directly.
- Output: zero or more `Observation<BroadcastState>` values with a
  `Confidence` in `[0, 1]` reflecting that detector's own certainty, not an
  opinion about final replacement eligibility.
- No detector may call into `BroadcastStateFusion` or
  `BroadcastReplacementPolicy` -- per AGENTS.md's layering rule, detectors
  emit evidence only, they never decide or switch.

This is what satisfies #6's acceptance criteria: deterministic/cheap
evidence is prioritized before VLM (the MVP set is entirely tiers 1 and 3,
nothing past tier 3 is even scheduled yet); each detector has a bounded
input/output contract (this section, restated per-issue); the three MVP
issues are independently assignable (none depends on another's
implementation, only on the already-merged #3 contract); and no single
detector owns the final commercial decision (that's `BroadcastStateFusion`
+ `BroadcastReplacementPolicy` from #3/#4, already built and unaffected by
which detectors feed them).
