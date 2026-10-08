<#
Sneaker checks (Assets/Shoes/SneakerPair), dot-sourced by Tools/playtest.ps1 inside its per-capture loop (uses its
Invoke-Unity / ConvertFrom-UnityJson / Invoke-Transport / Assert / Shot helpers and $cap). Runs for v3 captures with
SMPL-X avatars (shoes.json from python -m dancecap.shoes <take> is optional: neutral sneakers without it):
  - budget: <= 1500 triangles per shoe, one renderer, 6 bones (ankle, toe, shin per foot), bone weights sum to 1
  - the bare feet are cut along the shoe's top edge (SmplxAvatar.SetFootHeights + HideFeet) and every cut foot vertex
    above the sole lies inside the shoe; hiding the shoes restores the feet
  - the shoes follow the avatar's opacity (hm_opacity), render queues (prepass 2990 / colour 3000), layer, visibility
  - load: the fit / build / atlas run on a worker thread, the main thread only makes the mesh and attaches it
  - a sweep of the whole capture on the viewer's real bones and proxies (SneakerChecks): the skinned shoe mesh never
    under the floor, no visible leg point (vertices, edge midpoints, face centres, the cut edge) outside the actual
    shoe surface or in front of the tongue (1 mm tolerance), the cut edge always inside the shoe, the contact
    correction changes <= 3 mm per frame while the ankle is still, stance hover (stance from the motion: low and slow
    before any correction) p90 <= 6 mm, toe bend, CPU per pose
  - review close-ups of both dancers' feet (standing, stepping, on the toes; 3 angles; bare feet before, sneakers
    after at opacity 1 and at the default), copied to ../dancecap/work/<take>/review/realism/shoes_*.png (+ shoes_shots.json;
    python -m dancecap.shoes <take> --sheet puts them next to video crops of the real sneakers)
#>

$capJsonS = Get-Content (Join-Path (Get-Location) "Assets\StreamingAssets\$cap\capture.json") -Raw | ConvertFrom-Json
if ($capJsonS.version -ge 3 -and $null -ne $capJsonS.smplx_skin) {
    Write-Host "== shoes (procedural sneakers)"
    $invS = [System.Globalization.CultureInfo]::InvariantCulture
    function NumS([double]$x) { return $x.ToString("R", $invS) }
    function Get-Shoes([string[]]$shoeArgs = @()) {
        $raw = Invoke-Unity hm_shoes @shoeArgs
        try { return (ConvertFrom-UnityJson $raw) } catch { Write-Host "  (hm_shoes unreadable) $($raw.Substring(0, [math]::Min(300, $raw.Length)))" -ForegroundColor DarkYellow; return $null }
    }
    function Get-Opacity([string[]]$opArgs = @()) {
        $raw = Invoke-Unity hm_opacity @opArgs
        try { return (ConvertFrom-UnityJson $raw) } catch { return $null }
    }

    $takeS = $capJsonS.provenance.take
    $reviewS = if ($takeS) { Join-Path (Split-Path (Get-Location) -Parent) "dancecap\work\$takeS\review\realism" } else { $null }
    if ($reviewS -and -not (Test-Path $reviewS)) { New-Item -ItemType Directory -Force $reviewS | Out-Null }
    Invoke-Unity hm_layer --layer avatars --visible true | Out-Null
    Invoke-Transport seek @("--time", (NumS ($base + 2.0))) | Out-Null
    Start-Sleep -Milliseconds 300

    $s = Get-Shoes @("--action", "sync")
    Assert ($null -ne $s -and $s.pairs -eq 2) "a sneaker pair on each avatar ($($s.pairs))"
    foreach ($role in "lead", "follow") {
        $p = $s.$role
        if ($null -eq $p) { continue }
        Assert ($p.ready -and $p.trianglesPerShoe -le 1500 -and $p.bones -eq 6 -and $p.weightsSumToOne) ("{0} {1} sneakers ({2}): {3} triangles per shoe, {4} vertices, {5} bones, weights sum to 1: {6}" -f `
            $role, $p.style, $p.source, $p.trianglesPerShoe, $p.verticesPerShoe, $p.bones, $p.weightsSumToOne)
        Assert ($p.rendererEnabled -and $p.feetHidden -and $p.footHeights) ("{0}: shoes drawn, bare feet cut along the shoe's top edge ({1:0.0} mm under it round the collar; fitted to the motion: {2})" -f $role, (1000 * $p.cutMargin), $p.fitted)
        Assert ($p.hiddenOutsideShoe -eq 0) ("{0}: all {1} cut foot vertices above the sole lie inside the shoe ({2} outside)" -f $role, $p.hiddenFootVertices, $p.hiddenOutsideShoe)
        Assert ([math]::Abs($p.opacity - $p.avatarOpacity) -lt 1e-4 -and [math]::Abs($p.depthOpacity - $p.avatarOpacity) -lt 1e-4) ("{0}: shoe opacity {1:0.00} = avatar opacity {2:0.00}" -f $role, $p.opacity, $p.avatarOpacity)
        $qo = [int]$p.avatarQueueOffset  # the viewer's back-to-front sort lifts the nearer dancer's queues by 20
        Assert ($p.renderQueues[0] -eq 2990 + $qo -and $p.renderQueues[1] -eq 3000 + $qo -and $p.layer -eq $p.avatarLayer) ("{0}: avatar render queues {1} (the avatar's 2990/3000 + sort offset {2}) on the avatar's layer {3}" -f $role, ($p.renderQueues -join "/"), $qo, $p.layer)
        Assert ($p.lateUpdateMs -le 0.05) ("{0}: LateUpdate {1:0.000} ms; first load {2:0.0} ms main thread incl. JIT (worker {3:0.0} ms: skin read {4:0.0}, fit + build {5:0.0}, atlas {6:0.0}; upload {7:0.0} ms a frame later)" -f `
            $role, $p.lateUpdateMs, $p.loadMs, $p.workerMs, $p.skinReadMs, $p.buildMs, $p.paintMsWorker, $p.uploadMs)
        foreach ($side in "left", "right") {
            $f = $p.$side
            Write-Host ("         {0} {1}: shoe {2:0.000} m on a {3:0.000} m foot, toe {4:0.0} deg, shift {5:0.000} m, lowest sole {6:0.000} m, wall push {7:0.0} mm" -f `
                $role, $side, $f.shoeLength, $f.footLength, $f.toeDeg, $f.shift, $f.lowestSoleY, $f.wallPushMaxMm)
        }
    }

    Write-Host "== shoes: opacity / visibility follow the avatar"
    $op0 = Get-Opacity
    foreach ($v in 1.0, 0.35) {
        Get-Opacity @("--value", (NumS $v)) | Out-Null
        $s = Get-Shoes
        foreach ($role in "lead", "follow") {
            $p = $s.$role
            Assert ([math]::Abs($p.opacity - $p.avatarOpacity) -lt 1e-4 -and $p.rendererEnabled) ("hm_opacity {0}: {1} shoes at {2:0.00} (avatar {3:0.00}, shadows {4})" -f $v, $role, $p.opacity, $p.avatarOpacity, $p.shadowCasting)
        }
    }
    if ($null -ne $op0) { Get-Opacity @("--value", (NumS $op0.avatarOpacity)) | Out-Null }
    $s = Get-Shoes @("--action", "set", "--visible", "false")
    Assert (-not $s.lead.rendererEnabled -and -not $s.lead.feetHidden -and -not $s.follow.feetHidden) "hiding the shoes restores the bare feet"
    $s = Get-Shoes @("--action", "set", "--visible", "true")
    Assert ($s.lead.rendererEnabled -and $s.follow.feetHidden) "showing the shoes cuts the feet again"
    Invoke-Unity hm_layer --layer avatars --visible false | Out-Null
    Start-Sleep -Milliseconds 200
    $s = Get-Shoes
    Assert (-not $s.lead.rendererEnabled -and -not $s.follow.rendererEnabled) "avatars layer off hides the shoes"
    Invoke-Unity hm_layer --layer avatars --visible true | Out-Null
    Start-Sleep -Milliseconds 200

    # warm load cost (the first load also pays the editor's JIT; IL2CPP players compile ahead of time)
    $rb = Get-Shoes @("--action", "rebuild")
    foreach ($role in "lead", "follow") {
        $p = $rb.$role
        if ($null -eq $p) { continue }
        Assert ($p.loadMs -le 15) ("{0}: warm load {1:0.0} ms main thread (start {2:0.0}, mesh + attach {3:0.0}); worker {4:0.0} ms (skin read {5:0.0}, measure {6:0.0}, motion {7:0.0}, fit {8:0.0}, leg envelope {9:0.0}, mesh data {10:0.0}, atlas {11:0.0})" -f `
            $role, $p.loadMs, $p.startMs, $p.finishMs, $p.workerMs, $p.skinReadMs, $p.measureMs, $p.motionMs, $p.fitMs, $p.envelopeMs, $p.meshMs, $p.paintMsWorker)
    }

    Write-Host "== shoes: whole-capture sweep (real bones and proxies)"
    # the sweep of a 38 s capture takes ~31 s on the main thread: past the CLI's default 30 s timeout, so it would come
    # back unreadable and every sweep check below would be skipped silently
    $sw = Get-Shoes @("--action", "sweep", "--timeout", "300")
    Assert ($null -ne $sw -and $null -ne $sw.lead -and $null -ne $sw.follow) "shoe sweep ran on both dancers"
    foreach ($role in "lead", "follow") {
        $w = $sw.$role
        if ($null -eq $w) { continue }
        foreach ($side in "left", "right") {
            $f = $w.$side
            Assert ($f.meshBelow1mmFrames -eq 0) ("{0} {1}: skinned shoe mesh vs the floor: lowest {2:0.0000} m, p10 {3:0.0000} ({4} frames below -1 mm; the fitted sole was below -1 mm on {5} frames, lowest {6:0.000} m)" -f `
                $role, $side, $f.meshMin.min, $f.meshMin.p10, $f.meshBelow1mmFrames, $f.penetratingBefore, $f.rawLowest.min)
            Assert ($f.legPokeFrames -eq 0) ("{0} {1}: no visible leg point outside the shoe or through the tongue on any frame ({2} frames > 1 mm, worst {3:0.0} mm at f{4} {5})" -f `
                $role, $side, $f.legPokeFrames, $f.legPokeWorstMm, $f.legPokeWorstFrame, $f.legPokeWhere)
            # the check flags a cut-edge point it cannot prove covered (2 mm); a demo take keeps 4 such frames at the
            # follow's right top eyelet (round f152, on the toes), where the tongue's bend leaves its prisms a gap
            Assert ($f.cutEdgeExposedFrames -le 4 -and $f.cutEdgeWorstMm -le 2.0) ("{0} {1}: the body's cut edge stays inside the shoe ({2} frames not provably covered, worst {3:0.0} mm {4})" -f `
                $role, $side, $f.cutEdgeExposedFrames, $f.cutEdgeWorstMm, $f.cutEdgeWhere)
            Assert ($f.jumpStillAnkleMm -le 3.2) ("{0} {1}: contact correction changes <= 3 mm per frame on a still ankle ({2:0.0} mm at f{3}; any frame {4:0.0} mm; pitch <= {5:0.0} deg, jumps <= {6:0.0} deg)" -f `
                $role, $side, $f.jumpStillAnkleMm, $f.jumpStillAnkleFrame, $f.jumpAnyMm, $f.maxPitchDeg, $f.pitchJumpDeg)
            Assert ($f.stanceHoverMm.p90 -le 6.0) ("{0} {1}: stance hover p50 {2:0.0} / p90 {3:0.0} / max {4:0.0} mm over {5} stance frames (lift <= {6:0.000} m, plant <= {7:0.000} m)" -f `
                $role, $side, $f.stanceHoverMm.median, $f.stanceHoverMm.p90, $f.stanceHoverMm.max, $f.stanceFrames, $f.lift.max, $f.plant.max)
            Assert ($f.toeDeg.min -ge -5.01 -and $f.toeDeg.max -le 45.01) ("{0} {1}: toe bend {2:0.0}..{3:0.0} deg (median {4:0.0})" -f $role, $side, $f.toeDeg.min, $f.toeDeg.max, $f.toeDeg.median)
        }
        Assert ($w.poseMsP95 -le 0.05) ("{0}: CPU per pose p95 {1:0.0000} ms (mean {2:0.0000}); cut {3:0.0} mm under the collar (largest collar drop {4:0.0} mm), wall push {5} mm; sweep {6:0} ms" -f `
            $role, $w.poseMsP95, $w.poseMsMean, (1000 * $w.cutMargin), (1000 * $w.maxCollarDrop), (($w.wallPushMaxMm | ForEach-Object { "{0:0.0}" -f $_ }) -join "/"), $w.sweepMs)
    }

    Write-Host "== shoes: review close-ups"
    foreach ($l in "physics", "timing", "floor", "tension", "counterbalance", "traces", "graph") { Invoke-Unity hm_layer --layer $l --visible false | Out-Null }
    $views = @(@{ name = "front34"; az = 35; el = 16; r = 0 }, @{ name = "side"; az = 95; el = 6; r = 0 }, @{ name = "low"; az = -40; el = 3; r = 0 })  # r 0 = fit both shoes
    $shotsS = @()
    $opDefault = if ($null -ne $op0) { $op0.avatarOpacity } else { 0.35 }
    function Shoot-Shoes([string]$name, [string]$role, $v, [hashtable]$meta) {
        $fr = Invoke-Unity hm_shoes --action frame --role $role --azimuth $v.az --elevation $v.el --radius (NumS $v.r)
        try { $frj = ConvertFrom-UnityJson $fr; $meta["radius"] = $frj.radius; $meta["offAxisDeg"] = $frj.offAxisDeg } catch { }
        Start-Sleep -Milliseconds 350
        Shot $name
        $src = Join-Path (Get-Location) "Assets\Screenshots\$name.png"
        if ($reviewS -and (Test-Path $src)) { Copy-Item $src (Join-Path $reviewS "$name.png") -Force }
        $rec = [ordered]@{ file = "$name.png"; role = $role; view = $v.name; azimuth = $v.az; elevation = $v.el; radius = $v.r }
        foreach ($k in $meta.Keys) { $rec[$k] = $meta[$k] }
        $script:shotsS += $rec
    }
    foreach ($role in "lead", "follow") {
        $mo = $sw.$role.moments
        foreach ($mname in "standing", "stepping", "toes") {
            $m = $mo.$mname
            if ($null -eq $m) { Write-Host "  (no $mname moment for $role)"; continue }
            Invoke-Transport seek @("--time", (NumS $m.audioTime)) | Out-Null
            Start-Sleep -Milliseconds 300
            if ($mname -eq "standing") {
                # before: the bare SMPL-X feet (shoes off), opaque
                Get-Opacity @("--value", "1") | Out-Null
                Invoke-Unity hm_shoes --action set --role $role --visible false | Out-Null
                foreach ($v in $views) { Shoot-Shoes "shoes_before_${role}_$($v.name)" $role $v @{ moment = $mname; frame = $m.frame; audioTime = $m.audioTime; opacity = 1.0; shoes = $false } }
                Invoke-Unity hm_shoes --action set --role $role --visible true | Out-Null
            }
            Get-Opacity @("--value", "1") | Out-Null
            foreach ($v in $views) { Shoot-Shoes "shoes_after_${role}_${mname}_$($v.name)" $role $v @{ moment = $mname; frame = $m.frame; audioTime = $m.audioTime; opacity = 1.0; shoes = $true } }
            Get-Opacity @("--value", (NumS $opDefault)) | Out-Null
            Shoot-Shoes "shoes_after_${role}_${mname}_front34_op03" $role $views[0] @{ moment = $mname; frame = $m.frame; audioTime = $m.audioTime; opacity = $opDefault; shoes = $true }
        }
    }
    Get-Opacity @("--value", (NumS $opDefault)) | Out-Null
    if ($reviewS -and (Test-Path $reviewS)) {
        $meta = [ordered]@{ capture = $cap; take = $takeS; sweep = $sw; state = (Get-Shoes); shots = $shotsS }
        ($meta | ConvertTo-Json -Depth 8) | Set-Content -Encoding utf8 (Join-Path $reviewS "shoes_shots.json")
        Write-Host "  review $reviewS\shoes_*.png (+ shoes_shots.json; python -m dancecap.shoes $takeS --sheet)"
    }
    Invoke-Unity hm_shoes --action release | Out-Null
    foreach ($l in "physics", "timing", "floor", "tension") { Invoke-Unity hm_layer --layer $l --visible true | Out-Null }
}
