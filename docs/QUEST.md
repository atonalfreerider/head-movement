# Quest and PC VR: how the VR mode runs, how to test it, what a standalone build needs

The viewer has a VR mode (VIEWER_SPEC section 9.5): a lesson menu in the world, the lesson played life-size with the user's head as the only
camera, and every control (chapters, slow motion, seeking, viewpoints, teleport, snap turn) on the hands. This page is the practical side:
the setup, the steps to try it on a headset, the commands, the limits, and the plan for a Quest standalone (Android) build.

Status of this page's claims is marked: **verified** = measured in the editor on simulated hands (`Tools/playtest_vr.ps1`, the EditMode
tests) or by a build that ran; **untested on a headset** = written against the OpenXR / XR Hands APIs and reviewed, never run on a device.

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
| Quest quality tier | `VrQuality` | applied and restored in the editor (hair off, MSAA 2x, no HDR, no shadows); not measured on a Quest |
| Data root for Android | `HmData` | trivial; untested on a device |
| Data layout and adb push | `Tools/deploy_data.ps1` | verified (layout); push untested |
| Windows (PC VR) build | `hm_vr_build` | see section 6 |
| Android build | `hm_vr_build --target android` | not run (see section 7) |

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
| Slow motion | 1x, 0.5x, 0.25x, 0.1x on the panel (captions, 3D layers and the soundtrack follow) |
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
  runtime expects an interaction profile; the viewer ignores controllers); on Android *Meta Quest Support* and *Foveated Rendering*.
  Render mode single-pass instanced (every viewer shader carries the stereo macros), no depth submission.
- **Android player**: minimum API 29 (Quest 2 runs Android 10+), ARM64, IL2CPP, Vulkan then GLES3, ASTC textures, package
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
2. **The first build.** Switching the editor to Android re-imports every asset (the project is script and shader heavy, little imported
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

**Quest quality tier** (`VrQuality`, `Tier.Quest`; default on Android, `-hmvr-tier quest` to try it on a PC): MSAA 2x instead of 4x, no HDR
(the additive overlay glow clips; the overlays stay visible), no shadows, no extra lights, eye texture 0.9, **hair simulation and strands
off** (the avatars keep their cards-less look), fixed foveation level 1; every value restored when VR mode ends. Further steps, in the order they pay:
bake the avatar albedo to ASTC 6x6 at import (the viewer loads the PNGs at run time today; load a pre-compressed texture instead),
decimate the room mesh to under 50 k triangles (the exporter's `room_mesh.bin` is 100 KB for the heat: already small), cap the physics
and trace overlays' line counts, turn the translucent avatar's depth prepass into a single opaque pass at opacity 1 for the tier.

**Install and run on the headset** (once built): `adb install -r headmovement.apk`; push the data; start *Head Movement* from the headset's
*Unknown Sources* library. Hand tracking must be on (Settings > Movement tracking). The app asks for the hand-tracking permission on
first start. `adb logcat -s Unity` shows the `VrMode:` lines.

## 8. Known limits (honest list)

- Never run with a real headset: the XR start, the head pose, the XR Hands joint reading, the floor origin, foveation and everything on
  the Quest are untested. The hand logic is tested against simulated joints built from the same 26-joint layout; real tracking is noisier
  than the simulation (the pinch hysteresis, debounce, the One Euro ray filter and the poke "finger points into the surface" rule are the
  defences; their thresholds are the first things to tune with a real hand).
- The film's *screen-space* overlays (title card, move caption, readouts, beat counter, the call-out text labels) are not in VR; the 3D
  call-outs (arrows, rings, floor marks) are. Only the transcript is a world panel.
- The phones' real position viewpoints use the phone's pose at the moment you choose them, standing at its height (1.2 to 2.0 m).
- Panels are drawn over the dancers (they sort after the translucent avatars); the film's 3D call-outs use an always-on-top shader and can
  draw over a panel.
- Passthrough / mixed reality (VIEWER_SPEC 9.1) is not built.
