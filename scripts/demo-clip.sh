#!/usr/bin/env bash
# Turns the raw Playwright recording into the two files the documentation uses:
#
#   docs/img/login-flow.gif   GitHub renders this inline in the README
#   docs/img/login-flow.mp4   smaller and sharper, for anything that plays video
#
# Run `make demo`, which records the clip first. The webm stays in tests/runs/demo/.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
clip="$root/tests/runs/demo/login-flow.webm"
out="$root/docs/img"

[ -f "$clip" ] || { echo "no recording at $clip — run 'make demo' first" >&2; exit 1; }
command -v ffmpeg >/dev/null || { echo "ffmpeg is not installed" >&2; exit 1; }

mkdir -p "$out"

# H.264 in a faststart container: plays in a browser without downloading the whole file.
ffmpeg -y -loglevel error -i "$clip" \
  -movflags +faststart -pix_fmt yuv420p -c:v libx264 -crf 24 -preset slow \
  -vf "scale=1280:-2" \
  "$out/login-flow.mp4"

# A README GIF has to stay small enough that the page still loads on a phone: half the
# frame rate, 800px wide, and a palette generated from the clip itself rather than the
# web-safe one, which turns Jellyfin's dark surfaces into bands.
palette="$(mktemp -t demo-palette-XXXXXX.png)"
trap 'rm -f "$palette"' EXIT

filters="fps=10,scale=800:-2:flags=lanczos"
ffmpeg -y -loglevel error -i "$clip" -vf "$filters,palettegen=max_colors=64" "$palette"
ffmpeg -y -loglevel error -i "$clip" -i "$palette" \
  -lavfi "$filters[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=3" \
  "$out/login-flow.gif"

for file in "$out/login-flow.mp4" "$out/login-flow.gif"; do
  printf '%s  %s\n' "$(du -h "$file" | cut -f1)" "${file#"$root/"}"
done
