<#
Floor-craft playtest (VIEWER_SPEC 3.1 / 3.5 / 3.6 / 3.8 / 3.9): the leader's STABLE T axis, the faded floor record (old
Ts, the dotted T-to-T path, axis pivots, dials), the physics-mode balance axes and the small, faint floor crosses.
Standalone: enters Play mode, loads each capture, asserts on hm_state and saves review screenshots.

    powershell -File Tools/playtest_floorcraft.ps1 [-Capture <capture>[,<capture>...]] [-ReviewDir dir] [-NoShots]

-Capture defaults to every StreamingAssets capture with a version-3 floorcraft.json. Screenshots of the first capture are
named unity_<what>.png, later ones unity_<what>_<2-character capture prefix>.png.

Checks (captures with a version-3 floorcraft.json; others only get the floor and the live-axis fallback):
  - floor crosses small and faint: arm <= 4.5 cm, line <= 6 mm, brightness <= 0.5 of the earlier look
  - the T is STATIC while the leader walks on its axes: over the longest T, from the end of its forming window to its
    end, the drawn T (id, origin, heading) never changes (<= 1 mm, <= 0.01 deg) while his chest moves along the stem /
    crossbar (span >= 15 cm) and stays inside the T's plus-shaped region on most frames
  - a NEW T when he leaves or rotates: at every T boundary the old T is current up to its end and the next one after it
    forms; Ts that touch differ by more than pivot_deg in heading (rotation) or by more than the corridor half width
    in origin (exit); nothing is current in a gap between Ts (a counterbalance, or brief Ts dropped as flashes); every
    T but the last holds >= 0.7 s (~1 beat) after forming (no T only flashes)
  - the record: nothing from the future at the start; at the end every old T (faint teal), drawable transition, axis
    pivot and dial is on the floor
  - dials: none before t0; mid-way the running value is strictly between 0 and |degrees|; at the end the read-out is
    |degrees|; kept afterwards. The counterbalance dial is compared with a human label when the take has one
    (work/<take>/counterbalance/truth_user.json)
  - physics mode: balance axes only in physics mode; hidden with no grounded foot; drawn from the skeleton's chest
    (<= 2 cm from the analysis' chest); brightness a non-decreasing function of the alignment score and
    non-increasing with the tilt where the tilt limits the score; dashed exactly below the aligned score; the couple
    axis only inside a counterbalance pivot, brightness from the couple score; verdicts only in physics mode, on a
    dial only from its end (never while it runs), on pivots only when graded (a pivot on one foot)
  - the record fades: at the end of the take no pivot label older than ~2 measures and no dial label but the
    counterbalance's (and recent ones)
Screenshots: <ReviewDir>/unity_*.png (overhead at 25/50/75/100 %, T static while walking, a pivot, the counterbalance
dial, physics balance axes bright / dim, floor crosses).
#>
param(
    [string[]]$Capture = @(),
    [string]$ReviewDir = "",
    [switch]$NoShots
)

$Capture = @($Capture | ForEach-Object { $_ -split "," } | Where-Object { $_ })
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)
if ($Capture.Count -eq 0) {
    $Capture = @(Get-ChildItem (Join-Path "Assets" "StreamingAssets") -Directory | Where-Object {
        $fcPath = Join-Path $_.FullName "floorcraft.json"
        (Test-Path $fcPath) -and ((Get-Content $fcPath -Raw | ConvertFrom-Json).version -ge 3)
    } | Sort-Object Name | ForEach-Object { $_.Name })
}
$script:projectPath = (Get-Location).Path
$failures = [System.Collections.Generic.List[string]]::new()
$inv = [System.Globalization.CultureInfo]::InvariantCulture
if (-not $ReviewDir) { $ReviewDir = Join-Path (Split-Path (Get-Location) -Parent) "dancecap\work\review\unity_floorcraft" }
New-Item -ItemType Directory -Force $ReviewDir | Out-Null

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

function Num([double]$x) { return $x.ToString("R", $inv) }

function Seek([double]$t) {
    $raw = Invoke-Unity hm_transport --action seek --time (Num $t)
    try { return (ConvertFrom-UnityJson $raw) } catch { return Get-State }
}

function Seek-Frame([int]$k) { return Seek $script:frameTimes[[math]::Max(0, [math]::Min($k, $script:frameTimes.Count - 1))] }

function Frame-At([double]$t) {
    # first frame at or after t (clamped)
    for ($k = 0; $k -lt $script:frameTimes.Count; $k++) { if ($script:frameTimes[$k] -ge $t - 1e-4) { return $k } }
    return $script:frameTimes.Count - 1
}

function Frame-Before([double]$t) {
    for ($k = $script:frameTimes.Count - 1; $k -ge 0; $k--) { if ($script:frameTimes[$k] -le $t + 1e-4) { return $k } }
    return 0
}

function Assert($condition, [string]$message) {
    if ($condition) { Write-Host "  ok   $message" } else { Write-Host "  FAIL $message" -ForegroundColor Red; $failures.Add("[$script:current] $message") }
}

function Shot([string]$name) {
    if ($NoShots) { return }
    Start-Sleep -Milliseconds 700
    Invoke-Unity capture_game_view --save_path "Screenshots/$name.png" --width 1280 --height 720 | Out-Null
    $src = Join-Path (Get-Location) "Assets\Screenshots\$name.png"
    if (Test-Path $src) { Copy-Item $src (Join-Path $ReviewDir "$name.png") -Force; Write-Host "  shot $ReviewDir\$name.png" }
}

function Wait-Until([scriptblock]$condition, [int]$seconds = 60) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 500 }
    return $false
}

function Overhead([double]$t) {
    # the Overhead floor craft view state, held paused at t (goto may start playback: pause after it)
    Invoke-Unity hm_tour --action goto --state overhead | Out-Null
    Invoke-Unity hm_transport --action pause | Out-Null
    Start-Sleep -Milliseconds 1500  # let the state's camera blend finish
    return Seek $t
}

$script:current = "setup"
Write-Host "== enter play mode"
if (((Invoke-Unity editor_status) | ConvertFrom-Json).playMode -ne "playing") {
    Invoke-Unity open_scene --path Assets/head-movement.unity | Out-Null
    Invoke-Unity editor_play | Out-Null
    Assert (Wait-Until { try { ((Invoke-Unity editor_status) | ConvertFrom-Json).playMode -eq "playing" } catch { $false } } 180) "editor playing"
    Start-Sleep -Seconds 2
}

foreach ($cap in $Capture) {
    $script:current = $cap
    $short = if ($cap -eq $Capture[0]) { "" } else { "_$($cap.Substring(0, [math]::Min(2, $cap.Length)))" }
    Invoke-Unity clear_console | Out-Null
    Write-Host "== load $cap"
    Invoke-Unity hm_load --name $cap | Out-Null
    Assert (Wait-Until { $st = Get-State; $st.audioLoaded -and $st.capture -eq $cap } 120) "capture loaded with audio"
    Invoke-Unity hm_transport --action pause | Out-Null
    Invoke-Unity hm_tour --action stop | Out-Null
    Invoke-Unity hm_skeleton --mode rhythm | Out-Null
    foreach ($layer in "grid", "axis", "floorcraft", "balance") { Invoke-Unity hm_layer --layer $layer --visible true | Out-Null }

    $capDir = Join-Path (Get-Location) "Assets\StreamingAssets\$cap"
    $capJson = Get-Content (Join-Path $capDir "capture.json") -Raw | ConvertFrom-Json
    $timeToAudio = if ($null -ne $capJson.time_to_audio) { [double]$capJson.time_to_audio } else { 0.0 }
    $script:frameTimes = @((Get-Content (Join-Path $capDir $capJson.times) -Raw | ConvertFrom-Json) | ForEach-Object { [double]$_ + $timeToAudio })
    $n = $script:frameTimes.Count
    $t0 = $script:frameTimes[0]; $tEnd = $script:frameTimes[$n - 1]
    $take = if ($capJson.provenance) { $capJson.provenance.take } else { $null }
    function Hud([double]$t) { return $t - $t0 }

    Write-Host "== floor crosses (VIEWER_SPEC 3.1: smaller and fainter)"
    $s = Seek-Frame ([int]($n / 2))
    Assert ($s.layers.grid -and $s.grid.crosses -gt 20) "floor crosses on: $($s.grid.crosses) at 1 m"
    Assert ([double]$s.grid.crossArmM -le 0.045 -and [double]$s.grid.crossWidthM -le 0.006 -and [double]$s.grid.crossBrightness -le 0.5) `
        ("crosses small and faint: {0:0} cm across, {1:0.0} mm lines, brightness {2:0.00}" -f (200 * [double]$s.grid.crossArmM), (1000 * [double]$s.grid.crossWidthM), [double]$s.grid.crossBrightness)
    if ($cap -eq $Capture[0]) {
        # the same camera and frame (mid-take) as the review's "before" stills
        Invoke-Unity hm_orbit --azimuth 60 --elevation 25 --radius 3.4 | Out-Null
        Seek-Frame ([int]($n / 2)) | Out-Null
        Invoke-Unity hm_orbit --azimuth 60 --elevation 25 --radius 3.4 | Out-Null
        Shot "unity_crosses_after_orbit"
        Invoke-Unity hm_orbit --azimuth 90 --elevation 89 --radius 5.2 | Out-Null
        Shot "unity_crosses_after_overhead"
    }

    $fc = $s.floorCraft
    if ($null -eq $fc) { Assert $false "floor craft overlay present"; continue }
    if ($fc.mode -ne "t_axes") {
        Write-Host "  (no version-3 floorcraft.json: live-axis fallback, $($fc.dials) dial(s) from counterbalance pivots)"
        Assert ($fc.axisVisible -and $fc.recordVisible) "axis + floor-craft layers on by default ($($fc.axisSource))"
        if ($null -ne $s.floorCraft.axisToChestM) { Assert ([double]$s.floorCraft.axisToChestM -le 0.05) ("live axis under the leader's chest ({0:0} mm)" -f (1000 * [double]$s.floorCraft.axisToChestM)) }
        continue
    }

    Assert ($fc.axisVisible -and $fc.recordVisible) "axis + floor-craft layers on by default ($($fc.axisSource), $($fc.tAxes) Ts, $($fc.transitions) transitions, $($fc.axisPivots) axis pivots, $($fc.dials) dials)"
    $ts = @($fc.tList)  # [id, t0, tFormed, t1, ox, oz, yaw, start, end, sameAs]
    $hw = [double]$fc.tHalfWidthM; $pivotDeg = [double]$fc.pivotDeg
    $dt = if ($n -gt 1) { ($tEnd - $t0) / ($n - 1) } else { 1 / 30 }

    Write-Host "== the T is static while he walks on its axes (VIEWER_SPEC 3.6)"
    $best = $null; $bestLen = 0
    foreach ($a in $ts) { $len = [double]$a[3] - [double]$a[2]; if ($len -gt $bestLen) { $bestLen = $len; $best = $a } }
    $ka = Frame-At ([double]$best[2] + $dt); $kb = Frame-Before ([double]$best[3] - $dt)
    $origins = @(); $yaws = @(); $ids = @(); $along = @(); $side = @(); $inRegion = 0; $samples = 0
    $stepT = if ($bestLen -lt 2.0) { 1 } else { 3 }
    for ($k = $ka; $k -le $kb; $k += $stepT) {
        $st = Seek-Frame $k
        $f = $st.floorCraft
        $ids += [int]$f.currentT
        if ([int]$f.currentT -ne [int]$best[0]) { continue }
        $origins += , @([double]$f.axisOrigin[0], [double]$f.axisOrigin[1]); $yaws += [double]$f.currentTYawShownDeg
        $along += [double]$f.chestInT[0]; $side += [double]$f.chestInT[1]
        if ($f.chestInRegion) { $inRegion++ }
        $samples++
    }
    $maxMove = 0.0; $maxYaw = 0.0
    foreach ($o in $origins) { $maxMove = [math]::Max($maxMove, [math]::Sqrt([math]::Pow($o[0] - $origins[0][0], 2) + [math]::Pow($o[1] - $origins[0][1], 2))) }
    foreach ($y in $yaws) { $maxYaw = [math]::Max($maxYaw, [math]::Abs($y - $yaws[0])) }
    $spanAlong = ($along | Measure-Object -Maximum -Minimum); $spanSide = ($side | Measure-Object -Maximum -Minimum)
    $walk = [math]::Max($spanAlong.Maximum - $spanAlong.Minimum, $spanSide.Maximum - $spanSide.Minimum)
    Assert ($samples -gt 5 -and @($ids | Where-Object { $_ -ne [int]$best[0] }).Count -eq 0) ("T {0} is the current T on all {1} sampled frames of HUD {2:0.00}-{3:0.00} s" -f $best[0], $ids.Count, (Hud $best[2]), (Hud $best[3]))
    Assert ($maxMove -le 0.001 -and $maxYaw -le 0.01) ("T {0} does not move: origin {1:0.0} mm, heading {2:0.000} deg over {3} frames" -f $best[0], ($maxMove * 1000), $maxYaw, $samples)
    $walkText = "his chest on the T: {0:0.00} m along the stem ({1:+0.00;-0.00}..{2:+0.00;-0.00}), {3:0.00} m along the crossbar ({4:+0.00;-0.00}..{5:+0.00;-0.00})" -f `
        ($spanAlong.Maximum - $spanAlong.Minimum), $spanAlong.Minimum, $spanAlong.Maximum, ($spanSide.Maximum - $spanSide.Minimum), $spanSide.Minimum, $spanSide.Maximum
    if ($bestLen -ge 2.0) { Assert ($walk -ge 0.15) "meanwhile he walks on it - $walkText" }
    else { Write-Host ("  (longest T held only {0:0.00} s - not asserted: {1})" -f $bestLen, $walkText) }
    Assert ($samples -gt 0 -and $inRegion / $samples -ge 0.8) ("his chest stays in the T's allowed region (+-{0:0.00} m corridors) on {1:P0} of those frames" -f $hw, ($inRegion / [math]::Max(1, $samples)))
    if (-not $NoShots) {
        # close-ups of the static T while he moves on it: one parked camera aimed at the T for all three stills
        Invoke-Unity hm_orbit --mode park --at ("{0},{1}" -f (Num $best[4]), (Num $best[5])) --azimuth 210 --elevation 50 --radius 2.8 | Out-Null
        $i = 0
        foreach ($frac in 0.1, 0.5, 0.9) {
            $i++
            Seek ([double]$best[2] + $frac * ([double]$best[3] - [double]$best[2])) | Out-Null
            Shot ("unity_t_static{0}_{1}" -f $short, $i)
        }
        Invoke-Unity hm_orbit --azimuth 60 --elevation 25 --radius 3.4 | Out-Null  # un-park
    }

    Write-Host "== a new T when he leaves the T or turns it (> $pivotDeg deg)"
    $bad = 0; $checked = 0
    for ($i = 0; $i -lt $ts.Count; $i++) {
        $a = $ts[$i]
        $kEnd = Frame-Before ([double]$a[3] - 0.5 * $dt)
        if ($kEnd -gt (Frame-At ([double]$a[2]))) {
            $st = Seek-Frame $kEnd
            if ([int]$st.floorCraft.currentT -ne [int]$a[0]) { $bad++; Write-Host ("  T {0}: not current just before its end (HUD {1:0.00}: current {2})" -f $a[0], (Hud $script:frameTimes[$kEnd]), $st.floorCraft.currentT) }
        }
        if ($i + 1 -lt $ts.Count) {
            $b = $ts[$i + 1]
            $kNew = Frame-At ([double]$b[2] + 0.5 * $dt)
            $st = Seek-Frame $kNew
            if ([int]$st.floorCraft.currentT -ne [int]$b[0]) { $bad++; Write-Host ("  T {0}: not current after it formed (HUD {1:0.00}: current {2})" -f $b[0], (Hud $script:frameTimes[$kNew]), $st.floorCraft.currentT) }
            $dYaw = [math]::Abs(((([double]$b[6] - [double]$a[6]) % 360) + 540) % 360 - 180)
            $dOrigin = [math]::Sqrt([math]::Pow([double]$b[4] - [double]$a[4], 2) + [math]::Pow([double]$b[5] - [double]$a[5], 2))
            $why = [string]$a[8]
            $touch = [double]$b[1] -le [double]$a[3] + 2 * $dt
            # Ts separated by a gap (a counterbalance, or brief Ts dropped as flashes) are checked by "nothing current"
            $ok = (-not $touch) -or ($why -like "*rotation*" -and $dYaw -gt $pivotDeg) -or ($why -like "*exit*" -and $dOrigin -gt $hw) -or ($why -notlike "*rotation*" -and $why -notlike "*exit*")
            if (-not $ok) { $bad++ }
            Write-Host ("  T {0} -> T {1} at HUD {2:0.00} s: {3}, heading {4:0} deg, origin {5:0.00} m{6}" -f $a[0], $b[0], (Hud $b[1]), $why, $dYaw, $dOrigin, $(if ($ok) { "" } else { "  <- not a new T" }))
            if ([double]$b[1] -gt [double]$a[3] + 2 * $dt) {
                $st = Seek ((([double]$a[3] + [double]$b[1]) / 2))
                if ([int]$st.floorCraft.currentT -ne -1) { $bad++; Write-Host ("  gap HUD {0:0.00}-{1:0.00}: a T is current ({2})" -f (Hud $a[3]), (Hud $b[1]), $st.floorCraft.currentT) }
            }
            $checked++
        }
    }
    Assert ($bad -eq 0) "every T boundary hands over: $checked new Ts after a rotation / exit, the old T current up to its end, none in the gaps ($bad wrong)"
    $flash = @($ts | Select-Object -SkipLast 1 | Where-Object { [double]$_[3] - [double]$_[2] -lt 0.7 })
    Assert ($flash.Count -eq 0) ("no T only flashes: every T but the last holds >= 0.7 s after forming (shortest {0:0.00} s)" -f `
        (@($ts | Select-Object -SkipLast 1 | ForEach-Object { [double]$_[3] - [double]$_[2] }) | Measure-Object -Minimum).Minimum)

    Write-Host "== the record (VIEWER_SPEC 3.5)"
    $first = Seek-Frame 0
    Assert ([int]$first.floorCraft.oldTShown -eq 0 -and [int]$first.floorCraft.transitionsShown -eq 0 -and [int]$first.floorCraft.dialsShown -eq 0) `
        "nothing from the future at the start ($($first.floorCraft.oldTShown) old Ts, $($first.floorCraft.transitionsShown) transitions, $($first.floorCraft.dialsShown) dials)"
    $end = Seek-Frame ($n - 1)
    $f = $end.floorCraft
    Assert ([int]$f.oldTShown + $(if ([int]$f.currentT -ge 0) { 1 } else { 0 }) -eq $ts.Count -and [int]$f.transitionsShown -eq [int]$f.transitionsDrawable -and
            [int]$f.axisPivotsShown -eq [int]$f.axisPivotsDrawable -and [int]$f.dialsShown -eq [int]$f.dials) `
        ("by the end the floor keeps the whole record: {0} old Ts (+ current {1}), {2}/{3} transitions, {4}/{5} axis pivots, {6}/{7} dials" -f `
            $f.oldTShown, $f.currentT, $f.transitionsShown, $f.transitionsDrawable, $f.axisPivotsShown, $f.axisPivotsDrawable, $f.dialsShown, $f.dials)
    $recentS = [double]$f.recentSeconds
    $cbDials = @($fc.dialList | Where-Object { $_[1] -eq "counterbalance" }).Count
    $recentDials = @($fc.dialList | Where-Object { [double]$_[3] -gt $tEnd - $recentS }).Count
    $recentPivots = @($fc.pivotList | Where-Object { [int]$_[5] -lt 0 -and [double]$_[2] -gt $tEnd - $recentS }).Count
    Assert ([int]$f.dialLabelsShown -le $cbDials + $recentDials -and [int]$f.pivotLabelsShown -le $recentPivots) `
        ("the record fades without words: at the end {0} dial label(s) (counterbalance {1} + recent {2}), {3} pivot label(s) (recent {4})" -f `
            $f.dialLabelsShown, $cbDials, $recentDials, $f.pivotLabelsShown, $recentPivots)
    $teal = @($f.oldTColour)
    Assert ([double]$f.oldTBrightnessMax -le 0.1 -and [double]$teal[2] -gt [double]$teal[0] -and [double]$teal[1] -gt [double]$teal[0]) `
        ("old Ts very faint teal-blue: brightness {0:0.00}, colour ({1:0.00}, {2:0.00}, {3:0.00})" -f $f.oldTBrightnessMax, $teal[0], $teal[1], $teal[2])

    Write-Host "== dials (repeated turns)"
    $truth = $null
    if ($take) {
        $tp = Join-Path (Split-Path (Get-Location) -Parent) "dancecap\work\$take\counterbalance\truth_user.json"
        if (Test-Path $tp) { $truth = Get-Content $tp -Raw | ConvertFrom-Json }
    }
    foreach ($d in @($fc.dialList)) {
        # [id, kind, t0, t1, degrees, verdict, runSource, runRawEnd, centre, radius]
        $id = [int]$d[0]; $dt0 = [double]$d[2]; $dt1 = [double]$d[3]; $abs = [math]::Abs([double]$d[4])
        $before = Seek-Frame ((Frame-At $dt0) - 1)
        $shownBefore = $null -ne $before.floorCraft.dial -and [int]$before.floorCraft.dial.id -eq $id
        $mid = Seek (($dt0 + $dt1) / 2)
        $md = $mid.floorCraft.dial
        $endK = Frame-At $dt1
        $atEnd = Seek-Frame $endK
        $ed = $atEnd.floorCraft.dial
        $after = Seek ([math]::Min($tEnd, $dt1 + 2.0))
        $ad = $after.floorCraft.dial
        $want = "{0}{1}" -f [math]::Round($abs), [char]0x00B0  # ASCII-only script: PowerShell 5.1 reads it as ANSI
        Assert (-not $shownBefore -and $md.id -eq $id -and $md.running -and [math]::Abs([double]$md.runningDeg) -gt 0 -and [math]::Abs([double]$md.runningDeg) -lt $abs) `
            ("dial {0} ({1}, HUD {2:0.00}-{3:0.00} s): none before t0; mid-way {4:0} deg of {5:0} deg ({6})" -f $id, $d[1], (Hud $dt0), (Hud $dt1), [math]::Abs([double]$md.runningDeg), $abs, $d[6])
        Assert ($ed.id -eq $id -and ([string]$ed.label).Split("`n")[0] -eq $want) ("dial {0} reads {1} at its end (label '{2}')" -f $id, $want, ([string]$ed.label).Split("`n")[0])
        Assert ($null -ne $ad -and [int]$after.floorCraft.dialsShown -ge 1 -and ($ad.id -ne $id -or (-not $ad.running -and ([string]$ad.label).Split("`n")[0] -eq $want))) ("dial {0} stays on the floor afterwards ({1} dial(s) shown 2 s later)" -f $id, $after.floorCraft.dialsShown)
        if ($d[1] -eq "counterbalance" -and $null -ne $truth -and $truth.counterbalance) {
            $h = @($truth.counterbalance[0].hud)
            $a0 = [math]::Max((Hud $dt0), [double]$h[0]); $a1 = [math]::Min((Hud $dt1), [double]$h[1])
            $iou = [math]::Max(0, $a1 - $a0) / ([math]::Max((Hud $dt1), [double]$h[1]) - [math]::Min((Hud $dt0), [double]$h[0]))
            Assert ($iou -ge 0.9) ("counterbalance dial HUD {0:0.00}-{1:0.00} s vs the human label {2:0.00}-{3:0.00} s: IoU {4:0.00}" -f (Hud $dt0), (Hud $dt1), $h[0], $h[1], $iou)
        }
    }

    Write-Host "== physics mode: balance axes (VIEWER_SPEC 3.8)"
    $s = Seek-Frame ([int]($n / 2))
    Assert ($null -ne $s.balanceAxes -and $s.balanceAxes.loaded -and -not $s.balanceAxes.visible -and [int]$s.floorCraft.verdictsShown -eq 0) "rhythm mode (default): no balance axes, no verdicts on the floor"
    Invoke-Unity hm_skeleton --mode physics | Out-Null
    $rows = @(); $hiddenBad = 0; $coupleBad = 0; $chestFar = 0; $verdictless = 0; $runningVerdict = 0; $dashBad = 0; $pivotIconBad = 0
    $step = [math]::Max(4, [int]($n / 160))
    for ($k = 0; $k -lt $n; $k += $step) {
        $st = Seek-Frame $k
        $b = $st.balanceAxes
        if (-not $b.visible) { $hiddenBad++; continue }
        foreach ($who in "lead", "follow") {
            $x = $b.$who
            if ($x.stance -eq "none") { if ($x.shown -or [double]$x.brightness -gt 0) { $hiddenBad++ }; continue }
            if (-not $x.shown) { continue }
            if ([double]$x.chestToSkeletonM -gt 0.02) { $chestFar++ }
            if ([bool]$x.dashed -ne ([double]$x.score -lt 0.5)) { $dashBad++ }
            $rows += [pscustomobject]@{ who = $who; k = $k; score = [double]$x.score; tilt = [double]$x.tiltDeg; off = [double]$x.offsetM; b = [double]$x.brightness }
        }
        $inPivot = [int]$b.couplePivot -ge 0
        if ($b.couple.shown -ne $inPivot) { $coupleBad++ }
        if ($b.couple.shown) { $rows += [pscustomobject]@{ who = "couple"; k = $k; score = [double]$b.couple.score; tilt = [double]$b.couple.tiltDeg; off = [double]$b.couple.offsetM; b = [double]$b.couple.brightness } }
        if ([int]$st.floorCraft.dialVerdictsShown -ne [int]$st.floorCraft.dialVerdictsDue) { $verdictless++ }
        if ($null -ne $st.floorCraft.dial -and $st.floorCraft.dial.running -and ([string]$st.floorCraft.dial.label).Contains("`n")) { $runningVerdict++ }
    }
    # brightness is a non-decreasing function of the score: no pair with a higher score and a lower brightness
    $sorted = @($rows | Sort-Object score, b)
    $inversions = 0
    for ($i = 1; $i -lt $sorted.Count; $i++) { if ($sorted[$i].b -lt $sorted[$i - 1].b - 1e-4) { $inversions++ } }
    $pts = $s.balanceAxes.params  # good / bad offset, good / bad tilt
    function Ramp([double]$x, [double]$good, [double]$badv) { return [math]::Min(1, [math]::Max(0, ($badv - $x) / ($badv - $good))) }
    # where the tilt limits the score (tilt ramp below the offset ramp), a more vertical axis is never dimmer
    $tiltRows = @($rows | Where-Object { (Ramp $_.tilt $pts[2] $pts[3]) -lt (Ramp $_.off $pts[0] $pts[1]) - 0.02 } | Sort-Object tilt)
    $tiltInv = 0
    for ($i = 1; $i -lt $tiltRows.Count; $i++) { if ($tiltRows[$i].b -gt $tiltRows[$i - 1].b + 0.03) { $tiltInv++ } }
    $lit = @($rows | Where-Object { $_.score -ge 0.9 }).Count; $dim = @($rows | Where-Object { $_.score -le 0.1 }).Count
    # verticality: the mean brightness falls from bin to bin of the chest-foot tilt (<3, 3-6, 6-9, >9 deg)
    $bins = @()
    foreach ($edge in @(@(0, 3), @(3, 6), @(6, 9), @(9, 90))) {
        $m = @($rows | Where-Object { $_.tilt -ge $edge[0] -and $_.tilt -lt $edge[1] })
        if ($m.Count -ge 3) { $bins += [pscustomobject]@{ lo = $edge[0]; hi = $edge[1]; n = $m.Count; b = ($m | Measure-Object b -Average).Average } }
    }
    $binsDown = $true
    for ($i = 1; $i -lt $bins.Count; $i++) { if ($bins[$i].b -ge $bins[$i - 1].b) { $binsDown = $false } }
    Assert ($bins.Count -ge 3 -and $binsDown) ("the more vertical the axis, the brighter: mean brightness by tilt {0}" -f (($bins | ForEach-Object { "{0}-{1} deg {2:0.00} (n {3})" -f $_.lo, $_.hi, $_.b, $_.n }) -join ", "))
    Assert ($rows.Count -gt 20 -and $inversions -eq 0) ("balance axis brightness rises with the alignment score: {0} axes sampled ({1} lit >= 0.9, {2} dim <= 0.1), {3} inversions" -f $rows.Count, $lit, $dim, $inversions)
    Assert ($tiltInv -eq 0) ("where the tilt limits the score, the more vertical axis is the brighter one ({0} tilt-limited axes, {1} inversions)" -f $tiltRows.Count, $tiltInv)
    Assert ($hiddenBad -eq 0) "no balance axis without a grounded foot (and the layer visible in physics mode): $hiddenBad wrong"
    Assert ($chestFar -eq 0) "each axis starts at the skeleton's chest (<= 2 cm from the analysis' chest): $chestFar further"
    Assert ($coupleBad -eq 0) ("the couple axis (her anchored foot -> the couple's COM) shows exactly inside the counterbalance pivot ({0} couple samples, {1} wrong)" -f @($rows | Where-Object { $_.who -eq "couple" }).Count, $coupleBad)
    if (@($fc.dialList).Count -gt 0) {
        Assert ($verdictless -eq 0) "physics mode: every settled dial carries its verdict ($verdictless frames without)"
        Assert ($runningVerdict -eq 0) "physics mode: no verdict on a dial while it runs - it judges the whole turn ($runningVerdict frames with one)"
    }
    Assert ($dashBad -eq 0) "balance axes dashed exactly below the aligned score 0.5 ($dashBad wrong)"
    $endP = Seek-Frame ($n - 1)
    $fe = $endP.floorCraft
    Assert ([int]$fe.verdictsShown -eq [int]$fe.axisPivotsGraded + [int]$fe.dialVerdictsDue) `
        ("verdict icons only on graded turns: {0} shown at the end = {1} graded pivot(s) (one foot) + {2} dial(s); {3} pivots drawn" -f `
            $fe.verdictsShown, $fe.axisPivotsGraded, $fe.dialVerdictsDue, $fe.axisPivotsShown)
    if (-not $NoShots) {
        # bright vs dim: the leader's most and least aligned grounded frames (score), physics view
        $ld = @($rows | Where-Object { $_.who -eq "lead" } | Sort-Object score)
        if ($ld.Count -gt 2) {
            foreach ($pair in @(@("bright", $ld[$ld.Count - 1]), @("dim", $ld[0]))) {
                Seek-Frame $pair[1].k | Out-Null
                Invoke-Unity hm_orbit --azimuth 150 --elevation 18 --radius 3.0 | Out-Null
                Shot ("unity_physics_balance_{0}{1}" -f $pair[0], $short)
                Write-Host ("    {0}: frame {1}, lead score {2:0.00}, tilt {3:0.0} deg, COM {4:0.0} cm off" -f $pair[0], $pair[1].k, $pair[1].score, $pair[1].tilt, ($pair[1].off * 100))
            }
        }
    }

    if (-not $NoShots) {
        Write-Host "== review shots"
        $cb = @($fc.dialList | Where-Object { $_[1] -eq "counterbalance" }) | Select-Object -First 1
        if ($null -ne $cb) {
            $c0 = [double]$cb[2]; $c1 = [double]$cb[3]
            $at = "{0},{1}" -f (Num $cb[8][0]), (Num $cb[8][1])
            # physics mode: the couple axis over her anchored foot, the dial with its verdict
            Seek ($c0 + 0.5 * ($c1 - $c0)) | Out-Null
            Invoke-Unity hm_orbit --mode park --at $at --azimuth 200 --elevation 22 --radius 3.0 --look 0.6 | Out-Null
            Shot "unity_counterbalance_dial_physics"
            Invoke-Unity hm_skeleton --mode rhythm | Out-Null
            # one parked camera on the dial: a quarter in, half-way, at its end (the read-out fills to the total)
            Invoke-Unity hm_orbit --mode park --at $at --azimuth 200 --elevation 45 --radius 2.6 | Out-Null
            foreach ($pair in @(@("quarter", 0.25), @("mid", 0.5), @("end", 1.0))) {
                Seek ($c0 + $pair[1] * ($c1 - $c0)) | Out-Null
                Shot ("unity_counterbalance_dial_{0}" -f $pair[0])
            }
            Invoke-Unity hm_orbit --azimuth 60 --elevation 25 --radius 3.4 | Out-Null  # un-park
            Overhead ($c0 + 0.75 * ($c1 - $c0)) | Out-Null
            Shot "unity_counterbalance_dial_overhead"
            Invoke-Unity hm_tour --action stop | Out-Null
        }
        Invoke-Unity hm_skeleton --mode rhythm | Out-Null
        # an axis pivot (not folded into a dial), just after it ends: rhythm and physics
        $pv = @($fc.pivotList | Where-Object { [int]$_[5] -lt 0 } | Sort-Object { -[math]::Abs([double]$_[3]) } | Where-Object { [math]::Abs([double]$_[3]) -le 180 }) | Select-Object -First 1
        if ($null -ne $pv) {
            Invoke-Unity hm_orbit --mode park --at ("{0},{1}" -f (Num $pv[8][0]), (Num $pv[8][1])) --azimuth 120 --elevation 55 --radius 2.4 | Out-Null
            Seek ([double]$pv[1] + 0.5 * ([double]$pv[2] - [double]$pv[1])) | Out-Null
            Shot ("unity_pivot{0}_during" -f $short)
            Seek ([double]$pv[2] + 0.25) | Out-Null
            Shot ("unity_pivot{0}" -f $short)
            Invoke-Unity hm_skeleton --mode physics | Out-Null
            Shot ("unity_pivot_physics{0}" -f $short)
            Invoke-Unity hm_skeleton --mode rhythm | Out-Null
            Invoke-Unity hm_orbit --azimuth 60 --elevation 25 --radius 3.4 | Out-Null  # un-park
            Write-Host ("    pivot {0}: {1:+0;-0} deg at HUD {2:0.00}-{3:0.00} s, verdict {4}" -f $pv[0], [double]$pv[3], (Hud $pv[1]), (Hud $pv[2]), $pv[4])
        }
        foreach ($pct in 25, 50, 75, 100) {
            $k = [math]::Min($n - 1, [int][math]::Round(($n - 1) * $pct / 100))
            Overhead $script:frameTimes[$k] | Out-Null
            Shot ("unity_overhead{0}_{1:000}" -f $short, $pct)
        }
        Invoke-Unity hm_tour --action stop | Out-Null
    }
    Invoke-Unity hm_skeleton --mode rhythm | Out-Null
    $err = (Invoke-Unity console_status | ConvertFrom-Json).groundTruth
    Assert ($err.consoleErrors -eq 0) "no console errors while testing $cap ($($err.consoleErrors))"
}

Write-Host ""
if ($failures.Count -eq 0) { Write-Host "ALL FLOOR-CRAFT CHECKS PASSED" -ForegroundColor Green }
else { Write-Host "$($failures.Count) FAILURE(S):" -ForegroundColor Red; $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red } }
exit $failures.Count
