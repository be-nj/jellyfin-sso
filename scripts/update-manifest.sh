#!/usr/bin/env bash
# Add a version entry to manifest.json, the file Jellyfin reads when the plugin
# repository is added in the dashboard.
#
#   scripts/update-manifest.sh <version> <checksum> <sourceUrl> [changelog]
#
# The target ABI is taken from the Jellyfin.Controller reference in the csproj,
# so it can never drift from what the plugin was compiled against. Newest
# version first; an existing entry for the same version is replaced.
set -euo pipefail

VERSION=${1:?usage: scripts/update-manifest.sh <version> <checksum> <sourceUrl> [changelog]}
CHECKSUM=${2:?missing checksum}
SOURCE_URL=${3:?missing sourceUrl}
CHANGELOG=${4:-}

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
MANIFEST="$REPO_ROOT/manifest.json"
CSPROJ="$REPO_ROOT/Jellyfin.Plugin.Sso/Jellyfin.Plugin.Sso.csproj"

CONTROLLER_VERSION=$(grep -oP '"Jellyfin\.Controller" Version="\K[^"]+' "$CSPROJ")
TARGET_ABI="$CONTROLLER_VERSION.0"

entry=$(jq -n \
    --arg version "$VERSION" \
    --arg checksum "$CHECKSUM" \
    --arg sourceUrl "$SOURCE_URL" \
    --arg targetAbi "$TARGET_ABI" \
    --arg changelog "$CHANGELOG" \
    --arg timestamp "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" \
    '{version: $version, changelog: $changelog, targetAbi: $targetAbi,
      sourceUrl: $sourceUrl, checksum: $checksum, timestamp: $timestamp}')

tmp=$(mktemp)
jq --argjson entry "$entry" --arg version "$VERSION" \
    '.[0].versions = ([$entry] + (.[0].versions | map(select(.version != $version))))' \
    "$MANIFEST" > "$tmp"
mv "$tmp" "$MANIFEST"

# The same file under a short name, so the URL people type into Jellyfin can be
# jf.be-nj.net/m.json. Generated rather than maintained, so the two cannot drift.
cp "$MANIFEST" "$REPO_ROOT/m.json"

echo "manifest.json (and its m.json copy) now list $VERSION (targetAbi $TARGET_ABI)"
