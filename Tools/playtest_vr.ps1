<#
Acceptance test of the VR mode (docs/QUEST.md, VIEWER_SPEC 9.5), on SIMULATED hands, through the Unity CLI (the Editor open on this project,
com.unity.pipeline; works with Windows PowerShell 5.1 and pwsh). No headset is needed or used: the same recognisers, panels and film
machinery a headset drives run on simulated hands (HmVr.SimInputSource).

    powershell -File Tools/playtest_vr.ps1                 # the whole scenario on the first ready film of the library
    powershell -File Tools/playtest_vr.ps1 -Recompile      # recompile first
    powershell -File Tools/playtest_vr.ps1 -Shots Screenshots/vr   # also screenshots (Assets/Screenshots/vr, git-ignored)

It runs hm_vr --action selftest (Assets/VR/Editor/VrScenarios.cs) and prints every check with its measured numbers:
  menu      one card per library film, 5.8 cm Play buttons, >= 3 cm between cards, Processing disabled, hover and click sound
  lesson    picked with the pointing ray and a pinch; the head camera is the only camera and the film never moves it; no flat overlay in
            the view; the start vantage (2-5.5 m from the dancers, facing them); viewpoint presets
  panel     palm-up shows the hand panel; poke play / pause, +5 s / -5 s, chapters; slow motion 0.25x / 0.1x (clock, soundtrack pitch and
            sync); the pinch-drag scrubber (holds the film, audio re-synced on release); the chapter list; each viewpoint (a phone's
            position); teleport; snap turn; the wrist menu button; Back to library stops the lesson cleanly; Exit VR restores the desktop
  console   no errors or warnings from the VR code during the run
The unit tests of the input logic are the EditMode tests of HmVr.Core.Tests (Test Runner, or unity command run_tests).
#>
param(
    [string]$Film = "",
    [switch]$Recompile,
    [string]$Shots = "",
    [switch]$KeepPlaying
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)
$script:projectPath = (Get-Location).Path
$script:failures = [System.Collections.Generic.List[string]]::new()

function Invoke-Unity {
    return ((unity command @args --project-path $script:projectPath --result-only --no-banner 2>&1 | ForEach-Object { "$_" }) -join "`n")
}

function ConvertFrom-UnityJson([string]$raw) {
    $inner = $raw | ConvertFrom-Json
    if ($inner -isnot [string]) { return $inner }
    $inner = $inner -replace '(?<=[:,\[])\s*-?(NaN|Infinity)\s*(?=[,}\]])', '"$1"'
    return ($inner | ConvertFrom-Json)
}

function Vr([string[]]$more = @()) {
    for ($try = 1; $try -le 3; $try++) {
        $raw = Invoke-Unity hm_vr @more
        try { return (ConvertFrom-UnityJson $raw) } catch { Start-Sleep -Seconds 1 }
    }
    throw "hm_vr $more unreadable: $raw"
}

function Assert($condition, [string]$message) {
    if ($condition) { Write-Host "  ok   $message" } else { Write-Host "  FAIL $message" -ForegroundColor Red; $script:failures.Add($message) }
}

function Shot([string]$name) {
    if (-not $Shots) { return }
    Invoke-Unity capture_game_view --save_path "$Shots/$name.png" --width 1280 --height 720 | Out-Null
    Write-Host "  shot Assets/$Shots/$name.png"
}

# ------------------------------------------------------------------------------------------------ setup
if ($Recompile) {
    Invoke-Unity editor_stop | Out-Null
    $r = (Invoke-Unity recompile) | ConvertFrom-Json
    Assert (-not $r.compilationFailed -and -not $r.failed) "no compile errors"
}

Invoke-Unity clear_console | Out-Null
Invoke-Unity editor_play | Out-Null
$ok = $false
for ($i = 0; $i -lt 60 -and -not $ok; $i++) {
    Start-Sleep -Seconds 1
    try { $s = Vr @("--action", "state"); $ok = $null -ne $s.library.library -and $s.library.library.mode -eq "library" } catch { }
}
Assert $ok "Play mode: the viewer is up with the library"
if (-not $ok) { exit 1 }
Start-Sleep -Seconds 2

# ------------------------------------------------------------------------------------------------ the scenario
Write-Host "== scenario (simulated hands)"
if ($Film) { Vr @("--action", "selftest", "--widget", $Film) | Out-Null } else { Vr @("--action", "selftest") | Out-Null }
$deadline = (Get-Date).AddSeconds(420)
$r = $null
do {
    Start-Sleep -Seconds 3
    $r = Vr @("--action", "result")
} while (-not $r.done -and (Get-Date) -lt $deadline)

Assert $r.done "the scenario ran to its end (stage '$($r.stage)')"
foreach ($c in $r.checks) {
    if ($c -like "ok*") { Write-Host "  $c" } else { Write-Host "  $c" -ForegroundColor Red }
}
Assert ($r.failed -eq 0 -and $r.passed -gt 0) "$($r.passed) check(s) passed, $($r.failed) failed (film '$($r.film)')"
foreach ($f in $r.failures) { $script:failures.Add($f) }

# ------------------------------------------------------------------------------------------------ console
# the two innermost stack frames: a warning that the viewer raises while a VR click is merely its caller is not the VR code's
function Top($e) { return (($e.stackTrace -split "`n" | Select-Object -First 2) -join " ") }
$con = (Invoke-Unity console --level warn --tail 200) | ConvertFrom-Json
$mine = @($con.entries | Where-Object { $_.message -notmatch 'BasePipelineServer|/api/exec|com\.unity\.pipeline' })
$vrErr = @($mine | Where-Object { $_.level -eq "error" -and ($_.message -match 'HmVr|Assets.VR|VrMode|VrRig|VrMenu' -or (Top $_) -match 'HmVr') })
$vrWarn = @($mine | Where-Object { $_.level -eq "warn" -and ($_.message -match 'HmVr|Assets.VR' -or (Top $_) -match 'HmVr') })
Assert ($vrErr.Count -eq 0) "no console errors from the VR code ($($vrErr.Count))"
foreach ($e in $vrErr) { Write-Host "       $($e.message)" -ForegroundColor Red }
Assert ($vrWarn.Count -eq 0) "no console warnings from the VR code ($($vrWarn.Count))"
foreach ($w in $vrWarn) { Write-Host "       $($w.message)" -ForegroundColor DarkYellow }
if (-not $KeepPlaying) { Invoke-Unity editor_stop | Out-Null }

Write-Host ""
if ($script:failures.Count -eq 0) { Write-Host "VR playtest: all checks passed" -ForegroundColor Green; exit 0 }
Write-Host "VR playtest: $($script:failures.Count) check(s) failed" -ForegroundColor Red
$script:failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
exit 1
