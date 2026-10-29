# Capture proof: Viewer state endpoint and SSE (issue #134, lane L2)

Result: PASS for a recorded-fixture run. NOT a live run. No tuner, HDHomeRun or real channel was touched.
A true live capture remains blocked (see Blocker).

## Run record

| Item | Value |
| --- | --- |
| Base commit (`git rev-parse HEAD`) | `c7fbd70cf7eb1b5b3c9da92c8308d17b7563d4ff` (origin/dev; approval named 5712d0b, dev moved only by docs-only #138) |
| Date (UTC) | 2026-10-29, about 02:09 to 02:12Z (2026-10-28 22:09 to 22:12 local) |
| OS | Arch Linux, kernel 7.2.6-arch2-1 |
| dotnet | 10.0.112 |
| ffmpeg | n9.0.2 |
| Host load average | very high and variable: 16 / 9 / 21 at start, 36 to 46 (1 min) during the runs. Timeouts were set generously (startup 90 s) |
| Run type | Recorded fixture, replayed through a loopback stand-in tuner. Not live. |

## How the Viewer ingests a stream

The Viewer has no file or recorded-input mode. It only pulls a live HTTP MPEG-TS stream from a tuner:
`ChannelPipelineManager` builds the input `http://<host of Tuner:BaseUrl>:<Tuner:StreamPort>/auto/v{guideNumber}`
and tees the original TS bytes to ffmpeg (HLS) and to rolling `raw-*.ts` capture windows.
`SegmentEvidenceSource` inspects those raw windows (SCTE-35, PCR clock, black-frame, audio loudness), and
`ChannelStateService` fuses the evidence. State is exposed at `GET /api/channels/{n}/state` and as SSE at
`GET /api/channels/{n}/events` (current state on connect, then one event per change). Both return 404 unless the
channel's pipeline is running, and a pipeline starts only on a request for `/hls/{n}/index.m3u8` (it is idle-reaped
after `Viewer:IdleSeconds`=30 with no playlist or segment requests).

No source was changed. The only substitution is configuration: `Tuner:BaseUrl` and `Tuner:StreamPort` point at a
throwaway Python server on 127.0.0.1 that serves `/lineup.json` (one fake channel "FIXTURE", guide number 2.1) and loops
`fixtures/captures/sample-live-capture.ts` (376 KB, recorded) as the `/auto/v2.1` stream. The stand-in tuner script lived in
scratch only and is not committed.

## Commands run

Scratch dir `S=/home/lancer1977/.claude/scratch/claude-1000/-home-lancer1977-projects/5258875e-81a1-494d-90aa-50ad598d6eca/scratchpad/rmd/lanes/l2`

```
dotnet build src/SmartTVRelay.Viewer -c Release -o $S/out          # Build succeeded, 0 warnings, 0 errors
timeout 150 python3 $S/fake_tuner.py fixtures/captures/sample-live-capture.ts 18101 18102 &
Viewer__WorkDir=$S/work Viewer__StartupTimeoutSeconds=90 \
Tuner__BaseUrl=http://127.0.0.1:18101 Tuner__StreamPort=18102 \
ASPNETCORE_URLS=http://127.0.0.1:18100 timeout 140 dotnet $S/out/SmartTVRelay.Viewer.dll &
curl -s -m 10 http://127.0.0.1:18100/healthz
curl -s -m 10 http://127.0.0.1:18100/api/channels
curl -s -m 100 http://127.0.0.1:18100/hls/2.1/index.m3u8          # starts the pipeline
curl -s -m 10 http://127.0.0.1:18100/api/channels/2.1/state        # x3, 3 s apart
curl -sN -m 20 http://127.0.0.1:18100/api/channels/2.1/events      # SSE, while re-requesting the playlist
dotnet test tests/SmartTVRelay.Viewer.Tests --filter "FullyQualifiedName~Scte35CommercialStateProofTests|FullyQualifiedName~ChannelStateTests"
```

Everything bound to 127.0.0.1 (ports 18100 to 18102), under `timeout`. After the run no Viewer, fake tuner or ffmpeg
process remained, the work dir was removed, and ports 18100 to 18102 were closed.

## Observed output (recorded-fixture run)

```
/healthz        {"status":"ok","service":"SmartTVRelay.Viewer"}
/api/channels   [{"guideNumber":"2.1","name":"FIXTURE","videoCodec":"MPEG2"}]
/hls/2.1/index.m3u8  HTTP 200
  #EXTM3U / #EXT-X-VERSION:3 / #EXT-X-TARGETDURATION:4 / #EXT-X-MEDIA-SEQUENCE:0 / #EXTINF:4.004000, / seg00000.ts
/api/channels/2.1/state  (3 polls, all HTTP 200)
  {"guideNumber":"2.1","state":"Unknown","confidence":0.2,"evidence":[{"source":"viewer-2.1","kind":"audio-loudness"}],"asOf":"2026-10-29T02:10:14.3767538+00:00"}
  {"guideNumber":"2.1","state":"Unknown","confidence":0.2,"evidence":[{"source":"viewer-2.1","kind":"audio-loudness"}],"asOf":"2026-10-29T02:10:17.944284+00:00"}
  {"guideNumber":"2.1","state":"Unknown","confidence":0.2,"evidence":[{"source":"viewer-2.1","kind":"audio-loudness"}],"asOf":"2026-10-29T02:10:21.9479577+00:00"}
/api/channels/9.9/state (pipeline not running)  HTTP 404
/api/channels/2.1/events  (text/event-stream, first event on connect)
  data: {"guideNumber":"2.1","state":"Unknown","confidence":0.2,"evidence":[{"source":"viewer-2.1","kind":"audio-loudness"}],"asOf":"2026-10-29T02:11:55.6110448+00:00"}
```

Interpretation: the state stays `Unknown` at confidence 0.2 (the documented ceiling for non-agreeing or sub-threshold
evidence). Only the audio-loudness detector produced evidence, and alone it is sub-threshold. This is the correct
fail-open outcome for a capture with no usable SCTE-35 marker; it does not show a Program or Commercial transition.
Only one SSE event was received, as expected because the state never changed. The first SSE attempt returned HTTP 200
but I did not capture its body, and one later state poll got 404 because the pipeline had been idle-reaped (30 s without a
playlist request); the captured SSE above is from a second attempt that kept requesting the playlist. A second SSE
read hit the end of the process timeout (140 s) and was not retried. Only the one data event above was observed
for SSE; no keep-alive comment (15 s interval) was captured in the file.

## Fixture replay tests (Commercial transitions via the real endpoint)

`dotnet test` (filtered to `Scte35CommercialStateProofTests` and `ChannelStateTests`): Total 37, Passed 37, Failed 0,
7.0 s test time (about 30 s wall with build, under load). These replay hand-encoded SCTE-35 raw transport through the real route
(raw window, SegmentEvidenceSource, ChannelStateService, HTTP endpoint) with a fake ffmpeg runner and a fake tuner handler.
Notable passes:

| Test | Result |
| --- | --- |
| `Scte35CommercialStateProofTests.State_is_Commercial_only_inside_the_SCTE35_break` (splice_insert and time_signal+segmentation) | Passed |
| `Scte35CommercialStateProofTests.Commercial_goes_stale_to_Unknown_when_the_capture_stops_updating` | Passed |
| `Scte35CommercialStateProofTests.Stream_without_a_SCTE35_PID_is_Unknown_however_far_the_clock_runs` | Passed |
| `Scte35CommercialStateProofTests.Cancelled_CueOut_never_reports_Commercial` | Passed |
| `ChannelStateTests.Sse_emits_Program_to_Commercial_transition_for_clean_break` | Passed |
| `ChannelStateTests.Sse_emits_initial_state_on_connect` | Passed |
| `ChannelStateTests.Source_failure_fails_open_to_Unknown` | Passed |

## What this does and does not prove

Proves: the built Viewer runs, ingests a recorded MPEG-TS over its real HTTP-input path, starts the HLS pipeline,
and serves the state endpoint and SSE, failing open to `Unknown` on a capture without markers. Offline tests show
Commercial reported only inside a SCTE-35 break.

Does not prove: anything about real broadcast content, real tuner behaviour, RF or ffmpeg behaviour against a live
HDHomeRun stream, or a Program/Commercial transition from `sample-live-capture.ts` itself (whose SCTE-35 content was
not inspected in this lane).

## Blocker for a true live capture

A live run needs the HDHomeRun tuner (default `Tuner:BaseUrl` http://192.168.0.66, stream port 5004) and a real channel.
This lane was told not to capture from live tuners or real channels, so no live run was attempted. Required to unblock:
explicit approval to tune a named channel, and a tuner reachable from the host. The Viewer has no recorded-file input mode;
the loopback stand-in tuner above is the closest honest route and was used with configuration only.
