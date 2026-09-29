# SCTE-35 Ad-Marker Extraction Findings

## Objective
Determine whether explicit splice/ad markers can be extracted from MPEG-TS files and
normalized as timestamped evidence for downstream processing.

## What Was Implemented

### Scte35MarkerExtractor
A minimal in-repo binary parser for SCTE-35 (ANSI/SCTE 35) splice markers in MPEG-TS
transport streams, with no external CLI dependencies or NuGet packages.

- Locates SCTE-35 streams in the PMT by `stream_type` 0x86
- Extracts `splice_insert` commands (`splice_command_type` 0x05)
- Decodes `out_of_network_indicator` to distinguish CueOut (ad break start) vs CueIn (ad break end)
- Extracts PTS-based timestamps and converts to `DateTimeOffset` using the 90kHz domain formula
- Graceful error handling: returns an empty list on malformed files, missing streams, or any
  parsing error (no exceptions thrown)

## Bugs found and fixed during review

The first implementation of both `Scte35MarkerExtractor.cs` and its fixture generator
(`fixtures/synthetic/generate-scte35-fixture.cs`) had real, confirmed bit-packing defects that
were not caught before this review because the parser and its own fixture generator shared the
same incorrect assumptions -- the round-trip test passed by agreeing with itself, not by
correctly implementing the spec. This is worth documenting in detail since it's exactly the
class of defect a "prototype" like this needs to guard against before being trusted downstream.

1. **Missing `pointer_field` handling.** Every MPEG-TS PSI/private section (PAT, PMT, and this
   SCTE-35 section) is preceded, on the packet where `payload_unit_start_indicator=1`, by a
   mandatory single-byte `pointer_field` (ISO/IEC 13818-1) before the section itself starts.
   Confirmed directly against our real captured fixture
   (`fixtures/captures/sample-live-capture.ts`): its first PAT packet's payload is
   `00 00 b0 0d ...` -- the first `00` is the `pointer_field`, and the *actual* `table_id`
   (also `0x00` for PAT) is the byte after it. The original parser read the `pointer_field`
   itself as `table_id`; it only "worked" by coincidence because both bytes happen to be zero,
   and every subsequent field would have been read one byte out of alignment against real data.
   Fixed by reading and skipping `1 + pointer_field` bytes whenever a new section starts.

2. **Wrong `splice_time()` bit-packing (off-by-one-byte, plus a spurious marker-bit pattern).**
   Per spec, `splice_time()` when `time_specified_flag=1` is exactly 5 bytes: 1 bit
   `time_specified_flag` + 6 bits reserved + 33-bit `pts_time`, sharing the first byte's low bit
   as `pts_time`'s MSB, with **no marker bits** anywhere in the field. The original
   implementation (and its matching fixture generator) treated this as 6 bytes -- a full,
   separate flag byte followed by 5 more bytes decoded with PES-optional-PTS-style marker-bit
   masking (a *different* field, used only in PES packet headers, that does have marker bits).
   Confirmed by hand-computing the correct 5-byte encoding for `pts_time=90000` and showing the
   original parser computed a value in the millions instead of 90000 when fed the correct bytes.
   Fixed both the parser (`ExtractPtsTime33`, a plain 33-bit big-endian read) and the fixture
   generator to use the real 5-byte encoding.

3. **Wrong PMT header offset (off-by-two).** `ParsePmtSection`'s walk to `program_info_length`
   used a hardcoded offset of 12; the correct offset (table_id + section_length +
   program_number + version/current_next + section_number + last_section_number + PCR_PID =
   1+2+2+1+1+1+2) is 10. This made every real-world PMT -- and, once fixes #1 and #2 above were
   applied, even the self-generated fixture -- fail to find the SCTE-35 elementary stream entry
   at all. Fixed to 10, and this fix is directly why the "no SCTE-35 in a real capture" test
   (below) is now trustworthy: confirmed via manual tracing that it walks the real PMT's
   elementary stream loop correctly (finds `stream_type=0x02` MPEG-2 video and three
   `stream_type=0x81` AC-3 audio entries, matching independent `ffprobe` output) and correctly
   determines none of them is `0x86`, rather than failing to parse the PMT at all and returning
   empty for the wrong reason.

4. **Vacuous test assertions.** The two tests meant to verify CueOut/CueIn extraction
   (`Extract_WithSyntheticFixture_ReturnsCueOutSignal` / `...CueInSignal`) only asserted
   `Assert.NotNull(signals)` -- a `List<T>` is never null, so these passed unconditionally
   regardless of whether extraction found anything at all. They did not check `Kind`, count, or
   `ObservedAt`. Replaced with a single test that asserts exactly 2 signals with the correct
   `Kind` and `ObservedAt` for both the CueOut and CueIn events. Also added
   `Extract_HandCraftedSpecCompliantSection_DecodesCorrectPtsTime`, which builds its own MPEG-TS
   bytes directly from the spec (not via the shared fixture generator) as an independent oracle,
   specifically to stop a future version of this class of bug (parser and generator sharing an
   incorrect assumption) from hiding behind a passing test again.

All of the above were found and fixed in the same review pass, verified via a scratch console
harness that calls the real compiled `Scte35MarkerExtractor` directly and prints intermediate
PID/stream-type/signal values, not just by re-running the test suite (which, prior to fix #4,
could not have caught #1-#3 at all).

## Bugs found and fixed on re-review (PR #70 automated review)

A second, independent review pass (before merge) found five more real defects, all confirmed by
hand-tracing the bit layout against the ANSI/SCTE 35 and ISO/IEC 13818-1 specs:

5. **`splice_event_cancel_indicator` and the flags shared one byte (P1).** In a
   standards-compliant `splice_insert()`, `splice_event_cancel_indicator` is bit 7 of its own
   byte (the remaining 7 bits are reserved); `out_of_network_indicator`, `program_splice_flag`,
   `duration_flag`, and `splice_immediate_flag` are bits 7-4 of the *next* byte, present only
   when `splice_event_cancel_indicator=0`. The original code read all five flags from the single
   byte immediately after `splice_event_id`, so the normal all-zero reserved-bit pattern in that
   byte was interpreted as the flags themselves while the real flags and `splice_time()` were
   read one byte out of alignment -- a real CueIn could be misread as a high-confidence CueOut
   (or vice versa). Fixed by reading `splice_event_cancel_indicator` from its own byte and the
   four flags from the following byte.
6. **`pts_adjustment` was read for nothing (P2).** `splice_info_section()`'s header carries a
   33-bit `pts_adjustment` in the same 5 bytes previously used only to check `encrypted_packet`.
   The effective splice time is `(pts_time + pts_adjustment) mod 2^33`, not `pts_time` alone.
   Fixed by extracting `pts_adjustment` (same plain-33-bit layout as `pts_time`, reusing
   `ExtractPtsTime33`) and adding it to `pts_time` before converting to a timestamp.
7. **Immediate/unspecified splices fabricated a wrong timestamp (P1).** When
   `splice_immediate_flag=1`, or `time_specified_flag=0`, no timestamp exists in the section at
   all. The original code fell back to `DateTimeOffset.UtcNow` at parse time -- but `Extract`
   only ever parses recorded files, so "now" has no relationship to the file's actual broadcast
   time. Emitting this as a signal risked promoting stale/unrelated timing into fresh,
   high-confidence commercial-replacement evidence, which is exactly the failure this project's
   design rule says is worse than showing some commercials. Fixed by emitting **no signal** in
   either case rather than a fabricated one. This supersedes limitation 5 below: it is no longer
   a caveat about a suspect value, because no value is produced at all.
8. **`pointer_field` continuation bytes were discarded (P2).** When a `payload_unit_start`
   packet's `pointer_field` is nonzero, the bytes between it and the next section boundary
   finish the section already in progress, not the new one. The original code finalized the
   in-progress section *before* reading those bytes, then skipped over them entirely, silently
   truncating any PAT/PMT/SCTE-35 section that happens to span exactly that packet boundary.
   Fixed by appending those continuation bytes to the in-progress section before finalizing it.
   This is a rare-in-practice edge case (our real capture's sections all have `pointer_field=0`)
   but a real spec violation.
9. **Command parsing was bounded against the 4096-byte scratch buffer, not the section (P2).**
   `TryParseSpliceInsert` checked bounds against the full backing array length rather than the
   validated section end or the declared `splice_command_length`, so a truncated or malformed
   section could still read packet stuffing or stale bytes from a previous section as if they
   were real command data, instead of the promised empty result. Fixed by computing the
   command's actual end from the section length and the declared `splice_command_length` (or the
   section end, when the spec's `0xFFF` "length not specified" sentinel is used) and bounding all
   reads against that.

All five were fixed together, and the hand-crafted independent-oracle test
(`Extract_HandCraftedSpecCompliantSection_DecodesCorrectPtsTime`) and the checked-in synthetic
fixture were both updated to the corrected two-byte flags layout -- both previously encoded the
same collapsed-single-byte bug the parser had, so neither would have caught it. New tests
(`Extract_ImmediateSplice_EmitsNoSignal`, `Extract_UnspecifiedSpliceTime_EmitsNoSignal`,
`Extract_WithPtsAdjustment_AppliesItToTheObservedTime`) pin fixes 6 and 7.

## Bugs found and fixed on second re-review (PR #70, second automated review pass)

A third review pass, after the fixes above were pushed, found three more real defects:

10. **A canceled event stayed in the returned signals (P1).** When a scheduled CueOut/CueIn is
    later canceled by a second `splice_insert` sharing the same `splice_event_id` and
    `splice_event_cancel_indicator=1`, the original code's cancellation handling only affected
    the cancel command's own section -- it did nothing to the earlier signal already appended to
    the result list for that same event, so a genuinely canceled event still came back as
    high-confidence commercial evidence. Fixed by tracking `splice_event_id` per pending signal
    and per cancellation across the whole PID scan (not per-section, since the cancellation can
    arrive in any later section), then filtering canceled IDs out at the very end -- correct
    regardless of whether the cancellation happens to arrive before or after the event it
    cancels.
11. **Multiple sections packed into one TS packet were merged into a single blob (P2).** When two
    or more complete PSI sections are packed into the same TS packet's payload -- realistic for
    SCTE-35, since a CueOut and CueIn are each small -- the section accumulator invoked
    `onSection` only once per `payload_unit_start` boundary, treating the whole span (both
    sections concatenated) as if it were one section. Each specific parser only reads its own
    section's declared length from the front of that blob, so every section after the first in
    the same payload was silently dropped -- concretely, a packed CueIn immediately following a
    CueOut would vanish, leaving only the CueOut in the result. Fixed by rewriting the
    accumulator to determine each section's own length from its `table_id`/`section_length`
    header and dispatch every complete section it finds, compacting only a genuinely incomplete
    trailing section forward to the next packet. This required also fixing the fixture
    generators (the checked-in synthetic fixture, its C# generator, and the test file's
    hand-crafted helpers) to fill unused TS packet tail bytes with `0xFF` stuffing
    (ISO/IEC 13818-1 2.4.4.7) instead of leaving them zero-initialized: a demuxer that correctly
    stops at the section boundary once it sees non-section data cannot tell zero-initialized
    padding apart from the start of a genuine PAT-table_id (`0x00`) section with a bogus
    zero-length -- confirmed by reproducing exactly that corruption (the checked-in fixture's
    second section vanished) before fixing the fixture generators.
12. **Caller cancellation was silently swallowed into a "successful" result (P2).** `Extract`'s
    outer `catch (Exception)` also caught `OperationCanceledException`, so cancelling the
    supplied token mid-parse returned whatever had been extracted so far as if it were a normal,
    complete result, rather than surfacing the cancellation to the caller. Fixed by catching and
    rethrowing `OperationCanceledException` before the broader catch.

New tests pin all three: `Extract_TwoSectionsPackedInOnePacket_ReturnsBothSignals`,
`Extract_CanceledEvent_ExcludesItFromReturnedSignals`,
`Extract_AlreadyCanceledToken_ThrowsOperationCanceledExceptionRatherThanReturningPartialResults`.

## Known Limitations (intentional, not bugs)

1. **No CRC validation** -- sections are parsed but `CRC_32` is not verified.
2. **No `encrypted_packet` support** -- encrypted sections are skipped safely (no signal emitted).
3. **No component-splice-per-PID** -- only program-splice (`program_splice_flag=1`) is handled.
4. **No duration/avail fields** -- descriptors beyond the `splice_insert` command itself are ignored.
5. **Immediate splices (`splice_immediate_flag=1`) and unspecified `splice_time()`
   (`time_specified_flag=0`)** -- no timestamp is present in the section at all in either case;
   the extractor emits **no signal** for these rather than a fabricated one (see "Bugs found and
   fixed on re-review" #7 above -- this used to fall back to `DateTimeOffset.UtcNow`, which is
   not the actual broadcast time for a recorded file). A pipeline that needs immediate-splice
   timing specifically would need to track the packet's own source position/PCR baseline, which
   this prototype does not do.
6. **No other `splice_command_type`s** -- `time_signal`, `bandwidth_reservation`,
   `private_command`, etc. are silently ignored (no signal emitted, no error).
7. **PTS-domain timestamps, not wall-clock.** `pts_time` is in the stream's own 90kHz PTS/PCR
   clock domain, not wall-clock time. The `DateTimeOffset.UnixEpoch + pts_time/90000` formula
   (matching the pattern already used for pts-like values in `FrameAudioSampler.cs` and
   `TransportStreamInspector.cs`) is a reference-frame approximation, not a real timestamp,
   unless the caller tracks the stream's own PCR baseline separately.

## Real-Capture Validation

**Key finding**: our actual HDHomeRun OTA (over-the-air) ATSC capture fixture
(`fixtures/captures/sample-live-capture.ts`) contains no SCTE-35 data -- only video
(`stream_type=0x02`, MPEG-2) and AC-3 audio streams (`stream_type=0x81`), independently
confirmed via `ffprobe`. SCTE-35 markers are conventionally inserted at a cable/IPTV head-end
during distribution, not at broadcast origination, so this is expected for a raw ATSC OTA
capture and not a defect in the extraction logic.

**Validation approach**: correctness of splice_insert extraction is validated against a
synthetic fixture and an independent hand-built oracle (see "Vacuous test assertions" above).
The real capture is used only to validate the "no SCTE-35 present" path -- and, per finding #3
above, that path is now confirmed to walk the real PMT correctly rather than failing silently.

## Recommendation for Downstream Integration (observation, not a decision)

- If the ingest pipeline needs to support OTA sources specifically, SCTE-35 will not be
  available there; a different marker source would be needed for OTA (e.g. caption-based cues,
  manual markers).
- Timestamp reliability (see limitation 7) means these timestamps are useful for relative
  ordering within a single splice_insert, but not for correlating against wall-clock events,
  unless paired with real PCR/PTS baseline tracking.
- Before wiring this into a real ingest path, it would be worth validating against an actual
  cable/IPTV capture with genuine SCTE-35 markers, since none of our current fixtures can
  exercise that (our only real capture is OTA).

## Files Changed
- `src/SmartTVRelay.Core/Ingest/Scte35MarkerExtractor.cs` -- extractor implementation
- `fixtures/synthetic/generate-scte35-fixture.cs` -- fixture generator (spec-compliant; produces the checked-in fixture below)
- `fixtures/synthetic/scte35-sample.ts` -- checked-in synthetic fixture (CueOut @ 1s, CueIn @ 2s)
- `tests/SmartTVRelay.Core.Tests/Ingest/Scte35MarkerExtractorTests.cs` -- tests, including a hand-built independent oracle
- `docs/scte35-findings.md` -- this document

## Build & Test Results
`dotnet build SmartTVRelay.slnx -c Release` -- 0 warnings, 0 errors.
`dotnet test SmartTVRelay.slnx -c Release` -- 176/176 passing (171 prior + 5 new for this issue).
