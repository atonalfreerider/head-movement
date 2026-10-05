<#
Scripted playtest of the head-movement scene through the Unity CLI (requires the Editor open on this
project with com.unity.pipeline). Exercises the same public methods as the keyboard/VR bindings via the
hm_* [CliCommand]s in Assets/Editor/HeadMovementCliCommands.cs, asserts on hm_state, and saves screenshots
to Assets/Screenshots (git-ignored).

    pwsh Tools/playtest.ps1                       # default capture 00_SyntheticDemo
    pwsh Tools/playtest.ps1 -Capture LarissaKadu  # any StreamingAssets folder
#>
param(
    [string]$Capture = "00_SyntheticDemo",
    [switch]$Recompile
)

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

function Assert($condition, [string]$message) {
    if ($condition) { Write-Host "  ok   $message" } else { Write-Host "  FAIL $message" -ForegroundColor Red; $failures.Add($message) }
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

if ($Recompile) {
    Invoke-Unity editor_stop | Out-Null
    Invoke-Unity recompile | Out-Null
    $done = Wait-Until { ((Invoke-Unity recompile_status) | ConvertFrom-Json).status -in "completed", "up_to_date" } 300
    Assert $done "scripts recompiled"
    Assert (-not ((Invoke-Unity console_status) | ConvertFrom-Json).groundTruth.compilationFailed) "no compile errors"
}

Write-Host "== enter play mode"
Invoke-Unity open_scene --path Assets/head-movement.unity | Out-Null
Invoke-Unity editor_play | Out-Null
Assert (Wait-Until { ((Invoke-Unity editor_status) | ConvertFrom-Json).playMode -eq "playing" } 60) "editor playing"
Start-Sleep -Seconds 2

Write-Host "== load $Capture"
Invoke-Unity hm_load --name $Capture | Out-Null
Assert (Wait-Until { (Get-State).audioLoaded } 90) "capture loaded with audio"
$s = Get-State
Assert ($s.capture -eq $Capture) "capture name is $Capture"
Assert ($s.frameCount -gt 0) "frames: $($s.frameCount) @ $($s.fps) fps"
Assert ($s.steps -gt 0) "floor steps detected: $($s.steps) ($($s.stepsOnBeat) on beat)"
Invoke-Unity hm_orbit --azimuth 60 --elevation 20 --radius 3 | Out-Null
Shot "pt_loaded"

Write-Host "== playback"
Invoke-Unity hm_transport --action play | Out-Null
Start-Sleep -Seconds 3
$s = Get-State
Assert ($s.playing -and $s.frame -gt 30) "frames advance while playing (frame $($s.frame))"
$expected = [math]::Round($s.audioTime * $s.fps)
Assert ([math]::Abs($s.frame - $expected) -le 2) "pose frame tracks audio clock ($($s.frame) vs $expected)"
Shot "pt_playing"
Invoke-Unity hm_transport --action pause | Out-Null

Write-Host "== lesson transport"
Invoke-Unity hm_transport --action restart | Out-Null
$prev = (Get-State).measure
foreach ($i in 1..3) {
    $s = (Invoke-Unity hm_transport --action "measure+") | ConvertFrom-Json | ConvertFrom-Json
    Assert ($s.measure -eq $prev + 1) "measure+ advances $prev -> $($s.measure)"
    $prev = $s.measure
}
$s = (Invoke-Unity hm_transport --action "measure-") | ConvertFrom-Json | ConvertFrom-Json
Assert ($s.measure -eq $prev - 1) "measure- goes back $prev -> $($s.measure)"
$t0 = $s.audioTime
$s = (Invoke-Unity hm_transport --action "beat+") | ConvertFrom-Json | ConvertFrom-Json
Assert ($s.audioTime -gt $t0 -and -not $s.playing) "beat+ steps forward and stays paused"
$s = (Invoke-Unity hm_transport --action loop) | ConvertFrom-Json | ConvertFrom-Json
Assert $s.loopMeasure "loop measure on"
$loopMeasure = $s.measure
Invoke-Unity hm_transport --action play | Out-Null
Start-Sleep -Seconds 5
$s = Get-State
Assert ($s.measure -eq $loopMeasure) "loop keeps playback inside measure $loopMeasure (now $($s.measure))"
Invoke-Unity hm_transport --action loop | Out-Null
Invoke-Unity hm_transport --action pause | Out-Null

Write-Host "== layers"
foreach ($layer in "floor", "tension", "splats", "cameras") {
    Invoke-Unity hm_layer --layer $layer --visible false | Out-Null
    Assert (-not (Get-State).layers.$layer) "$layer hides"
    Invoke-Unity hm_layer --layer $layer --visible true | Out-Null
}
Invoke-Unity hm_orbit --azimuth 0 --elevation 70 --radius 3.5 | Out-Null
Shot "pt_topdown"
Invoke-Unity hm_orbit --azimuth 30 --elevation 35 --radius 9 | Out-Null
Shot "pt_room"

Write-Host "== console"
$console = (Invoke-Unity console --level error --tail 20) | ConvertFrom-Json
Assert ($console.counts.error -eq 0) "no runtime errors ($($console.counts.error))"
foreach ($e in $console.entries) { Write-Host "  error: $($e.message)" -ForegroundColor Red }

Invoke-Unity editor_stop | Out-Null
if ($failures.Count) { Write-Host "`n$($failures.Count) FAILED" -ForegroundColor Red; exit 1 }
Write-Host "`nall checks passed" -ForegroundColor Green
