<#
Hair checks (Assets/Hair/HairStrands), dot-sourced by Tools/playtest.ps1 inside its per-capture loop (uses its
Invoke-Unity / ConvertFrom-UnityJson / Invoke-Transport / Assert / Shot helpers and $cap / $prefix / $base). Runs
only for captures with hair_groom.json (python -m dancecap.hair_groom <take>):
  - counts within the Quest budget (<= 200 guides x 16 segments, one ribbon mesh), roots on the exported skin
  - the old LineRenderer HairSimulation is gone on v3 captures, no LineRenderer left at its default origin bar
  - seeks forward / backward, a looped measure, restart: no NaN, no stretch, no strand beyond its length, roots
    pinned, nothing near the floor / origin
  - a sweep of the whole capture: stability, CPU ms per simulation step, spread r95 / trailing angle vs the hair
    reference targets in hair_groom.json (dancecap docs/HAIR_REFERENCE.md section 3)
  - review screenshots of the fastest head moments from 3 angles, tip bloom on and off, copied to
    ../dancecap/work/<take>/review/hair_*.png (+ hair_shots.json for python -m dancecap.hair_groom <take> --sheet)
#>

$hairGroomPath = Join-Path (Get-Location) "Assets\StreamingAssets\$cap\hair_groom.json"
if (Test-Path $hairGroomPath) {
    Write-Host "== hair (groomed guide strands)"
    $invH = [System.Globalization.CultureInfo]::InvariantCulture
    function NumH([double]$x) { return $x.ToString("R", $invH) }
    function Get-Hair([string[]]$hairArgs = @()) {
        $raw = Invoke-Unity hm_hair @hairArgs
        try { return (ConvertFrom-UnityJson $raw) } catch { Write-Host "  (hm_hair unreadable) $($raw.Substring(0, [math]::Min(200, $raw.Length)))" -ForegroundColor DarkYellow; return $null }
    }

    $groomH = Get-Content $hairGroomPath -Raw | ConvertFrom-Json
    $capH = Get-Content (Join-Path (Get-Location) "Assets\StreamingAssets\$cap\capture.json") -Raw | ConvertFrom-Json
    $takeH = $capH.provenance.take
    $reviewH = if ($takeH) { Join-Path (Split-Path (Get-Location) -Parent) "dancecap\work\$takeH\review" } else { $null }
    Invoke-Unity hm_layer --layer avatars --visible true | Out-Null

    $h = Get-Hair
    Assert ($null -ne $h -and $h.present -and $h.visible) "hair present and visible"
    Assert ($h.guides -ge 100 -and $h.guides -le 200 -and $h.segments -eq 16) "hair: $($h.guides) guides x $($h.segments) segments, $($h.ribbons) ribbons, $($h.vertices) vertices, $($h.triangles) triangles (Quest <= 200 x 16)"
    Assert ($h.legacyHairObjects -eq 0) "old LineRenderer HairSimulation disabled on this v3 capture"
    Assert ($h.defaultLineRenderers -eq 0) "no LineRenderer left at its default bar from the origin ($($h.defaultLineRenderers))"
    Assert ($h.rootMismatchMm -lt 5) ("roots on the exported skin's scalp (groom vs skin {0:0.000} mm)" -f $h.rootMismatchMm)

    function Assert-HairSane([string]$what) {
        $s = Get-Hair @("--action", "sync")
        if ($null -eq $s) { Assert $false "${what}: hair state"; return }
        $ok = $s.nonFinite -eq 0 -and $s.maxStretch -lt 1.05 -and $s.maxReachRatio -le 1.0 -and $s.rootPinErrorMm -lt 0.5 -and
              $s.frame -eq $s.avatarFrame -and $s.minWorldY -gt 0.3
        Assert $ok ("{0}: frame {1}, stretch {2:0.000}, reach {3:0.00} of length, roots {4:0.000} mm, lowest hair {5:0.00} m, NaN {6}" -f `
            $what, $s.frame, $s.maxStretch, $s.maxReachRatio, $s.rootPinErrorMm, $s.minWorldY, $s.nonFinite)
    }

    foreach ($dt in 1.0, 4.8, 0.3, 5.5) {
        Invoke-Transport seek @("--time", (NumH ($base + $dt))) | Out-Null
        Start-Sleep -Milliseconds 300
        Assert-HairSane ("seek first frame + {0} s" -f $dt)
    }
    Invoke-Transport restart | Out-Null
    Start-Sleep -Milliseconds 300
    Assert-HairSane "restart"
    Invoke-Transport loop_on | Out-Null
    Invoke-Unity hm_transport --action play | Out-Null
    Start-Sleep -Seconds 4
    Invoke-Unity hm_transport --action pause | Out-Null
    Invoke-Transport loop_off | Out-Null
    Assert-HairSane "after 4 s of a looped measure"
    $h = Get-Hair
    Assert ($h.strandResets -eq 0) "no strand re-hung by the blow-up guard ($($h.strandResets))"

    Write-Host "== hair: whole-capture sweep"
    $sw = Get-Hair @("--action", "sweep")
    $tg = $groomH.dynamics.targets
    Assert ($sw.nonFinite -eq 0 -and $sw.strandResets -eq 0 -and $sw.maxStretch -lt 1.05) ("sweep of {0} frames stable (stretch {1:0.000}, resets {2}, NaN {3})" -f $sw.frames, $sw.maxStretch, $sw.strandResets, $sw.nonFinite)
    if ($null -ne $tg.turn_spread_r95_m) {
        Assert ([math]::Abs($sw.spreadR95TurnMedianM - $tg.turn_spread_r95_m) -le 0.06) ("turn spread r95 {0:0.000} m vs reference {1:0.000} +-0.06 (calm {2:0.000}, turn p90 {3:0.000})" -f $sw.spreadR95TurnMedianM, $tg.turn_spread_r95_m, $sw.spreadR95CalmMedianM, $sw.spreadR95TurnP90M)
    }
    if ($null -ne $tg.trailing_angle_deg) {
        Assert ([math]::Abs($sw.trailingDegTurnMedian - $tg.trailing_angle_deg) -le 20) ("hair trails the head in turns by {0:0} deg vs reference {1:0} +-20 (IQR {2:0}..{3:0})" -f $sw.trailingDegTurnMedian, $tg.trailing_angle_deg, $sw.trailingDegTurnP25, $sw.trailingDegTurnP75)
    }
    Assert ($sw.stepMsMean -lt 3.0) ("CPU per simulation step (one per capture frame, 30 Hz): mean {0:0.00} ms, p95 {1:0.00} ms, max {2:0.00} ms (sim job {3:0.00} ms, ribbon job {4:0.00} ms, Burst {5}, {6} substeps)" -f `
        $sw.stepMsMean, $sw.stepMsP95, $sw.stepMsMax, $sw.simJobMsMean, $sw.meshJobMsMean, $sw.burst, $sw.substeps)

    Write-Host "== hair: review shots of the fastest head moments"
    foreach ($l in "physics", "timing", "floor", "tension", "counterbalance", "traces", "graph") { Invoke-Unity hm_layer --layer $l --visible false | Out-Null }
    $peaksH = (Get-Hair @("--action", "peaks", "--count", "2")).peaks
    $shotsH = @()
    $views = @(@{ name = "back"; az = 180; el = 8; r = 1.5 }, @{ name = "side"; az = 100; el = 8; r = 1.5 }, @{ name = "front34"; az = 35; el = 12; r = 1.5 })
    $i = 0
    foreach ($pk in $peaksH) {
        $i++
        Invoke-Transport seek @("--time", (NumH $pk.audioTime)) | Out-Null
        Start-Sleep -Milliseconds 300
        foreach ($glow in 1, 0) {
            Invoke-Unity hm_hair --action set --tipglow $glow | Out-Null
            Get-Hair @("--action", "sync") | Out-Null
            foreach ($v in $views) {
                Invoke-Unity hm_hair --action frame --azimuth $v.az --elevation $v.el --radius (NumH $v.r) | Out-Null
                Start-Sleep -Milliseconds 400
                $name = "hair_peak{0}_{1}{2}" -f $i, $v.name, $(if ($glow -eq 1) { "" } else { "_noglow" })
                Shot $name
                $src = Join-Path (Get-Location) "Assets\Screenshots\$name.png"
                if ($reviewH -and (Test-Path $src)) { Copy-Item $src (Join-Path $reviewH "$name.png") -Force }
                $shotsH += [ordered]@{ file = "$name.png"; peak = $i; frame = $pk.frame; audioTime = $pk.audioTime; degPerS = $pk.degPerS; view = $v.name; azimuth = $v.az; elevation = $v.el; radius = $v.r; tipGlow = $glow }
            }
        }
    }
    if ($reviewH -and (Test-Path $reviewH)) {
        $meta = [ordered]@{ capture = $cap; take = $takeH; sweep = $sw; shots = $shotsH }
        ($meta | ConvertTo-Json -Depth 6) | Set-Content -Encoding utf8 (Join-Path $reviewH "hair_shots.json")
        Write-Host "  review $reviewH\hair_peak*.png (+ hair_shots.json)"
    }
    Invoke-Unity hm_hair --action release | Out-Null
    Invoke-Unity hm_hair --action set --tipglow 1 | Out-Null
    foreach ($l in "physics", "timing", "floor", "tension") { Invoke-Unity hm_layer --layer $l --visible true | Out-Null }
}
