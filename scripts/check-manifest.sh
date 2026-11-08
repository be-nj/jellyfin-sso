#!/usr/bin/env bash
# Checks that the plugin manifest says what it ought to: every version points at a release
# asset of this repository, and every checksum is the checksum of the file at that address.
#
#   scripts/check-manifest.sh
#
# The manifest is what Jellyfin servers poll, and nothing else in this repository looked at
# it. One commit changing a sourceUrl and its checksum would have redirected every polling
# server to an arbitrary ZIP: the branch rules ask for a pull request but for no approving
# review, and none of the required checks read this file. The build attestations exist, but
# nothing in the install path consults them. So this is the second record of the truth.
#
# Needs curl and md5sum. Downloads each asset, so it is a job of its own rather than a step
# somebody runs on every push.
set -uo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
MANIFEST="$REPO_ROOT/manifest.json"
PREFIX="https://github.com/be-nj/jellyfin-sso/releases/download/"
failures=0

if ! diff -q "$MANIFEST" "$REPO_ROOT/m.json" >/dev/null; then
    echo "::error::m.json is not a copy of manifest.json" >&2
    failures=$((failures + 1))
fi

count=$(jq '.[0].versions | length' "$MANIFEST")
echo "== $count version(s) in the manifest"

for i in $(seq 0 $((count - 1))); do
    version=$(jq -r ".[0].versions[$i].version" "$MANIFEST")
    url=$(jq -r ".[0].versions[$i].sourceUrl" "$MANIFEST")
    stated=$(jq -r ".[0].versions[$i].checksum" "$MANIFEST")

    if [[ $url != "$PREFIX"* ]]; then
        echo "::error::$version points outside this repository's releases: $url" >&2
        failures=$((failures + 1))
        continue
    fi

    # The catalog text has to be the CHANGELOG.md section, character for character.
    # Editing one and not the other is the whole failure mode: the manifest is generated,
    # and a generated file that somebody has touched by hand stops being a record of
    # anything. Checking equality also covers the emptiness this used to check for,
    # because a section that exists is not empty.
    changelog=$(jq -r ".[0].versions[$i].changelog // \"\"" "$MANIFEST")
    if ! section=$("$REPO_ROOT/scripts/changelog-section.sh" "$version" 2>/dev/null); then
        echo "::error::$version has no section in CHANGELOG.md" >&2
        failures=$((failures + 1))
    elif [[ $changelog != "$section" ]]; then
        echo "::error::$version does not carry its CHANGELOG.md section — run scripts/manifest-changelogs.sh" >&2
        failures=$((failures + 1))
    fi

    actual=$(curl -fsSL "$url" | md5sum | cut -d' ' -f1)
    if [[ -z $actual ]]; then
        echo "::error::$version could not be downloaded from $url" >&2
        failures=$((failures + 1))
    elif [[ $actual != "$stated" ]]; then
        echo "::error::$version says $stated but the asset is $actual" >&2
        failures=$((failures + 1))
    else
        printf '  ok   %-12s %s\n' "$version" "$stated"
    fi
done

echo
if (( failures > 0 )); then
    echo "$failures problem(s) with the manifest" >&2
    exit 1
fi

echo "every version points at this repository and matches its asset"
