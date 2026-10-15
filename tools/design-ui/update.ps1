# Regenerates docs/design-ui from the code:
#   tokens.json   from src/Ww.ExiledData.App/Theme/Tokens.axaml (via ThemeTokensTests with UPDATE_DESIGN_UI=1)
#   screens/*.png from the app's --screenshot mode (opens windows on this desktop for a minute; do not click them)
# The app runs on an empty temporary data folder, so only sample data is shown and your sessions are never touched.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$screens = Join-Path $repo 'docs\design-ui\screens'

$env:UPDATE_DESIGN_UI = '1'
try {
    dotnet test --project (Join-Path $repo 'tests\Ww.ExiledData.Tests')
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed ($LASTEXITCODE)" }
}
finally { Remove-Item Env:UPDATE_DESIGN_UI -ErrorAction SilentlyContinue }

$data = Join-Path ([IO.Path]::GetTempPath()) ("ww-exileddata-design-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $data | Out-Null
New-Item -ItemType Directory -Force $screens | Out-Null
Get-ChildItem $screens -Filter *.png | Remove-Item
$env:WW_EXILEDDATA_HOME = $data
try {
    dotnet run --project (Join-Path $repo 'src\Ww.ExiledData.App') -- --screenshot $screens
    if ($LASTEXITCODE -ne 0) { throw "screenshot run failed ($LASTEXITCODE)" }
}
finally {
    Remove-Item Env:WW_EXILEDDATA_HOME -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
}
Write-Host "Updated $repo\docs\design-ui (tokens.json, $((Get-ChildItem $screens -Filter *.png).Count) screenshots)"
