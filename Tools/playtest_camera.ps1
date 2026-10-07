<#
Camera rig and avatar opacity playtest (VIEWER_SPEC 5.2 / 3.2), dot-sourced by Tools/playtest.ps1 per capture (uses its
Invoke-Unity / Get-State / Invoke-Transport / Assert / Shot helpers and $cap / $prefix / $base). Checks:
  - hm_orbit places the free camera spherically around the look point at the look height (0.95 m)
  - while the dancers play (and dip / bounce) the eye and look heights never change; the XZ follow lags <= 0.45 m
  - free-fly keys: E raises the eye (look height kept), W dollies horizontally (eye height kept), Z tilts
  - director states set their own heights (orbit 1.54-2.06 m, physics 1.75 m); a camera key takes over with a
    continuous eye while the tour keeps its state; hm_tour stop leaves free-fly
  - seeks / loop wraps are cuts (snap), not glides; pausing does not glide the view (it coasts to a stop)
  - hm_hair frame parks the camera even while a tour state is shown
  - director blends never pop: a per-frame trace (hm_camtrace) across EVERY ordered pair of view states, interrupted
    blends, the running tour's own advances (fingerprint -> orbit) and O-key hand-overs has no one-frame jump > 0.5 m
    that is not a cut (seek / loop wrap)
  - avatars are semi-transparent by default (hm_opacity), per-state opacity scales the default; the two dancers are
    sorted back to front per camera (the nearer one's translucent queues +20), so each shows through the other
Review shots go to Assets/Screenshots/<prefix>_camera_*.png; the trace to Assets/Screenshots/<prefix>_camtrace.csv.
#>

function Get-PairWalk([int]$n) {
    # Hierholzer: a closed walk over the complete directed graph on n states that takes every ordered pair once
    $out = @{}
    for ($i = 0; $i -lt $n; $i++) {
        $out[$i] = [System.Collections.Generic.Queue[int]]::new()
        for ($j = 0; $j -lt $n; $j++) { if ($j -ne $i) { $out[$i].Enqueue($j) } }
    }
    $stack = [System.Collections.Generic.Stack[int]]::new()
    $walk = [System.Collections.Generic.List[int]]::new()
    $stack.Push(0)
    while ($stack.Count -gt 0) {
        $v = $stack.Peek()
        if ($out[$v].Count -gt 0) { $stack.Push($out[$v].Dequeue()) } else { $walk.Add($stack.Pop()) }
    }
    $walk.Reverse()
    return , $walk.ToArray()
}

function Tap([string]$key, [int]$ms = 400) {
    Invoke-Unity simulate_key --key $key --action down | Out-Null
    Start-Sleep -Milliseconds $ms
    Invoke-Unity simulate_key --key $key --action up | Out-Null
    Start-Sleep -Milliseconds 1000  # the key velocity ramps out over ~0.12 s
}

$cs = Get-State
if ($null -ne $cs.camera) {
    Write-Host "== camera rig (VIEWER_SPEC 5.2)"
    Invoke-Unity hm_transport --action pause | Out-Null
    Invoke-Unity hm_tour --action stop | Out-Null
    $o = Invoke-Unity hm_orbit --azimuth 60 --elevation 15 --radius 3 --look 0.95
    $c = (Get-State).camera
    Assert ([math]::Abs($c.eye[1] - 1.7265) -lt 0.002 -and [math]::Abs($c.look[1] - 0.95) -lt 1e-4 -and $c.mode -eq "free") `
        ("hm_orbit 60/15/3: eye y {0:0.000} (1.726), look y {1:0.000} (0.95), mode {2}" -f $c.eye[1], $c.look[1], $c.mode)

    # play: the dancers bounce, the camera height does not
    Invoke-Unity hm_transport --action restart | Out-Null
    Invoke-Unity hm_orbit --azimuth 60 --elevation 15 --radius 3 | Out-Null
    $cuts0 = (Get-State).camera.cuts
    Invoke-Unity hm_transport --action play | Out-Null
    $eyeY = @(); $lookY = @(); $lags = @(); $heads = @()
    $deadline = (Get-Date).AddSeconds(4)
    while ((Get-Date) -lt $deadline) {
        $s = Get-State
        if (-not $s.playing) { break }
        $eyeY += $s.camera.eye[1]; $lookY += $s.camera.look[1]; $lags += $s.camera.lag
        if ($null -ne $s.avatars.follow) { $heads += $s.avatars.follow.head[1] }
    }
    $cuts1 = (Get-State).camera.cuts
    Invoke-Unity hm_transport --action pause | Out-Null
    if ($eyeY.Count -ge 3) {
        $span = ($eyeY | Measure-Object -Maximum -Minimum); $lspan = ($lookY | Measure-Object -Maximum -Minimum)
        $lagMax = ($lags | Measure-Object -Maximum).Maximum
        $hs = if ($heads.Count) { ($heads | Measure-Object -Maximum -Minimum) } else { $null }
        $headSpan = if ($hs) { $hs.Maximum - $hs.Minimum } else { 0 }
        Assert (($span.Maximum - $span.Minimum) -lt 1e-4 -and ($lspan.Maximum - $lspan.Minimum) -lt 1e-4) `
            ("camera height fixed while playing: eye y span {0:0.00000} m, look y span {1:0.00000} m over {2} samples (follow head moved {3:0.000} m)" -f ($span.Maximum - $span.Minimum), ($lspan.Maximum - $lspan.Minimum), $eyeY.Count, $headSpan)
        Assert ($lagMax -le 0.45) ("XZ follow stays on the couple: max look-anchor lag {0:0.000} m" -f $lagMax)
        Assert ($cuts1 -eq $cuts0) "no cuts while playing straight through ($cuts0 -> $cuts1)"
    } else { Write-Host "  (too few samples while playing: $($eyeY.Count))" }

    # pausing is not a cue to move: the follow coasts to a stop where it is (VIEWER_SPEC 5.2)
    Invoke-Unity hm_transport --action restart | Out-Null
    Invoke-Unity hm_orbit --azimuth 60 --elevation 15 --radius 3 | Out-Null
    Invoke-Unity hm_transport --action play | Out-Null
    Start-Sleep -Milliseconds 2500
    Invoke-Unity hm_transport --action pause | Out-Null
    $p0 = (Get-State).camera
    Start-Sleep -Milliseconds 1500
    $p1 = (Get-State).camera
    $glide = [math]::Sqrt([math]::Pow($p1.lookAnchor[0] - $p0.lookAnchor[0], 2) + [math]::Pow($p1.lookAnchor[1] - $p0.lookAnchor[1], 2))
    Assert ($glide -lt 0.03 -and -not $p1.settling) ("paused: the view stays put ({0:0.000} m of look-anchor motion in 1.5 s after the pause)" -f $glide)

    # free-fly keys (focus the Game view; simulate_key drives the Input System)
    Invoke-Unity hm_orbit --azimuth 60 --elevation 15 --radius 3 | Out-Null
    $c0 = (Get-State).camera
    Tap E 500
    $c1 = (Get-State).camera
    Assert (($c1.eyeHeight - $c0.eyeHeight) -gt 0.2 -and [math]::Abs($c1.lookHeight - $c0.lookHeight) -lt 1e-4) `
        ("E cranes up: eye height {0:0.000} -> {1:0.000} m, look height kept {2:0.000}" -f $c0.eyeHeight, $c1.eyeHeight, $c1.lookHeight)
    Tap W 500
    $c2 = (Get-State).camera
    Assert (($c1.distance - $c2.distance) -gt 0.2 -and [math]::Abs($c2.eye[1] - $c1.eye[1]) -lt 1e-3) `
        ("W dollies horizontally: distance {0:0.000} -> {1:0.000} m, eye y kept {2:0.000}" -f $c1.distance, $c2.distance, $c2.eye[1])

    if ($null -ne $cs.avatars.lead) {
        Write-Host "== director heights + takeover"
        Invoke-Unity hm_tour --action goto --state orbit | Out-Null
        Start-Sleep -Milliseconds 1500
        $c = (Get-State).camera
        Assert ($c.mode -eq "director" -and $c.eye[1] -ge 1.53 -and $c.eye[1] -le 2.07 -and [math]::Abs($c.look[1] - 0.95) -lt 1e-3) `
            ("orbit state: mode {0}, eye y {1:0.000} in [1.54, 2.06], look y {2:0.000}" -f $c.mode, $c.eye[1], $c.look[1])
        Invoke-Unity hm_tour --action goto --state physics | Out-Null
        Start-Sleep -Milliseconds 1500
        $c = (Get-State).camera
        Assert ([math]::Abs($c.eye[1] - 1.754) -lt 0.01 -and [math]::Abs($c.look[1] - 0.90) -lt 1e-3) ("physics state: eye y {0:0.000} (1.754), look y {1:0.000} (0.90)" -f $c.eye[1], $c.look[1])
        Tap Z 150
        $t = (Get-State)
        $d = Dist $c.eye $t.camera.eye
        Assert ($t.camera.mode -eq "free" -and $d -lt 0.01 -and $t.tour.state -eq "physics") `
            ("Z takes the camera: mode {0}, eye moved {1:0.0000} m, tour still in {2}" -f $t.camera.mode, $d, $t.tour.state)

        # hm_hair frame parks the camera even while a director state is shown
        if (Test-Path (Join-Path (Get-Location) "Assets\StreamingAssets\$cap\hair_groom.json")) {
            Invoke-Unity hm_tour --action goto --state orbit | Out-Null
            Invoke-Unity hm_hair --action frame --azimuth 150 --elevation 10 --radius 1.4 | Out-Null
            $p0 = (Get-State).camera
            Start-Sleep -Milliseconds 1000
            $p1 = (Get-State).camera
            Assert ($p1.mode -eq "parked") "hm_hair frame parks the camera during a tour state (mode $($p1.mode))"
            Invoke-Unity hm_hair --action release | Out-Null
            Assert ((Get-State).camera.mode -eq "free") "hm_hair release returns to free-fly"
        }
        Invoke-Unity hm_tour --action stop | Out-Null
        Assert ((Get-State).camera.mode -eq "free") "hm_tour stop leaves the camera in free-fly"

        Write-Host "== director blends never pop (per-frame trace: every state pair, interrupts, the running tour, O key)"
        $states = @("orbit", "overhead", "geometry", "physics", "dance_graph", "fingerprint")
        $walk = Get-PairWalk $states.Count
        Invoke-Unity hm_transport --action restart | Out-Null
        Invoke-Unity hm_transport --action loop_on | Out-Null  # keep the dancers moving (each wrap is a cut)
        Invoke-Unity hm_transport --action play | Out-Null
        Invoke-Unity hm_orbit --azimuth 60 --elevation 15 --radius 3 | Out-Null
        Invoke-Unity hm_camtrace --action start | Out-Null
        foreach ($i in $walk) {
            Invoke-Unity hm_tour --action goto --state $states[$i] | Out-Null
            Start-Sleep -Milliseconds 1300  # blends last 1 s
        }
        # interrupted blends: a new state while the last blend runs (also overhead <-> fingerprint, the big ones)
        foreach ($pair in @(@("overhead", 450), @("fingerprint", 300), @("orbit", 600), @("dance_graph", 250), @("geometry", 500),
                            @("fingerprint", 700), @("overhead", 350), @("physics", 1300))) {
            Invoke-Unity hm_tour --action goto --state $pair[0] | Out-Null
            Start-Sleep -Milliseconds $pair[1]
        }
        # the O key: free <-> director hand-overs, also mid-blend
        foreach ($ms in @(800, 300, 1200, 250, 1300)) {
            Invoke-Unity hm_orbit --mode toggle | Out-Null
            Start-Sleep -Milliseconds $ms
        }
        Invoke-Unity hm_transport --action loop_off | Out-Null
        # the running tour's own measure-boundary advances, from the fingerprint (fingerprint -> orbit -> overhead)
        Invoke-Unity hm_tour --action start --state fingerprint --measures 1 | Out-Null
        $tourDeadline = (Get-Date).AddSeconds(25)
        $seen = @{}
        while ((Get-Date) -lt $tourDeadline) {
            $ts = (Get-State).tour.state
            $seen[$ts] = $true
            if ($seen.ContainsKey("overhead")) { break }
            Start-Sleep -Milliseconds 700
        }
        Start-Sleep -Milliseconds 1300
        Invoke-Unity hm_tour --action stop | Out-Null
        Invoke-Unity hm_transport --action pause | Out-Null
        $csv = Join-Path (Get-Location) "Assets\Screenshots\${prefix}_camtrace.csv"
        $tr = ConvertFrom-UnityJson (Invoke-Unity hm_camtrace --action stop --path $csv)
        Write-Host ("  trace: {0} frames over {1:0.0} s, {2} shot changes, {3} cuts, max eye step {4:0.000} m, max look step {5:0.000} m, max speed {6:0.0} m/s" -f `
            $tr.samples, $tr.seconds, $tr.shotChanges, $tr.cuts, $tr.maxEyeStep, $tr.maxLookStep, $tr.maxSpeed)
        foreach ($b in @($tr.bad)) {
            if ($null -ne $b) { Write-Host ("    step {0:0.000} m eye / {1:0.000} m look in {2:0.000} s, {3} -> {4}, spike {5}, slow frame {6}" -f $b.eyeStep, $b.lookStep, $b.dt, $b.prevState, $b.state, $b.spike, $b.slowFrame) }
        }
        Assert ($tr.shotChanges -ge ($walk.Count + 8)) ("the trace covers every state pair, the interrupts and the tour ({0} shot changes)" -f $tr.shotChanges)
        Assert ($seen.ContainsKey("orbit") -and $seen.ContainsKey("overhead")) "the running tour advanced fingerprint -> orbit -> overhead by itself"
        Assert ($tr.pops -eq 0 -and $tr.jumps -eq 0) ("no camera pops: {0} one-frame jumps > 0.5 m outside cuts ({1} speed spikes; {2} larger steps only in slow editor frames)" -f $tr.jumps, $tr.pops, $tr.slowSteps)
    }

    # loop wrap = cut (snap), no glide back across the floor
    $s = Get-State
    if ($null -ne $s.measure) {
        Invoke-Unity hm_transport --action restart | Out-Null
        Invoke-Unity hm_transport --action loop_on | Out-Null
        $k0 = (Get-State).camera.cuts
        Invoke-Unity hm_transport --action play | Out-Null
        Start-Sleep -Seconds 5
        $k1 = (Get-State).camera.cuts
        Invoke-Unity hm_transport --action loop_off | Out-Null
        Invoke-Unity hm_transport --action pause | Out-Null
        Assert ($k1 -gt $k0) "measure loop wraps are camera cuts ($k0 -> $k1)"
    }

    Write-Host "== avatar opacity (VIEWER_SPEC 3.2)"
    $s = Get-State
    if ($null -ne $s.avatars.lead) {
        Assert ($s.avatarOpacity -gt 0.2 -and $s.avatarOpacity -lt 0.5) ("avatars semi-transparent by default ({0:0.00})" -f $s.avatarOpacity)
        Assert ([math]::Abs($s.avatars.lead.opacity - $s.avatarOpacity) -lt 1e-3 -and [math]::Abs($s.avatars.follow.opacity - $s.avatarOpacity) -lt 1e-3) "both bodies at the default opacity"
        $def = $s.avatarOpacity
        $r = ConvertFrom-UnityJson (Invoke-Unity hm_opacity --value 0.5)
        Assert ([math]::Abs($r.avatars.lead.opacity - 0.5) -lt 1e-3) "hm_opacity 0.5 reaches the bodies"
        Invoke-Unity hm_opacity --value $def | Out-Null

        # VIEWER_SPEC 3.3 option: a translucent body can skip its own skeleton's pixels (stencil); off by default
        $k0 = ConvertFrom-UnityJson (Invoke-Unity hm_opacity)
        $k1 = ConvertFrom-UnityJson (Invoke-Unity hm_opacity --skeletons over)
        $k2 = ConvertFrom-UnityJson (Invoke-Unity hm_opacity --value 1)
        Invoke-Unity hm_opacity --value $def --skeletons dimmed | Out-Null
        Assert (-not $k0.skeletonsOverBodies -and -not $k0.avatars.lead.skeletonOverBody -and $k1.avatars.lead.skeletonOverBody -and
                $k1.avatars.follow.skeletonOverBody -and $k1.avatars.lead.skeletonStencilBit -eq 1 -and $k1.avatars.follow.skeletonStencilBit -eq 2 -and
                -not $k2.avatars.lead.skeletonOverBody) `
            "skeleton option: off by default; --skeletons over skips each body's own skeleton (bits 1 / 2) while translucent, not at opacity 1"

        # back-to-front: the dancer nearer to the camera draws after the other (queue +20), from either side
        Invoke-Unity hm_transport --action seek --time "$($base + 4.0)" | Out-Null
        foreach ($az in @(0, 180)) {
            Invoke-Unity hm_orbit --azimuth $az --elevation 10 --radius 3 | Out-Null
            Start-Sleep -Milliseconds 400  # rendered frames: the sort runs per camera before culling
            $st = Get-State
            $dl = Dist $st.camera.eye $st.avatars.lead.pelvis; $df = Dist $st.camera.eye $st.avatars.follow.pelvis
            $nearer = if ($dl -lt $df) { "lead" } else { "follow" }; $farther = if ($nearer -eq "lead") { "follow" } else { "lead" }
            $q = ConvertFrom-UnityJson (Invoke-Unity hm_opacity)
            $qn = @($q.avatars.$nearer.queues.PSObject.Properties.Value); $qf = @($q.avatars.$farther.queues.PSObject.Properties.Value)
            $maxF = ($qf | Measure-Object -Maximum).Maximum; $minN = ($qn | Measure-Object -Minimum).Minimum
            if ([math]::Abs($dl - $df) -gt 0.06) {
                Assert ($q.avatars.$nearer.queueOffset -eq 20 -and $q.avatars.$farther.queueOffset -eq 0 -and $minN -gt $maxF) `
                    ("azimuth {0}: the nearer {1} ({2:0.00} m vs {3:0.00} m) draws after the {4}: queues {5}-{6} after <= {7}" -f $az, $nearer, [math]::Min($dl, $df), [math]::Max($dl, $df), $farther, $minN, ($qn | Measure-Object -Maximum).Maximum, $maxF)
            } else { Write-Host ("  (azimuth {0}: the dancers are equally far, {1:0.00} / {2:0.00} m)" -f $az, $dl, $df) }
        }

        Invoke-Unity hm_layer --layer hud --visible false | Out-Null
        Invoke-Unity hm_transport --action seek --time "$($base + 4.0)" | Out-Null
        Invoke-Unity hm_orbit --azimuth 120 --elevation 12 --radius 2.6 | Out-Null
        Shot "${prefix}_camera_opacity_default"
        Invoke-Unity hm_layer --layer hud --visible true | Out-Null
    }
}
