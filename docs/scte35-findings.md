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

## Known Limitations (intentional, not bugs)

1. **No CRC validation** -- sections are parsed but `CRC_32` is not verified.
2. **No `encrypted_packet` support** -- encrypted sections are skipped safely (no signal emitted).
3. **No component-splice-per-PID** -- only program-splice (`program_splice_flag=1`) is handled.
4. **No duration/avail fields** -- descriptors beyond the `splice_insert` command itself are ignored.
5. **Immediate splices (`splice_immediate_flag=1`)** -- no timestamp is present in the section at
   all in this case; the extractor falls back to `DateTimeOffset.UtcNow` at parse time. **This is
   not the actual broadcast time** and is a real gap, not a nice-to-have caveat, for any pipeline
   that cares about immediate-splice timing specifically.
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
