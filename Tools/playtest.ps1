<#
Scripted playtest of the head-movement scene through the Unity CLI (requires the Editor open on this
project with com.unity.pipeline). Exercises the same public methods as the keyboard/VR bindings via the
hm_* [CliCommand]s in Assets/Editor/HeadMovementCliCommands.cs, asserts on hm_state, and saves screenshots
to Assets/Screenshots (git-ignored).

    pwsh Tools/playtest.ps1                       # default capture 00_SyntheticDemo
    pwsh Tools/playtest.ps1 -Capture <capture folder>  # any StreamingAssets folder
    pwsh Tools/playtest.ps1 -Capture 00_SyntheticDemo,01_SyntheticSMPLX -Recompile
    pwsh Tools/playtest.ps1 -Capture <capture folder> -Only dance_layers   # base checks + one sub-suite
    pwsh Tools/playtest.ps1 -Only role_hidden                              # the hidden / fading-in role spans (VIEWER_SPEC 3.14), on the capture that declares role_hidden
    pwsh Tools/playtest.ps1 -Capture <capture folder> -Only role_hidden    # the same on a given capture
    $env:HM_PLAYTEST_STRIDE_SCALE = 8   # dance_layers on a whole-dance take: sample every 8th as many frames (default 1)

Captures with capture.json version >= 3 (dancecap export) also get the v3 checks: playback driven by times.json
(binary search by audio time), skinned SMPL-X avatars (55 bones, FK agrees with the exported joints, plausible
body bounds, mesh deforms between frames), and the avatars / timing / physics layers. Captures that declare
smplx_albedo must show textured (seam-split) avatars; the splat and room layers start off (VIEWER_SPEC 3.2a: splats
show the dancers only, the room is not displayed by default); a room mesh loads on demand when its layer is shown and
must sit on the floor (y ~ 0). Tools/playtest_camera.ps1 checks the camera rig (XZ-only follow, fixed heights, director
states, free-fly keys, blends that never pop: a per-frame hm_camtrace across every state pair) and the semi-transparent
avatars, sorted back to front so each dancer shows through the other (VIEWER_SPEC 3.2 / 5.2). Captures with hair_groom.json also run
Tools/playtest_hair.ps1 (the follow's simulated hair: stability on seek / loop, Quest budget, CPU per step,
motion vs the hair reference, review shots of the fastest head moments). v3 captures with avatars run
Tools/playtest_shoes.ps1 (the procedural sneakers: budget, foot cut, opacity, floor contact, close-ups). Seek times are relative to the capture's first frame
(real captures start at audio time ~26 s). Quest-relevant render stats are printed per capture.
#>
param(
    [string[]]$Capture = @("00_SyntheticDemo"),
    [switch]$Recompile,
    [string[]]$Only = @()  # run only these sub-suites (camera, dance_layers, hair, shoes, spine, ...); default all
)

$Capture = @($Capture | ForEach-Object { $_ -split "," } | Where-Object { $_ })  # powershell -File passes "a,b" as one string
$Only = @($Only | ForEach-Object { $_ -split "," } | Where-Object { $_ })
# the role_hidden sub-suite alone, without -Capture: the newest StreamingAssets capture that declares role_hidden (never a retired capture)
if (-not $PSBoundParameters.ContainsKey("Capture") -and $Only.Count -eq 1 -and $Only[0] -eq "role_hidden") {
    $declares = Get-ChildItem (Join-Path (Split-Path $PSScriptRoot -Parent) "Assets\StreamingAssets") -Directory -ErrorAction SilentlyContinue |
        Where-Object { (Test-Path "$($_.FullName)\capture.json") -and ((Get-Content "$($_.FullName)\capture.json" -Raw) -match '"role_hidden"') } |
        Sort-Object Name -Descending
    if ($declares) { $Capture = @($declares[0].Name) }
}
function Want([string]$suite) { return $Only.Count -eq 0 -or $Only -contains $suite }
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)
$script:projectPath = (Get-Location).Path
$failures = [System.Collections.Generic.List[string]]::new()

function Invoke-Unity {
    # an explicit --project-path: auto-detection can take ~60 s per call when other editors/projects are registered
    $out = (unity command @args --project-path $script:projectPath --result-only --no-banner --caller plugin --skill unity-cli 2>&1 | ForEach-Object { "$_" }) -join "`n"
    return $out
}

function ConvertFrom-UnityJson([string]$raw) {
    # hm_* results are a JSON string holding JSON; ConvertFrom-Json (5.1) cannot read bare NaN/Infinity
    $inner = $raw | ConvertFrom-Json
    if ($inner -isnot [string]) { return $inner }
    $inner = $inner -replace '(?<=[:,\[])\s*-?(NaN|Infinity)\s*(?=[,}\]])', '"$1"'
    return ($inner | ConvertFrom-Json)
}

function Get-State {
    # retried: a CLI bridge hiccup (dropped connection, timeout text) must not abort the whole playtest
    for ($try = 1; $try -le 3; $try++) {
        $raw = Invoke-Unity hm_state
        try { return (ConvertFrom-UnityJson $raw) }
        catch {
            Write-Host "  (hm_state unreadable, try $try): $($raw.Substring(0, [math]::Min(200, $raw.Length)))" -ForegroundColor DarkYellow
            Start-Sleep -Seconds 2
        }
    }
    throw "hm_state unreadable"
}

function Invoke-Transport([string]$action, [string[]]$more = @()) {
    $raw = Invoke-Unity hm_transport --action $action @more
    try { return (ConvertFrom-UnityJson $raw) }
    catch {
        Write-Host "  (hm_transport $action unreadable): $($raw.Substring(0, [math]::Min(200, $raw.Length)))" -ForegroundColor DarkYellow
        return Get-State
    }
}

function Assert($condition, [string]$message) {
    if ($condition) { Write-Host "  ok   $message" } else { Write-Host "  FAIL $message" -ForegroundColor Red; $failures.Add("[$script:current] $message") }
}

function Shot([string]$name) {
    Invoke-Unity capture_game_view --save_path "Screenshots/$name.png" --width 1280 --height 720 | Out-Null
    Write-Host "  shot Assets/Screenshots/$name.png"
}

function Wait-Until([scriptblock]$condition, [int]$seconds = 60) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Dist($a, $b) {
    return [math]::Sqrt([math]::Pow($a[0] - $b[0], 2) + [math]::Pow($a[1] - $b[1], 2) + [math]::Pow($a[2] - $b[2], 2))
}

$script:current = "setup"
if ($Recompile) {
    Invoke-Unity editor_stop | Out-Null
    Invoke-Unity recompile | Out-Null
    $done = Wait-Until { ((Invoke-Unity recompile_status) | ConvertFrom-Json).status -in "completed", "up_to_date" } 300
    Assert $done "scripts recompiled"
    Assert (-not ((Invoke-Unity console_status) | ConvertFrom-Json).groundTruth.compilationFailed) "no compile errors"
}

Write-Host "== enter play mode"
Invoke-Unity open_scene --path Assets/head-movement.unity | Out-Null
Invoke-Unity clear_console | Out-Null
Invoke-Unity editor_play | Out-Null
Assert (Wait-Until { ((Invoke-Unity editor_status) | ConvertFrom-Json).playMode -eq "playing" } 60) "editor playing"
Start-Sleep -Seconds 2

foreach ($cap in $Capture) {
    $script:current = $cap
    $prefix = if ($cap -eq "00_SyntheticDemo") { "pt" } else { "pt_$cap" }

    Invoke-Unity clear_console | Out-Null  # each capture is judged on its own console errors
    Write-Host "== load $cap"
    Invoke-Unity hm_load --name $cap | Out-Null
    Assert (Wait-Until { $st = Get-State; $st.audioLoaded -and $st.capture -eq $cap } 90) "capture loaded with audio"
    $s = Get-State
    Assert ($s.capture -eq $cap) "capture name is $cap"
    Assert ($s.frameCount -gt 0) "frames: $($s.frameCount) @ $($s.fps) fps"
    $base = 0.0  # audio time of the first pose frame (seek targets below are relative to it)
    if ($null -ne $s.frameAudioTime) { $base = [double](Invoke-Transport restart).frameAudioTime }
    # measures the capture covers: a 6 s window of a real take spans 2-3 measures (and may start mid-measure)
    $firstMeasure = (Invoke-Transport restart).measure
    $lastMeasure = (Invoke-Transport seek @("--time", "100000")).measure
    Invoke-Unity hm_transport --action restart | Out-Null
    Write-Host "  measures in the capture: $firstMeasure..$lastMeasure"
    $capJsonPt = Get-Content (Join-Path (Get-Location) "Assets\StreamingAssets\$cap\capture.json") -Raw | ConvertFrom-Json
    # a capture that mutes its leader at the start (capture.json role_hidden; hm_hide) is checked unmuted here: the checks below look at his avatar,
    # touchdown rings and physics markers a few seconds in (the "role_hidden" sub-suite, Tools/playtest_role_hidden.ps1, tests the muted spans)
    if ($null -ne $capJsonPt.role_hidden) { Invoke-Unity hm_hide --role lead --clear true | Out-Null }
    Assert ($s.steps -gt 0) "floor steps detected: $($s.steps) ($($s.stepsOnBeat) on beat)"
    $v3 = $s.version -ge 3
    $s0 = Get-State
    Assert (-not $s0.layers.splats) "splat layer off by default (dancers-only splats, VIEWER_SPEC 3.2a)"
    Assert (-not $s0.layers.room) "room layer off by default"
    Assert (-not $s0.layers.physics -and $s0.skeletonMode -eq "rhythm") "rhythm mode by default: skeletons pulse with the beat, physics view off (user 2026-10-07)"
    Assert ($s0.layers.grid -and $s0.footprints.mode -eq "recent") "floor crosses on, footprints recent-only by default"
    foreach ($layer in "floor", "tension", "avatars", "timing", "physics") { Invoke-Unity hm_layer --layer $layer --visible true | Out-Null }
    Invoke-Unity hm_orbit --azimuth 60 --elevation 20 --radius 3 | Out-Null
    Shot "${prefix}_loaded"

    Write-Host "== seek before first play (fresh AudioSource is stopped, not paused)"
    $target = [math]::Min($firstMeasure + 2, $lastMeasure)
    $s = Invoke-Transport measure @("--measure", "$target")
    Assert ($s.measure -eq $target -and $s.frame -gt 0) "jump to measure $target right after load (measure $($s.measure), frame $($s.frame))"
    $s = Invoke-Transport seek @("--time", "$($base + 2.0)")
    Assert ([math]::Abs($s.audioTime - ($base + 2.0)) -lt 0.05) "seek to first frame + 2.0 s while paused ($($s.audioTime))"
    Invoke-Unity hm_transport --action restart | Out-Null

    Write-Host "== playback"
    Invoke-Unity hm_transport --action play | Out-Null
    Start-Sleep -Seconds 3
    $s = Get-State
    # a 6 s real capture can reach its last frame (and auto-pause there) before the slow CLI reads the state
    Assert (($s.playing -or $s.frame -eq $s.frameCount - 1) -and $s.frame -gt 30) "frames advance while playing (frame $($s.frame)/$($s.frameCount), playing $($s.playing))"
    if ($null -ne $s.frameAudioTime) {
        # the shown frame's own timestamp vs the audio clock (state is read a little after the frame was picked)
        $lag = [math]::Abs($s.frameAudioTime - $s.audioTime)
        Assert ($lag -le 3 * $s.meanFrameInterval) ("pose frame tracks audio clock (frame time {0:0.000} vs audio {1:0.000})" -f $s.frameAudioTime, $s.audioTime)
    } else {
        $expected = [math]::Round($s.audioTime * $s.fps)
        Assert ([math]::Abs($s.frame - $expected) -le 2) "pose frame tracks audio clock ($($s.frame) vs $expected)"
    }
    Shot "${prefix}_playing"
    Invoke-Unity hm_transport --action pause | Out-Null

    Write-Host "== lesson transport"
    Invoke-Unity hm_transport --action restart | Out-Null
    $prev = (Get-State).measure
    foreach ($i in 1..3) {
        $s = Invoke-Transport "measure+"
        $want = [math]::Min($prev + 1, $lastMeasure)  # past the capture's last measure: stays (clamped to the last frame)
        Assert ($s.measure -eq $want) "measure+ advances $prev -> $($s.measure) (capture measures $firstMeasure..$lastMeasure)"
        $prev = $s.measure
    }
    Invoke-Transport measure @("--measure", "$prev") | Out-Null  # to the measure's start (measure- from inside restarts it)
    $s = Invoke-Transport "measure-"
    Assert ($s.measure -eq [math]::Max($prev - 1, $firstMeasure)) "measure- goes back $prev -> $($s.measure)"
    $t0 = $s.audioTime
    $s = Invoke-Transport "beat+"
    Assert ($s.audioTime -gt $t0 -and -not $s.playing) "beat+ steps forward and stays paused"
    $s = Invoke-Transport loop
    Assert $s.loopMeasure "loop measure on"
    $loopMeasure = $s.measure
    Invoke-Unity hm_transport --action play | Out-Null
    Start-Sleep -Seconds 5
    $s = Get-State
    Assert ($s.measure -eq $loopMeasure) "loop keeps playback inside measure $loopMeasure (now $($s.measure))"
    Invoke-Unity hm_transport --action loop | Out-Null
    Invoke-Unity hm_transport --action pause | Out-Null

    Write-Host "== layers"
    $layers = @("floor", "tension", "splats", "cameras")
    if ($v3) { $layers += @("avatars", "timing", "physics") }
    if ($null -ne $capJsonPt.room) { $layers += @("room") }
    foreach ($layer in $layers) {
        Invoke-Unity hm_layer --layer $layer --visible false | Out-Null
        Assert (-not (Get-State).layers.$layer) "$layer hides"
        Invoke-Unity hm_layer --layer $layer --visible true | Out-Null
    }
    Invoke-Unity hm_orbit --azimuth 0 --elevation 70 --radius 3.5 | Out-Null
    Shot "${prefix}_topdown"
    Invoke-Unity hm_orbit --azimuth 30 --elevation 35 --radius 9 | Out-Null
    Shot "${prefix}_room"
    # back to the default layer set (dancers only)
    Invoke-Unity hm_layer --layer splats --visible false | Out-Null
    Invoke-Unity hm_layer --layer room --visible false | Out-Null

    if ($v3) {
        Write-Host "== v3: times-driven playback"
        $s = Get-State
        Assert $s.timesDriven "playback driven by times.json"
        Assert ($s.warnings.Count -eq 0) "no load warnings ($($s.warnings -join '; '))"
        $half = 0.5 * $s.meanFrameInterval + 0.002
        foreach ($dt in 1.0, 3.517, 7.7333, 11.01) {
            $t = $base + $dt
            $s = Invoke-Transport seek @("--time", "$t")
            Assert ($s.frame -eq $s.frameForAudioTime -and [math]::Abs($s.frameAudioTime - $s.audioTime) -le $half) `
                ("seek {0}: frame {1} at {2:0.0000} s is the nearest frame to audio {3:0.0000} s" -f $t, $s.frame, $s.frameAudioTime, $s.audioTime)
        }

        Write-Host "== v3: SMPL-X avatars"
        $a = Invoke-Transport seek @("--time", "$($base + 5.0)")
        foreach ($role in "lead", "follow") {
            $av = $a.avatars.$role
            Assert ($null -ne $av -and $av.visible) "$role avatar present and visible"
            if ($null -eq $av) { continue }
            Assert ($av.bones -eq 55 -and $av.vertices -gt 10000) "$role avatar: $($av.bones) bones, $($av.vertices) vertices"
            Assert ($av.frame -eq $a.frame) "$role avatar on the shown frame ($($av.frame) vs $($a.frame))"
            Assert ($av.fkErrorMm -lt 1.0) ("{0} bones match exported joints (FK error {1:0.000} mm)" -f $role, $av.fkErrorMm)
            $d = Dist $av.head $av.skeletonHead
            Assert ($d -lt 0.01) ("{0} avatar head on the glowing skeleton's head ({1:0.0} mm)" -f $role, ($d * 1000))
            Assert ($av.minY -gt -0.08 -and $av.minY -lt 0.15 -and $av.maxY -gt 1.3 -and $av.maxY -lt 2.1) `
                ("{0} skinned body stands on the floor: y {1:0.00}..{2:0.00} m" -f $role, $av.minY, $av.maxY)
        }
        $b = Invoke-Transport seek @("--time", "$($base + 5.6)")
        $moved = (Dist $a.avatars.lead.head $b.avatars.lead.head) + [math]::Abs($a.avatars.follow.sizeX - $b.avatars.follow.sizeX)
        Assert ($moved -gt 0.01) ("skinned meshes deform between frames (head moved + width change {0:0.000} m)" -f $moved)
        if ($null -ne $capJsonPt.smplx_albedo) {
            foreach ($role in "lead", "follow") {
                $av = $b.avatars.$role
                Assert ($av.textured -and $av.textureSize -ge 512) "$role avatar textured ($($av.textureSize) px, $($av.textureFormat), $($av.vertices) split vertices, $($av.triangles) triangles)"
            }
        }
        Invoke-Unity hm_layer --layer avatars --visible false | Out-Null
        $s = Get-State
        Assert (-not $s.avatars.lead.visible -and -not $s.avatars.follow.visible) "avatars layer hides both bodies"
        Invoke-Unity hm_layer --layer avatars --visible true | Out-Null

        Write-Host "== v3: overlays"
        $s = Get-State
        if ($null -ne $s.timing) {
            Assert ($s.timing.touchdowns -gt 0) "timing overlay: $($s.timing.touchdowns) touchdown markers"
            Assert ($s.timing.visibleRings -gt 0) "touchdown rings shown around the playhead ($($s.timing.visibleRings))"
        } else { Write-Host "  (no timing.json in this capture)" }
        if ($null -ne $s.physics) {
            Assert ($s.physics.frame -eq $s.frame -and $s.physics.lead.comMarkerActive) "physics overlay on the shown frame (COM, XCoM, support)"
            Assert ($s.physics.lead.fNetN -gt 200 -and $s.physics.lead.fNetN -lt 3000) ("lead F_net {0:0} N is body-weight scale (HUD number; no arrow)" -f $s.physics.lead.fNetN)
            Assert ($s.physics.lead.axisLines -eq 0 -and $s.physics.follow.axisLines -eq 0) "no F_net up-arrow / plumb axis through the leader or the follower (user 2026-10-07)"
        } else { Write-Host "  (no physics.json in this capture)" }

        Invoke-Unity hm_layer --layer floor --visible false | Out-Null
        Invoke-Unity hm_layer --layer physics --visible false | Out-Null
        Invoke-Unity hm_orbit --azimuth 120 --elevation 12 --radius 2.4 | Out-Null
        Shot "${prefix}_avatars"
        Invoke-Unity hm_layer --layer physics --visible true | Out-Null
        Invoke-Unity hm_layer --layer avatars --visible false | Out-Null
        Invoke-Unity hm_orbit --azimuth 0 --elevation 75 --radius 2.6 | Out-Null
        Shot "${prefix}_overlays"
        Invoke-Unity hm_layer --layer avatars --visible true | Out-Null
        Invoke-Unity hm_layer --layer floor --visible true | Out-Null
    }

    if ($null -ne $capJsonPt.room) {
        Write-Host "== room (Quest mesh, on demand)"
        Invoke-Unity hm_layer --layer room --visible true | Out-Null  # loads it
        Invoke-Unity hm_layer --layer room --visible false | Out-Null
        $s = Get-State
        Assert $s.room.loaded "room mesh loaded ($($s.room.triangles) triangles, $($s.room.vertices) vertices, $($s.room.submeshes) submeshes, $($s.room.textures) textures)"
        Assert ($s.room.triangles -eq $capJsonPt.room.triangles) "room triangles match the export ($($capJsonPt.room.triangles))"
        Assert ($s.room.boundsMin[1] -gt -0.25 -and $s.room.boundsMin[1] -lt 0.05) ("room floor at y ~ 0 (lowest point {0:0.000} m)" -f $s.room.boundsMin[1])
        Assert (-not $s.layers.room) "room layer hidden after the layer checks (shown on demand only)"
        Invoke-Unity hm_layer --layer room --visible true | Out-Null
        Assert ((Get-State).layers.room) "room shows on demand"
        Invoke-Unity hm_layer --layer room --visible false | Out-Null
        Assert (-not (Get-State).layers.room) "room hides again"
    }

    Write-Host "== Quest-relevant render stats (default layers: no splats, no room)"
    Invoke-Unity hm_layer --layer splats --visible false | Out-Null
    Invoke-Unity hm_layer --layer room --visible false | Out-Null
    Invoke-Unity hm_orbit --azimuth 60 --elevation 15 --radius 3 | Out-Null
    Start-Sleep -Milliseconds 500
    $perf = Invoke-Unity get_performance_stats
    Write-Host "  $($perf -replace '\s+', ' ')"

    # VIEWER_SPEC 5.2 camera rig + 3.2 avatar opacity (+ before/after review shots for the realism pass)
    if ((Want "camera") -and (Test-Path "$PSScriptRoot/playtest_camera.ps1")) { . "$PSScriptRoot/playtest_camera.ps1" }

    # VIEWER_SPEC v3 dance layers: origin, counterbalance, follower traces, dance graph, hm_tour (+ review shots)
    if ((Want "dance_layers") -and (Test-Path "$PSScriptRoot/playtest_dance_layers.ps1")) { . "$PSScriptRoot/playtest_dance_layers.ps1" }

    # role hidden spans (capture.json role_hidden, hm_hide; VIEWER_SPEC 3.14): the muted role's avatar, skeleton and derived overlays, the hard cut
    if ((Want "role_hidden") -and (Test-Path "$PSScriptRoot/playtest_role_hidden.ps1")) { . "$PSScriptRoot/playtest_role_hidden.ps1" }

    # the follow's groomed hair (Assets/Hair) on captures with hair_groom.json: stability, budget, CPU, review shots
    if ((Want "hair") -and (Test-Path "$PSScriptRoot/playtest_hair.ps1")) { . "$PSScriptRoot/playtest_hair.ps1" }

    # procedural sneakers (Assets/Shoes) on the SMPL-X avatars: budget, foot cut, opacity, floor contact sweep, close-ups
    if ((Want "shoes") -and (Test-Path "$PSScriptRoot/playtest_shoes.ps1")) { . "$PSScriptRoot/playtest_shoes.ps1" }

    # the follower's spine as a bead chain (Assets/Overlays/SpineBeads.cs): no spine line, 1 instanced draw, the rhythm
    # pulse climbing bead by bead, physics colours, visibility, review shots spine_*.png
    if ((Want "spine") -and (Test-Path "$PSScriptRoot/playtest_spine.ps1")) { . "$PSScriptRoot/playtest_spine.ps1" }

    Write-Host "== console"
    $console = (Invoke-Unity console --level error --tail 50) | ConvertFrom-Json
    # errors of the Unity CLI bridge itself (a dropped HTTP connection to /api/exec) are not the app's: report them
    $bridge = @($console.entries | Where-Object { $_.message -match '/api/exec|^Pipeline: ' })
    $app = @($console.entries | Where-Object { $_.message -notmatch '/api/exec|^Pipeline: ' })
    Assert ($app.Count -eq 0) "no runtime errors ($($app.Count); CLI bridge errors ignored: $($bridge.Count))"
    foreach ($e in $app) { Write-Host "  error: $($e.message)" -ForegroundColor Red }
    foreach ($e in $bridge) { Write-Host "  (cli bridge) $($e.message)" -ForegroundColor DarkYellow }
}

Invoke-Unity editor_stop | Out-Null
if ($failures.Count) {
    Write-Host "`n$($failures.Count) FAILED" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  $f" -ForegroundColor Red }
    exit 1
}
Write-Host "`nall checks passed" -ForegroundColor Green
