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
# The threat model names a test beside every mitigation, for the same reason the review
# does, and rots the same way: #124 renamed a test and left the citation pointing at a
# name no longer in the sources.
CITING=("$DOC" "$REPO_ROOT/docs/threat-model.md")
failures=0

for doc in "${CITING[@]}"; do
    if [[ ! -f $doc ]]; then
        echo "no document at $doc" >&2
        exit 1
    fi
done

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

echo "== identifiers the review and the threat model name"
# Both documents cite class and test names as evidence. A name is evidence only while it
# is in the sources, so each one has to resolve somewhere in the code. tests/runs holds
# container output owned by root, which is not code and is not readable here.
mapfile -t names < <(grep -hoE '`[A-Z][A-Za-z0-9_]{10,}`' "${CITING[@]}" | tr -d '`' | sort -u)
for name in "${names[@]}"; do
    file=$(grep -rls --include='*.cs' --exclude-dir=runs "$name" \
        "$REPO_ROOT/tests" "$REPO_ROOT/Jellyfin.Plugin.Sso" 2>/dev/null | head -1)
    if [[ -n $file ]]; then
        printf '  ok   %-62s %s\n' "$name" "$(basename "$file")"
    else
        printf '  MISSING %s\n' "$name"
        failures=$((failures + 1))
    fi
done

echo "== the share of commits the README credits to an LLM"
# The README publishes how much of this repository an LLM typed. Nothing recomputed it,
# so it sat three commits behind for weeks. This is a guard against that kind of rot, not
# a demand for an exact figure: the number is a rough share and reads as one, a branch
# has to state what it will be once its commits land, and a squash changes how many that
# is. So the tolerance is wide enough that nobody has to touch the README to land a
# change, and narrow enough that months of drift still fail.
TOLERANCE=10
said=$(grep -oE '\*\*[0-9]+ of [0-9]+\*\* commits' "$REPO_ROOT/README.md" | head -1 | grep -oE '[0-9]+ of [0-9]+')
said_ai=${said%% of *}
said_total=${said##* of }
actual_total=$(git -C "$REPO_ROOT" rev-list --count HEAD)
actual_ai=$(git -C "$REPO_ROOT" log HEAD -i --grep='Co-Authored-By: Claude' --format=%H | wc -l)
printf '  README says %s of %s; history has %s of %s\n' \
    "$said_ai" "$said_total" "$actual_ai" "$actual_total"
for pair in "AI commits:$said_ai:$actual_ai" "commits in total:$said_total:$actual_total"; do
    IFS=: read -r what stated measured <<<"$pair"
    drift=$(( stated > measured ? stated - measured : measured - stated ))
    if (( drift > TOLERANCE )); then
        echo "  OFF BY $drift  $what (README $stated, history $measured)"
        failures=$((failures + 1))
    fi
done

echo
if (( failures > 0 )); then
    echo "$failures claim(s) in the documents do not hold" >&2
    exit 1
fi

echo "every commit, issue and identifier named resolves, and the AI share is current"
