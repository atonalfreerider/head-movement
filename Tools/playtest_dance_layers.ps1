<#
VIEWER_SPEC v3 dance-layer checks, dot-sourced by Tools/playtest.ps1 inside its per-capture loop (uses its
Invoke-Unity / Get-State / Invoke-Transport / Assert / Shot helpers and $cap / $prefix). Covers:
  - origin (3.0): couple centre within 1 cm of (0,0) at the first frame; physics COM moved with the dancers
  - counterbalance (3.9/3.5): yellow axis on/off within +-1 frame of the synthetic truth interval, floor dot
    under the truth combined COM (<= 2 cm), pivot ring at the truth anchored foot (<= 3 cm), pivot ring timing
  - follower traces (3.7) and the counterbalance / traces / graph layer switches
  - dance graph (3.10/3.11): every segment maps to a node, the miniature couple reaches each node by the segment
    start, glides in between, new links flagged, placeholder badge; fingerprint dwell seconds sum to the
    labelled duration
  - hm_tour (6): the state sequence, per-state avatar opacity, layers restored after stop
Review screenshots are also copied to $ReviewDir (default ../dancecap/work/review/unity_moves).
#>

if (-not $ReviewDir) { $ReviewDir = Join-Path (Split-Path (Get-Location) -Parent) "dancecap\work\review\unity_moves" }
New-Item -ItemType Directory -Force $ReviewDir | Out-Null
$inv = [System.Globalization.CultureInfo]::InvariantCulture

function Num([double]$x) { return $x.ToString("R", $inv) }

function ReviewShot([string]$name) {
    Start-Sleep -Milliseconds 400  # let the blended state settle for a frame or two
    Shot $name
    $src = Join-Path (Get-Location) "Assets\Screenshots\$name.png"
    if (Test-Path $src) { Copy-Item $src (Join-Path $ReviewDir "$name.png") -Force; Write-Host "  review $ReviewDir\$name.png" }
}

function Invoke-Json([string[]]$cmd) {
    $raw = Invoke-Unity @cmd
    try { return ($raw | ConvertFrom-Json | ConvertFrom-Json) } catch { Write-Host "  (unparsed) $raw"; return $null }
}

function Seek-Frame([int]$k) { return Invoke-Transport seek @("--time", (Num $script:frameTimes[$k])) }

$firstPivot = $null; $truth = $null  # this file is dot-sourced once per capture: no leftovers from the previous one
$capDir = Join-Path (Get-Location) "Assets\StreamingAssets\$cap"
$capJson = Get-Content (Join-Path $capDir "capture.json") -Raw | ConvertFrom-Json
$timeToAudio = 0.0
if ($null -ne $capJson.time_to_audio) { $timeToAudio = [double]$capJson.time_to_audio }
$script:frameTimes = @()
if ($capJson.times -and (Test-Path (Join-Path $capDir $capJson.times))) {
    $script:frameTimes = @((Get-Content (Join-Path $capDir $capJson.times) -Raw | ConvertFrom-Json) | ForEach-Object { [double]$_ + $timeToAudio })
}
$take = $null
if ($capJson.provenance) { $take = $capJson.provenance.take }

Write-Host "== dance layers: origin (VIEWER_SPEC 3.0)"
$s = Invoke-Transport restart
Assert ($null -ne $s.origin) "hm_state reports the dance origin ($($s.origin.source))"
if ($null -ne $s.origin) {
    Assert ($s.origin.coupleCentreAtStartErrorM -le 0.01) ("couple centre at the first frame is at (0,0): {0:0.0} mm off (offset {1:0.000},{2:0.000})" -f ($s.origin.coupleCentreAtStartErrorM * 1000), $s.origin.offset[0], $s.origin.offset[2])
    Assert ($s.frame -eq 0 -and [math]::Sqrt([math]::Pow($s.origin.coupleCentre[0], 2) + [math]::Pow($s.origin.coupleCentre[1], 2)) -le 0.01) "after restart the shown frame 0 has the couple at the origin"
    if ($null -ne $s.physics -and $null -ne $s.physics.lead -and $null -ne $s.physics.follow) {
        $cx = ($s.physics.lead.com[0] + $s.physics.follow.com[0]) / 2; $cz = ($s.physics.lead.com[2] + $s.physics.follow.com[2]) / 2
        $d = [math]::Sqrt([math]::Pow($cx - $s.origin.coupleCentre[0], 2) + [math]::Pow($cz - $s.origin.coupleCentre[1], 2))
        Assert ($d -lt 0.3) ("physics COM moved with the dancers ({0:0.00} m from the couple centre)" -f $d)
    }
}

Write-Host "== dance layers: layer switches"
foreach ($layer in "counterbalance", "traces", "graph") {
    $before = [bool](Get-State).layers.$layer
    $flip = (-not $before).ToString().ToLower(); $back = $before.ToString().ToLower()
    Invoke-Unity hm_layer --layer $layer --visible $flip | Out-Null
    $after = [bool](Get-State).layers.$layer
    Assert ($after -eq (-not $before)) "hm_layer $layer toggles ($before -> $after)"
    Invoke-Unity hm_layer --layer $layer --visible $back | Out-Null
}

$cb = (Get-State).counterbalance
if ($cb.loaded -and $script:frameTimes.Count -gt 0) {
    Write-Host "== counterbalance (VIEWER_SPEC 3.9, 14)"
    Invoke-Unity hm_layer --layer counterbalance --visible true | Out-Null
    Assert ($cb.intervals -gt 0) "counterbalance.json: $($cb.intervals) interval(s), $($cb.pivots) pivot(s)"
    # data-independent alignment: mid-pivot, the ring sits under the follower's anchored foot as the skeleton shows it
    $firstPivot = @($cb.intervalTimes | Where-Object { $null -ne $_[2] }) | Select-Object -First 1
    if ($null -ne $firstPivot) {
        $st = Invoke-Transport seek @("--time", (Num (([double]$firstPivot[2] + [double]$firstPivot[3]) / 2)))
        Assert ($st.counterbalance.pivotRunning -and $st.counterbalance.pivotToFootM -le 0.03) ("pivot ring under the follower's anchored foot (skeleton): {0:0.0} mm" -f ($st.counterbalance.pivotToFootM * 1000))
        Assert ($st.counterbalance.axisVisible) "yellow axis shown mid-pivot"
    }
    $truthPath = if ($take) { Join-Path (Split-Path (Get-Location) -Parent) "dancecap\work\$take\truth.json" } else { $null }
    $truth = $null
    if ($truthPath -and (Test-Path $truthPath)) { $truth = Get-Content $truthPath -Raw | ConvertFrom-Json }
    if ($null -ne $truth -and $truth.counterbalance) {
        $iv = $truth.counterbalance[0]
        $n = $script:frameTimes.Count
        function First-Frame-At-Or-After([double]$t) { for ($k = 0; $k -lt $n; $k++) { if ($script:frameTimes[$k] -ge $t) { return $k } }; return $n - 1 }
        function Last-Frame-At-Or-Before([double]$t) { for ($k = $n - 1; $k -ge 0; $k--) { if ($script:frameTimes[$k] -le $t) { return $k } }; return 0 }
        foreach ($edge in @(@{ name = "axis"; t0 = [double]$iv.t0; t1 = [double]$iv.t1; key = "axisVisible" },
                            @{ name = "pivot ring"; t0 = [double]$iv.pivot_t0; t1 = [double]$iv.pivot_t1; key = "pivotRunning" })) {
            $fa = First-Frame-At-Or-After ($edge.t0 + $timeToAudio)
            $fb = Last-Frame-At-Or-Before ($edge.t1 + $timeToAudio)
            $firstOn = $null; $lastOn = $null
            foreach ($k in ($fa - 3)..($fa + 3)) { $st = Seek-Frame $k; if ($st.counterbalance.($edge.key) -and $null -eq $firstOn) { $firstOn = $k } }
            foreach ($k in ($fb - 3)..($fb + 3)) { $st = Seek-Frame $k; if ($st.counterbalance.($edge.key)) { $lastOn = $k } }
            Assert ($null -ne $firstOn -and [math]::Abs($firstOn - $fa) -le 1) ("{0} appears at frame {1} (truth {2}, t0 {3:0.000} s): within 1 frame" -f $edge.name, $firstOn, $fa, $edge.t0)
            Assert ($null -ne $lastOn -and [math]::Abs($lastOn - $fb) -le 1) ("{0} last shown at frame {1} (truth {2}, t1 {3:0.000} s): within 1 frame" -f $edge.name, $lastOn, $fb, $edge.t1)
        }
        # mid-pivot: floor dot under the truth combined COM, ring at the truth anchored foot
        $tm = ([double]$iv.pivot_t0 + [double]$iv.pivot_t1) / 2
        $km = First-Frame-At-Or-After ($tm + $timeToAudio)
        $st = Seek-Frame $km
        $off = $st.origin.offset
        $tref = $script:frameTimes[$km] - $timeToAudio
        $ct = $truth.com.t; $cc = $truth.com.combined
        $j = 0; while ($j -lt $ct.Count - 2 -and $ct[$j + 1] -lt $tref) { $j++ }
        $w = if ($ct[$j + 1] -ne $ct[$j]) { ($tref - $ct[$j]) / ($ct[$j + 1] - $ct[$j]) } else { 0 }
        $comX = $cc[$j][0] + $w * ($cc[$j + 1][0] - $cc[$j][0]) + $off[0]
        $comZ = -($cc[$j][2] + $w * ($cc[$j + 1][2] - $cc[$j][2])) + $off[2]
        $dd = [math]::Sqrt([math]::Pow($st.counterbalance.dot[0] - $comX, 2) + [math]::Pow($st.counterbalance.dot[1] - $comZ, 2))
        Assert ($dd -le 0.02) ("yellow dot under the truth combined COM: {0:0.0} mm" -f ($dd * 1000))
        $px = $iv.pivot[0] + $off[0]; $pz = -$iv.pivot[2] + $off[2]
        $dp = [math]::Sqrt([math]::Pow($st.counterbalance.pivot[0] - $px, 2) + [math]::Pow($st.counterbalance.pivot[2] - $pz, 2))
        Assert ($dp -le 0.03) ("pivot ring at the truth anchored foot: {0:0.0} mm" -f ($dp * 1000))
        Assert ($st.counterbalance.connection) "taut connection line drawn during the counterbalance"
        Assert (-not $st.counterbalance.dashed) "high-confidence interval drawn solid"
        $tr = (Get-State).traces
        Invoke-Unity hm_layer --layer traces --visible true | Out-Null
        $tr = (Get-State).traces
        Assert ($tr.visible -and $tr.emphasised -ge 1) "follower traces emphasised during the counterbalance ($($tr.emphasised) extremities)"
        Invoke-Unity hm_layer --layer traces --visible false | Out-Null

        Write-Host "== counterbalance review shots"
        Invoke-Json @("hm_tour", "--action", "goto", "--state", "orbit") | Out-Null
        Seek-Frame $km | Out-Null
        ReviewShot "${cap}_orbit_counterbalance_axis"
        $k90 = First-Frame-At-Or-After ([double]$iv.pivot_t0 + 0.9 * ([double]$iv.pivot_t1 - [double]$iv.pivot_t0) + $timeToAudio)
        Invoke-Json @("hm_tour", "--action", "goto", "--state", "overhead") | Out-Null
        Seek-Frame $k90 | Out-Null
        $st = Get-State
        Assert ($st.counterbalance.pivotsShown -ge 1 -and $st.counterbalance.sweptDeg -gt 200) ("overhead: pivot ring + leader arc ({0:0} deg swept so far)" -f $st.counterbalance.sweptDeg)
        ReviewShot "${cap}_overhead_pivot_ring_arc"
        Invoke-Json @("hm_tour", "--action", "goto", "--state", "geometry") | Out-Null
        Seek-Frame $km | Out-Null
        ReviewShot "${cap}_geometry_follower_traces"
        Invoke-Json @("hm_tour", "--action", "stop") | Out-Null
    } else {
        Write-Host "  (no truth.json for take '$take' - timing checks skipped)"
        Seek-Frame ([int]($script:frameTimes.Count / 2)) | Out-Null
    }
}

$g = (Get-State).graph
if ($null -ne $g -and $g.nodes -gt 0) {
    Write-Host "== dance graph path (VIEWER_SPEC 3.10, 14)"
    $r = Invoke-Json @("hm_graph", "--mode", "path")
    $s = Get-State
    $g = $s.graph
    Assert ($g.mode -eq "path" -and $s.tour.state -eq "dance_graph") "hm_graph path holds the Dance graph state"
    Assert ($g.nodes -ge 143 -and $g.links -ge 181) "graph: $($g.nodes) nodes, $($g.links) links ($($g.drawCalls) instanced draw calls)"
    Assert ($g.steps -gt 0 -and $g.segmentsMapped -eq $g.segments) "every label segment maps to one node ($($g.segmentsMapped)/$($g.segments), $($g.steps) steps)"
    Assert ($g.pathArrivalMaxErrorM -lt 0.001) ("couple on its node at every segment start (max {0:0.000} mm)" -f ($g.pathArrivalMaxErrorM * 1000))
    if ($g.provenance -eq "placeholder") { Assert ($g.badge -eq "PLACEHOLDER - not an analysis") "placeholder badge shown ('$($g.badge)')" }
    $interval = $s.meanFrameInterval
    $glideShot = $null; $dwellShot = $null
    for ($i = 0; $i -lt $g.steps; $i++) {
        $t0 = [double]$g.stepTimes[$i][0]; $t1 = [double]$g.stepTimes[$i][1]
        $st = Invoke-Transport seek @("--time", (Num ($t0 + 0.6 * $interval)))
        $ok = $st.graph.step -eq $i -and $st.graph.atNode -and $st.graph.nodeDistanceM -lt 0.01 -and $st.graph.currentNode -eq $g.stepNodes[$i]
        Assert $ok ("step {0} ({1}) at {2:0.00} s: couple on node (distance {3:0.0} mm)" -f $i, $st.graph.currentMove, $t0, ($st.graph.nodeDistanceM * 1000))
        if ($i -eq 1 -and $null -eq $dwellShot) {
            $st = Invoke-Transport seek @("--time", (Num ($t0 + 0.45 * ($t1 - $t0))))
            ReviewShot "${cap}_graph_path_dwell"; $dwellShot = $true
        }
        if ($i -ge 1 -and $st.graph.currentNode -ne $g.stepNodes[$i - 1]) {
            $st = Invoke-Transport seek @("--time", (Num ($t0 - 0.2)))
            Assert ($st.graph.gliding -and $st.graph.step -eq $i - 1) ("0.2 s before step {0}: gliding along the link" -f $i)
            if ($g.stepNewLink[$i] -and $null -eq $glideShot) {
                $st = Invoke-Transport seek @("--time", (Num ($t0 - 0.35)))
                ReviewShot "${cap}_graph_path_glide_new_link"; $glideShot = $true
            }
        }
    }
    if ($null -eq $glideShot -and $g.steps -ge 3) { Invoke-Transport seek @("--time", (Num ([double]$g.stepTimes[2][0] - 0.3))) | Out-Null; ReviewShot "${cap}_graph_path_glide" }
    Assert ($g.newLinks -eq (@($g.stepNewLink | Where-Object { $_ }).Count)) "new links in path.json flagged on their steps ($($g.newLinks))"

    Write-Host "== fingerprint (VIEWER_SPEC 3.11, 14)"
    Invoke-Json @("hm_graph", "--mode", "fingerprint") | Out-Null
    $s = Get-State
    $fp = $s.graph.fingerprint
    if ($null -ne $fp) {
        Assert ([math]::Abs($fp.nodeSecondsSum - $fp.labelledSeconds) -lt 0.01) ("node dwell seconds sum {0:0.000} = labelled {1:0.000}" -f $fp.nodeSecondsSum, $fp.labelledSeconds)
        Assert ([math]::Abs($fp.bandSecondsSum - $fp.labelledSeconds) -lt 0.01) ("energy-band seconds sum {0:0.000} = labelled" -f $fp.bandSecondsSum)
        Assert ([math]::Abs($fp.labelsSecondsSum - $fp.labelledSeconds) -lt 0.01) ("labels.json segments sum {0:0.000} = labelled" -f $fp.labelsSecondsSum)
        Assert ([math]::Abs($fp.shareSum - 1) -lt 0.01) ("dwell shares sum to {0:0.000}" -f $fp.shareSum)
        Assert ($fp.panel) "fingerprint side panel shown"
        Start-Sleep -Seconds 2
        ReviewShot "${cap}_fingerprint"
    } else { Assert $false "fingerprint.json loaded" }
    Invoke-Json @("hm_graph", "--mode", "off") | Out-Null
}

Write-Host "== hm_tour (VIEWER_SPEC 6)"
$layersBefore = (Get-State).layers
$s0 = Get-State
$expected = @("orbit", "overhead", "geometry", "physics")
if ($s0.graph.nodes -gt 0) { $expected += @("dance_graph", "fingerprint") }
$r = Invoke-Json @("hm_tour", "--action", "start", "--measures", "1")
Assert ($r.running -and $r.state -eq "orbit") "tour starts in Orbit"
$deadline = (Get-Date).AddSeconds(150)
$t = $r
while ((Get-Date) -lt $deadline) {
    $t = Invoke-Json @("hm_tour", "--action", "status")
    if ($null -ne $t -and @($t.history).Count -gt $expected.Count) { break }  # wrapped back to Orbit
    Start-Sleep -Milliseconds 700
}
$hist = @($t.history)
$order = @($hist | Select-Object -First $expected.Count | ForEach-Object { $_.state })
Assert (($order -join ",") -eq ($expected -join ",")) "tour order: $($order -join ' -> ') (camera tour slot: $($t.note))"
if ($hist.Count -gt $expected.Count) { Assert ($hist[$expected.Count].state -eq "orbit") "tour loops back to Orbit" }
$frameDt = $s0.meanFrameInterval
# a short take can start mid-measure (02_LarissaKadu: 0.14 s into measure 17); a state entered at the capture's first
# frame (restart / loop wrap) is then on the earliest instant of that measure the take has
$takeStart = if ($null -ne $base) { [double]$base } else { [double]::NaN }
foreach ($h in ($hist | Select-Object -First $expected.Count)) {
    if ($null -ne $h.measureStart) {
        $onMeasure = [math]::Abs($h.time - $h.measureStart) -le 2.5 * $frameDt
        $onTakeStart = $h.measureStart -lt $takeStart -and [math]::Abs($h.time - $takeStart) -le 2.5 * $frameDt
        Assert ($onMeasure -or $onTakeStart) ("{0} starts on a measure (t {1:0.000} s, measure {2} at {3:0.000} s{4})" -f $h.state, $h.time, $h.measure, $h.measureStart, $(if ($onTakeStart -and -not $onMeasure) { "; the take starts mid-measure at {0:0.000} s and the state starts on its first frame" -f $takeStart } else { "" }))
    }
}
Invoke-Json @("hm_tour", "--action", "stop") | Out-Null
Invoke-Unity hm_transport --action pause | Out-Null

# one review shot per tour state (held, deterministic time: mid-pivot when there is one, else 40 % into the take)
$shotTime = if ($null -ne $firstPivot) { ([double]$firstPivot[2] + [double]$firstPivot[3]) / 2 } elseif ($script:frameTimes.Count -gt 0) { $script:frameTimes[[int]($script:frameTimes.Count * 0.4)] } else { 5.0 }
# per-state avatar opacity = the user default (hm_opacity, ~0.3) x the state factor (VIEWER_SPEC 4)
$opBase = [double](Get-State).avatarOpacity
if ($opBase -le 0) { $opBase = 0.3 }
$opacity = @{ orbit = $opBase; overhead = $opBase * 2 / 3; geometry = $opBase * 0.5; physics = $opBase / 3; dance_graph = 0.0; fingerprint = 0.0 }
$k = 0
foreach ($state in $expected) {
    $k++
    Invoke-Json @("hm_tour", "--action", "goto", "--state", $state) | Out-Null
    Invoke-Transport seek @("--time", (Num $shotTime)) | Out-Null
    Start-Sleep -Milliseconds 1600
    $t = Invoke-Json @("hm_tour", "--action", "status")
    if ($s0.avatars.lead) { Assert ([math]::Abs($t.avatarAlpha - $opacity[$state]) -lt 0.02) ("{0}: avatar opacity {1:0.000} (spec {2:0.000})" -f $state, $t.avatarAlpha, $opacity[$state]) }
    if ($state -in "dance_graph", "fingerprint") { Assert ($t.skeleton -lt 0.01 -and $t.graphFade -gt 0.99) "${state}: full-size dance faded out, graph in" }
    ReviewShot ("{0}_tour_{1}_{2}" -f $cap, $k, $state)
}
Invoke-Json @("hm_tour", "--action", "stop") | Out-Null
$layersAfter = (Get-State).layers
$same = $true
foreach ($p in $layersBefore.PSObject.Properties) { if ($layersAfter.($p.Name) -ne $p.Value) { $same = $false; Write-Host "  layer $($p.Name): $($p.Value) -> $($layersAfter.($p.Name))" } }
Assert $same "hm_tour stop restores every layer"
