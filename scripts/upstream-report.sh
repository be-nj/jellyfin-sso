#!/usr/bin/env bash
# Turn a failed scheduled upstream run into an issue, or a comment on the open one.
#
# Reads JELLYFIN_RESULT, PROVIDERS_RESULT and RUN_URL from the workflow, and needs a token
# that may write issues.
set -euo pipefail

title='Weekly upstream run is failing'

broken=()
[ "${JELLYFIN_RESULT}" = "success" ] || broken+=("the newest Jellyfin (\`${JELLYFIN_RESULT}\`)")
[ "${PROVIDERS_RESULT}" = "success" ] || broken+=("the newest Authentik, Keycloak and Authelia (\`${PROVIDERS_RESULT}\`)")

open=$(gh issue list --state open --search "\"${title}\" in:title" --json number --jq '.[0].number // empty')

if [ ${#broken[@]} -eq 0 ]; then
  # Nothing to report, except to an issue that is still open: the run going green again
  # is the most useful thing that issue can be told.
  if [ -n "$open" ]; then
    gh issue comment "$open" --body "The weekly upstream run passed again: ${RUN_URL}

Leaving this open on purpose — it is closed by whoever establishes what changed, with the
cause written down. If the break was upstream and has been reverted there, say so and
close it."
  fi
  exit 0
fi

list=''
for item in "${broken[@]}"; do
  list="${list}- ${item}"$'\n'
done

body="The scheduled run against current upstream images failed.

What failed:

${list}
Run, with the image digests in each job's summary: ${RUN_URL}

This is the same code the pull requests test; only the container images differ, so the
first question is which image moved. The pinned versions live next to the
\`TestImages.Resolve\` calls in \`tests/Jellyfin.Plugin.Sso.E2eTests\` — raising a pin is
how a fix for a real upstream change gets proved."

if [ -n "$open" ]; then
  gh issue comment "$open" --body "$body"
else
  gh issue create --title "$title" --body "$body"
fi
