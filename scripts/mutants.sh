#!/usr/bin/env bash
# Mutation testing: Stryker changes the code and watches whether the tests notice. A suite
# that stays green while the code is broken is worth nothing, and no ordinary test run
# shows that.
#
# This wrapper exists for the output path: Stryker writes to ./StrykerOutput next to the
# project, and takes a different one only on the command line — stryker-config.json has no
# setting for it. Everything a test run produces belongs under tests/runs/.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/tests/runs/stryker"

command -v dotnet-stryker >/dev/null \
  || { echo "dotnet-stryker is not installed — dotnet tool install -g dotnet-stryker" >&2; exit 1; }

mkdir -p "$out"
cd "$root/Jellyfin.Plugin.Sso"

exec dotnet-stryker \
  --config-file "$root/stryker-config.json" \
  --output "$out" \
  "$@"
