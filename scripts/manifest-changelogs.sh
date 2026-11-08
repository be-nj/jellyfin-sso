#!/usr/bin/env bash
# Copy every version's CHANGELOG.md section into the manifest.
#
#   scripts/manifest-changelogs.sh
#
# Jellyfin shows this field under each version in the catalog, rendered as Markdown inside
# a collapsed accordion — jellyfin-web's PluginRevisions.tsx passes it to MarkdownBox.
#
# The source is CHANGELOG.md and not the GitHub release body, which this script used to
# read. A release body is the long form: 1.12.19's runs to 6429 characters across five
# headings, because the reasoning behind twenty findings belongs somewhere. The catalog is
# not that somewhere — it is read by somebody deciding whether to click Install — so the
# two are written separately and CHANGELOG.md is the short one.
#
# Idempotent, so it is also how an edited section reaches the manifest.
set -euo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
MANIFEST="$REPO_ROOT/manifest.json"

changed=0
missing=0

for version in $(jq -r '.[0].versions[].version' "$MANIFEST"); do
    if ! section=$("$REPO_ROOT/scripts/changelog-section.sh" "$version"); then
        missing=$((missing + 1))
        continue
    fi

    before=$(jq -r --arg v "$version" \
        '.[0].versions[] | select(.version == $v) | .changelog // ""' "$MANIFEST")

    if [ "$before" = "$section" ]; then
        printf '  ok   %-12s unchanged, %s characters\n' "$version" "${#section}"
        continue
    fi

    tmp=$(mktemp)
    jq --arg v "$version" --arg body "$section" \
        '(.[0].versions[] | select(.version == $v) | .changelog) = $body' \
        "$MANIFEST" > "$tmp"
    mv "$tmp" "$MANIFEST"

    printf '  set  %-12s %s characters\n' "$version" "${#section}"
    changed=$((changed + 1))
done

# The same file under a short name, the way update-manifest.sh keeps it.
cp "$MANIFEST" "$REPO_ROOT/m.json"

echo
echo "$changed changelog(s) written"

if (( missing > 0 )); then
    echo "$missing version(s) have no CHANGELOG.md section" >&2
    exit 1
fi
