<#
Lays out the viewer's data for a build that cannot read the project's StreamingAssets and the workspace beside it: a standalone Windows
build placed anywhere, or the Quest (Android: StreamingAssets is inside the APK, so the data lives in the app's own folder, HmData.Root).

    powershell -File Tools/deploy_data.ps1 -Out Builds/data                  # lay the data out in a folder
    powershell -File Tools/deploy_data.ps1 -Out Builds/data -Push            # ... and push it to a Quest over adb
    powershell -File Tools/deploy_data.ps1 -Out Builds/data -Only demo,recap # some films only
    powershell -File Tools/deploy_data.ps1 -Out Builds/data -PhoneVideo      # include the phones' frame strips (hundreds of MB; VR does not draw them)

What it copies, for every film of StreamingAssets/library/library.json that is ready:
  <capture>/                  the capture folder without .meta files (and without the phones' *.hmj frame strips unless -PhoneVideo)
  library/films/<id>/         the film: direction.json, narration/timeline*.json, audio/<name>_mix.wav (the soundtrack), emoji, chapters
  library/thumbs/             the posters
  library/library.json        rewritten so every path is relative to the library folder
The output folder is what HmData.Root points at: copy it into a Windows build's StreamingAssets, set HM_DATA to it, or push it
(Android/data/com.headmovement.viewer/files/hm_data on the headset). Nothing here is committed (the data is private: keep -Out under Builds/).

adb is found in the Unity editor's Android module (platform-tools). The headset needs USB debugging on and the PC authorised.
#>
param(
    [string]$Out = "Builds/data",
    [string[]]$Only = @(),
    [switch]$PhoneVideo,
    [switch]$Push,
    [string]$Package = "com.headmovement.viewer"
)

$ErrorActionPreference = "Stop"
$project = Split-Path $PSScriptRoot -Parent
$workspace = Split-Path $project -Parent
$sa = Join-Path $project "Assets/StreamingAssets"
$libDir = Join-Path $sa "library"
$libFile = Join-Path $libDir "library.json"
if (-not (Test-Path $libFile)) { throw "no ${libFile}: nothing to deploy" }
$out = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $project $Out }
if ($out -notmatch "[\\/][Bb]uilds[\\/]") { throw "-Out must be under Builds/ (git-ignored): $out" }

function Resolve-Data([string]$p) {
    if ([string]::IsNullOrWhiteSpace($p)) { return $null }
    $p = $p.Trim().Replace("\", "/")
    if ([System.IO.Path]::IsPathRooted($p)) { return $p }
    foreach ($root in @($libDir, $project, $workspace)) {
        $full = [System.IO.Path]::GetFullPath((Join-Path $root $p))
        if (Test-Path $full) { return $full }
    }
    return $null
}

function Copy-Tree([string]$from, [string]$to, [string[]]$exclude = @()) {
    New-Item -ItemType Directory -Force $to | Out-Null
    & robocopy $from $to /E /NFL /NDL /NJH /NJS /NP /XF *.meta @exclude | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy $from failed ($LASTEXITCODE)" }
}

$lib = Get-Content $libFile -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force (Join-Path $out "library/thumbs") | Out-Null
$films = @()
foreach ($f in $lib.films) {
    if ($Only.Count -gt 0 -and $Only -notcontains $f.id) { continue }
    $capDir = Join-Path $sa $f.capture
    $dir = Resolve-Data $f.direction
    if (-not (Test-Path $capDir) -or -not $dir -or -not (Test-Path $dir)) { Write-Host "skip $($f.id): capture or film not ready"; continue }
    Write-Host "== $($f.id)  ($($f.capture))"
    $exclude = if ($PhoneVideo) { @() } else { @("*.hmj") }
    Copy-Tree $capDir (Join-Path $out $f.capture) $exclude

    $film = Split-Path $dir -Parent
    $name = (Get-Content $dir -Raw | ConvertFrom-Json).name
    $dest = Join-Path $out "library/films/$($f.id)"
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item $dir (Join-Path $dest "direction.json") -Force
    foreach ($sub in @("narration")) {
        $src = Join-Path $film $sub
        if (Test-Path $src) {
            New-Item -ItemType Directory -Force (Join-Path $dest $sub) | Out-Null
            Get-ChildItem $src -Filter "timeline*.json" | Copy-Item -Destination (Join-Path $dest $sub) -Force
        }
    }

    $mix = Join-Path $film "audio/${name}_mix.wav"
    if (Test-Path $mix) {
        New-Item -ItemType Directory -Force (Join-Path $dest "audio") | Out-Null
        Copy-Item $mix (Join-Path $dest "audio") -Force
    }
    else { Write-Host "  warning: no soundtrack $mix" }

    if (Test-Path (Join-Path $film "emoji")) { Copy-Tree (Join-Path $film "emoji") (Join-Path $dest "emoji") }
    $chap = Resolve-Data $f.chaptersFile
    $entry = $f | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    if ($chap -and (Test-Path $chap)) {
        Copy-Item $chap (Join-Path $dest "chapters.json") -Force
        $entry.chaptersFile = "films/$($f.id)/chapters.json"
    }

    $entry.direction = "films/$($f.id)/direction.json"
    $thumb = Resolve-Data $f.thumbnail
    if ($thumb -and (Test-Path $thumb)) {
        Copy-Item $thumb (Join-Path $out "library/thumbs") -Force
        $entry.thumbnail = "thumbs/" + (Split-Path $thumb -Leaf)
    }

    $films += $entry
}

$lib.films = $films
$lib | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $out "library/library.json") -Encoding UTF8
$size = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("deployed {0} film(s) to {1} ({2:0} MB)" -f $films.Count, $out, $size)

if ($Push) {
    $adb = (Get-ChildItem "C:/Program Files/Unity/Hub/Editor/*/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe" | Select-Object -Last 1).FullName
    if (-not $adb) { throw "adb not found in the Unity editor's Android module" }
    & $adb devices
    $target = "/sdcard/Android/data/$Package/files/hm_data"
    & $adb shell "mkdir -p $target"
    & $adb push "$out/." $target
    Write-Host "pushed to $target"
}
