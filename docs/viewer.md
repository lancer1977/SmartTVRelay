# SmartTVRelay.Viewer (HLS relay)

ASP.NET Core minimal API that re-serves HDHomeRun channels as HLS (H.264/AAC, 4s segments, rolling window of 6).
Requires `ffmpeg` on PATH (or `Viewer:FfmpegPath`).

## Endpoints
| Endpoint | Behaviour |
| --- | --- |
| `GET /api/channels` | `[{"guideNumber","name","videoCodec"}]` from `{Tuner:BaseUrl}/lineup.json`; 502 if tuner unreachable |
| `GET /hls/{guideNumber}/index.m3u8` | Starts one ffmpeg per channel on first request, waits up to `Viewer:StartupTimeoutSeconds` for the playlist (504 on timeout). Unknown channel 404; over cap 503 |
| `GET /hls/{guideNumber}/segNNNNN.ts` | Segment from the running pipeline; 404 if not running |

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

ffmpeg processes are killed and work dirs deleted on host shutdown.

## Extension points
`IFfmpegRunner` (process launch), `ITunerLineup`, and `IChannelPipelineRegistry` (`RunningChannels`, `GetWorkDirectory`).
