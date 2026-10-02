#!/usr/bin/env bash
# Test double for TransportStreamInspectorTests: simulates a hung ffprobe subprocess that
# never produces output on stdout/stderr and never exits on its own, so tests can verify the
# configured timeout actually bounds the call (and doesn't just rely on a real process
# happening to be fast). Terminates only when killed.
sleep 300
