#!/usr/bin/env bash
# Print the CHANGELOG.md section for one version, without its heading.
#
#   scripts/changelog-section.sh 1.12.20.0
#   scripts/changelog-section.sh 1.12.20
#
# A manifest version is four-part and the heading is three (1.12.20.0 is "## 1.12.20").
# The heading itself is left out: Jellyfin already shows the version on the accordion that
# the text appears under, so repeating it there wastes the first line.
#
# Exits non-zero when there is no section, which is what lets the release fail rather than
# publish a version with nothing to read.
set -euo pipefail

VERSION=${1:?usage: scripts/changelog-section.sh <version>}

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
CHANGELOG="$REPO_ROOT/CHANGELOG.md"

# Four-part versions lose their last component; a three-part one is already the heading.
short="$VERSION"
if [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    short="${VERSION%.*}"
fi

section=$(awk -v want="## $short" '
    $0 == want { inside = 1; next }
    inside && /^## / { exit }
    inside { print }
' "$CHANGELOG")

# Strip the blank lines awk leaves at both ends of a section.
section=$(printf '%s\n' "$section" | sed -e '/./,$!d' -e ':a' -e '/^\n*$/{$d;N;ba' -e '}')

if [ -z "${section//[[:space:]]/}" ]; then
    echo "CHANGELOG.md has no section for $short" >&2
    exit 1
fi

printf '%s\n' "$section"
