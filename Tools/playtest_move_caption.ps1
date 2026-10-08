<#
Move caption, unlabelled gaps and the graph inset (VIEWER_SPEC 3.10-3.12, 10), dot-sourced by
Tools/playtest_dance_layers.ps1 after its dance-graph checks (uses its helpers Invoke-Unity / Invoke-Json / Get-State /
Invoke-Transport / Assert / ReviewShot / Num and $cap / $capDir / $timeToAudio / $script:frameTimes). Checked against the
capture's moves/labels.json, path.json and graph.json (for the demo takes: the user's narration, provenance
"narration"):
  - the caption is on in the default view (layer "moves") and shows the Brazilian Portuguese name, the English alias
    (graph.json moves[].name / name_en), the narration-match confidence and the provenance tag
  - it changes on the first frame at or after every narrated boundary (within 1 frame of the exported f0)
  - unlabelled spans (labels.json "unresolved": narrated phrases no move matched; spans with no label) read
    "unlabelled", never a move; below 50 % confidence the caption reads "uncertain" with two candidates, except a
    narrated name that matched exactly / by alias (score >= 95) but was heard faintly: the single narrated name,
    "heard faintly - recogniser NN %", never a second move nobody named
  - Dance graph state: the couple crosses each unlabelled gap with no node lit and is on the next node by its start
  - no placeholder text on screen or in moves/*.json unless the timeline is a placeholder
  - directed playback: caption + graph inset (current node lit) in Orbit, inset hidden in Dance graph / Fingerprint
Non-ASCII text (Portuguese names, dashes) is compared with non-ASCII characters stripped on both sides: the CLI's
stdout and Windows PowerShell 5.1 disagree on encodings.
#>

$movesDir = Join-Path $capDir "moves"
$labelsPath = Join-Path $movesDir "labels.json"
$graphPath = Join-Path $movesDir "graph.json"
$pathPath = Join-Path $movesDir "path.json"
if (-not ((Test-Path $labelsPath) -and (Test-Path $graphPath))) { Write-Host "  (no moves/labels.json - move caption checks skipped)"; return }

function NormText($x) {
    if ($null -eq $x) { return "" }
    return ((("$x") -replace '[^\x20-\x7E]', ' ') -replace '\s+', ' ').Trim()
}

$labels = Get-Content $labelsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$graphJson = Get-Content $graphPath -Raw -Encoding UTF8 | ConvertFrom-Json
$pathJson = if (Test-Path $pathPath) { Get-Content $pathPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$moveName = @{}; $moveEnglish = @{}
foreach ($m in $graphJson.moves) { $moveName[$m.slug] = $m.name; $moveEnglish[$m.slug] = $m.name_en }
$nodeName = @{}
foreach ($n in $graphJson.nodes) { $nodeName[$n.id] = $n.name }
$stepOfSegment = @{}
if ($null -ne $pathJson) { foreach ($st in $pathJson.steps) { if ($st.segment) { $stepOfSegment[$st.segment] = $st } } }
$shift = if ($labels.time_base -eq "reference") { $timeToAudio } else { 0.0 }
$provs = @($labels.segments | ForEach-Object { $_.provenance } | Select-Object -Unique)
$placeholderTimeline = ($provs.Count -eq 1 -and $provs[0] -eq "placeholder")
$narrated = ($provs -contains "narration")

# spans: segments + unresolved gaps on the audio clock
$spans = @()
foreach ($sg in @($labels.segments)) {
    $spans += [pscustomobject]@{ t0 = [double]$sg.t0 + $shift; t1 = [double]$sg.t1 + $shift; gap = $false; item = $sg }
}
foreach ($u in @($labels.unresolved)) {
    if ($null -eq $u -or $null -eq $u.t0) { continue }
    $spans += [pscustomobject]@{ t0 = [double]$u.t0 + $shift; t1 = [double]$u.t1 + $shift; gap = $true; item = $u }
}
$spans = @($spans | Sort-Object t0)
$nF = $script:frameTimes.Count
Write-Host ("== move caption (VIEWER_SPEC 3.12): {0} segments + {1} unlabelled narrated phrases, provenance {2}" -f @($labels.segments).Count, @($labels.unresolved | Where-Object { $_ }).Count, ($provs -join ","))
if ($spans.Count -eq 0 -or $nF -eq 0) { Write-Host "  (empty move timeline - skipped)"; return }

function SpanAt([double]$t) {
    for ($i = $spans.Count - 1; $i -ge 0; $i--) {
        if ($spans[$i].t0 -le $t) { if ($t -lt $spans[$i].t1) { return $i }; return -1 }
    }
    return -1
}

function FirstFrameAtOrAfter([double]$t) {
    for ($k = 0; $k -lt $nF; $k++) { if ($script:frameTimes[$k] -ge $t) { return $k } }
    return -1
}

# the expected caption of span i (-1: no label)
function Expected([int]$i) {
    if ($i -lt 0) { return [pscustomobject]@{ kind = "unlabelled"; name = "unlabelled"; alias = $null; conf = $null; phrase = $null; prov = $null; move = $null } }
    $sp = $spans[$i]; $it = $sp.item
    if ($sp.gap) { return [pscustomobject]@{ kind = "unlabelled"; name = "unlabelled"; alias = $null; conf = $null; phrase = $it.unresolved; prov = $it.provenance; move = $null } }
    $st = $stepOfSegment[$it.id]
    $name = if ($null -ne $st -and $nodeName.ContainsKey($st.node)) { $nodeName[$st.node] } else { $moveName[$it.move] }
    $en = $moveEnglish[$it.move]
    if ($en -and ((NormText $en) -eq (NormText $name))) { $en = $null }
    $conf = if ($null -ne $it.confidence) { [double]$it.confidence } else { $null }
    $sure = $it.provenance -eq "narration" -and $it.match.status -eq "matched" -and $null -ne $it.match.score -and [double]$it.match.score -ge 95
    $kind = if ($it.provenance -in "narration", "auto" -and $null -ne $conf -and $conf -lt 0.5) { if ($sure) { "faint" } else { "uncertain" } } else { "move" }
    return [pscustomobject]@{ kind = $kind; name = $name; alias = $en; conf = $conf; phrase = $null; prov = $it.provenance; move = $it.move }
}

$narratedTag = "NARRATED " + [char]0x2014 + " not yet reviewed"
function Test-Caption($st, [int]$k, [string]$label) {
    $mc = $st.graph.moveCaption
    $i = SpanAt $script:frameTimes[$k]
    $e = Expected $i
    $shown = NormText $mc.name
    $ok = $mc.shown
    $why = @()
    if ($e.kind -eq "unlabelled") {
        if ($shown -ne "unlabelled") { $ok = $false; $why += "name '$shown'" }
        if ($null -ne $mc.confidenceBar) { $ok = $false; $why += "a confidence bar" }
        if ($e.phrase -and -not ((NormText $mc.alias) -like "*$(NormText $e.phrase)*")) { $ok = $false; $why += "alias '$(NormText $mc.alias)' lacks the phrase" }
    } elseif ($e.kind -eq "uncertain") {
        if (-not ($shown -like "uncertain*" -and $shown -like "*$(NormText $e.name)*")) { $ok = $false; $why += "name '$shown'" }
    } else {
        if ($shown -ne (NormText $e.name)) { $ok = $false; $why += "name '$shown' (want '$(NormText $e.name)')" }
        if ((NormText $mc.alias) -ne (NormText $e.alias)) { $ok = $false; $why += "alias '$(NormText $mc.alias)' (want '$(NormText $e.alias)')" }
        if ($e.kind -eq "faint" -and -not ((NormText $mc.confidence) -like "*heard faintly*recogniser*")) { $ok = $false; $why += "confidence text '$(NormText $mc.confidence)' (want 'heard faintly - recogniser')" }
    }
    if ($e.kind -ne "unlabelled" -and $e.prov -eq "narration") {
        $pct = if ("$($mc.confidence)" -match '(\d+)\s*%') { [int]$Matches[1] } else { -1 }
        if ([math]::Abs($pct - 100 * $e.conf) -gt 0.51) { $ok = $false; $why += "confidence '$(NormText $mc.confidence)' (want $([math]::Round(100 * $e.conf, 1)) %)" }
        if ([math]::Abs([double]$mc.confidenceBar - $e.conf) -gt 0.001) { $ok = $false; $why += "bar $($mc.confidenceBar)" }
    }
    if ($e.prov -eq "narration" -and (NormText $mc.tag) -ne (NormText $narratedTag)) { $ok = $false; $why += "tag '$(NormText $mc.tag)'" }
    $desc = if ($i -ge 0 -and -not $spans[$i].gap) { "$($e.name) [$($e.kind)]" } elseif ($i -ge 0) { "unlabelled '$($e.phrase)'" } else { "unlabelled (no label)" }
    Assert $ok ("{0}: frame {1} ({2:0.000} s) shows {3}{4}" -f $label, $k, $script:frameTimes[$k], $desc, $(if ($why.Count) { " - " + ($why -join "; ") } else { "" }))
}

function Seek-Mid([int]$k) {
    # a third of a frame after frame k's time: the transport shows the nearest frame
    $st = Invoke-Transport seek @("--time", (Num ($script:frameTimes[$k] + 0.3 * $frameDt)))
    if ($st.frame -ne $k) { Assert $false "seek to frame $k landed on frame $($st.frame)" }
    return $st
}

$frameDt = [double](Get-State).meanFrameInterval
Invoke-Unity hm_graph --mode off | Out-Null
$s = Get-State
Assert ($s.layers.moves) "layer 'moves' on by default"
Invoke-Unity hm_layer --layer moves --visible false | Out-Null
$s = Seek-Mid ([int]($nF / 2))
Assert (-not $s.graph.moveCaption.shown -and -not $s.layers.moves) "hm_layer moves false hides the caption"
Invoke-Unity hm_layer --layer moves --visible true | Out-Null
$s = Seek-Mid ([int]($nF / 2))
if ($placeholderTimeline) {
    Assert (-not $s.graph.moveCaption.shown) "a placeholder timeline is not captioned in the free view"
} else {
    Assert ($s.graph.moveCaption.shown) "the caption shows in the default view ($($s.skeletonMode) skeletons, lower-left, lift $([math]::Round([double]$s.graph.moveCaption.lift)) px)"

    # every narrated boundary: the frame before shows the previous span, the first frame at or after shows the new one
    $checked = 0; $edgeWorst = 0
    for ($i = 0; $i -lt $spans.Count; $i++) {
        $sp = $spans[$i]
        $fa = FirstFrameAtOrAfter $sp.t0
        if ($fa -lt 0) { continue }  # starts after the capture
        $label = "span {0} start {1:0.000} s" -f $i, $sp.t0
        if ($fa -eq 0) {
            if ($sp.t1 -gt $script:frameTimes[0]) { Test-Caption (Seek-Mid 0) 0 "$label (before the capture's first frame)"; $checked++ }
            continue
        }
        if ($null -ne $sp.item.f0) {
            $edge = [math]::Abs($fa - [int]$sp.item.f0); $edgeWorst = [math]::Max($edgeWorst, $edge)
            Assert ($edge -le 1) ("{0}: caption changes at frame {1}, exported f0 {2} (within 1 frame)" -f $label, $fa, $sp.item.f0)
        }
        Test-Caption (Seek-Mid ($fa - 1)) ($fa - 1) "$label, frame before"
        Test-Caption (Seek-Mid $fa) $fa "$label, first frame"
        $checked++
        # the end of a span followed by nothing (a hole or the end of the timeline): "unlabelled" from its end
        $nextT0 = if ($i + 1 -lt $spans.Count) { $spans[$i + 1].t0 } else { [double]::MaxValue }
        if ($sp.t1 -lt $nextT0 - 0.01) {
            $fe = FirstFrameAtOrAfter $sp.t1
            if ($fe -gt 0) { Test-Caption (Seek-Mid $fe) $fe ("span {0} end {1:0.000} s, first frame after" -f $i, $sp.t1) }
        }
    }
    Assert ($checked -gt 0) "$checked span starts checked (worst change vs exported f0: $edgeWorst frame)"

    # the next move previewed in the last beat of a span
    $pick = $null
    for ($i = 0; $i -lt $spans.Count - 1; $i++) { if (-not $spans[$i].gap -and -not $spans[$i + 1].gap -and $spans[$i].t1 -lt $script:frameTimes[$nF - 1]) { $pick = $i; break } }
    if ($null -ne $pick) {
        $kEnd = (FirstFrameAtOrAfter $spans[$pick].t1) - 2
        $kMid = FirstFrameAtOrAfter (($spans[$pick].t0 + $spans[$pick].t1) / 2)
        if ($kMid -gt 0 -and $kEnd -gt $kMid) {
            $a = Seek-Mid $kMid; $b = Seek-Mid $kEnd
            $want = NormText (Expected ($pick + 1)).name
            Assert ([string]::IsNullOrEmpty($a.graph.moveCaption.next) -or ($spans[$pick].t1 - $script:frameTimes[$kMid]) -lt 0.8) "no next-move preview mid-span"
            Assert ((NormText $b.graph.moveCaption.next) -like "*$want*") "the last beat previews the next move ('$(NormText $b.graph.moveCaption.next)')"
        }
    }

    # cross-fade: the new caption fades in over ~0.15 s from the span start
    $fa = FirstFrameAtOrAfter $spans[[math]::Min(2, $spans.Count - 1)].t0
    if ($fa -gt 0 -and $fa + 8 -lt $nF) {
        $a = (Seek-Mid $fa).graph.moveCaption.alpha; $b = (Seek-Mid ($fa + 8)).graph.moveCaption.alpha
        Assert ($a -lt $b -and $b -gt 0.99) ("caption cross-fades in at a boundary (alpha {0:0.00} -> {1:0.00})" -f $a, $b)
    }
}

# no placeholder anywhere for a real timeline: files and on-screen text in every graph mode
if (-not $placeholderTimeline) {
    $hits = @(Get-ChildItem $movesDir -Filter *.json | Select-String -Pattern '"provenance"\s*:\s*"placeholder"|PLACEHOLDER|placeholder walk' -List)
    Assert ($hits.Count -eq 0) "no placeholder timeline in $cap/moves ($(@($hits | ForEach-Object { $_.Filename }) -join ', '))"
    $texts = @()
    foreach ($mode in "off", "path", "fingerprint") {
        Invoke-Unity hm_graph --mode $mode | Out-Null
        $st = Seek-Mid ([int]($nF * 0.45))
        $g = $st.graph
        $texts += @($g.badge, $g.topBadge, $g.caption, $g.captionSub, $g.moveCaption.tag, $g.moveCaption.confidence, $g.moveCaption.info, $g.moveCaption.next)
        if ($null -ne $g.fingerprint) { $texts += @($g.fingerprint.panelText) }
        if ($mode -eq "fingerprint" -and $narrated) {
            Assert ((NormText $g.topBadge) -eq (NormText $narratedTag)) "fingerprint badge '$(NormText $g.topBadge)' (narrated, not reviewed)"
            Assert (-not $g.moveCaption.shown) "no move caption over the fingerprint"
        }
        if ($mode -eq "path" -and $narrated) { Assert ((NormText $g.badge) -eq (NormText $narratedTag) -and $null -eq $g.topBadge) "Dance graph: the caption carries the narrated tag ('$(NormText $g.badge)'), no PLACEHOLDER badge" }
    }
    $bad = @($texts | Where-Object { "$_" -match '(?i)placeholder' })
    Assert ($bad.Count -eq 0) "no placeholder text on screen ($($bad -join ' | '))"
    Invoke-Unity hm_graph --mode off | Out-Null
}

# Dance graph: every unlabelled gap crossed with no node lit, the next node reached by its segment start
$gapSpans = @($spans | Where-Object { $_.gap })
if ($gapSpans.Count -gt 0 -and $null -ne $pathJson) {
    Invoke-Unity hm_graph --mode path | Out-Null
    foreach ($gp in $gapSpans) {
        $km = FirstFrameAtOrAfter (($gp.t0 + $gp.t1) / 2)
        if ($km -le 0) { continue }
        $st = Seek-Mid $km
        $g = $st.graph
        Assert ($g.inGap -and $g.gliding -and (NormText $g.moveCaption.name) -eq "unlabelled") ("unlabelled '{0}' {1:0.00}-{2:0.00} s: Dance graph caption '{3}', couple crossing the gap (gliding {4}, no node lit {5})" -f $gp.item.unresolved, $gp.t0, $gp.t1, (NormText $g.moveCaption.name), $g.gliding, $g.inGap)
        $kn = FirstFrameAtOrAfter $gp.t1
        if ($kn -gt 0) {
            $st = Seek-Mid $kn
            $g = $st.graph
            Assert ($g.atNode -and -not $g.inGap -and $g.nodeDistanceM -lt 0.001) ("after '{0}': on the next node at its start (frame {1}, {2:0.0} mm, {3})" -f $gp.item.unresolved, $kn, ($g.nodeDistanceM * 1000), $g.currentMove)
        }
    }
    $breaks = @($g.stepBreak | Where-Object { $_ }).Count
    Assert ($breaks -ge @($pathJson.steps | Where-Object { $_.break_before }).Count) "path breaks flagged on their steps ($breaks)"
    Invoke-Unity hm_graph --mode off | Out-Null
}

# directed playback: caption + graph inset (current node lit) outside the graph states (spec 3.12)
$moveSpan = $null
for ($i = 0; $i -lt $spans.Count; $i++) { if (-not $spans[$i].gap -and $spans[$i].t0 -gt $script:frameTimes[0] -and $stepOfSegment.ContainsKey($spans[$i].item.id)) { $moveSpan = $i; break } }
if ($null -ne $moveSpan) {
    $sp = $spans[$moveSpan]
    $wantNode = $stepOfSegment[$sp.item.id].node
    Invoke-Json @("hm_tour", "--action", "goto", "--state", "orbit") | Out-Null
    $st = Seek-Mid (FirstFrameAtOrAfter (($sp.t0 + $sp.t1) / 2))
    Start-Sleep -Milliseconds 300
    $st = Get-State
    Assert ($st.graph.moveCaption.shown -and $st.graph.inset.shown -and $st.graph.inset.currentNode -eq $wantNode) ("Orbit (directed): caption '{0}' + graph inset with the current node lit ({1})" -f (NormText $st.graph.moveCaption.name), $st.graph.inset.currentNode)
    if ($gapSpans.Count -gt 0) {
        $kg = FirstFrameAtOrAfter (($gapSpans[0].t0 + $gapSpans[0].t1) / 2)
        if ($kg -gt 0) { $st = Seek-Mid $kg; Assert ($st.graph.inset.shown -and $null -eq $st.graph.inset.currentNode) "inset during an unlabelled move: no node lit" }
    }
    foreach ($state in "dance_graph", "fingerprint") {
        Invoke-Json @("hm_tour", "--action", "goto", "--state", $state) | Out-Null
        $st = Get-State
        Assert (-not $st.graph.inset.shown) "${state}: inset hidden (the full graph is on screen)"
        Assert ($st.graph.moveCaption.shown -eq ($state -eq "dance_graph")) "${state}: move caption $(if ($state -eq 'dance_graph') { 'shown' } else { 'hidden' })"
    }
    Invoke-Json @("hm_tour", "--action", "stop") | Out-Null
    $st = Get-State
    Assert (-not $st.graph.inset.shown -and ($st.graph.moveCaption.shown -eq (-not $placeholderTimeline))) "after the tour: inset gone, caption back to its layer"
}
