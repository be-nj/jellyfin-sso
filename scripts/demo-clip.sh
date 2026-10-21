#!/usr/bin/env bash
# Turns the raw Playwright recordings into the files the documentation uses:
#
#   docs/img/login-flow.gif             GitHub renders this inline in the README
#   docs/img/login-flow.mp4             smaller and sharper, for anything that plays video
#   docs/img/login-flow-keycloak.{gif,mp4}   the same login with the other two providers,
#   docs/img/login-flow-authelia.{gif,mp4}   shown side by side under the first one
#
# Run `make demo`, which records the clips first. The webm files stay in tests/runs/demo/.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
clips="$root/tests/runs/demo"
out="$root/docs/img"

command -v ffmpeg >/dev/null || { echo "ffmpeg is not installed" >&2; exit 1; }

mkdir -p "$out"

# Authentik is the login the README shows; the other two are linked from it.
written=()

mp4() {
  local clip=$1 target=$2
  # H.264 in a faststart container: plays in a browser without downloading the whole file.
  ffmpeg -y -loglevel error -i "$clip" \
    -movflags +faststart -pix_fmt yuv420p -c:v libx264 -crf 24 -preset slow \
    -vf "scale=1280:-2" \
    "$target"
  written+=("$target")
}

gif() {
  local clip=$1 target=$2 width=$3 fps=$4 speed=${5:-0.8}
  # A README GIF has to stay small enough that the page still loads on a phone: a
  # reduced frame rate, a slightly brisker run than the recording, the width it is
  # actually displayed at, and a palette generated from the clip itself rather than the
  # web-safe one, which turns Jellyfin's dark surfaces into bands.
  #
  # The speed is on the GIF alone. The MP4 beside it keeps the pace a person typed at,
  # which is what somebody who clicks "sharper version" came to see.
  local palette
  palette="$(mktemp -t demo-palette-XXXXXX.png)"

  local filters="setpts=$speed*PTS,fps=$fps,scale=$width:-2:flags=lanczos"
  # Three GIF-specific economies, worth about 40 % together on the clip the README
  # embeds, and none of them touches the part a reader is looking at:
  #
  #   max_colors=32   a login page is dark chrome, white panels and one photograph
  #   stats_mode=diff builds the palette from what moves, not from the still backdrop
  #   diff_mode=rect  writes only the changed rectangle of each frame
  #
  # The Bayer dither stays: without it the sky behind Authentik's form bands visibly,
  # and that costs more in how finished the clip looks than the 40 KB it saves.
  ffmpeg -y -loglevel error -i "$clip" \
    -vf "$filters,palettegen=max_colors=32:stats_mode=diff" "$palette"
  ffmpeg -y -loglevel error -i "$clip" -i "$palette" \
    -lavfi "$filters[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle" \
    "$target"
  rm -f "$palette"
  written+=("$target")
}

for provider in authentik keycloak authelia; do
  clip="$clips/login-flow-$provider.webm"
  if [ ! -f "$clip" ]; then
    echo "no recording at ${clip#"$root/"} — skipping $provider" >&2
    continue
  fi

  if [ "$provider" = authentik ]; then
    mp4 "$clip" "$out/login-flow.mp4"
    gif "$clip" "$out/login-flow.gif" 680 4 0.55
  else
    # Half the width: these two stand side by side under the first one, and three
    # full-size GIFs would be more than a README should ask a phone to download.
    mp4 "$clip" "$out/login-flow-$provider.mp4"
    gif "$clip" "$out/login-flow-$provider.gif" 420 4 0.55
  fi
done

[ ${#written[@]} -gt 0 ] || { echo "nothing to convert — run 'make demo' first" >&2; exit 1; }

for file in "${written[@]}"; do
  printf '%s  %s\n' "$(du -h "$file" | cut -f1)" "${file#"$root/"}"
done
