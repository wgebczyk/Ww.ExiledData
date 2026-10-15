#!/usr/bin/env bash
# Regenerates docs/design-ui from the code:
#   tokens.json   from src/Ww.ExiledData.App/Theme/Tokens.axaml (via ThemeTokensTests with UPDATE_DESIGN_UI=1)
#   screens/*.png from the app's --screenshot mode (opens windows on this desktop for a minute; do not click them)
# The app runs on an empty temporary data folder, so only sample data is shown and your sessions are never touched.
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
screens="$repo/docs/design-ui/screens"

UPDATE_DESIGN_UI=1 dotnet test --project "$repo/tests/Ww.ExiledData.Tests"

data="$(mktemp -d)"
trap 'rm -rf "$data"' EXIT
mkdir -p "$screens"
rm -f "$screens"/*.png
WW_EXILEDDATA_HOME="$data" dotnet run --project "$repo/src/Ww.ExiledData.App" -- --screenshot "$screens"
echo "Updated $repo/docs/design-ui (tokens.json, $(ls "$screens"/*.png | wc -l) screenshots)"
