# HDHomeRun live source

Configure the device base URL and select a channel by its guide number. The source reads the device's `lineup.json` and opens the selected stream URL. An explicit URL is the supported discovery path: it works across network segments and is deterministic in local and CI tests. UDP broadcast discovery is deferred because it would add a LAN-only network dependency without improving the configured source path.

After a stream interruption, the source makes up to three reconnect attempts with a short cancellation-aware delay. It resolves the lineup once per read and preserves source-relative time across reconnects. `IngestDiagnostics.ReconnectAttempts` counts attempts across reads; source status and errors include the source ID but omit stream paths and query strings.
