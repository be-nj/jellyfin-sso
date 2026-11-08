#!/usr/bin/env bash
# Check jellyfin-versions.json — the server versions the pull requests test.
#
# The file is a JSON array of "major.minor" tags, lowest first. Two rules matter:
#
#   * The lowest entry is the oldest server the plugin claims to work on, and that claim
#     is already made in machine-readable form by targetAbi in the manifest, which comes
#     from the Jellyfin.Controller reference in the csproj. If the two disagree, one of
#     them is lying to users: either the catalog offers the plugin to a server nothing
#     ever tested, or the suite spends a job on a server the catalog hides it from.
#   * The list is sorted and unique, because it is also the order the matrix reports in,
#     and a duplicate silently halves what a job proves.
#
# Everything between the lowest and the highest entry is deliberately not tested here —
# the weekly Upstream run covers every 12.x there is. See CONTRIBUTING.md.
set -euo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
VERSIONS="$REPO_ROOT/jellyfin-versions.json"
CSPROJ="$REPO_ROOT/Jellyfin.Plugin.Sso/Jellyfin.Plugin.Sso.csproj"

fail() {
    echo "$1" >&2
    exit 1
}

jq -e 'type == "array"' "$VERSIONS" >/dev/null \
    || fail "jellyfin-versions.json is not a JSON array"

mapfile -t listed < <(jq -r '.[]' "$VERSIONS")

# One entry is legal and is the right answer for a major whose first minor is also its
# newest. Whether a second exists is a question for Jellyfin's releases, which this check
# deliberately does not ask: it gates every pull request, and a required check that calls
# somebody else's API fails when their API does.
[ "${#listed[@]}" -ge 1 ] \
    || fail "jellyfin-versions.json is empty"

for version in "${listed[@]}"; do
    [[ "$version" =~ ^[0-9]+\.[0-9]+$ ]] \
        || fail "'$version' is not a major.minor server tag"
done

sorted=$(printf '%s\n' "${listed[@]}" | sort -V -u)
if [ "$sorted" != "$(printf '%s\n' "${listed[@]}")" ]; then
    fail "jellyfin-versions.json must be sorted oldest first and hold no duplicates; got: ${listed[*]}"
fi

controller=$(grep -oP '"Jellyfin\.Controller" Version="\K[^"]+' "$CSPROJ")
floor="${controller%.*}"

[ "${listed[0]}" = "$floor" ] \
    || fail "the oldest tested server is ${listed[0]}, but the plugin is built against Jellyfin.Controller $controller, so the manifest offers it from $floor"

echo "server versions tested per pull request: ${listed[*]}"
echo "floor ${listed[0]} matches Jellyfin.Controller $controller (targetAbi $controller.0)"
