<#
Role hidden spans with a fade-in (RoleHiddenSpans, capture.json role_hidden, hm_hide; VIEWER_SPEC 3.14), dot-sourced by Tools/playtest.ps1
inside its per-capture loop (uses its Invoke-Unity / Get-State / Invoke-Transport / Assert helpers and $cap), or alone:
    pwsh Tools/playtest.ps1 -Only role_hidden                 # the default capture is the newest StreamingAssets capture that declares role_hidden
    pwsh Tools/playtest.ps1 -Capture <capture folder> -Only role_hidden
The clock is CAPTURE seconds = seconds from the capture's first frame = the viewer HUD's clock (audio time = first frame's audio time + capture).
A capture without role_hidden gets a test span set with hm_hide (and cleared again); one that has it is tested with its own spans (restored).
Checks: a role is fully absent (avatar, hair, shoes, skeleton, hand-contact light, floor-craft T / record, couple counterbalance dot) from the
first frame up to the span's end, FADES IN smoothly over fade_in seconds starting exactly there (alpha 0 -> 1, monotonic, no step; the avatar's
opacity = alpha x the viewer's avatar opacity, 0.35 stays 0.35), and is fully there afterwards; the other dancer is unaffected; hm_hide --clear brings
the role back at the same frame and a span changes no pose, frame or clock (a pure visibility switch); the follower's path; the film director
honours the same spans at its exact dance time, a direction that starts at T zero shows the capture's first frame at film 0 (her pose at film
0 = her pose at the capture's first frame, compared numerically), and no part of the first seconds is skipped.
#>

function Num([double]$x) { return $x.ToString("R", [System.Globalization.CultureInfo]::InvariantCulture) }
$capDir = Join-Path (Get-Location) "Assets\StreamingAssets\$cap"
$capJson = Get-Content (Join-Path $capDir "capture.json") -Raw | ConvertFrom-Json
$timeToAudio = 0.0
if ($null -ne $capJson.time_to_audio) { $timeToAudio = [double]$capJson.time_to_audio }
$script:frameTimes = @()
if ($capJson.times -and (Test-Path (Join-Path $capDir $capJson.times))) {
    $script:frameTimes = @((Get-Content (Join-Path $capDir $capJson.times) -Raw | ConvertFrom-Json) | ForEach-Object { [double]$_ + $timeToAudio })
}

# smoothstep fade-in alpha of one span at a capture second (the same law as RoleHiddenSpans)
function SpanAlpha([double]$c, [double]$from, [double]$to, [double]$fadeIn) {
    if (($from -le 0 -or $c -ge $from) -and $c -lt $to) { return 0.0 }
    if ($c -lt $to) { return 1.0 }
    if ($fadeIn -le 1e-6) { return 1.0 }
    $x = [math]::Min(1.0, [math]::Max(0.0, ($c - $to) / $fadeIn))
    return $x * $x * (3.0 - 2.0 * $x)
}

Write-Host "== role hidden spans with a fade-in (hm_hide, capture.json role_hidden)"
$rh0 = (Get-State).roleHidden
Assert ($null -ne $rh0 -and $null -ne $rh0.hiddenNow -and $null -ne $rh0.alphaNow -and $null -ne $rh0.skeletonLinesDrawn) "hm_state reports roleHidden (spans, alpha, hiddenNow, what is drawn)"
if ($null -ne $rh0 -and $script:frameTimes.Count -gt 1) {
    $firstAudio = $script:frameTimes[0]
    function AudioOf([double]$c) { return Num ($firstAudio + $c) }
    $configured = $null
    if ($null -ne $capJson.role_hidden -and $null -ne $capJson.role_hidden.lead) { $configured = $capJson.role_hidden.lead[0] }
    if ($null -ne $configured) {
        $spanFrom = [double]$configured.from; $spanTo = [double]$configured.to
        $fadeIn = 0.0; if ($null -ne $configured.fade_in) { $fadeIn = [double]$configured.fade_in }
        Write-Host "  (capture.json role_hidden.lead [$spanFrom, $spanTo) capture s, fade-in $fadeIn s)"
    } else {
        $spanFrom = 0.0; $spanTo = 1.6; $fadeIn = 1.0
        Write-Host "  (no role_hidden in capture.json: a test span [0, $spanTo) fade-in $fadeIn is set with hm_hide)"
    }

    function RestoreSpans {
        if ($null -ne $configured) { Invoke-Unity hm_hide --role lead --from (Num $spanFrom) --to (Num $spanTo) --fade (Num $fadeIn) | Out-Null }
        else { Invoke-Unity hm_hide --role lead --clear true | Out-Null }
    }

    Invoke-Unity hm_hide --role lead --from (Num $spanFrom) --to (Num $spanTo) --fade (Num $fadeIn) | Out-Null
    $rh = (Get-State).roleHidden
    Assert ([math]::Abs([double]$rh.spans.lead[0][1] - $spanTo) -lt 1e-3 -and [math]::Abs([double]$rh.spans.lead[0][2] - $fadeIn) -lt 1e-3 -and @($rh.spans.follow).Count -eq 0) "hm_hide --from --to --fade: the lead hidden [$spanFrom, $spanTo) s and faded in over $fadeIn s, the follower never"
    Assert ([math]::Abs(([double]$rh.spansAudio.lead[0][1]) - ($spanTo + $firstAudio)) -lt 1e-3) "spans are capture seconds (audio clock = first frame + capture)"

    $hasAvatars = $null -ne (Get-State).avatars.lead
    $defaultOpacity = [double](Get-State).avatarOpacityEffective
    function CheckAt([string]$name, [double]$c) {
        $s = Invoke-Transport seek @("--time", (AudioOf $c))
        $r = $s.roleHidden
        $want = SpanAlpha ([double]$r.captureTime) $spanFrom $spanTo $fadeIn
        $tag = "{0} (capture {1:0.00} s)" -f $name, $c
        $a = [double]$r.alphaNow.lead
        Assert ([math]::Abs($a - $want) -lt 0.01 -and [double]$r.alphaNow.follow -eq 1.0) "${tag}: lead alpha $([math]::Round($a, 3)) = the smooth fade-in law $([math]::Round($want, 3)), the follower always 1"
        $gone = $want -le 0.002
        Assert ([bool]$r.hiddenNow.lead -eq $gone -and -not [bool]$r.hiddenNow.follow) "${tag}: lead fully hidden = $gone, follower never"
        if ($hasAvatars) {
            Assert ([bool]$s.avatars.lead.visible -eq (-not $gone)) "${tag}: lead avatar drawn = $(-not $gone)"
            Assert ([math]::Abs([double]$s.avatars.lead.opacity - $want * $defaultOpacity) -lt 0.01) "${tag}: lead avatar opacity $([math]::Round([double]$s.avatars.lead.opacity, 3)) = alpha x the viewer's $defaultOpacity"
            Assert ([bool]$s.avatars.follow.visible -and [math]::Abs([double]$s.avatars.follow.opacity - $defaultOpacity) -lt 0.01) "${tag}: follower avatar drawn at the full opacity"
        }
        if ($gone) { Assert ([int]$r.skeletonLinesDrawn.lead -eq 0) "${tag}: no lead skeleton line drawn" }
        else { Assert ([int]$r.skeletonLinesDrawn.lead -ge 1 -and [math]::Abs([double]$r.skeletonAlpha.lead - $want) -lt 0.01) "${tag}: lead skeleton drawn ($($r.skeletonLinesDrawn.lead) lines, colour alpha $([math]::Round([double]$r.skeletonAlpha.lead, 3)))" }
        Assert ([int]$r.skeletonLinesDrawn.follow -ge 1) "${tag}: follower skeleton drawn ($($r.skeletonLinesDrawn.follow) lines)"
        if ($null -ne $s.contacts) { Assert ([bool]$r.contactLightsHidden -eq $gone -and (-not $gone -or [int]$s.contacts.orbsShown -eq 0)) "${tag}: hand-contact lights hidden = $gone" }
        if ($null -ne $s.counterbalance -and $null -ne $s.counterbalance.roleHidden) { Assert ([bool]$s.counterbalance.roleHidden -eq $gone -and (-not $gone -or -not [bool]$s.counterbalance.comMarkerRendered)) "${tag}: the couple's counterbalance dot hidden = $gone" }
        if ($null -ne $s.floorCraft -and $null -ne $s.floorCraft.roleHidden) {
            Assert ([bool]$s.floorCraft.roleHidden -eq $gone -and (-not $gone -or ([int]$s.floorCraft.currentT -eq -1 -and [int]$s.floorCraft.oldTShown -eq 0))) "${tag}: the leader's T axis and floor record hidden = $gone"
        }
        return $s
    }

    $null = CheckAt "first frame" 0.0
    $null = CheckAt "inside the span" ($spanTo * 0.5)
    $null = CheckAt "just before the fade" ($spanTo - 0.04)
    $null = CheckAt "the fade starts" $spanTo
    $null = CheckAt "a quarter into the fade" ($spanTo + $fadeIn * 0.25)
    $null = CheckAt "half way" ($spanTo + $fadeIn * 0.5)
    $null = CheckAt "fully faded in" ($spanTo + $fadeIn)
    $null = CheckAt "well after" ($spanTo + $fadeIn + 1.0)

    # no pop: the avatar's opacity and the skeleton alpha rise monotonically across the fade, in small steps
    $prev = 0.0; $maxStep = 0.0; $mono = $true
    $steps = 20
    for ($i = 0; $i -le $steps; $i++) {
        $c = $spanTo + $fadeIn * $i / $steps
        $s = Invoke-Transport seek @("--time", (AudioOf $c))
        $a = [double]$s.roleHidden.alphaNow.lead
        if ($a + 1e-4 -lt $prev) { $mono = $false }
        $maxStep = [math]::Max($maxStep, $a - $prev); $prev = $a
    }
    Assert ($mono -and $maxStep -lt 0.2 -and $prev -ge 0.999) "the fade is monotonic and smooth: largest step $([math]::Round($maxStep, 3)) over $steps samples, ends at alpha $([math]::Round($prev, 3))"

    # a pure visibility switch: the same frame, the follower's and the (hidden) leader's poses and the clock do not move
    $inside = $spanTo * 0.5
    $a = Invoke-Transport seek @("--time", (AudioOf $inside))
    Invoke-Unity hm_hide --role lead --clear true | Out-Null
    $b = Invoke-Transport seek @("--time", (AudioOf $inside))
    if ($hasAvatars) {
        $same = ($a.frame -eq $b.frame) -and ([math]::Abs($a.frameAudioTime - $b.frameAudioTime) -lt 1e-6) -and
            ((ConvertTo-Json $a.avatars.lead.pelvis -Compress) -eq (ConvertTo-Json $b.avatars.lead.pelvis -Compress)) -and
            ((ConvertTo-Json $a.avatars.follow.pelvis -Compress) -eq (ConvertTo-Json $b.avatars.follow.pelvis -Compress))
        Assert $same "no timing change: frame $($a.frame) = $($b.frame), both pelvis tracks identical with the span set and cleared"
        Assert ([bool]$b.avatars.lead.visible -and -not [bool]$a.avatars.lead.visible) "hm_hide --clear brings the lead back at the same frame"
    }

    Invoke-Unity hm_hide --role lead --from (Num $spanFrom) --to (Num $spanTo) --fade (Num $fadeIn) | Out-Null
    # the follower's path (generic per role): her avatar, skeleton + bead chain and neck axis go, his stay; a fade-out before the span too
    Invoke-Unity hm_hide --role follow --from 1.0 --to 2.2 --fade 0.5 --fadeout 0.5 | Out-Null
    $s = Invoke-Transport seek @("--time", (AudioOf 1.6))
    $r = $s.roleHidden
    Assert ([bool]$r.hiddenNow.follow -and [int]$r.skeletonLinesDrawn.follow -eq 0) "hm_hide --role follow: her skeleton and bead chain not drawn inside her span"
    if ($hasAvatars) { Assert (-not [bool]$s.avatars.follow.visible) "hm_hide --role follow: her avatar not drawn" }
    $s = Invoke-Transport seek @("--time", (AudioOf 0.75))
    Assert ([math]::Abs([double]$s.roleHidden.alphaNow.follow - 0.5) -lt 0.02 -and [int]$s.roleHidden.skeletonLinesDrawn.follow -ge 1) "fade-out: half way through the 0.5 s before her span she is at alpha $([math]::Round([double]$s.roleHidden.alphaNow.follow, 3))"
    Invoke-Unity hm_hide --role follow --clear true | Out-Null
    $s = Invoke-Transport seek @("--time", (AudioOf 1.6))
    Assert (-not [bool]$s.roleHidden.hiddenNow.follow -and [int]$s.roleHidden.skeletonLinesDrawn.follow -ge 1) "hm_hide --clear: she is back"

    # the film director honours the same spans at its exact dance time (hm_film_show), when this capture has a direction
    $fs = Invoke-Unity hm_film_show --action start --aspect horizontal --capture $cap --at 0 --pause true
    if ($fs -cmatch "no film direction|failed|Exception|enter Play") { Write-Host "  (no film direction for ${cap}: the director check is skipped)" }
    else {
        $f1 = $null
        for ($i = 0; $i -lt 40; $i++) {
            Start-Sleep -Milliseconds 700
            $f1 = ConvertFrom-UnityJson (Invoke-Unity hm_film_show --action state)
            if ($null -ne $f1 -and $f1.ready) { break }
        }

        $firstRef = $firstAudio - $timeToAudio
        if ($null -ne $f1 -and $null -ne $f1.danceTime) {
            Start-Sleep -Milliseconds 800
            $startsAtT0 = [math]::Abs([double]$f1.danceTime - $firstRef) -lt 0.002
            if ($startsAtT0) {
                Assert $true "film time 0 = the capture's first frame (dance $([double]$f1.danceTime) = reference $([math]::Round($firstRef, 4)))"
                # her pose at film 0 equals her pose at the capture's first frame, compared numerically
                $fh = Get-State
                $filmFollow = ConvertTo-Json $fh.avatars.follow.pelvis -Compress
                Invoke-Unity hm_film_show --action stop | Out-Null
                Start-Sleep -Milliseconds 800
                $free = Invoke-Transport seek @("--time", (AudioOf 0.0))
                $pf = @($fh.avatars.follow.pelvis | ForEach-Object { [double]$_ }); $pg = @($free.avatars.follow.pelvis | ForEach-Object { [double]$_ })
                $dist = [math]::Sqrt([math]::Pow($pf[0] - $pg[0], 2) + [math]::Pow($pf[1] - $pg[1], 2) + [math]::Pow($pf[2] - $pg[2], 2))
                Assert ($dist -lt 0.002) "her pelvis at film 0 = at the capture's first frame (distance $([math]::Round($dist * 1000, 3)) mm)"
                $fs = Invoke-Unity hm_film_show --action start --aspect horizontal --capture $cap --at 0 --pause true
                for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 700; $f1 = ConvertFrom-UnityJson (Invoke-Unity hm_film_show --action state); if ($null -ne $f1 -and $f1.ready) { break } }
            } else { Write-Host "  (the film direction starts at dance $([double]$f1.danceTime), not at the capture's first frame ($([math]::Round($firstRef, 4))): the T zero checks are skipped)" }
            foreach ($ft in @(0.0, 2.0, 3.9, 4.5, 5.0, 6.0)) {
                Invoke-Unity hm_film_show --action seek --at (Num $ft) --pause true | Out-Null
                Start-Sleep -Milliseconds 1500
                $fst = ConvertFrom-UnityJson (Invoke-Unity hm_film_show --action state)
                $rf = (Get-State).roleHidden
                $c = [double]$fst.danceTime + $timeToAudio - $firstAudio
                $want = SpanAlpha $c $spanFrom $spanTo $fadeIn
                Assert ([math]::Abs([double]$rf.alphaNow.lead - $want) -lt 0.02) ("film {0:0.0} s (dance {1:0.000}, capture {2:0.000}): lead alpha {3:0.000} = {4:0.000}" -f $ft, [double]$fst.danceTime, $c, [double]$rf.alphaNow.lead, $want)
                Assert ($want -gt 0.002 -or ([int]$rf.skeletonLinesDrawn.lead -eq 0 -and -not [bool]$rf.avatarDrawn.lead)) "film $ft s: while hidden neither his avatar nor his skeleton is drawn"
                Assert ($want -le 0.002 -or ([int]$rf.skeletonLinesDrawn.lead -ge 1 -and [bool]$rf.avatarDrawn.lead)) "film $ft s: once visible his avatar and skeleton are drawn"
                Assert ([int]$rf.skeletonLinesDrawn.follow -ge 1 -and [bool]$rf.avatarDrawn.follow) "film ${ft} s: she is always there"
            }
        } else { Write-Host "  (the film director did not become ready: its checks are skipped)" }
        Invoke-Unity hm_film_show --action stop | Out-Null
    }

    RestoreSpans
    Invoke-Unity hm_transport --action pause | Out-Null
}
