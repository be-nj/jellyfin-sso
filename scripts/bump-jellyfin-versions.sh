#!/usr/bin/env bash
# Rewrite jellyfin-versions.json as the floor plus the newest servers Jellyfin has
# released.
#
#   scripts/bump-jellyfin-versions.sh
#
# The floor is the oldest server the plugin claims, taken from the Jellyfin.Controller
# reference so it cannot drift from targetAbi. JELLYFIN_NEWEST (default 2) sets how many
# current servers join it.
#
# Why a script and a pull request instead of resolving the newest version inside the test
# workflow: a matrix that reads "newest" live turns somebody else's Tuesday release into
# a red pull request on a branch nobody touched. Upstream.yml makes the same argument for
# pinning its containers. So the pin stays, and this moves it — through review, like the
# release manifest does.
#
# Prints nothing but a verdict; the caller decides whether to open the pull request.
set -euo pipefail

NEWEST=${JELLYFIN_NEWEST:-2}

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
VERSIONS="$REPO_ROOT/jellyfin-versions.json"
CSPROJ="$REPO_ROOT/Jellyfin.Plugin.Sso/Jellyfin.Plugin.Sso.csproj"

controller=$(grep -oP '"Jellyfin\.Controller" Version="\K[^"]+' "$CSPROJ")
floor="${controller%.*}"

mapfile -t released < <("$REPO_ROOT/scripts/jellyfin-minors.sh")

# The floor has to be in the list even if it is old enough to have scrolled off the end
# of "newest": it is the version the catalog promises, so it is the version a job proves.
wanted=$(printf '%s\n' "$floor" "${released[@]: -$NEWEST}" | sort -V -u)

before=$(jq -c '.' "$VERSIONS")
after=$(printf '%s\n' "$wanted" | jq -R . | jq -sc .)

if [ "$before" = "$after" ]; then
    echo "jellyfin-versions.json is current: $before"
    echo "changed=false" >> "${GITHUB_OUTPUT:-/dev/null}"
    exit 0
fi

printf '%s\n' "$wanted" | jq -R . | jq -s . > "$VERSIONS"

echo "jellyfin-versions.json: $before -> $after"
echo "servers Jellyfin has released: ${released[*]}"
{
    echo "changed=true"
    echo "before=$before"
    echo "after=$after"
} >> "${GITHUB_OUTPUT:-/dev/null}"
