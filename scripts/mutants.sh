#!/usr/bin/env bash
# Mutation testing: Stryker changes the code and watches whether the tests notice. A suite
# that stays green while the code is broken is worth nothing, and no ordinary test run
# shows that.
#
# This wrapper exists for the output path: Stryker writes to ./StrykerOutput next to the
# project, and takes a different one only on the command line — stryker-config.json has no
# setting for it. Everything a test run produces belongs under tests/runs/.
#
# What the score covers, because the number is quoted in the README and deserves its
# footnote: the unit suite only, and the files in stryker-config.json's `mutate` list.
# The controllers are not in it — an action method needs a server around it, which only the
# end-to-end suite provides, and that suite is not wired to Stryker at all. A
# review in November 2026 found thirteen defects and eight of them were in the part this
# number does not measure, which is worth knowing before trusting it. The honest way to
# widen it is to keep lifting decisions out of the actions into classes a unit test can
# call — OutboundAddresses, LogoutTokenLedger, the discovery host check, and then
# SsoAddress, SsoCookies and SelfServiceLinks when the controller was split up, each came
# out that way, and each is in the list now.
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
