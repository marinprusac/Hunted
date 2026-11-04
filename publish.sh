#!/usr/bin/env bash
# Builds Hunted in Release mode and packages it for Thunderstore. With --publish it also uploads the package.
#
#   ./publish.sh             builds dist/Prussian-Hunted-<version>.zip only (nothing is uploaded)
#   ./publish.sh --publish   builds it and uploads it
#
# The version is read from Hunted.csproj. A version can only be uploaded once and has to be higher than the last one.
# The upload token comes from $TCLI_AUTH_TOKEN or from ~/.config/hunted/thunderstore-token (outside the repo, never commit it).
set -euo pipefail
cd "$(dirname "$0")"
export PATH="$PATH:$HOME/.dotnet/tools"

version=$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' Hunted.csproj | head -1)
[ -n "$version" ] || { echo "No <Version> in Hunted.csproj" >&2; exit 1; }


sed -i "s|\(\[BepInPlugin(\"Prussian.Hunted\", \"Hunted\", \"\)[^\"]*|\1$version|" Hunted.cs
sed -i "s|^versionNumber = .*|versionNumber = \"$version\"|" thunderstore.toml
grep -q "^## $version\b" CHANGELOG.md || echo "Warning: CHANGELOG.md has no '## $version' heading." >&2

dotnet build -c Release -nologo -v q
rm -rf dist
tcli build --config-path thunderstore.toml
zip="dist/Prussian-Hunted-$version.zip"
ls -l "$zip"

if [ "${1:-}" = "--publish" ]; then
  token="${TCLI_AUTH_TOKEN:-}"
  [ -n "$token" ] || { [ -f "$HOME/.config/hunted/thunderstore-token" ] && token=$(<"$HOME/.config/hunted/thunderstore-token"); }
  [ -n "$token" ] || { echo "No token: set TCLI_AUTH_TOKEN or write it to ~/.config/hunted/thunderstore-token" >&2; exit 1; }
  tcli publish --config-path thunderstore.toml --file "$zip" --token "$token"
fi
