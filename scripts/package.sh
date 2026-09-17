#!/usr/bin/env bash
# Build the plugin and pack it into a ZIP that Jellyfin's plugin installer accepts.
#
#   scripts/package.sh 1.1.0.0
#
# Produces dist/jellyfin-sso_<version>.zip and writes the ZIP's MD5 checksum to
# dist/jellyfin-sso_<version>.zip.md5. Both the release workflow and a manual
# release use this script, so the artifact is identical either way.
set -euo pipefail

VERSION=${1:?usage: scripts/package.sh <version>   (four-part, e.g. 1.1.0.0)}

if [[ ! $VERSION =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "version must be four-part (1.1.0.0), got: $VERSION" >&2
    exit 1
fi

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
PLUGIN_SRC="$REPO_ROOT/Jellyfin.Plugin.Sso"
PUBLISH_DIR=$(mktemp -d)
DIST_DIR="$REPO_ROOT/dist"
ZIP="$DIST_DIR/jellyfin-sso_$VERSION.zip"

trap 'rm -rf "$PUBLISH_DIR"' EXIT

# Dependencies Jellyfin does not ship itself; they have to live next to the plugin.
EXTRA_DLLS=(
    IdentityModel.dll
    Microsoft.IdentityModel.Abstractions.dll
    Microsoft.IdentityModel.JsonWebTokens.dll
    Microsoft.IdentityModel.Logging.dll
    Microsoft.IdentityModel.Protocols.dll
    Microsoft.IdentityModel.Protocols.OpenIdConnect.dll
    Microsoft.IdentityModel.Tokens.dll
    System.IdentityModel.Tokens.Jwt.dll
)

dotnet publish "$PLUGIN_SRC" -c Release -o "$PUBLISH_DIR" \
    -p:Version="$VERSION" -p:AssemblyVersion="$VERSION" -p:FileVersion="$VERSION"

mkdir -p "$DIST_DIR"
rm -f "$ZIP"

# Jellyfin unpacks the ZIP straight into plugins/<name>_<version>/, so the DLLs
# must sit at the root of the archive.
( cd "$PUBLISH_DIR" && zip -q -X "$ZIP" Jellyfin.Plugin.Sso.dll "${EXTRA_DLLS[@]}" )

md5sum "$ZIP" | cut -d' ' -f1 > "$ZIP.md5"

echo "built $ZIP"
echo "md5   $(cat "$ZIP.md5")"
