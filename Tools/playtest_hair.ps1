<#
Hair checks (Assets/Hair/HairStrands: guide-driven hair cards), dot-sourced by Tools/playtest.ps1 inside its
per-capture loop (uses its Invoke-Unity / ConvertFrom-UnityJson / Invoke-Transport / Assert / Shot helpers and $cap /
$prefix / $base). Runs only for captures with hair_groom.json (python -m dancecap.hair_groom <take> [--restyle]):
  - counts within the Quest budget (<= 200 guides x 16 segments; <= 12k triangles at LOD 0 incl. the tip glow; one
    card mesh with a depth + colour material, one glow mesh), roots on the exported skin, MSAA alpha-to-coverage on
  - the old LineRenderer HairSimulation is gone on v3 captures, no LineRenderer left at its default origin bar
  - the whole take is baked in the background: seek / restart / loop are a lookup (<= 1 ms per update in the editor,
    no re-hang); seeks forward / backward, a looped measure, restart: no NaN, no stretch, no strand beyond its length,
    roots pinned, nothing near the floor, NO guide point in the face box
  - opacity: the hair follows the avatar (avatar + 0.15 x smoothstep(0, 0.2, avatar), VIEWER_SPEC 3.2: +0.15 at the
    normal opacities, fading out with the body) and an override; the tip glow fades out with the avatar (smoothstep
    0..0.1); off-screen HDR renders (post-processing off, values above 1.0 kept) show a monotonic, near-proportional
    fade (it does not vanish at 0.45), tip glow pixels above 1.0 with the glow on and none with it off, a soft halo
    around the glow cores, the lit body under 0.7 linear; the colour against the source's target (portrait / video)
  - the scalp cap reaches the hairline painted into the follow's texture (cards.cap) and the front card roots drop
    toward it; the tip glows sit on atlas strands (anchored)
  - a sweep of the whole take: stability, 0 guide points in the face box on every frame, spread r95 vs the hair
    reference (dancecap docs/HAIR_REFERENCE.md 3; trailing angle reported, not asserted: unresolved), the front-right
    section's tips never turn up into a hook, CPU per update
  - review shots of calm frames and the fastest head moments from 3 angles, tip glow on / off, copied to
    ../dancecap/work/<take>/review/hair_*.png (+ hair_shots.json for python -m dancecap.hair_groom <take> --sheet)
#>

$hairGroomPath = Join-Path (Get-Location) "Assets\StreamingAssets\$cap\hair_groom.json"
if (Test-Path $hairGroomPath) {
    Write-Host "== hair (guide-driven cards)"
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

    $h = Get-Hair @("--action", "sync")
    Assert ($null -ne $h -and $h.present -and $h.visible) "hair present and visible"
    Assert ($h.guides -ge 100 -and $h.guides -le 200 -and $h.segments -eq 16) "hair: $($h.guides) guides x $($h.segments) segments (Quest <= 200 x 16)"
    if ($h.groomVersion -ge 2) {
        Assert ($h.mode -eq "cards" -and $h.cards -ge 100) ("cards: {0} ({1}), cap {2} triangles, {3} glow ribbons" -f $h.cards, (($h.cardsPerLayer.PSObject.Properties | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ", "), $h.capTriangles, $h.glowRibbons)
        Assert ($h.trianglesPerLod[0] -le 12000) ("triangles per LOD {0} (LOD 0 <= 12k incl. tip glow; Quest frame budget < 1.5M)" -f ($h.trianglesPerLod -join " / "))
        Assert ($h.alphaToMask) "alpha-to-coverage on (URP MSAA > 1)"
    } else {
        Write-Host "  (groom version 1: run python -m dancecap.hair_groom $takeH --restyle for the sleek cards)" -ForegroundColor DarkYellow
    }
    Assert ($h.legacyHairObjects -eq 0) "old LineRenderer HairSimulation disabled on this v3 capture"
    Assert ($h.defaultLineRenderers -eq 0) "no LineRenderer left at its default bar from the origin ($($h.defaultLineRenderers))"
    Assert ($h.rootMismatchMm -lt 5) ("roots on the exported skin's scalp (groom vs skin {0:0.000} mm)" -f $h.rootMismatchMm)
    if ($groomH.cards.cap.hairline_el_deg_vs_abs_az) {
        Assert ($h.cardsRootDropped -gt 0 -and $h.glowAnchored -eq $h.glowRibbons) ("cap down to the painted hairline ({0}), {1} front card roots dropped toward it, {2} of {3} tip glows on atlas strands" -f `
            (($groomH.cards.cap.hairline_el_deg_vs_abs_az | Select-Object -First 3 | ForEach-Object { "|az| $($_[0]): $($_[1]) deg" }) -join ", "), $h.cardsRootDropped, $h.glowAnchored, $h.glowRibbons)
    }
    Assert ($h.bake.complete) ("whole take baked: {0} frames in {1:0} ms wall ({2:0.00} ms per frame of background job + main-thread kinematics), cache {3:0.0} MB" -f `
        $h.bake.bakedFrames, $h.bake.wallMs, $h.bake.msPerFrame, ($h.bake.cacheBytes / 1MB))
    if ($h.rest) {
        Assert ($h.rest.faceBoxPoints -eq 0) "rest shape keeps the face box clear ($($h.rest.faceBoxPoints) guide points inside)"
        if ($h.rest.frontRightDrape -eq "behind_shoulder" -and [double]$h.rest.foreheadSweep -gt 0) {
            # 2026-10-07 evening "part forward, rest back": the forehead section is swept across the forehead and down the right
            # temple (beside the face, never in the face box), the front-right section still drapes behind the right shoulder
            Assert ([double]$h.rest.frontRightTipsBehindShoulder -ge 0.8 -and $h.rest.pointsBesideFace -le 16 -and [double]$h.rest.partStartBackM -eq 0) ("rest shape part forward, rest back: forehead sweep {0}, front-right tips behind the right shoulder {1:P0} of {2}, {3} guide points beside the face (temple sweep; face box clear), front-left behind the ear {4:P0} of {5}; side drape {6}, part starts at the hairline" -f `
                $h.rest.foreheadSweep, $h.rest.frontRightTipsBehindShoulder, $h.rest.frontRightGuides, $h.rest.pointsBesideFace, $h.rest.frontLeftBehindEar, $h.rest.frontLeftGuides, $h.rest.sideDrapeBack)
        } elseif ($h.rest.frontRightDrape -eq "behind_shoulder") {
            # 2026-10-07 "pulled back": the front-right section drapes behind the right shoulder, nothing hangs beside the face
            Assert ([double]$h.rest.frontRightTipsBehindShoulder -ge 0.8 -and $h.rest.pointsBesideFace -eq 0) ("rest shape pulled back: front-right tips behind the right shoulder {0:P0} of {1} (in front {2:P0}), {3} guide points beside / in front of the face, front-left behind the ear {4:P0} of {5}; pull {6}, side drape {7}, part starts {8} m back" -f `
                $h.rest.frontRightTipsBehindShoulder, $h.rest.frontRightGuides, $h.rest.frontRightTipsForwardOfShoulder, $h.rest.pointsBesideFace, $h.rest.frontLeftBehindEar, $h.rest.frontLeftGuides, $h.rest.frontPull, $h.rest.sideDrapeBack, $h.rest.partStartBackM)
        } else {
            Write-Host ("  rest shape: front-right tips in front of the right shoulder {0:P0} of {1}, front-left behind the ear {2:P0} of {3}" -f `
                $h.rest.frontRightTipsForwardOfShoulder, $h.rest.frontRightGuides, $h.rest.frontLeftBehindEar, $h.rest.frontLeftGuides)
        }
    }

    function Assert-HairSane([string]$what) {
        $s = Get-Hair @("--action", "sync")
        if ($null -eq $s) { Assert $false "${what}: hair state"; return $null }
        $ok = $s.nonFinite -eq 0 -and $s.maxStretch -lt 1.05 -and $s.maxReachRatio -le 1.0 -and $s.rootPinErrorMm -lt 0.5 -and
              $s.frame -eq $s.avatarFrame -and $s.minWorldY -gt 0.3 -and $s.faceIntrusions -eq 0
        Assert $ok ("{0}: frame {1}, stretch {2:0.000}, reach {3:0.00} of length, roots {4:0.000} mm, lowest hair {5:0.00} m, NaN {6}, face box {7} points ({8} card vertices){9}" -f `
            $what, $s.frame, $s.maxStretch, $s.maxReachRatio, $s.rootPinErrorMm, $s.minWorldY, $s.nonFinite, $s.faceIntrusions, $s.faceIntrusionsCards, $(if ($s.lastUpdateBaked) { ", baked" } else { "" }))
        return $s
    }

    $seekMs = @()
    foreach ($dt in 1.0, 4.8, 0.3, 5.5) {
        Invoke-Transport seek @("--time", (NumH ($base + $dt))) | Out-Null
        Start-Sleep -Milliseconds 300
        $s = Assert-HairSane ("seek first frame + {0} s" -f $dt)
        if ($s -and $s.lastUpdateBaked) { $seekMs += $s.lastUpdateMs }
    }
    Invoke-Transport restart | Out-Null
    Start-Sleep -Milliseconds 300
    Assert-HairSane "restart" | Out-Null
    if ($seekMs.Count -gt 0) {
        $worst = ($seekMs | Measure-Object -Maximum).Maximum
        Assert ($worst -le 1.5) ("seek / restart with the bake: {0:0.00} ms worst update (a lookup + the card mesh; was a 14-39 ms re-hang)" -f $worst)
    }
    Invoke-Transport loop_on | Out-Null
    Invoke-Unity hm_transport --action play | Out-Null
    Start-Sleep -Seconds 4
    Invoke-Unity hm_transport --action pause | Out-Null
    Invoke-Transport loop_off | Out-Null
    $s = Assert-HairSane "after 4 s of a looped measure"
    Assert ($s.rehangs -le 1) "no re-hang during playback / loops once baked (re-hangs $($s.rehangs))"
    $h = Get-Hair
    Assert ($h.strandResets -eq 0 -and $h.bake.strandResets -eq 0) "no strand re-hung by the blow-up guard (live $($h.strandResets), bake $($h.bake.strandResets))"

    Write-Host "== hair: opacity follows the avatar (VIEWER_SPEC 3.2: avatar + 0.15, ramped to 0 with the body)"
    $opH0 = [double](Get-State).avatarOpacity  # the viewer default (displayed opacity 0.35): restored after the checks
    function HairOpacityFor([double]$a) { $t = [math]::Min(1.0, [math]::Max(0.0, $a / 0.2)); return [math]::Min(1.0, $a + 0.15 * $t * $t * (3 - 2 * $t)) }
    foreach ($ov in 0.35, 0.1155, 0.035) {
        Invoke-Unity hm_opacity --value (NumH $ov) | Out-Null
        $h = Get-Hair @("--action", "sync")
        $want = HairOpacityFor ([double]$h.avatarOpacity)
        $tg = [math]::Min(1.0, [double]$h.avatarOpacity / 0.1); $glowWant = $tg * $tg * (3 - 2 * $tg)
        Assert ($h.opacitySource -eq "avatar" -and [math]::Abs([double]$h.opacity - $want) -lt 0.01 -and [math]::Abs([double]$h.glowFade - $glowWant) -lt 0.01) `
            ("avatar {0:0.000}: hair opacity {1:0.000} (= a + 0.15 x smoothstep(0, 0.2, a)), tip glow x {2:0.00}" -f $h.avatarOpacity, $h.opacity, $h.glowFade)
    }
    Invoke-Unity hm_opacity --value (NumH $opH0) | Out-Null
    $h = Get-Hair @("--action", "set", "--opacity", "0.65")
    Assert ($h.opacitySource -eq "override" -and [math]::Abs($h.opacity - 0.65) -lt 0.01) "hm_hair --opacity 0.65 overrides it"
    $h = Get-Hair @("--action", "set", "--opacity", "-1")
    Assert ($h.opacitySource -eq "avatar") "hm_hair --opacity -1 follows the avatar again"

    Write-Host "== hair: off-screen HDR renders (fade, tip glow, colour)"
    foreach ($l in "physics", "timing", "floor", "tension", "counterbalance", "traces", "graph", "splats", "room") { Invoke-Unity hm_layer --layer $l --visible false | Out-Null }
    Invoke-Transport seek @("--time", (NumH ($base + 1.0))) | Out-Null
    Invoke-Unity hm_hair --action set --lod 0 | Out-Null
    Invoke-Unity hm_hair --action frame --azimuth 160 --elevation 8 --radius 1.0 --drop 0.25 --solo true | Out-Null
    Invoke-Unity hm_opacity --value 1 | Out-Null  # an opaque body: its bright unsaturated region is the white-balance reference
    Start-Sleep -Milliseconds 400
    $pr = Get-Hair @("--action", "probe", "--opacities", "1,0.65,0.45,0.3")
    Invoke-Unity hm_opacity --value (NumH $opH0) | Out-Null
    if ($null -ne $pr) {
        $fadeTxt = ($pr.fade.PSObject.Properties | ForEach-Object { "{0}: {1:0.00}" -f $_.Name, $_.Value.hairShare }) -join ", "
        Assert ($pr.hairPixels -gt 2000) "hair pixels in the review render: $($pr.hairPixels)"
        Assert ($pr.fadeMonotonic -and $pr.fadeMaxDeviationFromProportional -le 0.12) ("opacity is a real fade (hair share of the pixel colour over what is behind it: {0}; max deviation from proportional {1:0.00})" -f $fadeTxt, $pr.fadeMaxDeviationFromProportional)
        Assert ($pr.fade.'0.45'.hairShare -ge 0.3) "the hair does not vanish at opacity 0.45 (the old shader cut it off below 0.5)"
        # post-processing off: the scene colour as URP's bloom sees it (HDR values above 1.0 kept)
        Assert ($pr.hdrPixelsGlowOn -ge 20 -and $pr.hdrPixelsGlowOff -eq 0) ("tip glow: {0} HDR pixels above 1.0 with the glow on, {1} with it off (only the tips bloom); {2} soft halo pixels beyond 2 px of the cores" -f `
            $pr.hdrPixelsGlowOn, $pr.hdrPixelsGlowOff, $pr.glowHaloPixels)
        Assert ($pr.glowHaloPixels -ge 20) "the tip glow has a soft halo, not only 1 px lines ($($pr.glowHaloPixels) pixels)"
        Assert ($pr.litBodyMaxLinear -le 0.7) ("lit hair body {0:0.000} linear (p99.5 over a dark background; soft clamp 0.66: below URP's bloom threshold 1.0)" -f $pr.litBodyMaxLinear)
        # reference colour targets are per-capture appearance data: they live in the git-ignored Tools/local.ps1
        if (Test-Path "$PSScriptRoot/local.ps1") { . "$PSScriptRoot/local.ps1" }
        if (-not $HairTargetPortrait) { $HairTargetPortrait = "(define `$HairTargetPortrait in Tools/local.ps1)" }
        if (-not $HairTargetVideo) { $HairTargetVideo = "(define `$HairTargetVideo in Tools/local.ps1)" }
        $target = if ($pr.colourSource -eq "portrait") { "portrait shadow / mid / highlight $HairTargetPortrait" } else { "video p10 / p50 / p90 $HairTargetVideo" }
        Write-Host ("  hair colour ({0}), white-balanced on the bright body reference (HAIR_REFERENCE 4: {1}): p10 {2} p50 {3} p90 {4} (raw {5} / {6} / {7})" -f `
            $pr.colourSource, $target, $pr.colour.p10.whiteBalancedHex, $pr.colour.p50.whiteBalancedHex, $pr.colour.p90.whiteBalancedHex, $pr.colour.p10.hex, $pr.colour.p50.hex, $pr.colour.p90.hex)
    }
    Invoke-Unity hm_hair --action release | Out-Null
    Invoke-Unity hm_hair --action set --lod auto | Out-Null

    Write-Host "== hair: whole-capture sweep"
    $sw = Get-Hair @("--action", "sweep", "--timeout", "300")  # a 38 s capture can exceed the CLI's default 30 s
    $tg = $groomH.dynamics.targets
    Assert ($sw.nonFinite -eq 0 -and $sw.strandResets -eq 0 -and $sw.maxStretch -lt 1.05) ("sweep of {0} frames stable (stretch {1:0.000}, resets {2}, NaN {3})" -f $sw.frames, $sw.maxStretch, $sw.strandResets, $sw.nonFinite)
    Assert ($sw.faceIntrusionsMax -eq 0) ("face clear on every frame: {0} guide points in the face box at worst ({1} frames); card vertices {2} at worst ({3} frames)" -f `
        $sw.faceIntrusionsMax, $sw.faceIntrusionFrames, $sw.faceIntrusionsCardsMax, $sw.faceIntrusionCardFrames)
    if ($null -ne $tg.turn_spread_r95_m) {
        Assert ($sw.spreadR95TurnMedianM -le $tg.turn_spread_r95_m + 0.03 -and $sw.spreadR95TurnMedianM -ge $sw.spreadR95CalmMedianM) ("turn spread r95 {0:0.000} m: beyond calm {1:0.000}, within the reference upper bound {2:0.000} (+0.03); turn p90 {3:0.000}" -f $sw.spreadR95TurnMedianM, $sw.spreadR95CalmMedianM, $tg.turn_spread_r95_m, $sw.spreadR95TurnP90M)
    }
    Write-Host ("  trailing angle in turns {0:0} deg (IQR {1:0}..{2:0}; not an acceptance criterion: unresolved in the reference)" -f $sw.trailingDegTurnMedian, $sw.trailingDegTurnP25, $sw.trailingDegTurnP75)
    Write-Host ("  right-front guides (|az| < 120): lower-half bends p90 {0:0} deg, max {1:0} deg (frame {2}), {3} frames with a bend over 45 deg, straightness p10 {4:0.00}" -f `
        $sw.frontRightBendP90Deg, $sw.frontRightBendMaxDeg, $sw.frontRightBendMaxFrame, $sw.frontRightSharpBendFrames, $sw.frontRightStraightP10)
    $hs = Get-Hair
    Assert ($hs.frontRightGuides -gt 0 -and $sw.frontRightBendMaxDeg -lt 90) ("no folded strands on the right front: sharpest lower-half bend {0:0} deg (front-right zone {1} guides, side zone {2})" -f $sw.frontRightBendMaxDeg, $hs.frontRightGuides, $hs.sideZoneGuides)
    Assert ($sw.stepMsMean -lt 1.5) ("CPU per frame update (baked: lookup + card mesh): mean {0:0.00} ms, p95 {1:0.00} ms, max {2:0.00} ms (card job {3:0.00} ms, Burst {4}); bake {5:0} ms wall for {6} frames" -f `
        $sw.stepMsMean, $sw.stepMsP95, $sw.stepMsMax, $sw.meshJobMsMean, $sw.burst, $sw.bake.wallMs, $sw.bake.bakedFrames)

    Write-Host "== hair: review shots (calm frame + the fastest head moments)"
    $peaksH = (Get-Hair @("--action", "peaks", "--count", "2")).peaks
    $moments = @(@{ name = "calm"; audioTime = $base + 1.0; frame = -1; degPerS = 0 })
    $i = 0
    foreach ($pk in $peaksH) { $i++; $moments += @{ name = "peak$i"; audioTime = $pk.audioTime; frame = $pk.frame; degPerS = $pk.degPerS } }
    $shotsH = @()
    $views = @(@{ name = "back"; az = 180; el = 8; r = 1.4 }, @{ name = "side"; az = 100; el = 8; r = 1.4 }, @{ name = "front34"; az = -35; el = 10; r = 1.2 })
    Invoke-Unity hm_hair --action set --lod 0 | Out-Null
    foreach ($m in $moments) {
        Invoke-Transport seek @("--time", (NumH $m.audioTime)) | Out-Null
        Start-Sleep -Milliseconds 300
        foreach ($glow in 1, 0) {
            Invoke-Unity hm_hair --action set --tipglow $glow | Out-Null
            Get-Hair @("--action", "sync") | Out-Null
            foreach ($v in $views) {
                Invoke-Unity hm_hair --action frame --azimuth $v.az --elevation $v.el --radius (NumH $v.r) --solo true | Out-Null
                Start-Sleep -Milliseconds 400
                $name = "hair_{0}_{1}{2}" -f $m.name, $v.name, $(if ($glow -eq 1) { "" } else { "_noglow" })
                Shot $name
                $src = Join-Path (Get-Location) "Assets\Screenshots\$name.png"
                if ($reviewH -and (Test-Path $src)) { Copy-Item $src (Join-Path $reviewH "$name.png") -Force }
                $shotsH += [ordered]@{ file = "$name.png"; peak = $m.name; frame = $m.frame; audioTime = $m.audioTime; degPerS = $m.degPerS; view = $v.name; azimuth = $v.az; elevation = $v.el; radius = $v.r; tipGlow = $glow }
            }
        }
    }
    if ($reviewH -and (Test-Path $reviewH)) {
        $meta = [ordered]@{ capture = $cap; take = $takeH; sweep = $sw; probe = $pr; shots = $shotsH }
        ($meta | ConvertTo-Json -Depth 6) | Set-Content -Encoding utf8 (Join-Path $reviewH "hair_shots.json")
        Write-Host "  review $reviewH\hair_*.png (+ hair_shots.json)"
    }
    Invoke-Unity hm_hair --action release | Out-Null
    Invoke-Unity hm_hair --action set --tipglow 1 --lod auto | Out-Null
    foreach ($l in "physics", "timing", "floor", "tension") { Invoke-Unity hm_layer --layer $l --visible true | Out-Null }
}
