#!/usr/bin/env bash
# The newest authentik, as an image reference, printed as "image=<reference>".
#
# The other three projects publish a moving tag that means "newest": jellyfin/jellyfin,
# quay.io/keycloak/keycloak and ghcr.io/authelia/authelia all keep :latest current.
# authentik does not. ghcr.io/goauthentik/server has no :latest at all, and its :stable
# was last built in February 2022 — an authentik old enough that the API endpoint this
# suite waits for did not exist yet, which is what "Authentik never became ready: HTTP
# 404" in the 2026-10-30 run was. authentik's own compose file pins an exact version
# instead, and so does this: the newest published release, asked for by name.
set -euo pipefail

# One page is the newest thirty releases, which is more than enough to find the top one;
# --paginate would run the filter once per page and print one answer per page.
tag=$(gh api repos/goauthentik/authentik/releases --jq '
  [.[] | select(.draft == false) | select(.prerelease == false) | .tag_name]
  | map(sub("^version/"; ""))
  | first')

if [ -z "${tag:-}" ]; then
  echo "::error::Could not work out authentik's newest release from its GitHub releases." >&2
  exit 1
fi

echo "image=ghcr.io/goauthentik/server:${tag}"
