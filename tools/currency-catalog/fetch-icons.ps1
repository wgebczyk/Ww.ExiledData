#Requires -Version 7
<#
.SYNOPSIS
    Downloads the icon of every catalog item from poe2db into the local data folder.
.DESCRIPTION
    Keeps poe2db's paths (icons\Currency\CurrencyRerollRare.webp), so catalog.json's "image" is also the local path.
    Existing files are skipped; items that share an image share one file. The icons are game art: they stay local,
    never in the repo. Same behavior as fetch-icons.sh.
.PARAMETER Out
    Default: $env:WW_EXILEDDATA_HOME\icons, else %LocalAppData%\Ww.ExiledData\icons
    (~/.local/share/Ww.ExiledData/icons where there is no LocalAppData).
#>
param(
    [string]$Out,
    [string]$Catalog = (Join-Path $PSScriptRoot 'catalog.json'),
    [double]$Delay = 0.2
)
$ErrorActionPreference = 'Stop'

if (-not $Out) {
    $home_ = if ($env:WW_EXILEDDATA_HOME) { $env:WW_EXILEDDATA_HOME }
    elseif ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Ww.ExiledData' }
    else { Join-Path $HOME '.local/share/Ww.ExiledData' }
    $Out = Join-Path $home_ 'icons'
}

$base = 'https://cdn.poe2db.tw/image/Art/2DItems/'
$images = @(Get-Content -Raw -Encoding utf8 $Catalog | ConvertFrom-Json | ForEach-Object image | Where-Object { $_ } | Sort-Object -Unique -CaseSensitive)
Write-Host "$($images.Count) images -> $Out"

$fetched = 0; $skipped = 0; $failed = 0
foreach ($image in $images) {
    if ($image -match '^https?:') { $url = $image; $image = $image.Split('/')[-1] } else { $url = "$base$image" }
    $target = Join-Path $Out $image
    if ((Test-Path $target) -and (Get-Item $target).Length -gt 0) { $skipped++; continue }
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    # Download to a temporary name so an interrupted run never leaves a broken icon behind.
    try {
        Invoke-WebRequest -UserAgent 'Ww.ExiledData icon fetch (personal tool)' -Uri $url -OutFile "$target.part"
        Move-Item -Force "$target.part" $target
        $fetched++
    }
    catch {
        Remove-Item -ErrorAction Ignore "$target.part"
        Write-Warning "failed: $url ($($_.Exception.Message))"
        $failed++
    }
    Start-Sleep -Milliseconds ([int]($Delay * 1000))
}
Write-Host "fetched $fetched, already there $skipped, failed $failed"
if ($failed) { exit 1 }
