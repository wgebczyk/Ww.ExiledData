#Requires -Version 7
<#
.SYNOPSIS
    Refreshes catalog.json from poe2db: names, images and stack sizes; codes for new items.
.DESCRIPTION
    Existing codes are kept (keyed by category + url), so hand edits survive. overrides.json is applied first.
    New items get a generated 2-4 letter code, unique case-insensitively across all categories (plus the
    category's one-letter prefix). Must give the same result as update-catalog.sh.
.PARAMETER PagesDir
    Read <Page>.html from this folder instead of downloading (offline runs, tests).
.PARAMETER Catalog
    The catalog to update; defaults to catalog.json next to this script. A missing file starts from scratch.
#>
param(
    [string]$PagesDir,
    [string]$Catalog = (Join-Path $PSScriptRoot 'catalog.json'),
    [string]$Overrides = (Join-Path $PSScriptRoot 'overrides.json')
)
$ErrorActionPreference = 'Stop'

$UrlBase = 'https://poe2db.tw/us/'
$ImageBase = 'https://cdn.poe2db.tw/image/Art/2DItems/'
$UserAgent = 'Ww.ExiledData currency catalog (personal tool)'
# poe2db page -> category, code prefix
$Pages = [ordered]@{
    Stackable_Currency = 'Currency', ''
    Augment            = 'Augment', 'a'
    Omen               = 'Omen', 'o'
    # No prefix: the leading l comes from the "Liquid" marker (overrides.json), the same on the Currency page.
    Liquid_Emotions    = 'Emotions', ''
    Essence            = 'Essence', 'e'
    Catalysts          = 'Catalysts', 'c'
}
$Prefix = @{}; foreach ($p in $Pages.Values) { $Prefix[$p[0]] = $p[1] }
$Keep = 'StackableCurrency', 'SoulCore', 'Omen'
$Card = '<div class="d-flex border-top rounded"><div class="flex-shrink-0"><a class="([^"]*)"[^>]*href="([^"]+)"><img[^>]*src="([^"]+)"[^>]*></a></div><div class="flex-grow-1 ms-2"><a[^>]*>(?:<img[^>]*>)?([\s\S]*?)</a>([\s\S]*?)(?=<div class="d-flex border-top rounded"|<div class="col|$)'
$Stop = 'of', 'the', 'a', 'an', 'and'
$Tier = '^(Lesser|Greater|Perfect) (.+)$'

function Get-Text([string]$s) {
    $s = $s -replace '<[^>]+>', ' '
    $s = [regex]::Replace($s, '&(#x?[0-9a-fA-F]+|\w+);?', {
            param($m)
            $e = $m.Groups[1].Value
            if ($e[0] -eq '#') {
                $hex = $e[1] -eq 'x' -or $e[1] -eq 'X'
                return [char]::ConvertFromUtf32([Convert]::ToInt32($e.Substring($(if ($hex) { 2 } else { 1 })), $(if ($hex) { 16 } else { 10 })))
            }
            switch ($e) { 'amp' { '&' } 'lt' { '<' } 'gt' { '>' } 'quot' { '"' } 'apos' { "'" } 'nbsp' { ' ' } default { $m.Value } }
        })
    return ($s -replace '\s+', ' ').Trim()
}

function Strip([string]$s, [string]$base) { if ($s -and $s.StartsWith($base)) { $s.Substring($base.Length) } else { $s } }

# --- read pages -----------------------------------------------------------------------------------------------------
$fresh = [System.Collections.Generic.List[object]]::new()
foreach ($page in $Pages.Keys) {
    $category = $Pages[$page][0]
    if ($PagesDir) {
        $html = Get-Content -Raw -Encoding utf8 (Join-Path $PagesDir "$page.html")
    }
    else {
        $html = (Invoke-WebRequest -UserAgent $UserAgent -Uri "$UrlBase$page").Content
        Start-Sleep -Milliseconds 500
    }
    $seen = @{}; $count = 0
    foreach ($m in [regex]::Matches($html, $Card)) {
        $class = ($m.Groups[1].Value.Trim() -split '\s+')[-1]
        $href = $m.Groups[2].Value
        if ($seen.ContainsKey($href) -or $class -notin $Keep) { continue }
        $seen[$href] = $true
        $stack = $null
        if ($m.Groups[5].Value -match 'Stack Size: <span[^>]*>([\s\S]*?)</span>') {
            $max = ((Get-Text $Matches[1]) -split '/')[-1].Trim()
            if ($max -match '^\d+$') { $stack = [int]$max }
        }
        $fresh.Add([pscustomobject]@{
                name = Get-Text $m.Groups[4].Value; code = $null; category = $category
                url = Strip $href $UrlBase; image = Strip $m.Groups[3].Value $ImageBase; maxStackSize = $stack
            })
        $count++
    }
    Write-Host ("{0,-10} {1}" -f $category, $count)
}
if ($fresh.Count -eq 0) { throw 'No items found: the page layout may have changed.' }

# --- codes ----------------------------------------------------------------------------------------------------------
$old = if (Test-Path $Catalog) { @(Get-Content -Raw -Encoding utf8 $Catalog | ConvertFrom-Json) } else { @() }
$overrideMap = @{}
$markers = @()
if (Test-Path $Overrides) {
    $ov = Get-Content -Raw -Encoding utf8 $Overrides | ConvertFrom-Json
    $ov.PSObject.Properties | Where-Object Name -NotIn '_comment', 'markers' | ForEach-Object { $overrideMap[$_.Name] = $_.Value }
    # Marker phrases, longest first, so "Concentrated Liquid" wins over "Liquid".
    if ($ov.markers) {
        $markers = @($ov.markers.PSObject.Properties | Where-Object Name -NE '_comment' |
                Sort-Object @{ e = { -$_.Name.Length } }, @{ e = { $_.Name } } -CaseSensitive)
    }
}

$owner = @{}        # lower-case base code -> who holds it ("url:..", "name:..", "tier:base|t")
$codeByUrl = @{}    # url -> base code
$codeByName = @{}   # name -> base code
$tiersOf = @{}      # base name -> tier letters present
$overridden = @{}   # lower-case codes set by overrides.json

function Get-TierOf([string]$name) {
    if ($name -cmatch $Tier) { return @{ base = $Matches[2]; t = $Matches[1].Substring(0, 1).ToLower() } }
    return $null
}
function Get-Override([string]$name) {
    if ($overrideMap.ContainsKey($name)) { return $overrideMap[$name] }
    $t = Get-TierOf $name
    if ($t -and $overrideMap.ContainsKey($t.base)) { return $overrideMap[$t.base] + $t.t }
    return $null
}
function Claim([string]$code, [string]$who) {
    $k = $code.ToLower()
    if ($owner.ContainsKey($k) -and $owner[$k] -ne $who) { throw "Code '$code' for $who is already taken by $($owner[$k])." }
    $owner[$k] = $who
}

foreach ($it in $fresh) { if ($t = Get-TierOf $it.name) { $tiersOf[$t.base] = @($tiersOf[$t.base]) + $t.t | Where-Object { $_ } | Select-Object -Unique } }

# Overrides first: they win over everything, including codes already in the catalog.
foreach ($it in $fresh) {
    if ($o = Get-Override $it.name) { Claim $o "url:$($it.url)"; $overridden[$o.ToLower()] = $true; $codeByUrl[$it.url] = $o; if (-not (Get-TierOf $it.name)) { $codeByName[$it.name] = $o } }
}
foreach ($it in $old) {
    # A blank code asks for a new one.
    if (-not $it.code -or $codeByUrl.ContainsKey($it.url)) { continue }
    $p = $Prefix[$it.category]
    $base = if ($p -and $it.code.StartsWith($p)) { $it.code.Substring($p.Length) } else { $it.code }
    # Hand-made codes are kept even when they clash; new codes avoid both.
    $k = $base.ToLower()
    if ($overridden[$k] -and $owner[$k] -ne "url:$($it.url)") { throw "overrides.json gives $($owner[$k]) the code of $($it.code) ($($it.name)); change one of them." }
    if ($owner.ContainsKey($k) -and $owner[$k] -ne "url:$($it.url)") { Write-Warning "$($it.code) ($($it.name)) clashes with $($owner[$k]) when case is ignored." }
    else { $owner[$k] = "url:$($it.url)" }
    $codeByUrl[$it.url] = $base
    if (-not $codeByName.ContainsKey($it.name)) { $codeByName[$it.name] = $base }
}

# Candidates, best first: initials, initials with one or two words extended, a middle word dropped, then the
# first letter with letters picked from the rest of the name. A number in the name is kept as digits.
function Get-Candidates([string]$name) {
    if ($name -match '\d+') {
        $num = $Matches[0]
        $ini = -join (($name -replace '\(.*?\)|\d+', '') -split '\s+' |
                Where-Object { $_ -match '^[A-Za-z]' -and $_.ToLower() -notin $Stop } | ForEach-Object { $_.Substring(0, 1).ToUpper() })
        if ($ini) { $ini.Substring(0, [Math]::Min($ini.Length, [Math]::Max(1, 4 - $num.Length))) + $num }
    }
    $all = @(($name -creplace "'s\b", '' -replace '[^A-Za-z\s-]', '') -split '[\s-]+' | Where-Object { $_ })
    $words = @($all | Where-Object { $_.ToLower() -notin $Stop })
    $ws = @($(if ($words.Count) { $words } else { $all }) | Select-Object -First 4)
    $up = { param($w) $w.Substring(0, 1).ToUpper() }
    $rest = { param($w) $w.Substring(1).ToLower() }
    $n = $ws.Count
    if ($n -gt 1) {
        -join ($ws | ForEach-Object { & $up $_ })
        foreach ($k in 1, 2) {
            for ($i = $n - 1; $i -ge 0; $i--) {
                -join (0..($n - 1) | ForEach-Object { (& $up $ws[$_]) + $(if ($_ -eq $i) { $r = & $rest $ws[$_]; $r.Substring(0, [Math]::Min($k, $r.Length)) }) })
            }
        }
        for ($i = 0; $i -lt $n; $i++) {
            for ($j = $i + 1; $j -lt $n; $j++) {
                -join (0..($n - 1) | ForEach-Object { (& $up $ws[$_]) + $(if ($_ -eq $i -or $_ -eq $j) { $r = & $rest $ws[$_]; $r.Substring(0, [Math]::Min(1, $r.Length)) }) })
            }
        }
        if ($n -gt 2) {
            for ($i = 1; $i -lt $n - 1; $i++) {
                Get-Candidates ((0..($n - 1) | Where-Object { $_ -ne $i } | ForEach-Object { $ws[$_] }) -join ' ')
            }
        }
    }
    $head = & $up $ws[0]
    $tail = ((& $rest $ws[0]) + ($ws[1..($n - 1)] -join '').ToLower())
    if ($n -eq 1) { $tail = & $rest $ws[0] }
    $len = $tail.Length
    for ($i = 0; $i -lt $len; $i++) { $head + $tail[$i] }
    for ($i = 0; $i -lt $len; $i++) { for ($j = $i + 1; $j -lt $len; $j++) { $head + $tail[$i] + $tail[$j] } }
    for ($i = 0; $i -lt $len; $i++) { for ($j = $i + 1; $j -lt $len; $j++) { for ($k = $j + 1; $k -lt $len; $k++) { $head + $tail[$i] + $tail[$j] + $tail[$k] } } }
}

# A name holding a marker phrase: the phrase is dropped and its letters lead the code (Liquid Paranoia -> lP).
function Get-MarkerCode($it) {
    foreach ($m in $markers) {
        $phrase = '(^|\s)' + [regex]::Escape($m.Name) + '(?=\s|$)'
        if ($it.name -cnotmatch $phrase) { continue }
        $restName = (($it.name -creplace $phrase, ' ') -replace '\s+', ' ').Trim()
        if (-not $restName) { return $null }
        $initials = -join ($restName -split '\s+' | Where-Object { $_ -match '^[A-Za-z]' -and $_.ToLower() -notin $Stop } | ForEach-Object { $_.Substring(0, 1).ToUpper() })
        foreach ($c in @($initials) + @(Get-Candidates $restName)) {
            if (-not $c -or $c.Length -gt 4) { continue }
            if (-not $owner.ContainsKey(($m.Value + $c).ToLower())) { return $m.Value + $c }
        }
        throw "No free code for $($it.name)."
    }
    return $null
}

# A base name's code, generated once; its tier codes (code + l/g/p) are reserved with it.
function Get-BaseCode([string]$name) {
    if ($codeByName.ContainsKey($name)) { return $codeByName[$name] }
    foreach ($c in Get-Candidates $name) {
        if ($c.Length -lt 2 -or $c.Length -gt 4) { continue }
        $variants = @($c) + @($tiersOf[$name] | Where-Object { $_ } | ForEach-Object { $c + $_ })
        if ($variants | Where-Object { $owner.ContainsKey($_.ToLower()) }) { continue }
        $owner[$c.ToLower()] = "name:$name"
        foreach ($t in $tiersOf[$name] | Where-Object { $_ }) { $owner[($c + $t).ToLower()] = "tier:$name|$t" }
        $codeByName[$name] = $c
        return $c
    }
    throw "No free code for $name."
}

function Get-Code($it) {
    if ($codeByUrl.ContainsKey($it.url)) { return $codeByUrl[$it.url] }
    $c = Get-MarkerCode $it
    $t = if (-not $c) { Get-TierOf $it.name }
    if ($t) {
        $candidate = (Get-BaseCode $t.base) + $t.t
        $k = $candidate.ToLower()
        if (-not $owner.ContainsKey($k) -or $owner[$k] -eq "tier:$($t.base)|$($t.t)") { $c = $candidate }
    }
    # Not a tier, or its tier code is taken by a hand-made code: a code of its own.
    if (-not $c) { $c = Get-BaseCode $it.name }
    $owner[$c.ToLower()] = "url:$($it.url)"
    $codeByUrl[$it.url] = $c
    Write-Host "new: $($it.category) $($it.name) -> $($Prefix[$it.category])$c"
    return $c
}

foreach ($it in $fresh) { $it.code = $Prefix[$it.category] + (Get-Code $it) }

# Items gone from poe2db stay (old stashes still hold them), after their category's current items.
$freshKeys = @{}; foreach ($it in $fresh) { $freshKeys["$($it.category)|$($it.url)"] = $true }
$result = [System.Collections.Generic.List[object]]::new()
foreach ($category in $Pages.Values | ForEach-Object { $_[0] }) {
    $fresh | Where-Object category -EQ $category | ForEach-Object { $result.Add($_) }
    foreach ($it in $old | Where-Object { $_.category -eq $category -and -not $freshKeys["$($_.category)|$($_.url)"] }) {
        Write-Host "kept (not on poe2db): $($it.category) $($it.name)"
        $result.Add($it)
    }
}

# --- write, formatted like JSON.stringify(list, null, 2) ------------------------------------------------------------
function Json($v) {
    if ($null -eq $v) { return 'null' }
    if ($v -is [string]) {
        $s = $v.Replace('\', '\\').Replace('"', '\"')
        $s = [regex]::Replace($s, '[\x00-\x1f]', { param($m) '\u{0:x4}' -f [int][char]$m.Value })
        return '"' + $s + '"'
    }
    return "$v"
}
$fields = 'name', 'code', 'category', 'url', 'image', 'maxStackSize'
$items = foreach ($it in $result) {
    "  {`n" + (($fields | ForEach-Object { "    `"$_`": $(Json $it.$_)" }) -join ",`n") + "`n  }"
}
$text = if ($result.Count) { "[`n" + ($items -join ",`n") + "`n]" } else { '[]' }
[IO.File]::WriteAllText($Catalog, $text, [Text.UTF8Encoding]::new($false))
Write-Host "total      $($result.Count) -> $Catalog"
