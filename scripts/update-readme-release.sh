#!/usr/bin/env bash
# Brings the two things the README says about this repository's own history up to date:
# the latest version with its month, and how many of the commits an LLM helped write.
#
#   scripts/update-readme-release.sh 1.12.23.0
#
# Run by the release, which commits the result together with the manifest. Both numbers
# were kept by hand before, and the second one was also checked on every pull request:
# the pull request that happened to push the drift past the allowed ten went red for a
# number it had nothing to do with, and whoever saw it had to edit the README to land an
# unrelated change. A number that can be computed is computed, at the one moment the
# README has to be right, which is when a version is published.
set -euo pipefail

version=${1:?usage: update-readme-release.sh <version>}
# The manifest's version has four parts, the tag people see has three.
shown=${version%.0}
[[ $shown =~ ^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$ ]] || { echo "not a version: $version" >&2; exit 1; }

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
README="$REPO_ROOT/README.md"

total=$(git -C "$REPO_ROOT" rev-list --count HEAD)
ai=$(git -C "$REPO_ROOT" log HEAD -i --grep='Co-Authored-By: Claude' --format=%H | wc -l)
month=$(date -u +%Y-%m)

row='^\| \*\*This one\*\* \|'
grep -qE "$row" "$README" || { echo "README has no row for this plugin in the comparison table" >&2; exit 1; }

sed -E -i \
    -e "/$row/ s/v[0-9]+(\.[0-9]+){2,3} · [0-9]{4}-[0-9]{2}/v$shown · $month/" \
    -e "/$row/ s/\*\*[0-9]+ of [0-9]+\*\* commits/**$ai of $total** commits/" \
    "$README"

# Said rather than assumed: a README whose wording changed would be left as it was.
grep -E "$row" "$README" | grep -qF "v$shown · $month" \
    || { echo "the README's latest version was not updated" >&2; exit 1; }
grep -E "$row" "$README" | grep -qF "**$ai of $total** commits" \
    || { echo "the README's commit share was not updated" >&2; exit 1; }

echo "README: v$shown · $month, $ai of $total commits"
