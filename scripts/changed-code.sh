#!/usr/bin/env bash
# Prints `code=true` when a change touches anything a test could fail on, `code=false`
# when it is Markdown and nothing else. Written for GITHUB_OUTPUT; run it by hand with a
# base revision to see what CI would decide.
#
# Markdown is the only thing treated as harmless. Images, the manifest, scripts and the
# workflows themselves all count as code, because each of them has broken a build here
# before.
set -euo pipefail

base="${1:-}"

# No usable base — a new branch, a force push, the very first commit — means the change
# cannot be read, and an unreadable change is treated as code.
if [[ -z "$base" || "$base" == 0000000000000000000000000000000000000000 ]] \
  || ! git cat-file -e "$base^{commit}" 2>/dev/null; then
  echo "code=true"
  exit 0
fi

files=$(git diff --name-only "$base" HEAD)

if [[ -z "$files" ]]; then
  echo "code=false"
  exit 0
fi

while IFS= read -r file; do
  case "$file" in
    *.md | LICENSE) ;;
    *)
      echo "code=true"
      exit 0
      ;;
  esac
done <<<"$files"

echo "code=false"
