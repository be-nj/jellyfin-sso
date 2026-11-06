#!/usr/bin/env bash
# Stand up the OpenID Foundation conformance suite and give it a certificate the plugin
# will accept.
#
# The suite runs from the prebuilt images in the OpenID Foundation's container registry,
# so nothing is compiled here. It serves itself at https://localhost.emobix.co.uk:8443,
# a name that resolves to 127.0.0.1 in public DNS, so no hosts file has to be edited.
#
# One thing has to be replaced. The suite's nginx image ships a certificate for
# "CN=localhost" with no subject alternative name at all, which no current TLS stack
# accepts for any hostname — and the plugin's discovery client refuses plain HTTP off
# loopback, so it cannot sidestep TLS either. The certificate the end-to-end stack mints
# is given to the suite instead: it is already mounted into the Jellyfin container as a
# trusted CA, and TestCertificate puts the suite's hostname on it.
#
# Run `make stack` first, in another terminal. This needs the certificate it mints.
set -euo pipefail
cd "$(dirname "$0")"

REPO=$(cd ../.. && pwd)
CERTS="$REPO/tests/runs/e2e/certs"
WORK="$REPO/tests/runs/conformance"

if [ ! -f "$CERTS/idp.crt" ]; then
    echo "No certificate at $CERTS/idp.crt — run 'make stack' first." >&2
    exit 1
fi

mkdir -p "$WORK/tls"
cp "$CERTS/idp.crt" "$WORK/tls/suite.crt"
cp "$CERTS/idp.key" "$WORK/tls/suite.key"

cd "$WORK"
cp "$REPO/tests/conformance/docker-compose.override.yml" .
[ -f docker-compose-prebuilt.yml ] || curl -sO \
    https://gitlab.com/openid/conformance-suite/-/raw/master/docker-compose-prebuilt.yml

docker compose -f docker-compose-prebuilt.yml -f docker-compose.override.yml pull
docker compose -f docker-compose-prebuilt.yml -f docker-compose.override.yml up -d

echo "waiting for the suite…"
for _ in $(seq 1 60); do
    code=$(curl -s --cacert tls/suite.crt -o /dev/null -w '%{http_code}' \
        https://localhost.emobix.co.uk:8443/api/runner/available 2>/dev/null || true)
    if [ "$code" = "200" ]; then
        echo "the suite answers over TLS the plugin trusts: HTTP $code"
        echo
        echo "now run, from the repository root:"
        echo "  python3 -I tests/conformance/drive.py        # create the plan"
        echo "  python3 -I tests/conformance/run_all.py      # the 28 login modules"
        echo "  python3 -I tests/conformance/run_discovery.py"
        echo "  python3 -I tests/conformance/run_logout.py"
        exit 0
    fi
    sleep 5
done

echo "the suite did not come up; check 'docker compose logs' in $WORK" >&2
exit 1
