# SmartTVRelay.Viewer (HLS relay)

ASP.NET Core minimal API that re-serves HDHomeRun channels as HLS (H.264/AAC, 4s segments, rolling window of 6).
Requires `ffmpeg` on PATH (or `Viewer:FfmpegPath`).

## Endpoints
| Endpoint | Behaviour |
| --- | --- |
| `GET /api/channels` | `[{"guideNumber","name","videoCodec"}]` from `{Tuner:BaseUrl}/lineup.json`; 502 if tuner unreachable |
| `GET /hls/{guideNumber}/index.m3u8` | Starts one ffmpeg per channel on first request, waits up to `Viewer:StartupTimeoutSeconds` for the playlist (504 on timeout). Unknown channel 404; over cap 503 |
| `GET /hls/{guideNumber}/segNNNNN.ts` | Segment from the running pipeline; 404 if not running |

| `GET /api/channels/{guideNumber}/state` | Fused detection state: `{"guideNumber","state":"Program\|Commercial\|Transition\|Unknown","confidence","evidence":[{"source","kind"}],"asOf"}`. 404 if the channel's pipeline is not running |
| `GET /api/channels/{guideNumber}/events` | `text/event-stream`. Sends the current state on connect, then one `data: <state JSON>` event per state change, plus `: keep-alive` comments. 404 if not running; the stream ends when the pipeline stops |

`guideNumber` must be digits and dots only; anything else is 404.

## Config keys
| Key | Default |
| --- | --- |
| `Tuner:BaseUrl` | `http://192.168.0.66` |
| `Tuner:StreamPort` | `5004` (ffmpeg input is `http://<tuner host>:5004/auto/v{guideNumber}`) |
| `Viewer:WorkDir` | `<tmp>/smarttvrelay-viewer` |
| `Viewer:MaxChannels` | `2` |
| `Viewer:IdleSeconds` | `30` (pipelines with no playlist/segment requests are stopped and deleted) |
| `Viewer:StartupTimeoutSeconds` | `15` |
| `Viewer:FfmpegPath` | `ffmpeg` |

State keys (`Viewer:State:*`): `PollMilliseconds` 2000, `KeepAliveSeconds` 15, `EvidenceWindowSeconds` 30 (older evidence is ignored), `ConfidenceThreshold` 0.75, `WindowSegments` 3, `AnalysisTimeoutSeconds` 10.

## Detection state
Observations come from `IChannelEvidenceSource` and are fused by Core's `BroadcastStateFusion` (Observation Core). No evidence, stale evidence, disputed/conflicting evidence, a source failure, or fused confidence below `ConfidenceThreshold` all report `Unknown` with low confidence (<= 0.2); `Commercial` is only reported with agreeing, confident evidence. The state endpoints only report; nothing here switches or substitutes the stream.

The default `SegmentEvidenceSource` concatenates the newest segments and runs the existing SCTE-35 extractor and black-frame detector. The viewer's ffmpeg re-encode does not carry SCTE-35 through, and black frames alone yield sub-threshold `Transition` evidence, so on real captures the state is expected to be `Unknown` until stronger detectors are wired into the source.

ffmpeg processes are killed and work dirs deleted on host shutdown.

## Extension points
`IFfmpegRunner` (process launch), `ITunerLineup`, `IChannelPipelineRegistry` (`RunningChannels`, `GetWorkDirectory`), and `IChannelEvidenceSource` (state evidence; registered by `AddChannelState()`, mapped by `MapChannelState()`).
