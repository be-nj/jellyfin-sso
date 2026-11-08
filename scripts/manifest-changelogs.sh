#!/usr/bin/env bash
# Fill every version in the manifest with the notes of its GitHub release.
#
#   scripts/manifest-changelogs.sh
#
# Jellyfin shows this field under each version in the catalog, rendered as Markdown
# inside a collapsed accordion — jellyfin-web's PluginRevisions.tsx passes it to
# MarkdownBox. So the whole release body belongs here: links work, and a collapsed
# accordion costs the reader nothing.
#
# Every entry carried "See the release notes for v1.12.20." and nothing else for five
# releases, because release.yml wrote that string by hand while creating the release with
# --generate-notes twenty-five lines earlier. This reads the notes that were there all
# along. Idempotent, so it can be run again after a release body is edited.
#
# A manifest version is four-part and its tag is three (1.12.20.0 is v1.12.20).
set -euo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
MANIFEST="$REPO_ROOT/manifest.json"

changed=0

for version in $(jq -r '.[0].versions[].version' "$MANIFEST"); do
    tag="v${version%.*}"

    if ! body=$(gh release view "$tag" --json body --jq '.body' 2>/dev/null); then
        echo "  skip $version — no release $tag" >&2
        continue
    fi

    # A release with an empty body would otherwise replace real notes with nothing, and
    # Jellyfin's own "No changelog" is more honest than a sentence pointing nowhere.
    if [ -z "${body//[[:space:]]/}" ]; then
        body="See the release notes: https://github.com/be-nj/jellyfin-sso/releases/tag/$tag"
    fi

    before=$(jq -r --arg v "$version" \
        '.[0].versions[] | select(.version == $v) | .changelog' "$MANIFEST")

    if [ "$before" = "$body" ]; then
        printf '  ok   %-12s unchanged\n' "$version"
        continue
    fi

    tmp=$(mktemp)
    jq --arg v "$version" --arg body "$body" \
        '(.[0].versions[] | select(.version == $v) | .changelog) = $body' \
        "$MANIFEST" > "$tmp"
    mv "$tmp" "$MANIFEST"

    printf '  set  %-12s %s characters from %s\n' "$version" "${#body}" "$tag"
    changed=$((changed + 1))
done

# The same file under a short name, the way update-manifest.sh keeps it.
cp "$MANIFEST" "$REPO_ROOT/m.json"

echo
echo "$changed changelog(s) written"
