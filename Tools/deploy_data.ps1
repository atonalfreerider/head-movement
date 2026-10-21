<#
Lays out the viewer's data for a build that cannot read the project's StreamingAssets and the workspace beside it: a standalone Windows
build placed anywhere, or the Quest (Android: StreamingAssets is inside the APK, so the data lives in the app's own folder, HmData.Root).

    powershell -File Tools/deploy_data.ps1 -Out Builds/data                  # lay the data out in a folder
    powershell -File Tools/deploy_data.ps1 -Out Builds/data -Push            # ... and push it to a Quest over adb
    powershell -File Tools/deploy_data.ps1 -Out Builds/data -Only demo,recap # some films only
    powershell -File Tools/deploy_data.ps1 -Out Builds/data -PhoneVideo      # include the phones' frame strips and clips (hundreds of MB; VR does not draw them)
    powershell -File Tools/deploy_data.ps1 -Out Builds/data -Bake -Push   # also bake the headset textures (editor open; see below)
    powershell -File Tools/deploy_data.ps1 -Out Builds/data -Push -Serial <adb serial>   # with several headsets attached, name the one (adb devices)
    powershell -File Tools/deploy_data.ps1 -Out Builds/data -Push -ReplaceSettings   # also replace the headset's quest_settings.json by the tier file for that headset
    powershell -File Tools/deploy_data.ps1 -Out Builds/data -Push -ReplaceSettings -Settings Tools/quest2_settings.json   # name the tier file yourself

The Quest tier file (pushed as hm_data/quest_settings.json) is picked per headset from its model (adb shell getprop ro.product.model):
"Quest 2" -> Tools/quest2_settings.json, anything else (Quest 3 / 3S / Pro) -> Tools/quest_settings.json; -Settings <file> overrides it.

What it copies, for every film of StreamingAssets/library/library.json that is ready:
  <capture>/                  the capture folder without .meta files: avatars (smplx bins, albedo), hair groom, shoes / head props, room mesh, audio,
                              physics / timing data - and without the phones' *.hmj frame strips and *.mp4 clips unless -PhoneVideo
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
    [switch]$Bake,
    [string]$Package = "com.headmovement.viewer",
    [string]$Serial = "",
    [switch]$ReplaceSettings,
    [string]$Settings = ""
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

$lib = Get-Content $libFile -Raw -Encoding UTF8 | ConvertFrom-Json
New-Item -ItemType Directory -Force (Join-Path $out "library/thumbs") | Out-Null
$films = @()
foreach ($f in $lib.films) {
    if ($Only.Count -gt 0 -and $Only -notcontains $f.id) { continue }
    $capDir = Join-Path $sa $f.capture
    $dir = Resolve-Data $f.direction
    if (-not (Test-Path $capDir) -or -not $dir -or -not (Test-Path $dir)) { Write-Host "skip $($f.id): capture or film not ready"; continue }
    Write-Host "== $($f.id)  ($($f.capture))"
    $exclude = if ($PhoneVideo) { @() } else { @("*.hmj", "*.mp4") }
    Copy-Tree $capDir (Join-Path $out $f.capture) $exclude

    $film = Split-Path $dir -Parent
    $name = (Get-Content $dir -Raw -Encoding UTF8 | ConvertFrom-Json).name
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
[System.IO.File]::WriteAllText((Join-Path $out "library/library.json"), ($lib | ConvertTo-Json -Depth 30), (New-Object System.Text.UTF8Encoding $false))
$size = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("deployed {0} film(s) to {1} ({2:0} MB)" -f $films.Count, $out, $size)

if ($Bake) {
    # the avatars' albedo (and the room / floor photos) as GPU-compressed mips + the transparency model: the headset loads them without decoding a PNG
    # (the Unity editor must be open on this project: com.unity.pipeline)
    Push-Location $project
    try { & unity command hm_vr_bake_textures --folder ($out.Substring($project.Length).TrimStart("\", "/").Replace("\", "/")) --project-path $project --result-only --no-banner }
    finally { Pop-Location }
}

if ($Push) {
    $adb = (Get-ChildItem "C:/Program Files/Unity/Hub/Editor/*/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe" | Select-Object -Last 1).FullName
    if (-not $adb) { throw "adb not found in the Unity editor's Android module" }
    $devs = @(& $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match "\sdevice$" } | ForEach-Object { ($_ -split "\s+")[0] })
    if ($devs.Count -eq 0) { throw "no authorised headset in 'adb devices'" }
    if (-not $Serial) {
        if ($devs.Count -gt 1) { throw "several devices attached ($($devs -join ', ')): pass -Serial <serial>" }
        $Serial = $devs[0]
    }
    elseif ($devs -notcontains $Serial) { throw "device $Serial is not attached and authorised ($($devs -join ', '))" }
    $a = @("-s", $Serial)
    $env:MSYS_NO_PATHCONV = "1"
    Write-Host "headset $Serial"
    & $adb @a shell "df -h /sdcard | tail -n 1"
    $target = "/sdcard/Android/data/$Package/files/hm_data"
    # a clean push: folders and files left by an earlier push cannot be reused ("fchown failed": chmod 755 took the setgid bit off the folder, so new files get the
    # wrong group) - the whole folder is made again under the app's own setgid folder, keeping quest_settings.json
    $keep = "$target/../hm_settings.keep"
    & $adb @a shell "if [ -f $target/quest_settings.json ]; then cp $target/quest_settings.json $keep; fi; rm -rf $target; mkdir -p $target; if [ -f $keep ]; then mv $keep $target/quest_settings.json; fi"
    # Android 11+ (scoped storage): adb push cannot create sub-folders under Android/data/<package>, but the shell can
    $dirs = @(Get-ChildItem $out -Recurse -Directory | ForEach-Object { $_.FullName.Substring($out.Length).TrimStart("\", "/").Replace("\", "/") })
    foreach ($chunk in ($dirs | ForEach-Object -Begin { $batch = @() } -Process { $batch += "$target/$_"; if ($batch.Count -ge 20) { ,$batch; $batch = @() } } -End { if ($batch.Count) { ,$batch } })) {
        & $adb @a shell ("mkdir -p " + ($chunk -join " "))
    }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & $adb @a push "$out/." $target
    Write-Host ("pushed {0:0} MB to {1} in {2:0} s" -f $size, $target, $sw.Elapsed.TotalSeconds)
    # the folders the shell made are private to it (owner shell, mode 770): the app (another uid) gets "access denied" listing them until they are world-readable
    & $adb @a shell "find $target -type d -exec chmod 755 {} + ; find $target -type f -exec chmod 644 {} +"
    Write-Host "made the pushed folders readable by the app"
    # the Quest's tier defaults (docs/QUEST.md): pushed when the headset has none, or always with -ReplaceSettings.
    # Pushed to the headset only, never laid out in -Out (a Windows player reads quest_settings.json from its data folder too).
    # Per headset: a Quest 2 gets Tools/quest2_settings.json (when it exists), every other headset Tools/quest_settings.json; -Settings names the file.
    $model = "$(& $adb @a shell getprop ro.product.model)".Trim()
    if ($Settings) {
        $qs = if ([System.IO.Path]::IsPathRooted($Settings)) { $Settings } else { Join-Path $project $Settings }
        if (-not (Test-Path $qs)) { throw "-Settings: no file $qs" }
    }
    elseif ($model -match "Quest 2" -and (Test-Path (Join-Path $PSScriptRoot "quest2_settings.json"))) { $qs = Join-Path $PSScriptRoot "quest2_settings.json" }
    else { $qs = Join-Path $PSScriptRoot "quest_settings.json" }
    Write-Host "headset model '$model': tier file $qs"
    if (Test-Path $qs) {
        $has = "$(& $adb @a shell "if [ -f $target/quest_settings.json ]; then echo yes; fi")".Trim()
        if ($ReplaceSettings -or $has -ne "yes") {
            & $adb @a push $qs /data/local/tmp/hm_quest_settings.json | Out-Null
            & $adb @a shell "cp /data/local/tmp/hm_quest_settings.json $target/quest_settings.json && chmod 644 $target/quest_settings.json && rm /data/local/tmp/hm_quest_settings.json"
            Write-Host "pushed the Quest tier defaults $qs"
        }
        else { Write-Host "kept the headset's own quest_settings.json (-ReplaceSettings pushes $qs)" }
    }
    & $adb @a shell "df -h /sdcard | tail -n 1"
}
