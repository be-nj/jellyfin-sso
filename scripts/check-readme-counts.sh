#!/usr/bin/env bash
# The README states how many tests there are, in four places. Those numbers drift the
# moment somebody adds a test and does not think about the README — which is exactly what
# happened: the table said 354 and 85 while the comparison below it still said 341 and 84,
# and both were weeks out of date.
#
# So the numbers are checked against the suites that produce them. Run from the repository
# root, with the .NET SDK on the path.
set -euo pipefail

unit_project=tests/Jellyfin.Plugin.Sso.Tests
e2e_project=tests/Jellyfin.Plugin.Sso.E2eTests

# The suite CI runs on every push leaves out the tiers that need somebody's attention:
# real providers, the demo recording, the screenshots and the manual stack.
ci_filter='Category!=RealIdp&Category!=Screenshots&Category!=Demo&Category!=Manual'

count() {
  local project=$1
  shift
  dotnet test "$project" -c Release --list-tests "$@" 2>/dev/null \
    | grep -cE '^    [A-Za-z]' || true
}

echo "Counting…" >&2
unit=$(count "$unit_project")
e2e_ci=$(count "$e2e_project" --filter "$ci_filter")
# Everything the suite will run unattended. The manual stack is in the same project but
# is not a test: it stands the environment up and waits for a person.
e2e_all=$(count "$e2e_project" --filter 'Category!=Manual')
real=$(count "$e2e_project" --filter 'Category=RealIdp')

stated() {
  grep -oE "$1" README.md | head -1 | grep -oE '[0-9]+' | head -1
}

said_unit=$(stated '\*\*[0-9]+ unit tests\*\*')
said_e2e=$(stated '\*\*[0-9]+ end-to-end tests\*\*')
said_real=$(stated '\*\*[0-9]+ real-provider logins\*\*')
said_sum=$(grep -oE '\| [0-9]+ \+ [0-9]+ \|' README.md | head -1 | grep -oE '[0-9]+ \+ [0-9]+' || true)

fail=0
check() {
  local what=$1 said=$2 actual=$3
  if [ "$said" != "$actual" ]; then
    echo "::error::README says $said $what; the suite has $actual." >&2
    fail=1
  else
    echo "ok: $what $actual" >&2
  fi
}

check "unit tests" "$said_unit" "$unit"
check "end-to-end tests" "$said_e2e" "$e2e_ci"
check "real-provider logins" "$said_real" "$real"
check "unit + end-to-end cases" "$said_sum" "$unit + $e2e_all"

exit "$fail"
