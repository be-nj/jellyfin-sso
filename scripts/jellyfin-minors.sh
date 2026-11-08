#!/usr/bin/env bash
# Every Jellyfin server minor the plugin claims, oldest first, one per line.
#
#   scripts/jellyfin-minors.sh
#
# The major comes from the Jellyfin.Controller reference in the csproj, the same number
# targetAbi is built from — so this lists the servers the catalog offers the plugin to,
# not an opinion about which ones matter.
#
# Read from Jellyfin's own releases rather than from the container registry. Docker Hub
# carries 13737 tags for this image: release candidates, and a dated per-architecture tag
# for every build (12.3-amd64.20261023-013714). Paginating that to find five numbers took
# 138 requests and earned an HTTP 403 for the trouble. A release is also the thing being
# asked about — "which servers exist" is a question about Jellyfin, not about an image
# host — and the container tag for one is its name without the leading v.
#
# Pre-releases are excluded. 12.0 shipped seven release candidates, and none of them is a
# server somebody runs on purpose.
set -euo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
CSPROJ="$REPO_ROOT/Jellyfin.Plugin.Sso/Jellyfin.Plugin.Sso.csproj"

controller=$(grep -oP '"Jellyfin\.Controller" Version="\K[^"]+' "$CSPROJ")
major="${controller%%.*}"

# Newest first, so the major the plugin targets is on the first page for as long as it is
# the current one. One request; a hundred releases is a decade of this project's history.
found=$(gh api 'repos/jellyfin/jellyfin/releases?per_page=100' \
    --jq '.[] | select(.prerelease == false) | .tag_name' \
    | grep -E "^v${major}\.[0-9]+$" \
    | sed 's/^v//' \
    | sort -V -u || true)

[ -n "$found" ] || {
    echo "no released $major.x server versions found in jellyfin/jellyfin" >&2
    exit 1
}

printf '%s\n' "$found"
