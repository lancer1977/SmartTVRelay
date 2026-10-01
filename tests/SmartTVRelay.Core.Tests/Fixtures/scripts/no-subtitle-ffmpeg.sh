#!/usr/bin/env bash
# Test double for CaptionExtractorTests: simulates ffmpeg's real, observed exit behavior when a file
# has no subtitle stream at all ("Output file does not contain any stream" on stderr, exit code 1) --
# deterministically, regardless of input, so embedded-caption fallback tests don't depend on ffmpeg
# being able to probe a synthetic MPEG-TS fixture that is only spec-shaped enough for
# PolyhydraGames.CaptionExtractor's own demuxer, not a fully valid H.264 elementary stream.
echo "Output file does not contain any stream" >&2
exit 1
