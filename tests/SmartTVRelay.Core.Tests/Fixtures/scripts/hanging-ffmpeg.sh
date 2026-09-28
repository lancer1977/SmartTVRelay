#!/usr/bin/env bash
# Test double for FrameAudioSamplerTests: simulates a hung ffmpeg subprocess that never
# produces output on stdout and never exits on its own, so tests can verify cancellation
# actually bounds a genuine hang rather than only ever running against real, fast media.
sleep 300
