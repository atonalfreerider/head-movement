<#
Follower spine bead checks (Assets/Overlays/SpineBeads.cs, user 2026-10-07: "the follower's spine as a set of small white
spheres, not a line renderer"). Dot-sourced by Tools/playtest.ps1 inside its per-capture loop (uses its Invoke-Unity /
ConvertFrom-UnityJson / Invoke-Transport / Assert / Shot helpers and $cap / $base), or standalone:

    powershell -File Tools/playtest_spine.ps1 -SpineCapture <capture folder>

Runs for v3 captures (SkeletonStyle colours). Checks:
  - the chain: beads > 0 from her pelvis to her neck (end beads on the joints), even spacing, no LineRenderer on her
    spine (her other 4 skeleton lines and the leader's 3 unchanged), GPU instanced (HM_Bead shader, opaque queue 2000)
  - draw calls: ONE instanced draw per camera render carrying every bead, and the Game view's draw-call count drops by
    exactly that one draw when the beads are switched off (get_performance_stats A/B)
  - rhythm mode: the beat pulse climbs the chain bead by bead (mid-travel frame: brightness falls from pelvis to neck,
    the neck beads brighten on the next frames)
  - physics mode: the beads take the trunk's estimated load colour (= her joint colours, compression blue)
  - visibility: the beads stay with the skeleton when the avatars layer hides / shows the bodies; anything that hides
    her skeleton lines (review close-ups: hm_shoes --clean) hides them; the skeletons-over-bodies stencil option
  - review shots spine_*.png (default orbit, close-up front and side, rhythm pulse mid-travel, physics mode), copied to
    ../dancecap/work/<take>/review/spine
#>
param(
    [string]$SpineCapture = $env:HM_SPINE_CAPTURE  # standalone only (dot-sourced: playtest.ps1's $cap); else $SpineCaptureDefault from the git-ignored Tools/local.ps1
)

$spineStandalone = -not (Get-Command Invoke-Unity -ErrorAction SilentlyContinue)
if ($spineStandalone -and -not $SpineCapture -and (Test-Path "$PSScriptRoot/local.ps1")) {
    . "$PSScriptRoot/local.ps1"   # git-ignored, per machine: $SpineCaptureDefault = "<capture folder>"
    if ($SpineCaptureDefault) { $SpineCapture = $SpineCaptureDefault }
}
if ($spineStandalone -and -not $SpineCapture) {
    throw "pass -SpineCapture <capture folder>, set HM_SPINE_CAPTURE, or define `$SpineCaptureDefault in the git-ignored Tools/local.ps1"
}
if ($spineStandalone) {
    $ErrorActionPreference = "Stop"
    Set-Location (Split-Path $PSScriptRoot -Parent)
    $script:projectPath = (Get-Location).Path
    $failures = [System.Collections.Generic.List[string]]::new()
    function Invoke-Unity {
        $out = (unity command @args --project-path $script:projectPath --result-only --no-banner --caller plugin --skill unity-cli 2>&1 | ForEach-Object { "$_" }) -join "`n"
        return $out
    }
    function ConvertFrom-UnityJson([string]$raw) {
        $inner = $raw | ConvertFrom-Json
        if ($inner -isnot [string]) { return $inner }
        $inner = $inner -replace '(?<=[:,\[])\s*-?(NaN|Infinity)\s*(?=[,}\]])', '"$1"'
        return ($inner | ConvertFrom-Json)
    }
    function Get-State {
        for ($try = 1; $try -le 3; $try++) {
            $raw = Invoke-Unity hm_state
            try { return (ConvertFrom-UnityJson $raw) } catch { Start-Sleep -Seconds 2 }
        }
        throw "hm_state unreadable"
    }
    function Invoke-Transport([string]$action, [string[]]$more = @()) {
        $raw = Invoke-Unity hm_transport --action $action @more
        try { return (ConvertFrom-UnityJson $raw) } catch { return Get-State }
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
        while ((Get-Date) -lt $deadline) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 500 }
        return $false
    }

    $cap = $SpineCapture
    $script:current = $cap
    Write-Host "== enter play mode ($cap)"
    Invoke-Unity open_scene --path Assets/head-movement.unity | Out-Null
    Invoke-Unity clear_console | Out-Null
    Invoke-Unity editor_play | Out-Null
    # entering play mode reloads the domain (~75 s on this project); editor_status has no playMode meanwhile
    Assert (Wait-Until { try { ((Invoke-Unity editor_status) | ConvertFrom-Json).playMode -eq "playing" } catch { $false } } 240) "editor playing"
    Start-Sleep -Seconds 2
    Invoke-Unity hm_load --name $cap | Out-Null
    Assert (Wait-Until { $st = Get-State; $st.audioLoaded -and $st.capture -eq $cap } 90) "capture loaded with audio"
    $base = [double](Invoke-Transport restart).frameAudioTime
}

$capJsonSp = Get-Content (Join-Path (Get-Location) "Assets\StreamingAssets\$cap\capture.json") -Raw | ConvertFrom-Json
if ($capJsonSp.version -ge 3) {
    Write-Host "== follower spine beads (user 2026-10-07)"
    $invSp = [System.Globalization.CultureInfo]::InvariantCulture
    function NumSp([double]$x) { return $x.ToString("R", $invSp) }
    function Get-Spine([string[]]$spArgs = @()) {
        $raw = Invoke-Unity hm_spine @spArgs
        try { return (ConvertFrom-UnityJson $raw) } catch { Write-Host "  (hm_spine unreadable) $($raw.Substring(0, [math]::Min(300, $raw.Length)))" -ForegroundColor DarkYellow; return $null }
    }
    function Get-DrawCalls {
        # the Game view's last rendered frame (UnityStats); median of 3 reads a few frames apart
        $v = @()
        foreach ($i in 1..3) {
            Start-Sleep -Milliseconds 350
            try { $p = (Invoke-Unity get_performance_stats) | ConvertFrom-Json } catch { continue }
            if ($null -ne $p.render.drawCalls) { $v += [int]$p.render.drawCalls }
        }
        if ($v.Count -eq 0) { return $null }
        return ($v | Sort-Object)[[math]::Floor($v.Count / 2)]
    }
    $takeSp = $capJsonSp.provenance.take
    $reviewSp = if ($takeSp) { Join-Path (Split-Path (Get-Location) -Parent) "dancecap\work\$takeSp\review\spine" } else { Join-Path (Split-Path (Get-Location) -Parent) "dancecap\work\review\spine\$cap" }
    New-Item -ItemType Directory -Force $reviewSp | Out-Null
    function SpineShot([string]$name) {
        Start-Sleep -Milliseconds 500
        Shot $name
        $src = Join-Path (Get-Location) "Assets\Screenshots\$name.png"
        if (Test-Path $src) { Copy-Item $src (Join-Path $reviewSp "$name.png") -Force; Write-Host "  review $reviewSp\$name.png" }
    }

    # default view: rhythm mode, avatars on, physics off, HUD off for the review shots
    Invoke-Unity hm_skeleton --mode rhythm | Out-Null
    foreach ($layer in "avatars", "floor") { Invoke-Unity hm_layer --layer $layer --visible true | Out-Null }
    Invoke-Unity hm_layer --layer hud --visible false | Out-Null
    Invoke-Transport seek @("--time", (NumSp ($base + 4.0))) | Out-Null
    Invoke-Unity hm_orbit --height 1.6 --look 0.95 --distance 2.9 | Out-Null
    Start-Sleep -Milliseconds 600

    $sp = Get-Spine
    Assert ($null -ne $sp -and $sp.count -gt 0) ("bead chain on her spine: {0} beads, radius {1:0.0} mm, spacing {2:0.0} mm along a {3:0.000} m pelvis-neck curve (steps {4:0.0}..{5:0.0} mm)" -f `
        $sp.count, (1000 * $sp.radiusM), (1000 * $sp.spacingM), $sp.typicalLengthM, $sp.centreStepMm[0], $sp.centreStepMm[1])
    Assert (-not $sp.followSpineLineRenderer -and $sp.followLineRenderers -eq 4 -and $sp.leadLineRenderers -eq 3) `
        ("no LineRenderer on her spine (her legs / shoulders / arms keep their {0} lines, the leader his {1})" -f $sp.followLineRenderers, $sp.leadLineRenderers)
    Assert ($sp.pelvisErrorMm -lt 1 -and $sp.neckErrorMm -lt 1) ("end beads on her pelvis ({0:0.00} mm) and neck ({1:0.00} mm) joints" -f $sp.pelvisErrorMm, $sp.neckErrorMm)
    Assert ($sp.instanced -and $sp.shader -eq "HeadMovement/Bead" -and $sp.renderQueue -eq 2000) ("GPU instanced {0}, opaque queue {1} (before the translucent avatars, like the skeleton lines), HDR gain {2:0.00}" -f $sp.shader, $sp.renderQueue, $sp.intensity)
    Assert ($sp.visible) "beads visible in the default view"

    Write-Host "== spine beads: draw calls"
    $cams = @($sp.camerasLastFrame)
    Assert ($sp.drawsLastFrame -ge 1 -and $sp.drawsLastFrame -eq $cams.Count -and $sp.instancesLastDraw -eq $sp.count) `
        ("one instanced draw per camera render carrying all {0} beads ({1} draws for {2})" -f $sp.instancesLastDraw, $sp.drawsLastFrame, ($cams -join ", "))
    $dcOn = Get-DrawCalls
    Get-Spine @("--action", "set", "--enabled", "false") | Out-Null
    $dcOff = Get-DrawCalls
    Get-Spine @("--action", "set", "--enabled", "true") | Out-Null
    $dcOn2 = Get-DrawCalls
    if ($null -ne $dcOn -and $null -ne $dcOff) {
        Assert (($dcOn - $dcOff) -eq 1 -and ($dcOn2 - $dcOff) -eq 1) ("Game view draw calls {0} with the beads, {1} without: the whole chain costs 1 draw call" -f $dcOn, $dcOff)
    } else { Write-Host "  (get_performance_stats has no draw-call count)" -ForegroundColor DarkYellow }

    SpineShot "spine_orbit_default"

    Write-Host "== spine beads: visibility"
    Invoke-Unity hm_layer --layer avatars --visible false | Out-Null
    $a = Get-Spine
    Invoke-Unity hm_layer --layer avatars --visible true | Out-Null
    $b = Get-Spine
    Assert ($a.visible -and $b.visible) "beads stay with her skeleton while the avatars layer hides and shows the bodies"
    $raw = Invoke-Unity hm_shoes --action frame --role follow --clean true
    $c = Get-Spine
    Invoke-Unity hm_shoes --action release | Out-Null
    Invoke-Unity hm_orbit --height 1.6 --look 0.95 --distance 2.9 | Out-Null
    $d = Get-Spine
    Assert ((-not $c.visible) -and $d.visible) "hiding her skeleton lines (review close-up, hm_shoes --clean) hides the beads; release shows them"
    # the tour's graph views fade the full-size skeletons out (DanceTour.SetSkeletonWidth on her lines): the beads follow
    $tg = Invoke-Unity hm_tour --action goto --state dance_graph
    if ($tg -match '"state') {
        $hidden = Wait-Until { -not (Get-Spine).visible } 8
        Invoke-Unity hm_tour --action goto --state orbit | Out-Null
        $shown = Wait-Until { (Get-Spine).visible } 8
        Invoke-Unity hm_tour --action stop | Out-Null
        Assert ($hidden -and $shown) "the tour's dance-graph view hides the beads with her skeleton, orbit shows them again"
        Invoke-Unity hm_layer --layer hud --visible false | Out-Null
        Invoke-Unity hm_orbit --height 1.6 --look 0.95 --distance 2.9 | Out-Null
    } else { Write-Host "  (no dance graph view in this capture: $($tg.Substring(0, [math]::Min(120, $tg.Length))))" -ForegroundColor DarkYellow }
    Invoke-Unity hm_opacity --skeletons over | Out-Null
    Start-Sleep -Milliseconds 400
    $e = Get-Spine
    Invoke-Unity hm_opacity --skeletons dimmed | Out-Null
    Start-Sleep -Milliseconds 400
    $f = Get-Spine
    Assert ($e.stencilPass -eq 2 -and $f.stencilPass -eq 0) "skeletons-over-bodies option: the beads write her stencil bit only while it is on (Replace $($e.stencilPass) / Keep $($f.stencilPass))"

    Write-Host "== spine beads: close-ups"
    Invoke-Unity hm_spine --action frame --view front --distance 0.9 --elevation 8 --partner hide | Out-Null
    SpineShot "spine_close_front"
    Invoke-Unity hm_spine --action frame --view side --distance 0.9 --elevation 8 --partner hide | Out-Null
    SpineShot "spine_close_side"
    Invoke-Unity hm_spine --action release | Out-Null

    Write-Host "== spine beads: rhythm pulse"
    $pl = Get-Spine @("--action", "pulse")
    $p0 = @($pl.profiles.'0'.brightness)
    $n = $p0.Count
    $falls = 0
    for ($i = 0; $i -lt $n - 1; $i++) { if ($p0[$i + 1] -le $p0[$i] + 0.02) { $falls++ } }
    # the lit front: beads brighter than 0.6 (idle ~0.22, a full pulse clamps at 1) counted from the pelvis
    function LitFront($b) { $k = 0; foreach ($x in $b) { if ($x -gt 0.6) { $k++ } else { break } }; return $k }
    $fm1 = LitFront @($pl.profiles.'-1'.brightness); $f0 = LitFront $p0
    $f1 = LitFront @($pl.profiles.'+1'.brightness); $f2 = LitFront @($pl.profiles.'+2'.brightness)
    Assert ($n -gt 0 -and ($p0[0] - $p0[$n - 1]) -gt 0.3 -and $falls -ge [math]::Floor(0.85 * ($n - 1))) `
        ("pulse mid-travel at frame {0}: brightness falls from pelvis {1:0.00} to neck {2:0.00} ({3}/{4} steps non-increasing)" -f $pl.frame, $p0[0], $p0[$n - 1], $falls, ($n - 1))
    Assert ($fm1 -lt $f0 -and $f0 -gt 0 -and $f0 -lt $n -and [math]::Max($f1, $f2) -gt $f0) `
        ("the pulse climbs the chain bead by bead: lit beads from the pelvis {0} -> {1} -> {2} -> {3} of {4} over frames {5}..{6}" -f $fm1, $f0, $f1, $f2, $n, ($pl.frame - 1), ($pl.frame + 2))
    Write-Host ("         profiles: -1 [{0}]  0 [{1}]  +1 [{2}]  +2 [{3}]" -f (($pl.profiles.'-1'.brightness | ForEach-Object { "{0:0.00}" -f $_ }) -join " "), `
        (($p0 | ForEach-Object { "{0:0.00}" -f $_ }) -join " "), (($pl.profiles.'+1'.brightness | ForEach-Object { "{0:0.00}" -f $_ }) -join " "), `
        (($pl.profiles.'+2'.brightness | ForEach-Object { "{0:0.00}" -f $_ }) -join " "))
    Invoke-Unity hm_spine --action frame --view side --distance 1.1 --elevation 5 --partner hide | Out-Null
    SpineShot "spine_rhythm_pulse"
    Invoke-Unity hm_spine --action release | Out-Null

    Write-Host "== spine beads: physics mode"
    Invoke-Transport seek @("--time", (NumSp ($base + 4.0))) | Out-Null
    Invoke-Unity hm_skeleton --mode physics | Out-Null
    $ph = Get-Spine
    $j0 = @($ph.jointRgb[0]); $b0 = @($ph.rgb[0]); $j4 = @($ph.jointRgb[4]); $bl = @($ph.rgb[$ph.count - 1])
    $err = [math]::Max([math]::Max([math]::Abs($j0[0] - $b0[0]), [math]::Abs($j0[1] - $b0[1])), [math]::Abs($j0[2] - $b0[2]))
    $err = [math]::Max($err, [math]::Max([math]::Max([math]::Abs($j4[0] - $bl[0]), [math]::Abs($j4[1] - $bl[1])), [math]::Abs($j4[2] - $bl[2])))
    Assert ($ph.skeletonMode -eq "physics" -and $err -lt 0.01 -and $b0[2] -gt $b0[0]) ("physics mode: beads take the trunk load colour (pelvis bead {0:0.00},{1:0.00},{2:0.00} = her pelvis joint, compression blue; max error {3:0.000})" -f $b0[0], $b0[1], $b0[2], $err)
    Invoke-Unity hm_spine --action frame --view side --distance 1.1 --elevation 8 --partner hide | Out-Null
    SpineShot "spine_physics"
    Invoke-Unity hm_spine --action release | Out-Null
    Invoke-Unity hm_skeleton --mode rhythm | Out-Null
    Invoke-Unity hm_layer --layer hud --visible true | Out-Null
}

if ($spineStandalone) {
    Write-Host "== console"
    $console = (Invoke-Unity console --level error --tail 50) | ConvertFrom-Json
    $app = @($console.entries | Where-Object { $_.message -notmatch '/api/exec|^Pipeline: ' })
    Assert ($app.Count -eq 0) "no runtime errors ($($app.Count))"
    foreach ($e in $app) { Write-Host "  error: $($e.message)" -ForegroundColor Red }
    Invoke-Unity editor_stop | Out-Null
    if ($failures.Count) {
        Write-Host "`n$($failures.Count) FAILED" -ForegroundColor Red
        foreach ($x in $failures) { Write-Host "  $x" -ForegroundColor Red }
        exit 1
    }
    Write-Host "`nall spine checks passed" -ForegroundColor Green
}
