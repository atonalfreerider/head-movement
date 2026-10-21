# Quest and PC VR: how the VR mode runs, how to test it, what a standalone build needs

The viewer has a VR mode (VIEWER_SPEC section 9.5): a lesson menu in the world, the lesson played life-size with the user's head as the only
camera, and every control (chapters, slow motion, seeking, viewpoints, teleport, snap turn) on the hands. This page is the practical side:
the setup, the steps to try it on a headset, the commands, the limits, and the plan for a Quest standalone (Android) build.

Status of this page's claims is marked: **verified** = measured in the editor on simulated hands (`Tools/playtest_vr.ps1`, the EditMode
tests) or by a build that ran; **untested on a headset** = written against the OpenXR / XR Hands APIs and reviewed, never run on a device.

## 0. The Quest 3 app (standalone APK, passthrough mixed reality by default)

What the app does on a Quest 3 (a Quest 2 too, where passthrough is supported). Everything below was written against the Meta OpenXR
packages and the headset's logs; items marked **verified in the editor** ran on simulated hands (`Tools/playtest_vr.ps1`), items marked
**device log** were read from `adb logcat` on a Quest 3; the rest needs a person looking.

**Passthrough is the default view.** The room is the background; only the dancers, hair, props and the graphics (T axis, floor craft, physics,
arrows, beat count and rhythm score where the lesson turns them on, captions when switched on) are drawn over it, life size.
- Setup: OpenXR features *Meta Quest: Session*, *Meta Quest: Camera (Passthrough)* (with the passthrough pre-splash) and *Display Utilities*
  (`hm_vr_setup`); the head camera clears to a solid colour with alpha 0 (`VrRig.SetPassthroughBackground`); AR Foundation's `ARSession` +
  `ARCameraManager` are added to the rig at run time (`VrDisplay`), which makes the Meta camera subsystem create the passthrough layer. The package
  `com.unity.xr.arfoundation` 6.6.2 comes with `com.unity.xr.meta-openxr`: nothing extra was downloaded. Manifest: `com.oculus.feature.PASSTHROUGH`,
  hand tracking permissions; minimum Android API 32 (the passthrough camera feature requires Android 12L; Horizon OS is Android 14).
- **Device log**: `VrDisplay: passthrough running (MetaOpenXRCameraSubsystem, layer 'Default Scene Layer' order 0 on blend Premultiply, layer
  'Passthrough' order -1 on blend Alpha)`. The scene layer is composited premultiplied over the passthrough layer, so the shaders write
  premultiplied-correct alpha (`Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha`; additive glow writes alpha 0).
- In passthrough: no room mesh, no sky or black background, no floor fill (the dark translucent plane is off; the grid is shortened to the dance
  area and fainter; a soft translucent contact disc sits under the couple), a flat ambient fill so the shaded side does not go black, hands drawn as
  fingertip cursors only (the real hands are in view), no teleport and no snap turn (the user walks around the dancers), no directed viewpoints.
- **Immersive** (opaque, room and floor) stays one button away: the menu footer's *View: Passthrough / Immersive*; the choice is remembered
  (`PlayerPrefs hm_vr_display`; `-hmvr-display immersive|passthrough`, `HM_VR_DISPLAY`, `quest_settings.json` "display"). If passthrough does not start
  within 6 s the app falls back to immersive and says so in the menu's note line and in the log (`VrDisplay: passthrough failed: ...`).

**The dance has a fixed place.** The capture's world origin is mapped once, when a lesson starts, to the origin (0, 0, 0) of the headset's Floor
tracking space (the centre of the Meta world floor), the capture's floor on the real floor, scale exactly 1.0 (`VrPlacement`; the log line
`VrPlacement: lesson '...' origin LOCKED: static offset (...) ... scale 1.000 OK (exactly 1:1)` states it). It never moves, drifts, re-centres or follows
anything afterwards; the dancers keep their captured motion and travel across the floor around it (nothing cancels their root motion). The one
static offset per lesson: the dancers' mean floor position over the whole take for a capture without an origin of its own (the demo and the class
recap), zero for a capture whose own `origin` already is that mean (Carlos + Aline, John + Lula). A constant turn about the vertical axis, applied
with it, makes the dancers' front (the start vantage's side) face where the user stands at the start. Only a teleport moves the origin relative to
the user (immersive mode); in passthrough teleport is off (it would make no sense in a real room), so the origin never moves. There are no place /
rotate / scale gestures. If the headset recentres its tracking space the dance recentres with it (a Meta anchor would pin it; not done).

**Menus are anchored.** Every panel (library menu, the in-lesson hand panel, captions, the beat / rhythm HUD, the loading splash) is placed ONCE when
it opens - 0.8 to 1.0 m ahead of the head along the horizontal gaze, at chest-to-eye height, facing the user with a slight tilt - and then stays
exactly there: no follow, no easing, no drift (`VrAnchor`). It never opens inside the dancers (it is moved out, to the user's side, unless the
user stands among them). Recall = close and open again in front of the user: raise the LEFT palm (the hand panel and the open library menu), or the
wrist button (an open menu that is out of view); a teleport or a viewpoint also brings the open panels along, once. The hand panel's *Close* button
closes it. Captions are OFF by default (the panel's CC button turns them on, and the choice is remembered).

**Loading does not freeze.** A lesson builds over many frames behind the splash (`HeadMovement.AsyncLoad`): the JSON of the poses, physics, rhythm,
counterbalance, floor craft and graph, the avatars' binaries, the jerk series, the phones' tracks are read on worker threads (`LoadPrefetch`), the
dance layers are built one layer per frame, and the avatars' albedo is a baked GPU texture (below). Lines starting `HM-LOAD` in logcat give the time of
every step; `HM-PERF lesson-load` gives the frames, the worst frame and the number over 40 / 100 ms during the load; `HM-STALL` names any frame over
100 ms and the step it followed. The first Quest 3 build froze 8.6 s (recap) and 4.8 s (demo) in one frame; see the numbers in NOTES_X3.
**Splash** (`VrSplash`): the wordmark, two stick dancers in a closed hold swaying to a slow beat, a T-axis ring, floor ripples, the lesson's title and
subtitle from the library data, a progress bar with the stage, a spinner that turns every frame; anchored like the menus (a dark backdrop behind it in
immersive mode), shown at app start for at least 2.4 s and during every load. The adaptive app icon is generated (`VrBranding`, `Assets/Icons`); the
Unity player splash is switched off where the licence allows it.

**Skeletons at full opacity (solid skeleton).** The owner twice reported "skeletons should be full opacity" after the first Quest builds. Root cause (found
by rendering the Quest tier in the editor over a bright background, X4 2026-10-11): the skeleton was never translucent as a material (opaque, alpha 1) but
its colour between beats is `SkeletonStyle.IdleLevel` 0.22 of the base, i.e. dark maroon / slate grey (~12 % linear light); only the beat pulse and the HDR
bloom of the desktop viewer light it up, and the Quest tier has neither (no HDR, no post). On a bright passthrough room it read as a faint, see-through
line; the translucent body over it (stencil skip, 14:13 build) was a second, smaller effect. In VR (`Dancer.SetSolidSkeleton`, set by `VrSession`) every
skeleton line and the follower's spine beads use `Resources/HM_SkeletonSolid` (beads: `HM_Bead` with `_Solid`): unlit, alpha 1, no blending, brightness mapped
into [0.85, 1] linear light with the hue and the beat kept (a rise to full plus a short white flash; the colour model is `HmVr.SolidSkeleton.Map`, unit
tested), a thin dark edge so a white or pale line reads against a bright room, and fades by `RoleAlpha` kept. Two materials per line: the depth-correct one
(queue Geometry, ZWrite on) and an over-the-bodies one (queue 3100: after the bodies 2990-3022 and the hair, before the VR panels 3900; ZTest Always, ZWrite
off), so no body opacity, hair or stencil plane is needed (the stencil skip is off in VR). The skeleton shows through the partner's body too (x-ray; the
bodies are translucent anyway). A lesson's `skeleton_lines` / the tours' skeleton fades never thin the lines below full width in VR (hidden at ~0 still hides).
Desktop viewer unchanged. Logcat: `HM-VR skeleton (lesson start | lesson +3 s): SOLID OK: solid True, 7 lines, lowest colour alpha 1.00; depth-correct: ...
queue 2000 blend One/Zero zwrite 1 ...; over the bodies: ... queue 3100 ... ztest Always; beads ... | eye buffer WxH colour .. depth/stencil .. (stencil plane
..) msaa .. | urp hdr .. | foveation ..`. The VR selftest (`Tools/playtest_vr.ps1`) checks the same state with the lesson at avatar opacity 0.25.

**Hair on the Quest.** The follow's groomed hair (160 guides x 16 segments, cards, Burst simulation baked in the background after load) stays ON in the
Quest tier. `VrQuality`: hair level *Full* (all cards close up, LOD by head size on screen, tip glow), *Light* (cheaper levels from a shorter distance,
no tip glow), *Off*; the menu footer's *Hair* button cycles them; when a lesson keeps missing the frame budget the level steps down by itself
(`adaptive`, logged as `HM-PERF adaptive` with the context of the decision; windows during the hair bake and the 6 s after a seek / chapter jump are
ignored, they are transients), never up. The tier is data: `hm_data/quest_settings.json` (keys in `VrSettings.cs`: `display`, `hair`, `hair_full`,
`hair_light`, `adaptive`, `hair_bake`, `hair_bake_chunk`, `hair_substeps`, `hair_iterations`, `msaa`, `eye_scale`, `refresh_hz`, `perf_log`); command line
`-hmhair full|light|off`. MSAA 4x on a Quest 3 (hair alpha-to-coverage needs it; 2x on a Quest 2), no HDR, no shadows, no extra lights, eye buffer 1.0
(0.9 on a Quest 2), the renderer's intermediate texture set to Auto (Meta's passthrough advice), 72 Hz asked from the runtime. The frame log
(`HM-PERF`, every 5 s, tag Unity): fps, mean / p95 / worst frame ms, share of frames over budget, GPU and CPU frame time from `FrameTimingManager`, hair
level and LOD, hair triangles, bake progress, memory.

**Splat dancers (2026-10-21).** The class recap shows the photoreal LHM++ splat dancers by default on the headset (the owner's choice);
the other lessons keep their mesh avatars. Off for a session: `-hmsplats off`, for good: `"splats": false` in `quest_settings.json`; the
Quest-only thinning (`splat_max`, `splat_max_sigma`), the render path and the splat depth are data too. Details, logcat lines and the
measurements: `docs/SPLAT_DANCERS.md` "Quest". The first install (2026-10-21 00:15, the full 2 x 160,000 Gaussians) ran at **5 fps**
with the dancers in view (fill bound: app GPU ~190 ms); the Quest now loads a **distilled set** per dancer (`splat_set`,
`splats/quest_25k/`: same look, 6-8 % of the fragments) and can draw the splats in a **lower-resolution layer** (`splat_scale`), see there.
The Quest tier defaults are pushed as `hm_data/quest_settings.json` from `Tools/quest_settings.json` (`deploy_data.ps1 -Push`, which
keeps a headset's own file unless `-ReplaceSettings`); it is never laid out in `Builds/data` (a Windows player reads that folder).
Frame-rate desk tests: `python Tools/quest_splat_bench.py run|plan|table` (VrApi FPS, stale frames, CPU/GPU level, app GPU ms).
**Charging**: on the PC's USB port the headset reports `Weak Charger: true` (900 mA): asleep it gains about 6 % an hour, a splat test
run costs about 1 %; use the Quest's own charger for long sessions.

**Test switches without hands** (adb): `adb shell am start -n com.headmovement.viewer/com.unity3d.player.UnityPlayerActivity -e unity "-hmplay recap -hmseek 120 -hmhair light"`
starts a lesson by itself (`-hmplay <film id>`), jumps to a time a few seconds after it started (`-hmseek <s>`), sets the hair level.
`-hmview start|front|side|overhead|phone:N` then takes that vantage point (immersive display: add `-hmvr-display immersive`),
`-hmshots N` writes N stereo stills of the eye images (both eyes side by side, the scene layer without the room) to
`/sdcard/Android/data/com.headmovement.viewer/files/hmshots/` (one every 6 s from 9 s after the start; `ScreenCapture` BothEyes,
not yet tried on the headset), and `-hmsplats on|off`,
`-hmsplatmax N`, `-hmsplatsigma M`, `-hmsplatpath composite|direct`, `-hmsplatdepth on|off`, `-hmsplatset NAME|full`,
`-hmsplatscale S`, `-hmsplatcutoff A`, `-hmsplatsort N` set the splat tier for that run.
A desk test needs a tracked headset: one that lay still in a dark room shows the system's "Finding position in room" dialog and holds
every app launch until it is picked up (logcat `VioInitialization: Skipping: Device is static`); `prox_close` alone does not help.

**Rebuild and push (about 15 minutes with warm caches; the first build on a cold cache took 76 minutes)**
1. Editor idle, lock held; move the contents of `Assets/StreamingAssets` out (an APK copies the whole folder): `Builds/_sa_hold`.
2. `unity command hm_vr_build --target android --allow_switch true --development false --output Builds/quest --timeout 5400` (`hm_vr_setup` first when
   the XR settings changed). Then switch the editor back to Windows (`switch_build_target StandaloneWindows64`), move the data back, `git checkout --` the
   files the build rewrites (`Assets/Bloom.asset`, `Assets/DefaultVolumeProfile.asset`, `Assets/UniversalRenderPipelineGlobalSettings.asset`,
   `ProjectSettings/UnityConnectSettings.asset`).
3. `adb -s <serial> install -r -g Builds/quest/headmovement.apk` (the serial of the Quest 3 when several headsets are attached).
4. `powershell -File Tools/deploy_data.ps1 -Out Builds/data -Bake -Push -Serial <serial>`: lays out the four lessons (about 520 MB without the phones' frame
   strips and clips), bakes the avatars' textures (`hm_vr_bake_textures`, editor open), creates the folders from the shell and pushes, then makes the
   folders world-readable. **Scoped storage (Android 11+) matters here**: `adb push` cannot create sub-folders under `Android/data/<package>`, so the
   script creates them with the shell; the folders the shell makes are owned by it with mode 770, which the app (another uid) cannot list - the app then
   logs `UnauthorizedAccessException ... hm_data is denied` and shows no cards. `chmod 755` on the folders (the script does it) fixes it.
5. Launch from the headset (Library > Unknown Sources > *Head Movement*) or `adb shell am start -n com.headmovement.viewer/com.unity3d.player.UnityPlayerActivity`.
   Logs: `adb -s <serial> logcat -G 16M` first (the default 256 KB buffer rolls over in a minute), then `adb logcat -s Unity` and
   `grep -E "VrDisplay|VrPlacement|VrMenu|HM-PERF|HM-LOAD|HM-STALL"`.

**Troubleshooting.** *No cards in the menu*: the data folder is not readable (step 4); the log says so. *Passthrough black*: the Meta camera feature
failed - look for `VrDisplay: passthrough failed`; the app is then in immersive. *Boundary / Guardian*: passthrough needs no boundary; the Floor
tracking origin needs the floor level set in the headset (Settings > Boundary), otherwise the app falls back to eye-level and puts the rig 1.6 m
above the floor (`VrRig.floorTracked`). *No hands*: Settings > Movement tracking > Hand tracking on, put the controllers down. *Headset asleep during a
desk test*: `adb shell am broadcast -a com.oculus.vrpowermanager.prox_close` fakes the proximity sensor (and `...automation_disable` undoes it);
never while someone wears it. *The app closed after a few minutes*: leaving to the system UI backgrounds and may kill it.

## 1. What exists

| Piece | Where | State |
|---|---|---|
| Hand-input logic (pinch, palm-up, pointing ray, poke, snap turn, teleport arc, panel and slider maths, viewpoints) | `Assets/VR/Core` (assembly `HmVr.Core`) | verified: 25 EditMode tests |
| Simulated hands (the same 26 joints a headset delivers) | `HandSynth`, `SimInputSource` | verified |
| XR hand tracking source (XR Hands, OpenXR) | `XrInputSource` | untested on a headset |
| Head pose from the headset, floor tracking origin, rig, comfort blink | `VrRig` | untested on a headset |
| World-space menu, hand panel, wrist button, captions, loading panel | `VrMenu`, `VrHandPanel`, `VrWristButton`, `VrCaptions` | verified (89-check scenario on 3 films) |
| Teleport, snap turn, viewpoints (a phone's real position) | `VrLocomotion`, `VrViewpoints` | verified in simulation |
| VR mode (enter / leave, camera swap, flat overlays kept out) | `VrMode`, `VrSession` | verified in simulation; the XR start path fails cleanly without a runtime (verified), untested with one |
| Quest quality tier | `VrQuality`, `VrSettings` | device log: hair on (levels), MSAA 4x on a Quest 3, 72 fps in the lessons (see section 0 and NOTES_X3) |
| Data root for Android | `HmData` | trivial; untested on a device |
| Data layout and adb push | `Tools/deploy_data.ps1` | verified (layout); push untested |
| Windows (PC VR) build | `hm_vr_build` | see section 6 |
| Android build | `hm_vr_build --target android` | built and installed on a Quest 2 and a Quest 3 (section 0) |

## 2. Try it now on a Quest 2 through a PC (Meta Quest Link / Air Link)

The headset is driven by the PC through the **OpenXR runtime** that the Meta software registers. The project is already configured for it
(section 4). What the PC must have:

1. **The Meta desktop software for the Quest** (the "Meta Quest Link" / "Meta Horizon Link" app). Without it no OpenXR runtime exists and
   the viewer stays in desktop mode. (On the machine this was developed on, at the time of writing, no Meta software and no OpenXR runtime
   were installed: `HKLM\SOFTWARE\Khronos\OpenXR\1` had no `ActiveRuntime`, and the Quest was visible to `adb` only.)
2. In that app: **Settings > General > "Set Meta Quest Link as active OpenXR runtime"**. The registry value `ActiveRuntime` under
   `HKLM\SOFTWARE\Khronos\OpenXR\1` must then name `oculus_openxr_64.json`. (SteamVR or Virtual Desktop work too, if they are the active runtime.)
3. On the headset: connect with the USB cable (USB 3) and choose **Quest Link**, or use **Air Link** (same Wi-Fi, 5 GHz). Turn
   **Hand tracking** on (Settings > Movement tracking); hands-only needs no controllers.
4. In the Unity editor: open `Assets/Scenes/head-movement.unity` and press Play. With the runtime active the viewer **enters VR by itself**
   (`VrMode`, preference `auto`). If it does not, press **F9** or the **Enter VR** button in the corner of the flat screen. The editor's
   *Edit > Project Settings > XR Plug-in Management > OpenXR > Play Mode OpenXR Runtime* must be System Default (or Meta Quest Link).
5. Or a Windows build (section 6): run `HeadMovement.exe` (it enters VR by itself) or `HeadMovement.exe -hmvr on`.

What to expect: you are in an empty dark space with three lesson cards on an arc about 0.8 m in front of you (a poster, the title, the
length, a big yellow **Play**). Poke a button with your index finger, or point your arm at it (a ray leaves your index knuckle) and pinch.
The lesson starts; you stand about 3 m from the dancers at the floor's edge, facing them.

**Hand gestures** (no controllers, no keyboard):

| You want | Do |
|---|---|
| Press a button | poke it with the index finger, or point at it and pinch (thumb and index together); opening the pinch on the same button clicks |
| Hand panel (play, slow motion, chapters, scrubber, viewpoints, library) | raise your **left** palm, facing up (or toward your face), in front of you; the panel floats above your hand. **Pin** keeps it in the air |
| Seek | the scrubber on the hand panel: pinch and drag it (it sticks to chapter ticks); or +5 s / -5 s, previous / next chapter, **Chapters** list |
| Slow motion | 1x, 0.5x, 0.25x, 0.1x on the panel (captions and 3D layers follow; music slows with the picture, a lesson with speech is silent in slow motion, the panel says "dialogue paused", and it is back at 1x) |
| Teleport | with the **right** hand, pinch where no panel is, hold (an arc and a landing disc appear), open the pinch to go |
| Turn 45 degrees | pinch with the **left** hand and flick it sideways; or Views > Turn 45 |
| Another viewpoint | Chapters/Views tabs > Views: floor edge, front row, from the side, overhead, and each filming phone's real place in the room |
| The menu again | the small round **menu button on the back of your left wrist** (it pauses the lesson), or **Library** on the hand panel (stops the lesson) |
| Left-handed | the menu footer: *Pointing: right hand* switches the roles |
| Leave VR | the menu footer's **Exit VR** (on the headset itself: **Quit**), or take the headset off and press F9 on the PC |

If the menu shows "No hand tracking from the headset: turn on Hand tracking ..." under its buttons, the OpenXR runtime delivers no hand data: turn Hand tracking on in the headset (Settings > Movement tracking) and put the controllers down (the viewer ignores controllers).

Troubleshooting: the log line `VrMode: ...` in the editor console (and the Player log) says why XR did not start ("no active OpenXR
runtime registered", "the OpenXR runtime refused (is the headset connected and Quest Link / Air Link running?)", "XR started but no
display is running"). `unity command hm_vr` prints the whole state (`runtime`, `xr`, `error`, `log`). `-hmvr off` keeps the viewer flat.

### 2a. The Quest 3 over Link on this PC, with the splat dancers (2026-10-21)

Ready on the PC: the Windows build `Builds/vr` (built 2026-10-21 from the working tree, section 6) with the lessons inside it and in
`Builds/data`; Meta Horizon Link installed and the active OpenXR runtime (`HKLM\SOFTWARE\Khronos\OpenXR\1` `ActiveRuntime` =
`C:\Program Files\Meta Horizon\Support\oculus-runtime\oculus_openxr_64.json`, OVRService running). Nothing on the PC, the headset or
Link was changed. Measurements and what to expect: SPLAT_DANCERS.md "PC VR over Meta Quest Link".

1. Connect the headset to a **USB 3** port with a data cable (the Link app's device page has a connection test), or use Air Link on
   5 GHz Wi-Fi. Open the **Meta Horizon Link** app on the PC; leave its settings (Devices > Meta Quest 3 > Graphics preferences: the
   logs show 72 Hz and the default render resolution, 1824 x 1952 per eye).
2. In the headset: Quick Settings > **Link** (Quest Link) > this PC > Launch. The grey Link home appears.
3. On the PC double-click **`head-movement\Builds\vr\Start Head Movement (Link).cmd`** (= `Tools/pcvr_link.ps1`: checks the runtime,
   sets HM_DATA, starts `HeadMovement.exe -force-d3d12 -hmvr on -hmvr-display immersive -hmsplats on`, logs to `Builds/vr/logs/`). The
   headset shows the lesson menu in the dark studio (passthrough is a standalone-only feature). Poke **Play** on *Class recap* or
   *John + Lula*. Without hands, start a lesson by itself from a terminal in `head-movement`:
   `powershell -ExecutionPolicy Bypass -File Tools/pcvr_link.ps1 -Play recap -Seek 23 -View start` (`-Play john_lula` for John & Lula).
4. If the menu says "No hand tracking from the headset": Hand tracking on in the headset (Settings > Movement tracking), controllers
   down; the Meta PC runtime may also want its Developer Runtime Features (Link app > Settings > Beta) for hands over Link - your call,
   it is a Link setting. The `-Play` switches need no hands.
5. **Look for**: the photoreal couple in BOTH eyes (close one eye, then the other), steady (no swimming, no flicker, no one-eye-only
   image), the hair swinging, life size when you walk up. Mesh avatars instead of splats after a few seconds = the safety net
   (`HM-SPLATS FALLBACK` in the summary). With the full set expect a low frame rate (Link's ASW halves to 36 fps or worse).
6. Close: the menu footer's *Exit VR*, then close the desktop window; the console prints the `HM-SPLATS` / `HM-PERF` / `VrMode` lines.

**What to measure** (nothing to install): the **Oculus Debug Tool** is installed
(`C:\Program Files\Meta Horizon\Support\oculus-diagnostics\OculusDebugTool.exe`): *Performance HUD* = *Performance Summary* (app fps,
ASW, dropped frames) and *Application Render Timing* (app GPU / CPU ms) in the headset; set it back to *Off* after (a Link debug setting
you change yourself; the same for *Asynchronous Spacewarp: Off* if you want the raw app fps). The Player log has `HM-PERF` every 5 s
(fps, frame ms, GPU / CPU frame time). OVR Metrics Tool shows standalone apps; over Link it would show the headset's compositor only.
Runs of about 20 s at the floor edge (`-View start`) at `-Seek 23`, `39` and `273` (`-Play recap`), each with: (a) as installed,
(b) `-SplatSigma 0.01`, (c) `-SplatSet quest_40k` (the distilled set: recap only so far), (d) `-Splats off` (the mesh avatars); then
walk up to about 1 m from the couple in (a)-(c). Note app GPU ms, app fps, ASW, dropped frames, and how the dancers look.

## 3. Commands, tests, switches

```
unity command hm_vr --action state                 # mode, runtime found, XR status, rig, pointers, menu, hand panel, captions, film, quality
unity command hm_vr --action enter --flag true     # VR mode on SIMULATED hands (no headset); --flag false = a real headset through XR
unity command hm_vr --action widgets               # every live widget with its id, world position and size in mm
unity command hm_vr --action aim --hand right --widget play:demo --value 0 | pinch --value 1 | poke --widget play --depth -0.012 | palm --hand left --flag true
unity command hm_vr --action selftest              # the whole scripted scenario; poll with --action result
unity command hm_vr_setup                          # (re)apply the XR project settings (section 4)
unity command hm_vr_settings                       # report them
unity command hm_vr_build --target windows --output Builds/vr --timeout 2400
powershell -File Tools/playtest_vr.ps1 [-Film recap] [-Shots Screenshots/vr]   # the acceptance test (simulated hands, Play mode)
```

Unit tests: Window > General > Test Runner > EditMode > `HmVr.Core.Tests` (25 tests of the pure input logic).

Switches: command line `-hmvr auto|on|off|sim`, environment `HM_VR`; `-hmvr-tier quest|pc`, `HM_VR_TIER` (try the Quest tier on a PC);
`HM_DATA=<folder>` (where the data lives, section 7). F9 toggles the mode.

## 4. Project settings (applied by `hm_vr_setup`, committed)

- **XR Plug-in Management**: OpenXR loader for Windows and for Android; *Initialize XR on Startup* **off** for Windows (the viewer starts XR
  itself, on demand, so a PC without a headset gets no XR error in the Unity console; the start is a coroutine in `VrMode`. The OpenXR loader still writes a few "no runtime" lines to the Player log at the start of a Windows build: it pre-initialises to pick the graphics adapter. On **Android** it is on: the headset app is VR from its first frame).
- **OpenXR features**: *Hand Tracking Subsystem* (package `com.unity.xr.hands` 1.9.0) on both; the Touch controller profile (the Quest
  runtime expects an interaction profile; the viewer ignores controllers); on Android *Meta Quest Support*, *Foveated Rendering*, *Meta Quest: Session*, *Meta Quest: Camera (Passthrough)* and *Display Utilities*.
  Render mode single-pass instanced (every viewer shader carries the stereo macros), no depth submission.
- **Android player**: minimum API 32 (the passthrough camera feature; Horizon OS is Android 14), product name *Head Movement*, ARM64, IL2CPP, Vulkan then GLES3, ASTC textures, package
  `com.headmovement.viewer`. The hand-tracking permission and feature entries of the manifest are added by the XR Hands build hook when
  the Hand Tracking feature is on (`com.oculus.permission.HAND_TRACKING`, `oculus.software.handtracking`).
- Packages added: `com.unity.xr.hands`. Already present: XR Plug-in Management, OpenXR, Unity OpenXR Meta, Input System.
- The older `GenericXR` rig in the scene (VRTKLite, controllers, `SDKManager`) stays inactive: it only turned on when XR was running at
  scene start, which no longer happens. The VR mode builds its own rig at run time; the scene file is unchanged.

## 5. How the VR mode touches the rest of the viewer

- `FilmDirector.HeadIsCamera` (static): when true the director leaves every camera alone (no orbit, tour, fly-to, re-lens, near-plane or
  aspect writes, no phone video drawn); everything else of the film runs unchanged. Set by `VrMode` only.
- `FilmPlaybackBar`: its speed label follows the director's rate, and its speed button continues from wherever the rate is (the VR hand
  panel sets 0.25x and 0.1x).
- `HmData.Root`: the one place the data folder is decided (StreamingAssets; on Android the app's own storage folder).
- The flat overlays (library, bar, captions, titles) are disabled while VR mode is on, so nothing flat reaches the headset or its mirror.
- A recording (frame-locked game) never enters VR; entering VR while a recording starts leaves it.
- The camera of VR mode is tagged MainCamera for its lifetime; the desktop camera object is deactivated and comes back on Exit.

## 6. Windows (PC VR) build

`unity command hm_vr_build --target windows --output Builds/vr --timeout 2400` builds the viewer scene into `Builds/vr` (git-ignored) with
Unity's own StreamingAssets copy. The captures and films resolve their files from the project and the workspace beside it, so a build
that is moved elsewhere needs its data laid out: `Tools/deploy_data.ps1 -Out Builds/data`, then copy that folder over the build's
`HeadMovement_Data/StreamingAssets`, or run the exe with `HM_DATA=<that folder>`.

Done 2026-10-21 (for Link, section 2a): `unity command hm_vr_build --target windows --output Builds/vr --development false
--timeout 5400` (625 s; the build rewrites `Assets/Bloom.asset`, `Assets/DefaultVolumeProfile.asset`,
`Assets/UniversalRenderPipelineGlobalSettings.asset` and `ProjectSettings/ProjectSettings.asset` preloaded assets: `git checkout --`
them), `Tools/deploy_data.ps1 -Out Builds/data`, then `robocopy Builds/data Builds/vr/HeadMovement_Data/StreamingAssets /MIR`. Start it
with `Tools/pcvr_link.ps1` (or `Builds/vr/Start Head Movement (Link).cmd`): `-force-d3d12` is required for the splat dancers on this
4-thread PC (Unity's automatic choice is D3D11, which has no splat path). In VR the Windows player shows the splat dancers by default
for lessons with `splats/` (SPLAT_DANCERS.md "PC VR over Meta Quest Link"); on the desktop it does not.

## 7. Quest 2 standalone (Android): audit and plan

**What is ready**: the Android module is installed (SDK platforms 34/36/37, build-tools 36, NDK, OpenJDK, IL2CPP); `adb` is in the editor's
`PlaybackEngines/AndroidPlayer/SDK/platform-tools`; a Quest 2 attached over USB shows as `device` in `adb devices` once USB debugging is
authorised in the headset. The XR settings, the manifest entries (through the XR Hands and Meta Quest features), the player settings, the
quality tier and the data root are in. `HmVr.Core` and the VR code have no desktop-only dependency (the registry probe is Windows-only; on
Android the runtime is assumed and XR is started at once).

**What is not ready** (in order of effort):

1. **The data.** The captures are about 1.1 GB for the three lessons (a capture is 90 to 670 MB: the recap's 672 MB is mostly the phones'
   frame strips; without them a lesson is 90 to 330 MB). StreamingAssets is inside the APK on Android and cannot be read with file calls,
   and an APK should stay small, so the data is **pushed to the app's own folder**: `Tools/deploy_data.ps1 -Out Builds/data -Push` lays out
   the captures, the films (direction, transcript timeline, soundtrack mix, emoji, chapters), the posters and a `library.json` whose paths are
   relative, and `adb push`es it to `/sdcard/Android/data/com.headmovement.viewer/files/hm_data` (`HmData.Root`). The phones' video strips
   (`*.hmj`, hundreds of MB) are left out unless `-PhoneVideo`: VR never draws them. Not done: an OBB for store distribution.
2. **The first build** (done 2026-10-11, see the installed workflow below). Switching the editor to Android re-imports every asset (the project is script and shader heavy, little imported
   art: expect 10 to 20 minutes) and an IL2CPP ARM64 build adds 10 to 20 on four cores. It was **not run** in this session: switching the
   active build target would have stalled the other work in the same editor. Do it in a separate clone, or in the editor when nothing else
   uses it: `unity command hm_vr_build --target android --allow_switch true --output Builds/quest --timeout 3600`, then
   `adb install -r Builds/quest/headmovement.apk` and `Tools/deploy_data.ps1 -Out Builds/data -Push`.
3. **Shaders on Vulkan mobile.** All viewer shaders are HLSL for URP with the stereo macros (they are single-pass-instanced safe); none is
   verified on Adreno. Expected trouble: the avatar shader's translucent two-pass sort and the hair shaders (strands, cards, tips). Hair is off
   in the Quest tier.
4. **Frame time.** The Quest 2 (XR2 Gen 1, 72 Hz = 13.9 ms) has a fraction of the PC's GPU. The cost drivers are the two translucent avatars
   with their back-face depth prepasses and the hair; the room mesh (decimated, one texture) is cheap. Plan: measure with Unity's Frame
   Debugger and Meta's performance tools, then trim in the order below.
5. **Memory.** The 6 GB device is ample for one lesson: avatar albedo 2 x ~4 MB PNG decoded to 2 x 16 MB (2048 px) or less when
   ASTC-compressed, the motion and skin binaries (17 + 17 MB for the recap), the physics and figure JSON (10 + 10 MB, parsed to objects:
   the largest managed allocations, worth converting to binary), the soundtrack (decoded PCM, 70 MB for the recap).

**Quest quality tier** (`VrQuality`, `Tier.Quest`; default on Android, `-hmvr-tier quest` to try it on a PC): MSAA 4x (2x on a Quest 2), no HDR
(the additive overlay glow clips; the overlays stay visible), no shadows, no extra lights, eye texture 1.0 (0.9 on a Quest 2), **hair kept on (section 0)**, fixed foveation level 1; every value restored when VR mode ends. Further steps, in the order they pay:
the avatar albedo is baked to ASTC 6x6 by the deploy step (`BakedTexture`, done),
decimate the room mesh to under 50 k triangles (the exporter's `room_mesh.bin` is 100 KB for the heat: already small), cap the physics
and trace overlays' line counts, turn the translucent avatar's depth prepass into a single opaque pass at opacity 1 for the tier.

**Install and run on the headset** (built and installed once, 2026-10-11): `adb install -r headmovement.apk`; push the data; start the app
from the headset's *Unknown Sources* library (its name there is the Player Settings product name). Hand tracking must be on (Settings >
Movement tracking). `com.oculus.permission.HAND_TRACKING` is an install-time permission (granted by the install). `adb logcat -s Unity`
shows the `VrMode:` lines.

**Installed workflow (generic)**

1. Editor idle, then `unity command hm_vr_build --target android --allow_switch true --development false --output Builds/quest --timeout 5400`.
   `Assets/StreamingAssets` is copied wholesale into an APK: move its contents out (to a folder outside `Assets`) for the build and move them
   back after, so the APK stays about 50 MB (the data goes to the app's folder instead). The first Android build on a 4-core PC took about
   75 minutes (texture and shader import for the target, 2880 URP post-processing variants, 1700 IL2CPP C++ units); later builds reuse the caches.
   Then switch the editor back to the Windows target and `git checkout` the files the build rewrites (`Assets/Bloom.asset`, `Assets/DefaultVolumeProfile.asset`,
   `Assets/UniversalRenderPipelineGlobalSettings.asset`, `ProjectSettings/ProjectSettings.asset` preloaded assets, `ProjectSettings/UnityConnectSettings.asset`).
2. `adb install -r Builds/quest/headmovement.apk`.
3. `powershell -File Tools/deploy_data.ps1 -Out Builds/data -Push` (about 490 MB without the phones' video strips; 15 s over USB).
   Package `com.headmovement.viewer`, data in `/sdcard/Android/data/com.headmovement.viewer/files/hm_data`.
4. `adb shell am start -n com.headmovement.viewer/com.unity3d.player.UnityPlayerActivity`.
5. Logs: `adb logcat -d -s Unity` (look for `VrMode: VR mode on (headset)`), `adb logcat -d | grep "VrApi   : FPS"` (frame rate, stale
   frames, CPU/GPU levels, temperature). With the headset off the head the OS keeps it asleep and the XR session never becomes ready;
   `adb shell am broadcast -a com.oculus.vrpowermanager.prox_close` fakes the proximity sensor for a desk test and
   `...automation_disable` undoes it.
6. Remove: `adb uninstall com.headmovement.viewer` (removes the pushed data too); reset only: `adb shell pm clear com.headmovement.viewer`.

## 8. Known limits (honest list)

- Android build installed and started on a Quest 2 (XR session focused, Vulkan, 72/72 fps on the menu scene), but not yet worn or used with hands: the head pose, the XR Hands joint reading, the floor origin, foveation and everything on
  the Quest are untested. The hand logic is tested against simulated joints built from the same 26-joint layout; real tracking is noisier
  than the simulation (the pinch hysteresis, debounce, the One Euro ray filter and the poke "finger points into the surface" rule are the
  defences; their thresholds are the first things to tune with a real hand).
- The film's *screen-space* overlays (title card, move caption, readouts, beat counter, the call-out text labels) are not in VR; the 3D
  call-outs (arrows, rings, floor marks) are. Only the transcript is a world panel.
- The phones' real position viewpoints use the phone's pose at the moment you choose them, standing at its height (1.2 to 2.0 m).
- Panels are drawn over the dancers (they sort after the translucent avatars); the film's 3D call-outs use an always-on-top shader and can
  draw over a panel.
- Passthrough placement is fixed (section 0); there is no Meta spatial anchor and no scene-model alignment, so a recentre of the headset's tracking space moves the dance with it.
