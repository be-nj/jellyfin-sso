#!/usr/bin/env bash
# Pull every image a scheduled upstream run is about to use, before the tests start.
#
# The first upstream run failed with nine words of Docker API error inside three test
# failures — "manifest unknown", because authentik publishes no :latest tag on ghcr. That
# is a mistake in this workflow, not news about the plugin, and it should not look like a
# regression. Pulling first separates the two: a tag that does not exist fails here, by
# name, before a single container starts.
set -uo pipefail

missing=0
for image in "$@"; do
  echo "::group::docker pull $image"
  if docker pull --quiet "$image"; then
    echo "ok: $image"
  else
    echo "::error::Could not pull $image. If the tag does not exist, the workflow names a tag the project does not publish — fix the workflow rather than looking for a plugin bug."
    missing=1
  fi
  echo "::endgroup::"
done

exit "$missing"
