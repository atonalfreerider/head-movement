<#
Scripted playtest of the head-movement scene through the Unity CLI (requires the Editor open on this
project with com.unity.pipeline). Exercises the same public methods as the keyboard/VR bindings via the
hm_* [CliCommand]s in Assets/Editor/HeadMovementCliCommands.cs, asserts on hm_state, and saves screenshots
to Assets/Screenshots (git-ignored).

    pwsh Tools/playtest.ps1                       # default capture 00_SyntheticDemo
    pwsh Tools/playtest.ps1 -Capture LarissaKadu  # any StreamingAssets folder
    pwsh Tools/playtest.ps1 -Capture 00_SyntheticDemo,01_SyntheticSMPLX -Recompile

Captures with capture.json version >= 3 (dancecap export) also get the v3 checks: playback driven by times.json
(binary search by audio time), skinned SMPL-X avatars (55 bones, FK agrees with the exported joints, plausible
body bounds, mesh deforms between frames), and the avatars / timing / physics layers.
#>
param(
    [string[]]$Capture = @("00_SyntheticDemo"),
    [switch]$Recompile
)

$Capture = @($Capture | ForEach-Object { $_ -split "," } | Where-Object { $_ })  # powershell -File passes "a,b" as one string
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)
$failures = [System.Collections.Generic.List[string]]::new()

function Invoke-Unity {
    $out = (unity command @args --result-only --no-banner --caller plugin --skill unity-cli 2>&1 | ForEach-Object { "$_" }) -join "`n"
    return $out
}

function Get-State {
    $raw = Invoke-Unity hm_state
    return ($raw | ConvertFrom-Json | ConvertFrom-Json)
}

function Invoke-Transport([string]$action, [string[]]$more = @()) {
    $raw = Invoke-Unity hm_transport --action $action @more
    return ($raw | ConvertFrom-Json | ConvertFrom-Json)
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
    Assert ($s.steps -gt 0) "floor steps detected: $($s.steps) ($($s.stepsOnBeat) on beat)"
    $v3 = $s.version -ge 3
    foreach ($layer in "floor", "tension", "splats", "avatars", "timing", "physics") { Invoke-Unity hm_layer --layer $layer --visible true | Out-Null }
    Invoke-Unity hm_orbit --azimuth 60 --elevation 20 --radius 3 | Out-Null
    Shot "${prefix}_loaded"

    Write-Host "== seek before first play (fresh AudioSource is stopped, not paused)"
    $s = Invoke-Transport measure @("--measure", "3")
    Assert ($s.measure -eq 3 -and $s.frame -gt 0) "jump to measure 3 right after load (measure $($s.measure), frame $($s.frame))"
    $s = Invoke-Transport seek @("--time", "2.0")
    Assert ([math]::Abs($s.audioTime - 2.0) -lt 0.05) "seek to 2.0 s while paused ($($s.audioTime))"
    Invoke-Unity hm_transport --action restart | Out-Null

    Write-Host "== playback"
    Invoke-Unity hm_transport --action play | Out-Null
    Start-Sleep -Seconds 3
    $s = Get-State
    Assert ($s.playing -and $s.frame -gt 30) "frames advance while playing (frame $($s.frame))"
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
        Assert ($s.measure -eq $prev + 1) "measure+ advances $prev -> $($s.measure)"
        $prev = $s.measure
    }
    $s = Invoke-Transport "measure-"
    Assert ($s.measure -eq $prev - 1) "measure- goes back $prev -> $($s.measure)"
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
    foreach ($layer in $layers) {
        Invoke-Unity hm_layer --layer $layer --visible false | Out-Null
        Assert (-not (Get-State).layers.$layer) "$layer hides"
        Invoke-Unity hm_layer --layer $layer --visible true | Out-Null
    }
    Invoke-Unity hm_orbit --azimuth 0 --elevation 70 --radius 3.5 | Out-Null
    Shot "${prefix}_topdown"
    Invoke-Unity hm_orbit --azimuth 30 --elevation 35 --radius 9 | Out-Null
    Shot "${prefix}_room"

    if ($v3) {
        Write-Host "== v3: times-driven playback"
        $s = Get-State
        Assert $s.timesDriven "playback driven by times.json"
        Assert ($s.warnings.Count -eq 0) "no load warnings ($($s.warnings -join '; '))"
        $half = 0.5 * $s.meanFrameInterval + 0.002
        foreach ($t in 1.0, 3.517, 7.7333, 11.01) {
            $s = Invoke-Transport seek @("--time", "$t")
            Assert ($s.frame -eq $s.frameForAudioTime -and [math]::Abs($s.frameAudioTime - $s.audioTime) -le $half) `
                ("seek {0}: frame {1} at {2:0.0000} s is the nearest frame to audio {3:0.0000} s" -f $t, $s.frame, $s.frameAudioTime, $s.audioTime)
        }

        Write-Host "== v3: SMPL-X avatars"
        $a = Invoke-Transport seek @("--time", "5.0")
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
        $b = Invoke-Transport seek @("--time", "5.6")
        $moved = (Dist $a.avatars.lead.head $b.avatars.lead.head) + [math]::Abs($a.avatars.follow.sizeX - $b.avatars.follow.sizeX)
        Assert ($moved -gt 0.01) ("skinned meshes deform between frames (head moved + width change {0:0.000} m)" -f $moved)
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
            Assert ($s.physics.frame -eq $s.frame -and $s.physics.lead.comMarkerActive) "physics overlay on the shown frame (COM, XCoM, F_net)"
            Assert ($s.physics.lead.fNetN -gt 200 -and $s.physics.lead.fNetN -lt 3000) ("lead F_net {0:0} N is body-weight scale" -f $s.physics.lead.fNetN)
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

    Write-Host "== console"
    $console = (Invoke-Unity console --level error --tail 20) | ConvertFrom-Json
    Assert ($console.counts.error -eq 0) "no runtime errors ($($console.counts.error))"
    foreach ($e in $console.entries) { Write-Host "  error: $($e.message)" -ForegroundColor Red }
}

Invoke-Unity editor_stop | Out-Null
if ($failures.Count) {
    Write-Host "`n$($failures.Count) FAILED" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  $f" -ForegroundColor Red }
    exit 1
}
Write-Host "`nall checks passed" -ForegroundColor Green
