#!/usr/bin/env bash
# Checks that the review's accounting is real: every commit the review document names
# exists in this repository's history, every issue it names exists, and every test it
# credits with holding a fix is actually in the test sources. A list of findings that
# nobody resolves is a claim; this makes it a check.
#
#   scripts/check-review-audit.sh
#
# Commits are checked with git alone. Issues are checked with gh when it is available
# and authenticated; without it, that half is skipped and reported as skipped rather
# than silently passing.
set -uo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
DOC="$REPO_ROOT/docs/reviews/2026-10-19-security-and-quality.md"
failures=0

if [[ ! -f $DOC ]]; then
    echo "no review document at $DOC" >&2
    exit 1
fi

echo "== commits named in the review"
mapfile -t commits < <(grep -oE '`[0-9a-f]{7,40}`' "$DOC" | tr -d '`' | sort -u)
for commit in "${commits[@]}"; do
    if git -C "$REPO_ROOT" cat-file -e "$commit^{commit}" 2>/dev/null; then
        subject=$(git -C "$REPO_ROOT" log -1 --format=%s "$commit")
        printf '  ok   %s  %s\n' "$commit" "${subject:0:58}"
    else
        printf '  MISSING %s\n' "$commit"
        failures=$((failures + 1))
    fi
done

echo "== issues named in the review"
mapfile -t issues < <(grep -oE 'issues/[0-9]+' "$DOC" | cut -d/ -f2 | sort -un)
if ! command -v gh >/dev/null || ! gh auth status >/dev/null 2>&1; then
    echo "  skipped: gh is not available or not authenticated (${#issues[@]} issues unchecked)"
else
    for issue in "${issues[@]}"; do
        if state=$(gh issue view "$issue" --json state,title --jq '"\(.state) \(.title)"' 2>/dev/null); then
            printf '  ok   #%-3s %s\n' "$issue" "${state:0:62}"
        else
            printf '  MISSING #%s\n' "$issue"
            failures=$((failures + 1))
        fi
    done
fi

echo "== tests the review names"
mapfile -t tests < <(grep -oE '`[A-Z][A-Za-z0-9_]{10,}`' "$DOC" | tr -d '`' | sort -u)
for test in "${tests[@]}"; do
    if grep -rqs --include='*.cs' "$test" "$REPO_ROOT/tests"; then
        file=$(grep -rls --include='*.cs' "$test" "$REPO_ROOT/tests" | head -1)
        printf '  ok   %-62s %s\n' "$test" "$(basename "$file")"
    else
        printf '  MISSING %s\n' "$test"
        failures=$((failures + 1))
    fi
done

echo
if (( failures > 0 )); then
    echo "$failures reference(s) in the review do not resolve" >&2
    exit 1
fi

echo "every commit, issue and test the review names resolves"
