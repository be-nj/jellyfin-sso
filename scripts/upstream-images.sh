#!/usr/bin/env bash
# Which images a run actually pulled, by digest, as a job summary.
#
# "The latest Keycloak broke it" is only useful with a digest attached: tags move, and the
# run that failed has to be able to say which bytes it ran. Testcontainers pulls through
# the host daemon, so the daemon knows.
set -euo pipefail

echo "### Images this run pulled"
echo
docker images --digests --format '- `{{.Repository}}:{{.Tag}}` — `{{.Digest}}`' \
  | grep -v '<none>:<none>' \
  | sort \
  || echo '- none recorded'
