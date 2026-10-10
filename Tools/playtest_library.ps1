<#
Acceptance test of the film library screen and the playback bar (docs/FILM_LIBRARY.md, VIEWER_SPEC 7.1 / 14), through the Unity CLI
(the Editor open on this project, com.unity.pipeline; works with Windows PowerShell 5.1 and pwsh). It enters Play mode, so the Game
view should be the size you want to look at (the screenshots are of whatever size it is).

    powershell -File Tools/playtest_library.ps1                       # the first ready film of StreamingAssets/library/library.json
    powershell -File Tools/playtest_library.ps1 -Film demo -Recompile
    powershell -File Tools/playtest_library.ps1 -Shots Screenshots/ui   # screenshots under Assets/Screenshots/ui (git-ignored)

Checks, in order:
  library   exactly the library's films are offered (cards, the pickers' capture list); a film that is not ready is "Processing" with a
            disabled Play (a click on it does nothing but say why); developer commands still see every capture (hm_state captures)
  play      the film starts through the Play button's own click (pointer) and ends in the playing state with the bar attached
  bar       the scrubber spans the screen, one tick per chapter start, buttons present and inside the screen
  seek      >= 12 seeks forward and backward, while playing and while paused (start, every chapter start, mid-film, 2 s before the end):
            the film time lands on the target, the segment and the dance time are the direction's own, the captions are the narration
            timeline's own, the lead's role-hidden fade is the capture's (capture.json role_hidden), the soundtrack is at the film time
            (playing: drift < 0.15 s; paused: held) and a seek back to a time already seen shows the same picture state (determinism)
  scrub     a pointer drag on the scrubber follows the pointer, holds the sound, and restores playing / paused at the release; a click on a
            chapter tick jumps to the chapter's start
  keys      Space, Left / Right, [ and ] and Home / End act on the film
  back      the Back button (and Esc) stop the film and show the library again; nothing of the film's overlay remains
  console   no errors or warnings produced by the library, the bar or the director during the run
#>
param(
    [string]$Film = "",
    [switch]$Recompile,
    [string]$Shots = "",
    [switch]$KeepPlaying
)

$ErrorActionPreference = "Stop"
# a script error is a failed check, not the end of the run (Play mode must still be left cleanly)
trap { Write-Host "  ERROR $_" -ForegroundColor Red; $script:failures.Add("[$script:current] script error: $_"); continue }
Set-Location (Split-Path $PSScriptRoot -Parent)
$script:projectPath = (Get-Location).Path
$script:failures = [System.Collections.Generic.List[string]]::new()
$script:current = "setup"
$inv = [System.Globalization.CultureInfo]::InvariantCulture

function Invoke-Unity {
    # an explicit --project-path: auto-detection can take ~60 s per call when other editors/projects are registered
    return ((unity command @args --project-path $script:projectPath --result-only --no-banner --caller plugin --skill unity-cli 2>&1 | ForEach-Object { "$_" }) -join "`n")
}

function ConvertFrom-UnityJson([string]$raw) {
    # hm_* results are a JSON string holding JSON; ConvertFrom-Json (5.1) cannot read bare NaN/Infinity
    $inner = $raw | ConvertFrom-Json
    if ($inner -isnot [string]) { return $inner }
    $inner = $inner -replace '(?<=[:,\[])\s*-?(NaN|Infinity)\s*(?=[,}\]])', '"$1"'
    return ($inner | ConvertFrom-Json)
}

function Lib([string[]]$more = @()) {
    for ($try = 1; $try -le 3; $try++) {
        $raw = Invoke-Unity hm_library @more
        try { return (ConvertFrom-UnityJson $raw) }
        catch { Start-Sleep -Seconds 1 }
    }
    throw "hm_library $more unreadable: $raw"
}

function State { return (Lib @("--action", "state")) }

function HmState {
    $raw = Invoke-Unity hm_state
    return (ConvertFrom-UnityJson $raw)
}

function Assert($condition, [string]$message) {
    if ($condition) { Write-Host "  ok   $message" } else { Write-Host "  FAIL $message" -ForegroundColor Red; $script:failures.Add("[$script:current] $message") }
}

function Wait-Until([scriptblock]$condition, [int]$seconds = 60) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

function Num([double]$x) { return $x.ToString("R", $inv) }

function Shot([string]$name) {
    if (-not $Shots) { return }
    Invoke-Unity capture_game_view --save_path "$Shots/$name.png" --width 1280 --height 720 | Out-Null
    Write-Host "  shot Assets/$Shots/$name.png"
}

$script:lastFocus = [datetime]::MinValue
function Focus-Editor {
    # the editor hands keyboard and pointer events to the game only while its Game view has the focus (another window may have taken it),
    # and the real mouse shares the Input System's one Mouse device with the simulated pointer: input is re-sent until it has an effect
    if (((Get-Date) - $script:lastFocus).TotalSeconds -lt 8) { return }
    Lib @("--action", "focus") | Out-Null
    $script:lastFocus = Get-Date
}

function Click([double]$x, [double]$y) {
    Focus-Editor
    # down and up in two calls: the editor tool's single "click" can land both in one frame, which a frame-sampled UI never sees as a press
    Invoke-Unity simulate_pointer --x (Num $x) --y (Num $y) --action move | Out-Null
    Invoke-Unity simulate_pointer --x (Num $x) --y (Num $y) --action down | Out-Null
    Start-Sleep -Milliseconds 250
    Invoke-Unity simulate_pointer --x (Num $x) --y (Num $y) --action up | Out-Null
    Start-Sleep -Milliseconds 250
}

function Key([string]$key) {
    Focus-Editor
    Invoke-Unity simulate_key --key $key --action down | Out-Null
    Start-Sleep -Milliseconds 200
    Invoke-Unity simulate_key --key $key --action up | Out-Null
    Start-Sleep -Milliseconds 300
}

# a key / click that is sent again (up to 3 times) until $effect holds: the editor's input injection can lose an event
function Key-Effect([string]$key, [scriptblock]$effect) {
    for ($i = 0; $i -lt 3; $i++) {
        $script:lastFocus = [datetime]::MinValue
        Key $key
        if (& $effect) { return $true }
    }
    return $false
}

function Click-Effect([double]$x, [double]$y, [scriptblock]$effect) {
    for ($i = 0; $i -lt 3; $i++) {
        $script:lastFocus = [datetime]::MinValue
        Click $x $y
        Start-Sleep -Milliseconds 300
        if (& $effect) { return $true }
    }
    return $false
}

# ------------------------------------------------------------------------------------------------ the film's own data (read from disk)
$libPath = Join-Path $script:projectPath "Assets\StreamingAssets\library\library.json"
if (-not (Test-Path $libPath)) { throw "no $libPath (python -m dancecap.film_library build --demo)" }
$libJson = Get-Content $libPath -Raw -Encoding UTF8 | ConvertFrom-Json
$workspace = Split-Path $script:projectPath -Parent
function ResolveFile([string]$p) {
    if ([System.IO.Path]::IsPathRooted($p)) { return $p }
    foreach ($root in @((Split-Path $libPath -Parent), $script:projectPath, $workspace)) {
        $full = Join-Path $root $p
        if (Test-Path $full) { return $full }
    }
    return $null
}
$entry = $null
foreach ($f in $libJson.films) {
    $d = ResolveFile $f.direction
    if ($Film -and $f.id -ne $Film -and $f.capture -ne $Film) { continue }
    if ($null -ne $d -and (Test-Path $d)) { $entry = $f; $directionPath = $d; break }
}
if ($null -eq $entry) { throw "no ready film in the library (-Film '$Film')" }
$direction = Get-Content $directionPath -Raw -Encoding UTF8 | ConvertFrom-Json
$filmDur = [double]$direction.timeline.film_duration_s
$chapters = @($entry.chapters)
$capDir = Join-Path $script:projectPath "Assets\StreamingAssets\$($entry.capture)"
$capJson = Get-Content (Join-Path $capDir "capture.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$timeline = $null
foreach ($tf in Get-ChildItem (Join-Path (Split-Path $directionPath -Parent) "narration") -Filter "timeline*.json" -ErrorAction SilentlyContinue) {
    $t = Get-Content $tf.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($t.direction -eq $direction.name) { $timeline = $t; break }
}
Write-Host ("film {0} ({1}): {2:0.0} s, {3} chapters, capture {4}" -f $entry.id, $direction.name, $filmDur, $chapters.Count, $entry.capture)

function SegmentAt([double]$t) {
    $segs = @($direction.segments)
    $hit = $segs[0]
    foreach ($s in $segs) { if ([double]$s.film[0] -le $t) { $hit = $s } }
    return $hit
}

function CaptionAt([double]$t, [string]$aspect) {
    # the chunk shown at film time t, "line / line" (CaptionsOverlay.CurrentText); $null = none; "?" = too close to an edge to say
    if ($null -eq $timeline) { return "?" }
    foreach ($c in @($timeline.captions.$aspect)) {
        if ($t -ge [double]$c.show - 0.25 -and $t -lt [double]$c.show + 0.0) { return "?" }
        if ($t -ge [double]$c.hide - 0.0 -and $t -lt [double]$c.hide + 0.25) { return "?" }
        if ($t -ge [double]$c.show -and $t -lt [double]$c.hide) { return (@($c.lines) -join " / ") }
    }
    return $null
}

# the capture's lead role-hidden span in capture seconds (capture second = film second while the open segment plays at 1x from the capture's first frame)
$leadSpan = $null
if ($null -ne $capJson.role_hidden -and $null -ne $capJson.role_hidden.lead) { $leadSpan = $capJson.role_hidden.lead[0] }
function SmoothStep01([double]$x) { $x = [math]::Min(1.0, [math]::Max(0.0, $x)); return $x * $x * (3.0 - 2.0 * $x) }

# ------------------------------------------------------------------------------------------------ enter Play mode
if ($Recompile) {
    Invoke-Unity editor_stop | Out-Null
    Invoke-Unity recompile | Out-Null
    $done = Wait-Until { ((Invoke-Unity recompile_status) | ConvertFrom-Json).status -in "completed", "up_to_date" } 400
    Assert $done "scripts recompiled"
    Assert (-not ((Invoke-Unity console_status) | ConvertFrom-Json).groundTruth.compilationFailed) "no compile errors"
}
Write-Host "== enter play mode"
Invoke-Unity open_scene --path Assets/Scenes/head-movement.unity | Out-Null
Invoke-Unity clear_console | Out-Null
Invoke-Unity editor_play | Out-Null
Assert (Wait-Until { ((Invoke-Unity editor_status) | ConvertFrom-Json).playMode -eq "playing" } 90) "editor playing"
Assert (Wait-Until { try { (State).library.mode -eq "library" } catch { $false } } 60) "the library screen is up at the start"
Start-Sleep -Seconds 1

# ------------------------------------------------------------------------------------------------ library screen
$script:current = "library"
Write-Host "== library screen"
$s = State
$screenW = [int]($s.library.screen -split "x")[0]; $screenH = [int]($s.library.screen -split "x")[1]
Assert ($s.library.ownsScreen -and $s.libraryOwnsScreen) "the library owns the screen (the IMGUI HUD and the lesson keys are quiet)"
Assert (@($s.library.cards).Count -eq @($libJson.films).Count) "one card per film of library.json ($(@($s.library.cards).Count))"
$offered = @($s.library.pickerCaptures)
$libCaptures = @($libJson.films | ForEach-Object { $_.capture })
Assert (@($offered | Where-Object { $libCaptures -notcontains $_ }).Count -eq 0) "the pickers offer library captures only ($($offered -join ', '))"
$hm = HmState
Assert (@($hm.captures).Count -gt @($offered).Count -or @($hm.captures).Count -eq @($libCaptures).Count) "developer state still lists the capture folders ($(@($hm.captures).Count))"
Assert (-not (@($hm.captures) -contains "library")) "the library folder is not a capture"
$ready = @($s.library.films | Where-Object { $_.ready })
$pending = @($s.library.films | Where-Object { -not $_.ready })
Assert ($ready.Count -ge 1) "at least one film is ready ($(($ready | ForEach-Object { $_.id }) -join ', '))"
foreach ($c in $s.library.cards) {
    $r = $c.card
    Assert ($r[0] -ge 0 -and $r[1] -ge 0 -and ($r[0] + $r[2]) -le $screenW -and ($r[1] + $r[3]) -le $screenH) "card $($c.id) lies inside the $screenW x $screenH screen"
}
Shot "01_library"
foreach ($p in $pending) {
    $card = @($s.library.cards | Where-Object { $_.id -eq $p.id })[0]
    Assert ($null -ne $card -and -not $card.ready) "$($p.id): shown as Processing ($($p.why))"
    Click ($card.play[0] + $card.play[2] / 2) ($card.play[1] + $card.play[3] / 2)
    $s2 = State
    Assert ($s2.library.mode -eq "library" -and $null -eq $s2.film) "a click on the Processing film's Play button starts nothing"
    Shot "02_processing_clicked"
}

# ------------------------------------------------------------------------------------------------ play (through the button)
$script:current = "play"
Write-Host "== play: click on the Play button of $($entry.id)"
$card = @($s.library.cards | Where-Object { $_.id -eq $entry.id })[0]
$started = Click-Effect ($card.play[0] + $card.play[2] / 2) ($card.play[1] + $card.play[3] / 2) { (State).library.mode -in "loading", "playing" }
Assert $started "the click starts loading the film"
Shot "03_loading"
Assert (Wait-Until { $x = State; $x.library.mode -eq "playing" -and $null -ne $x.bar -and $null -ne $x.film -and $x.film.running } 180) "the film plays with the bar"
Start-Sleep -Seconds 2
$s = State
Assert ($s.film.capture -eq $entry.capture) "the film's capture is $($entry.capture)"
Assert ([math]::Abs([double]$s.film.filmDuration - $filmDur) -lt 0.01) "film length $($s.film.filmDuration) s = the direction's $filmDur s"
Assert ($s.film.audio.loaded -and $s.film.audio.playing) "the soundtrack plays"
Assert ([double]$s.film.filmTime -gt 0.5) "the film clock runs ($($s.film.filmTime) s)"
Assert (-not $s.libraryOwnsScreen) "the library screen is gone"

# ------------------------------------------------------------------------------------------------ the bar
$script:current = "bar"
Write-Host "== bar"
$script:lastFocus = [datetime]::MinValue; Focus-Editor
Invoke-Unity simulate_pointer --x (Num ($screenW * 0.5)) --y (Num ($screenH * 0.6)) --action move | Out-Null
Start-Sleep -Milliseconds 500
$b = (State).bar
Assert ($b.visible) "the bar is visible after pointer activity"
$tr = $b.track
Assert ($tr[0] -gt 0 -and ($tr[0] + $tr[2]) -lt $screenW -and $tr[2] -gt $screenW * 0.6) "the scrubber spans the screen ($([int]$tr[2]) of $screenW px)"
$expectTicks = @($chapters | Where-Object { [double]$_.startS -gt 0.05 }).Count
Assert (@($b.ticks).Count -eq $expectTicks) "one tick per chapter start ($expectTicks)"
$ok = $true
for ($i = 0; $i -lt @($b.ticks).Count; $i++) {
    $want = $tr[0] + ([double]$b.ticks[$i][0] / $filmDur) * $tr[2]
    if ([math]::Abs($want - [double]$b.ticks[$i][1]) -gt 1.5) { $ok = $false }
}
Assert $ok "every tick sits at chapter start / film length of the scrubber"
foreach ($name in "Library", "Play pause", "Previous chapter", "Next chapter", "Back 5 s", "Forward 5 s", "Speed") {
    $btn = $b.buttons.$name
    $in = $null -ne $btn -and $btn.rect[0] -ge 0 -and $btn.rect[1] -ge 0 -and ($btn.rect[0] + $btn.rect[2]) -le $screenW -and ($btn.rect[1] + $btn.rect[3]) -le $screenH
    if ($name -eq "Library") { $in = $null -ne $b.buttons.Back -and $b.buttons.Back.rect[0] -ge 0 }
    Assert $in "button $name is on screen"
}
Lib @("--action", "bar", "--flag", "true") | Out-Null   # the screenshot calls are slow: the bar stays up for the rest of the run
Start-Sleep -Milliseconds 700
Shot "04_bar_playing"
# a recording never contains the bar: the recorder locks the frame rate, and the bar takes itself out of the picture
Invoke-Unity eval --code "UnityEngine.Time.captureFramerate = 30; return 1;" | Out-Null
Start-Sleep -Milliseconds 900
$r = State
Assert ($r.recording -and -not $r.bar.visible) "while the frame rate is locked (a recording) the bar is not drawn"
Invoke-Unity eval --code "UnityEngine.Time.captureFramerate = 0; return 1;" | Out-Null
Start-Sleep -Milliseconds 600
Assert ((State).bar.visible) "and it is back when the lock ends"

# ------------------------------------------------------------------------------------------------ seeks
$script:current = "seek"
Write-Host "== seeks (playing and paused, forward and backward)"
$script:seekCount = 0
$memo = @{}   # film time (rounded) -> picture state seen while paused, for the determinism check

function Check-Seek([double]$target, [bool]$paused, [string]$label) {
    $script:seekCount++
    Lib @("--action", "seek", "--at", (Num $target)) | Out-Null
    Start-Sleep -Milliseconds 900
    $x = State
    $f = $x.film
    $elapsed = if ($paused) { 0.0 } else { 2.5 }
    $t = [double]$f.filmTime
    $inWindow = $t -ge $target - 0.05 -and $t -le [math]::Min($filmDur, $target + $elapsed + 0.3)
    Assert $inWindow ("{0}: film time {1:0.00} for the target {2:0.00}" -f $label, $t, $target)
    if ($paused) { Assert ([math]::Abs($t - $target) -lt 0.02) ("{0}: paused exactly on the target" -f $label) }
    # the picture state is the direction's own at the time the film shows
    $seg = SegmentAt $t
    $nearEdge = $false
    foreach ($sg in @($direction.segments)) { if ([math]::Abs($t - [double]$sg.film[0]) -lt 0.1) { $nearEdge = $true } }
    if (-not $nearEdge) { Assert ($f.segment -eq $seg.id) ("{0}: segment {1} = the direction's {2}" -f $label, $f.segment, $seg.id) }
    $cap = CaptionAt $t $f.aspect
    if ($cap -ne "?") {
        $shown = $f.overlay.caption
        $same = ($null -eq $cap -and [string]::IsNullOrEmpty($shown)) -or ($cap -eq $shown)
        Assert $same ("{0}: caption {1}" -f $label, $(if ($null -eq $cap) { "none" } else { "'" + $cap.Substring(0, [math]::Min(40, $cap.Length)) + "'" }))
    }
    # the soundtrack follows
    $au = $f.audio
    if ($paused) { Assert ($au.held -and -not $au.playing) ("{0}: the soundtrack is held" -f $label) }
    elseif ($t -lt $filmDur - 0.5) { Assert ($au.playing -and [math]::Abs([double]$au.driftS) -lt 0.15) ("{0}: the soundtrack plays at the film time (drift {1} s)" -f $label, $au.driftS) }
    # the lead's role-hidden fade (capture.json role_hidden), around the span only
    if ($null -ne $leadSpan -and $paused) {
        $rh = (HmState).roleHidden
        $c = $t - 0.0   # capture second = film second in the opening segment (dance -3.115 -> capture 0)
        if ($seg.id -eq "open_title" -and ([math]::Abs($c - [double]$leadSpan.to) -gt 0.15 -and ($c -lt [double]$leadSpan.from - 0.2 -or $c -gt [double]$leadSpan.to + [double]$leadSpan.fade_in + 0.2 -or ($c -gt [double]$leadSpan.from + 0.2 -and $c -lt [double]$leadSpan.to - 0.2)))) {
            $a = if ($c -lt [double]$leadSpan.to) { 0.0 } else { 1.0 }
            Assert ([math]::Abs([double]$rh.alphaNow.lead - $a) -lt 0.02) ("{0}: the lead's role alpha {1:0.00} (expected {2:0.00}) at capture second {3:0.0}" -f $label, [double]$rh.alphaNow.lead, $a, $c)
        }
        elseif ($seg.id -eq "open_title" -and $c -gt [double]$leadSpan.to + 0.05 -and $c -lt [double]$leadSpan.to + [double]$leadSpan.fade_in - 0.05) {
            $a = SmoothStep01 (($c - [double]$leadSpan.to) / [double]$leadSpan.fade_in)
            Assert ([math]::Abs([double]$rh.alphaNow.lead - $a) -lt 0.05) ("{0}: the lead fades in: alpha {1:0.00} (expected {2:0.00})" -f $label, [double]$rh.alphaNow.lead, $a)
        }
    }
    # determinism: the same film time shown paused twice is the same picture state
    if ($paused) {
        $key = [math]::Round($t, 2).ToString($inv)
        $sig = "{0}|{1}|{2}|{3}|{4}" -f $f.segment, $f.danceTime, $f.camera.pos[0], $f.camera.pos[1], $f.camera.pos[2]
        if ($memo.ContainsKey($key)) { Assert ($memo[$key] -eq $sig) ("{0}: the same film time shows the same state as before ({1})" -f $label, $key) }
        else { $memo[$key] = $sig }
    }
    return $x
}

# targets: start, chapter starts (forward), mid, 2 s before the end, then backward through the same
$starts = @($chapters | ForEach-Object { [double]$_.startS })
$mid = [math]::Round($filmDur / 2.0, 1)
$endT = [math]::Round($filmDur - 2.0, 2)
$forward = @(0.0) + @($starts | Where-Object { $_ -gt 0.05 } | Select-Object -First 4) + @($mid) + @($starts | Where-Object { $_ -gt $mid } | Select-Object -First 2) + @($endT)
$backward = @($forward | Sort-Object -Descending | Select-Object -Unique)
Lib @("--action", "resume") | Out-Null
Assert (-not (State).film.paused) "playing"
foreach ($tt in $forward) { Check-Seek $tt $false ("playing forward {0:0.0}" -f $tt) | Out-Null }
Shot "05_after_forward_seeks"
foreach ($tt in $backward) { Check-Seek $tt $false ("playing backward {0:0.0}" -f $tt) | Out-Null }
Lib @("--action", "pause") | Out-Null
Start-Sleep -Milliseconds 500
foreach ($tt in $forward) { Check-Seek $tt $true ("paused forward {0:0.0}" -f $tt) | Out-Null }
foreach ($tt in $backward) { Check-Seek $tt $true ("paused backward {0:0.0}" -f $tt) | Out-Null }
# the lead's fade, sampled across the span while paused (also the frames the opening shows)
if ($null -ne $leadSpan) {
    foreach ($tt in 1.0, ([double]$leadSpan.to + [double]$leadSpan.fade_in * 0.5), ([double]$leadSpan.to + [double]$leadSpan.fade_in + 1.0), 2.0) { Check-Seek $tt $true ("paused role fade {0:0.00}" -f $tt) | Out-Null }
}
Assert ($script:seekCount -ge 12) "$($script:seekCount) seeks done"
# the phone video at a phone's point of view (FilmCameraVideo): shown where the direction flies into a phone, gone elsewhere, back after a seek
$povSeg = @($direction.segments | Where-Object { $_.camera.mode -eq "fly_to_camera" })[0]
if ($null -ne $povSeg) {
    # inside the hold: after the flight into the phone and the fade-in, before the fade-out
    $tPov = [math]::Round([double]$povSeg.film[0] + [double]$povSeg.camera.fly_s + [double]$povSeg.camera.fade_in_s + 1.5, 2)
    $x = Check-Seek $tPov $true ("paused phone video {0:0.0}" -f $tPov)
    Assert ($x.film.pov -and $x.film.videoShowing) ("the phone's video is on screen at film {0:0.0} (phone {1}, opacity {2})" -f $tPov, $x.film.pov, $x.film.povVideo)
    $x = Check-Seek 40.0 $true "paused away from the phone video"
    Assert (-not $x.film.videoShowing) "no phone video at film 40"
    $x = Check-Seek $tPov $true ("paused phone video again {0:0.0}" -f $tPov)
    Assert ($x.film.videoShowing) "the phone's video is back after seeking back"
    Shot "05b_phone_video"
}
Check-Seek 30.0 $true "paused 30" | Out-Null
Shot "06_paused_at_30"
Lib @("--action", "resume") | Out-Null

# ------------------------------------------------------------------------------------------------ scrubbing and the chapter ticks (pointer)
$script:current = "scrub"
Write-Host "== scrubber drag and chapter ticks (pointer)"
$script:lastFocus = [datetime]::MinValue; Focus-Editor
Lib @("--action", "bar", "--flag", "true") | Out-Null
Lib @("--action", "seek", "--at", "20") | Out-Null
Start-Sleep -Milliseconds 400
$b = (State).bar
$tr = $b.track
$y = $tr[1] + $tr[3] / 2
$x0 = $tr[0] + $tr[2] * 0.25
$x1 = $tr[0] + $tr[2] * 0.75
for ($try = 0; $try -lt 3; $try++) {
    $script:lastFocus = [datetime]::MinValue; Focus-Editor
    Invoke-Unity simulate_pointer --x (Num $x0) --y (Num $y) --action move | Out-Null
    Invoke-Unity simulate_pointer --x (Num $x0) --y (Num $y) --action down | Out-Null
    Start-Sleep -Milliseconds 400
    $d = State
    if ($d.bar.dragging) { break }
    Invoke-Unity simulate_pointer --x (Num $x0) --y (Num $y) --action up | Out-Null
}
Assert ($d.bar.dragging -and $d.film.scrubbing -and $d.film.paused) "pointer down on the scrubber: dragging, the film held"
Assert ([math]::Abs([double]$d.film.filmTime - $filmDur * 0.25) -lt $filmDur * 0.01) ("the film jumped under the pointer: {0:0.0} s (expected {1:0.0})" -f [double]$d.film.filmTime, ($filmDur * 0.25))
# (the editor tool's "move" releases the button: a drag is a series of "down" events at new positions)
Invoke-Unity simulate_pointer --x (Num (($x0 + $x1) / 2)) --y (Num $y) --action down | Out-Null
Start-Sleep -Milliseconds 300
Invoke-Unity simulate_pointer --x (Num $x1) --y (Num $y) --action down | Out-Null
Start-Sleep -Milliseconds 400
$d = State
Assert ([math]::Abs([double]$d.film.filmTime - $filmDur * 0.75) -lt $filmDur * 0.01) ("dragged to {0:0.0} s (expected {1:0.0})" -f [double]$d.film.filmTime, ($filmDur * 0.75))
Assert ($d.film.audio.held -and -not $d.film.audio.playing) "the sound is held while dragging"
Shot "07_scrubbing"
Invoke-Unity simulate_pointer --x (Num $x1) --y (Num $y) --action up | Out-Null
Start-Sleep -Milliseconds 600
$d = State
Assert (-not $d.bar.dragging -and -not $d.film.scrubbing -and -not $d.film.paused) "released: playing again (it was playing)"
Assert ($d.film.audio.playing -and [math]::Abs([double]$d.film.audio.driftS) -lt 0.15) "the sound resumed at the film time (drift $($d.film.audio.driftS) s)"
# a paused film stays paused through a scrub
Lib @("--action", "pause") | Out-Null
$t0 = [double](State).film.filmTime
Invoke-Unity simulate_pointer --x (Num $x0) --y (Num $y) --action move | Out-Null
Invoke-Unity simulate_pointer --x (Num $x0) --y (Num $y) --action down | Out-Null
Start-Sleep -Milliseconds 300
Invoke-Unity simulate_pointer --x (Num $x0) --y (Num $y) --action up | Out-Null
Start-Sleep -Milliseconds 400
$d = State
Assert ($d.film.paused -and -not $d.film.scrubbing) "a scrub of a paused film leaves it paused"
# a click on a chapter tick
if (@($b.ticks).Count -ge 3) {
    $k = [int][math]::Floor(@($b.ticks).Count / 2)
    $tickT = [double]$b.ticks[$k][0]; $tickX = [double]$b.ticks[$k][1]
    Click $tickX $y
    Start-Sleep -Milliseconds 400
    $d = State
    Assert ([math]::Abs([double]$d.film.filmTime - $tickT) -lt 0.06) ("a click on tick {0} jumps to the chapter start {1:0.00} s (film at {2:0.00})" -f $k, $tickT, [double]$d.film.filmTime)
    Invoke-Unity simulate_pointer --x (Num ($tickX + 3)) --y (Num $y) --action move | Out-Null
    Start-Sleep -Milliseconds 400
    $d = State
    $title = @($chapters | Where-Object { [math]::Abs([double]$_.startS - $tickT) -lt 0.01 })[0].title
    Assert ($d.bar.tooltip -like "*$title*") "hovering next to the tick names the chapter ('$($d.bar.tooltip)')"
    Shot "08_tick_hover"
}
Lib @("--action", "resume") | Out-Null

# ------------------------------------------------------------------------------------------------ keys
$script:current = "keys"
Write-Host "== keys"
$script:lastFocus = [datetime]::MinValue; Focus-Editor
Lib @("--action", "seek", "--at", "60") | Out-Null
Start-Sleep -Milliseconds 300
Assert (Key-Effect "Space" { [bool](State).film.paused }) "Space pauses"
Assert (Key-Effect "Space" { -not [bool](State).film.paused }) "Space plays again"
Lib @("--action", "pause") | Out-Null
# Left / Right step 5 s and REPEAT while held (the editor tool holds a key for as long as its two calls are apart: a second or more),
# so the keys are checked for whole 5 s steps in the right direction; the exact single step is checked with the +5 s / -5 s buttons
Lib @("--action", "seek", "--at", "150") | Out-Null
Start-Sleep -Milliseconds 300
Key-Effect "RightArrow" { [double](State).film.filmTime -gt 150.9 } | Out-Null
$t1 = [double](State).film.filmTime
$steps = ($t1 - 150.0) / 5.0
Assert ($steps -ge 0.99 -and [math]::Abs($steps - [math]::Round($steps)) -lt 0.05) ("Right arrow: whole 5 s steps forward ({0:0} step(s), film at {1:0.0})" -f [math]::Round($steps), $t1)
Lib @("--action", "seek", "--at", "150") | Out-Null
Start-Sleep -Milliseconds 300
Key-Effect "LeftArrow" { [double](State).film.filmTime -lt 149.1 } | Out-Null
$t1 = [double](State).film.filmTime
$steps = (150.0 - $t1) / 5.0
Assert (($steps -ge 0.99 -and [math]::Abs($steps - [math]::Round($steps)) -lt 0.05) -or $t1 -lt 0.1) ("Left arrow: whole 5 s steps back ({0:0} step(s), film at {1:0.0})" -f [math]::Round($steps), $t1)
Lib @("--action", "seek", "--at", "100") | Out-Null
Start-Sleep -Milliseconds 300
$bb = (State).bar.buttons
$fw = $bb.'Forward 5 s'.rect; $bw = $bb.'Back 5 s'.rect
Assert (Click-Effect ($fw[0] + $fw[2] / 2) ($fw[1] + $fw[3] / 2) { [math]::Abs([double](State).film.filmTime - 105.0) -lt 0.2 }) "the +5 s button: exactly 105.0"
Assert (Click-Effect ($bw[0] + $bw[2] / 2) ($bw[1] + $bw[3] / 2) { [math]::Abs([double](State).film.filmTime - 100.0) -lt 0.2 }) "the -5 s button: exactly 100.0"
Assert (Click-Effect ($bw[0] + $bw[2] / 2) ($bw[1] + $bw[3] / 2) { [math]::Abs([double](State).film.filmTime - 95.0) -lt 0.2 }) "the -5 s button again: exactly 95.0"
Lib @("--action", "seek", "--at", "60") | Out-Null
$cIdx = (State).bar.chapter
Assert (Key-Effect "RightBracket" { (State).bar.chapter -eq $cIdx + 1 }) "] goes to the next chapter (chapter $($cIdx + 2))"
Assert (Key-Effect "LeftBracket" { (State).bar.chapter -eq $cIdx }) "[ goes back to the chapter before"
Assert (Key-Effect "Home" { [double](State).film.filmTime -lt 0.1 }) "Home: the start"
Assert (Key-Effect "End" { [double](State).film.filmTime -gt $filmDur - 0.1 }) "End: the end of the film"
Lib @("--action", "rate", "--value", "1.5") | Out-Null
Assert ([math]::Abs([double](State).film.rate - 1.5) -lt 0.01) "speed 1.5x"
Lib @("--action", "rate", "--value", "1") | Out-Null

# ------------------------------------------------------------------------------------------------ back
$script:current = "back"
Write-Host "== back to the library"
$script:lastFocus = [datetime]::MinValue; Focus-Editor
Lib @("--action", "seek", "--at", "40") | Out-Null
Lib @("--action", "resume") | Out-Null
Start-Sleep -Milliseconds 500
$b = (State).bar
$btn = $b.buttons.Back
Assert (Click-Effect ($btn.rect[0] + $btn.rect[2] / 2) ($btn.rect[1] + $btn.rect[3] / 2) { (State).library.mode -eq "library" }) "the Back button returns to the library"
$d = State
Assert ($null -eq $d.film -and $null -eq $d.bar) "the film and its bar are gone"
$hm = HmState
Assert (-not $hm.externalTime -or $hm.externalTime -eq $null) "HeadMovement has its own clock back"
Shot "09_back_in_library"
# Esc from a film
Lib @("--action", "play", "--id", $entry.id) | Out-Null
Assert (Wait-Until { $x = State; $x.library.mode -eq "playing" -and $null -ne $x.bar } 180) "play again through the command"
Assert (Key-Effect "Escape" { (State).library.mode -eq "library" }) "Esc returns to the library"

# ------------------------------------------------------------------------------------------------ developer commands are not taken over
$script:current = "developer"
Write-Host "== developer commands"
Invoke-Unity hm_load --name $entry.capture | Out-Null
Assert (Wait-Until { (State).library.mode -eq "hidden" } 10) "hm_load puts the library away: a developer command wants the viewer"
Assert (-not (State).libraryOwnsScreen) "the HUD and the keys are the developer's again"
Assert (Wait-Until { (HmState).audioLoaded } 90) "the capture loads"
Invoke-Unity hm_film_show --action start --aspect horizontal --capture $entry.capture | Out-Null
Assert (Wait-Until { $x = State; $null -ne $x.film -and $x.film.ready } 120) "hm_film_show start: the film runs"
$x = State
Assert ($null -eq $x.bar) "hm_film_show without --audio plays the film without the bar (review stills, recordings)"
Invoke-Unity hm_film_show --action stop | Out-Null
Invoke-Unity hm_film_show --action start --aspect horizontal --capture $entry.capture --audio true | Out-Null
Assert (Wait-Until { $x = State; $null -ne $x.bar -and $null -ne $x.film } 120) "hm_film_show --audio true: the film with its soundtrack and the bar"
Invoke-Unity hm_film_show --action stop | Out-Null
Start-Sleep -Milliseconds 800
Lib @("--action", "show") | Out-Null
$x = State
Assert ($x.library.mode -eq "library" -and $null -eq $x.film) "hm_library show brings the library back"

# ------------------------------------------------------------------------------------------------ console
$script:current = "console"
# the errors and warnings of the run, minus the editor tool server's own ("Failed to handle /api/exec request": an aborted CLI call)
$con = (Invoke-Unity console --level warn --tail 200) | ConvertFrom-Json
$mine = @($con.entries | Where-Object { $_.message -notmatch 'BasePipelineServer|/api/exec|com\.unity\.pipeline' })
$errs = @($mine | Where-Object { $_.level -eq "error" })
$warns = @($mine | Where-Object { $_.level -eq "warn" })
Assert ($errs.Count -eq 0) "no console errors during the run ($($errs.Count))"
foreach ($e in $errs) { Write-Host "       $($e.message)" -ForegroundColor Red }
Assert (@($warns | Where-Object { $_.message -match 'Film|Library|Playback|Soundtrack' }).Count -eq 0) "no warnings from the library, the bar, the director or the soundtrack ($($warns.Count) warning(s) in all)"
foreach ($w in $warns) { Write-Host "       $($w.message)" -ForegroundColor DarkYellow }
try { Lib @("--action", "bar", "--flag", "false") | Out-Null } catch { }
if (-not $KeepPlaying) { Invoke-Unity editor_stop | Out-Null }

Write-Host ""
if ($script:failures.Count -eq 0) { Write-Host "library playtest: all checks passed" -ForegroundColor Green; exit 0 }
Write-Host "library playtest: $($script:failures.Count) check(s) failed" -ForegroundColor Red
$script:failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
exit 1
