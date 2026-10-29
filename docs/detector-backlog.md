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

## V1 coverage map (#133)

V1 includes SCTE-35 markers, black-frame, audio loudness, captions/OCR, plus
fusion on Observation Core and the fixture/replay/evaluation-gate harness.
This map names the code and the tests/fixtures that exist today; it adds no
new behavior. Paths are relative to the repo root; "Core" tests are in
`tests/SmartTVRelay.Core.Tests/`, "Viewer" tests in
`tests/SmartTVRelay.Viewer.Tests/`.

| Backlog line | Detector / source | Tests (file: names) | Fixtures |
|---|---|---|---|
| 1. Explicit stream/ad markers (SCTE-35), `explicit-marker` | `src/SmartTVRelay.Core/ExplicitMarkerDetector.cs`; PID/section decode in `src/SmartTVRelay.Core/Ingest/Scte35MarkerExtractor.cs` | Core `ExplicitMarkerDetectorTests.cs`: `Detect_WithCueOutMarker_ReturnsObservationWithCommercialState`, `Detect_WithCueInMarker_ReturnsObservationWithProgramState`, `Detect_WithNoneMarker_ReturnsEmptyList`, `Detect_WithUnrecognizedEnumValue_ReturnsEmptyList`, `Detect_EachCallGeneratesUniqueObservationId`. Core `Ingest/Scte35MarkerExtractorTests.cs`: `Extract_WithSyntheticFixture_ReturnsExactlyOneCueOutAndOneCueIn`, `Extract_RealIssue124TimeSignalFixture_UsesHandDecodedOracle`, `Extract_NoScte35Pid_ReturnsEmptyList`, `Extract_ImmediateSplice_EmitsNoSignal`, `Extract_CanceledEvent_ExcludesItFromReturnedSignals`, and the malformed/truncated/PAT/PMT cases in the same file. Viewer `Scte35CommercialStateProofTests.cs`: `The_break_boundary_is_the_cue_PTS_for_cues_announced_ahead_of_time`, `CueIn_without_a_CueOut_never_reports_Commercial`, `Cancelled_CueOut_never_reports_Commercial`, `Disputed_by_a_competing_detector_is_Unknown_with_provenance_kept`, `Stream_without_a_SCTE35_PID_is_Unknown_however_far_the_clock_runs`. | `fixtures/synthetic/scte35-sample.ts`, `scte35-time-signal-202-36.ts`, `scte35-time-signal-203-36.ts`, `scte35-time-signal-203-37.ts` (generator: `fixtures/synthetic/generate-scte35-fixture.cs`) |
| 3. Black-frame transition, `black-frame` | `src/SmartTVRelay.Core/BlackFrameDetector.cs`; frame luminance from `src/SmartTVRelay.Core/Ingest/FrameAudioSampler.cs` | Core `BlackFrameDetectorTests.cs`: `Detect_ClearBlackFrameSequence_EmitsTransitionObservation`, `Detect_NormalFrameSequence_ReturnsEmptyList`, `Detect_ExactlyAtThreshold_Included`, `Detect_JustAboveThreshold_Excluded`, `Detect_SceneCutFlashShorterThanMinimumDuration_IsSuppressed`, `Detect_DarkSceneTrap_BriefDipBelowThresholdIsSuppressedByMinimumDuration`, `Detect_PersistentLetterboxing_DoesNotEmitTransition`, plus constructor/argument validation tests. Sampler on real media, Core `Ingest/FrameAudioSamplerTests.cs`: `SampleVideoAsync_WithBlackFixture_YieldsExpectedLuminance`, `SampleVideoAsync_WithWhiteFixture_YieldsExpectedLuminance`. | `fixtures/synthetic/black-with-tone.ts`, `fixtures/synthetic/white-with-tone.ts` (sampler tests only; detector tests build `FrameLuminanceSample` lists in code) |
| 4. Audio/loudness transition, `audio-loudness` | `src/SmartTVRelay.Core/AudioLoudnessDetector.cs`; windows from `FrameAudioSampler.cs` | Core `AudioLoudnessDetectorTests.cs`: `ClearLoudnessJumpSequenceEmitsObservations`, `StableLoudnessSequenceBelowThresholdEmitsNothing`, `BorderlineAtThresholdIsIncludedJustBelowIsExcluded`, `SustainedNearSilenceEmitsDistinctDeadAirSignal`, `SilenceIsNeverReportedAsCommercial`, `OscillatingMusicOrActionPassageDoesNotFalsePositiveAsTransition`, `LoudnessJumpThatHoldsStillEmitsTransition`, `CustomThresholdIsRespected`, plus constructor/argument validation tests. Sampler on real media, Core `Ingest/FrameAudioSamplerTests.cs`: `SampleAudioAsync_WithBlackFixture_YieldsAtLeastOneSample`, `SampleAudioAsync_FirstSample_LoudnessDeltaIsZero`. End to end through the Viewer evidence source, Viewer `SegmentEvidenceLoudnessTests.cs`: `Window_with_a_loudness_step_yields_audio_loudness_evidence`, `Audio_sampler_failure_does_not_remove_other_evidence_or_throw`. | `fixtures/synthetic/black-with-tone.ts` (sampler tests); the Viewer test builds its loud-to-quiet window with ffmpeg at test time (no checked-in file) |
| 7. Captions/OCR language, `caption-ocr` | `src/SmartTVRelay.Core/CaptionOcrDetector.cs` (phrase matching over caption text; no image-OCR pipeline exists), `CaptionCommercialObservation.cs`; caption decode in `src/SmartTVRelay.Core/Ingest/CaptionExtractor.cs` | Core `CaptionOcrDetectorTests.cs`: `Detect_WithSingleCommercialPhrase_EmitsCommercialObservationWithMatchedFeature`, `Detect_MatchIsCaseInsensitive`, `Detect_MoreMatchedPhrasesYieldsHigherConfidenceCappedAtMax`, `Detect_WithNeutralDialogueOnly_EmitsNoObservations`, `Detect_HigherMinimumMatchCountSuppressesSingleMatchSamples`, `Detect_WithSparseWhitespaceOnlyCaptions_EmitsNoObservations`. Extractor on real media, Core `Ingest/CaptionExtractorTests.cs`: `ExtractAsync_WithSyntheticFixture_ReturnsExactlyTwoCaptionsWithExpectedTimingAndText`, `ExtractAsync_NoSubtitleStream_ReturnsEmptyListWithoutThrowing`; `Ingest/EmbeddedCaptionExtractorTests.cs`: `ExtractAsync_MpegTsWithEmbeddedCaptions_FallsBackAndAppliesNextCaptionEndsThisOneHeuristic`. | `fixtures/synthetic/captions-sample.mp4` (generator: `generate-captions-fixture.sh`); detector tests use in-code `CaptionCommercialObservation`-input samples. Gap, not covered: no test chains extractor output into the detector, and the detector is not yet called from `SegmentEvidenceSource`. |
| Fusion on Observation Core | `src/SmartTVRelay.Core/BroadcastStateFusion.cs` (over `Observation<T>` / `ObservationFusion<T>`); policy in `BroadcastReplacementPolicy.cs` | Core `BroadcastStateFusionTests.cs`: `AgreeingDetectorsFuseToAgreedState`, `DisagreementWithinConfiguredConfidenceMarginIsDisputedAndUnknown`, `SingleLowConfidenceObservationRemainsAgreed`, `DistinctScopesFuseIndependently`. Core `BroadcastReplacementPolicyTests.cs` (policy layer). Viewer `Scte35CommercialStateProofTests.cs`: `Disputed_by_a_competing_detector_is_Unknown_with_provenance_kept`. | none needed (in-code observations) |
| Fixture / replay / evaluation-gate harness | `src/SmartTVRelay.Core/Fixtures/` (`BroadcastFixtureHarness`, `BroadcastScoringEngine`, `FixtureEvaluationRunner`, `EvaluationGate`, `EvaluationReportGenerator`, `SyntheticFixtureGenerator`, `LabelCorrectionApplier`) | Core `Fixtures/BroadcastFixtureHarnessTests.cs`: `Run_DeterministicResults_ExecutesIdenticallyAcrossMultipleRuns`, `Run_ProgramCommercialProgram_AuthorizesReplacementOnlyDuringCommercial`, `Run_AmbiguousTransition_PreservesOriginalDuringDisputed`, `Run_FalseReplacementDetection_ReportsFalseReplacementWhenAuthorizingDuringNonCommercial`, `Run_MissedCommercialAndFalseReplacementDistinctCategories`. `Fixtures/FixtureEvaluationRunnerTests.cs`: `Evaluate_EverySyntheticScenario_RunsWithoutThrowingAndProducesAScoringReport`, `Evaluate_FalsePositiveTrap_OneLowConfidenceDissentingDetector_DoesNotAuthorizeFalseReplacement`, `Evaluate_SameFixtureTwice_ProducesIdenticalReport`. `Fixtures/EvaluationGateTests.cs`: `Evaluate_AnyFalseReplacement_IsARegressionByDefault`, `Evaluate_PrecisionBelowThreshold_IsARegression`, `Evaluate_RecallBelowThreshold_IsARegression`, `Evaluate_MultipleViolations_ReportsAllReasons`. Also `BroadcastScoringEngineTests.cs`, `EvaluationReportGeneratorTests.cs`, `SyntheticFixtureGeneratorTests.cs`, `LabelCorrectionApplierTests.cs`, `BroadcastFixtureValidatorTests.cs`. | `fixtures/samples/program-commercial-program.json`, `fixtures/samples/ambiguous-transition.json`, `fixtures/captures/sample-live-capture.ts`; seeded synthetic scenarios from `SyntheticFixtureGenerator` |

## Deferred past V1 (#133)

Not part of V1. Reasons are those recorded in "Deferred past MVP" above.

| Backlog line | Reason deferred | State in code today |
|---|---|---|
| 2. Known commercial fingerprints, `commercial-fingerprint` | The fingerprinting pipeline (hashing plus a maintained corpus) is a project in its own right; revisit as its own epic once the V1 set proves the fusion/policy/fixture pipeline. | Only the `IKnownCommercialFingerprintStore` interface is in scope for V1 (`src/SmartTVRelay.Core/IKnownCommercialFingerprintStore.cs`). A detector and in-memory store also exist (`KnownCommercialFingerprintDetector.cs`, `InMemoryCommercialFingerprintStore.cs`, with tests) but are not part of the V1 contract and no corpus exists. |
| 5. Scene-cut frequency, `scene-cut` | Noisier standalone than black-frame/loudness; more valuable as a later corroborating signal. | `SceneCutFrequencyDetector.cs` and its tests exist; not a V1 commitment. |
| 6. Station-logo presence, `logo-presence` | Needs a per-network logo asset/model before it is useful; an infrastructure prerequisite. Not on the V1 list. | `LogoPresenceDetector.cs` and its tests exist; not a V1 commitment. |
| 8. Optional VLM fallback, `vlm-observer` | Explicitly last resort per AGENTS.md; only worth building once cheaper detectors exist to compare against and calibrate its necessity. | Not a V1 commitment (a `Vlm/` folder exists in Core; not assessed here). |
