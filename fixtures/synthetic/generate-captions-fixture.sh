#!/usr/bin/env bash
# Generates fixtures/synthetic/captions-sample.mp4: a short synthetic video with an embedded
# text subtitle track (2 cues, matching CaptionExtractorTests' expected timestamps/text).
#
# MPEG-TS does not have a usable mapping for ffmpeg's text-subtitle codecs (confirmed
# empirically: muxing mov_text into a .ts container makes ffprobe report it as
# codec_type=data/codec_name=bin_data, not codec_type=subtitle -- so CaptionExtractor's
# `-map 0:s:0?` would never find it there). MP4 with mov_text is the container ffmpeg actually
# supports round-tripping a text subtitle stream through, so this fixture uses .mp4, not .ts.
#
# Usage: fixtures/synthetic/generate-captions-fixture.sh [outputPath]
set -euo pipefail

output_path="${1:-$(dirname "$0")/captions-sample.mp4}"
srt_path="$(mktemp --suffix=.srt)"
trap 'rm -f "$srt_path"' EXIT

cat > "$srt_path" << 'EOF'
1
00:00:01,000 --> 00:00:02,500
Hello from captions

2
00:00:03,000 --> 00:00:04,200
Second caption line
EOF

ffmpeg -y -v error \
  -f lavfi -i "color=c=black:s=320x240:d=5" \
  -i "$srt_path" \
  -map 0:v -map 1:s \
  -c:v libx264 -c:s mov_text \
  -t 5 \
  "$output_path"

echo "Generated captions fixture at $output_path"
