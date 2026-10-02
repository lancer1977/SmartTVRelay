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

## Web player
Static PWA served from `src/SmartTVRelay.Viewer/wwwroot` at `/` (no build step; hls.js 1.5.17 from cdnjs with SRI).
Channel list from `/api/channels`, tap to play `/hls/{n}/index.m3u8` (native HLS on Safari/iOS, hls.js elsewhere).
The first request can take ~25s while the tuner locks, so the player shows "Tuning… Ns". Errors are mapped:
503 tuners busy, 404 offline, 502 tuner unreachable, 504 not ready, plus a stalled-stream state with Retry.
A state badge (Program / Commercial / Unknown, text plus colour) comes from SSE `/api/channels/{n}/events`;
it is hidden if that endpoint is absent and reconnects back off exponentially (2s to 60s). Stop closes the player and
drops the HLS request so the idle reaper frees the tuner. The service worker caches the app shell only and never `/api` or `/hls`.
`UseDefaultFiles`/`UseStaticFiles` run before endpoint mapping; they only match files that exist, so `/api` and `/hls` routes are unaffected.
