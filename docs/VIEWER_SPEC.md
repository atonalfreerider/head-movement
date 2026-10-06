# Head Movement viewer — front-end specification

Version 2 · 2026-10-06 · supersedes the keyboard/lesson notes in README.md where they conflict.

The viewer turns a **completed dance** (the output of the dancecap capture pipeline) into an explorable,
directed and recordable 3D experience: on desktop, as rendered social-media video, and on Quest 3 in
passthrough mixed reality. This document is the contract for everything that happens in the Unity viewer.

---

## 1. Output targets

| Target | Purpose | Notes |
|---|---|---|
| **Desktop / Editor** | authoring, review, and the only place videos are rendered | Unity Recorder runs in the Editor only (Play mode) |
| **Rendered video, 16:9** | YouTube, desktop sharing | 1920×1080 (default) or 3840×2160, 30 or 60 fps, H.264 MP4 + AAC |
| **Rendered video, 9:16** | Reels / TikTok / Shorts | 1080×1920 (default) or 2160×3840, 30 or 60 fps, H.264 MP4 + AAC, vertical framing rules (§8.3) |
| **Quest 3 standalone** | immersive viewing in **passthrough MR**: the dance plays on the user's real floor | all visualisation layers available, within the Quest budget (§9.4) |

Every view state, overlay and directed sequence must work in all targets; differences are limited to
framing, UI and performance tiers.

---

## 2. Dance library

### 2.1 What counts as a completed dance

A dance is listed when its capture folder (StreamingAssets/&lt;Capture&gt;/, capture.json v4, §10) contains at
least the **required** layers. Optional layers appear when present; nothing in the viewer may assume an
optional layer exists.

| Layer | Required | Source (dancecap stage) |
|---|---|---|
| Timeline (`times.json`), audio, beat grid | yes | ingest / beats |
| SMPL-X motion for lead and follow (55-joint rotations + positions) | yes | MAMMA |
| Shaped avatar skins | yes | export |
| Skeleton (derived from SMPL-X) | yes | export |
| Contacts (floor + partner) | yes | MAMMA + timing |
| Timing analysis (footsteps, accents, asynchrony) | yes | timing |
| Physics (COM, ground reaction, limb force estimates, contact forces) | yes | physics |
| Floor craft (footprints, coverage) | derived in viewer | — |
| Photoreal avatar textures | optional | texture bake |
| Hair groom + simulation parameters | optional | hair |
| Source camera videos + per-frame camera tracks | optional (needed for the camera tour) | ingest + cameras |
| Room (Quest mesh / splats) | optional | room |
| 4D Gaussian splat sequence | optional, future | — |

### 2.2 Library UI

- Grid of dance cards: thumbnail (poster frame), title, dancers, duration, BPM, date, and **layer badges**
  (textures, hair, camera tour, room, 4DGS) plus a **quality badge** from the capture's QA numbers
  (reprojection error, sync residual).
- Select → loading screen (progress per layer) → default view (§3, state **Orbit**).
- Desktop: mouse/keyboard. VR: hand-tracked or controller-pointed panel, anchored in passthrough
  (§9.3). Natural-language commands ("open the Larissa and Kadu demo") use the existing TypeSafe router.

---

## 3. Visual language (the canonical look)

### 3.1 Floor
- **Black, translucent** floor plane (default alpha ≈ 0.6) so the floor is legible without hiding the
  room/passthrough below it.
- **1 m grid marked with teal-blue crosses** at every grid intersection (cross arm ≈ 6 cm, line ≈ 6 mm,
  colour teal ≈ #19C3D6). No continuous grid lines. The grid is aligned to the capture's world floor
  (y = 0) and origin; it is not re-oriented by the dancers.
- In passthrough MR the plane alpha drops (≈ 0.25) so the real floor remains visible; crosses stay.

### 3.2 Avatars
- **Default: semi-transparent** (alpha ≈ 0.3), photoreal-textured when the texture layer exists, otherwise
  a neutral shaded material (lead warm grey, follow cool grey). Depth-correct transparency (depth
  pre-pass) so a translucent body doesn't show its own back faces.
- Opacity is a per-state parameter (§4): ≈ 0.3 default, ≈ 0.1 in Physics, up to 1.0 when requested.
- Hair (follow) inherits the avatar's opacity, slightly higher (+0.15) so the hair motion reads.

### 3.3 Skeletons — always on, always opaque
- The stylised glowing skeletons are drawn **inside** the avatars, **fully opaque and bright** in every
  state, never faded by avatar opacity: **lead = red, follow = white** (current Dancer.cs style:
  spline-smoothed limbs, tapered widths).
- **Beat conduction:** on each beat a pulse of light travels through the skeleton. It starts where the
  body meets the floor (stance foot/feet), travels up the legs through the pelvis and spine, and out
  through the arms and head over ≈ 1/8 beat. Pulse strength depends on the beat type (zouk downbeat >
  accent > other) and is modulated by how well that dancer's own kinematic accent lands on the beat
  (from timing.json: a dancer on the beat flares; a late body part lags visibly).
- Joints with a detected accent at that moment flash briefly (accent markers from timing.json).

### 3.4 Source-camera video (camera POV)
- Each source phone is a **camera rig** in the scene, driven per frame by its tracked 6DoF pose **and
  zoom** (intrinsics per frame from the camera track). Its **original MP4** plays on a video plane at the
  camera's frustum (fixed distance, sized from the per-frame focal length and principal point, so that
  seen from the camera's own position the video exactly fills the view). Lens distortion is corrected in
  the exported video (or with the k1 term in the shader).
- **Video and 3D see each other:** the video has its own opacity (0–1, fadeable). Two compositing orders:
  - **3D over video** (default): the video is drawn first as a background layer (no depth write), the
    translucent avatars and opaque skeletons composite over it, so you see the avatars *through* to the
    footage and a correct reconstruction is visibly confirmed: the skeleton lands on the dancer in the video.
  - **Video over 3D**: the video is drawn last at partial opacity, so you see the avatars *through* the
    video. Skeletons can optionally stay on top in both orders.
  Fading the video out returns smoothly to the plain avatar view.
- Seen from elsewhere, each camera shows as a small frustum glyph with its video thumbnail, coloured by
  phone; inactive cameras are hidden or dimmed.
- **Alignment QA:** the viewer can show the reprojection error of the skeleton in the active camera
  (pixels, rolling median) — a direct check of the whole capture.

### 3.5 Floor craft
- **Footprints** for every detected step (timing.json touchdowns), outlined in the dancer's colour and
  filled by timing (on-beat green, near amber, off magenta — no red, which belongs to the lead), each with
  a **fading history** (recent steps bright,
  older steps fade over a configurable window, e.g. 4 measures), so the floor pattern of the figure reads.
- **Coverage**: an overhead heat-trail of where each dancer's feet have been (lead red, follow white),
  accumulated from the start of the take or from the current loop.

### 3.6 Leader floor axis (dancer frame, not room frame)
- Always drawn on the floor under the leader (all states except where explicitly hidden):
  - **Long axis**: a line through the leader's chest projection, **parallel to the leader's chest**
    (shoulder line / chest plane, projected onto the floor), ≈ 2 m long.
  - **Short axis**: perpendicular, projecting **forward from the leader's chest** on the floor, ≈ 0.8 m,
    with an arrowhead.
  - The axis origin is the leader's chest (spine3) projected to the floor; orientation is smoothed lightly
    (no lag visible at 0.5× speed). This frame is "where the dance is facing", independent of the room.
- The follower has **no** floor axis.

### 3.7 Follower geometry — spirals and the head axis
- 3D **traces** of the follower's hands (wrists), feet (ankles) and head, drawn as fading ribbons
  through space over a configurable window (e.g. 2 measures), coloured by time (bright = now).
  They reveal the circular/spiral paths of zouk extremities and head movement.
- **Head axis**: a ray projected from the top of the follower's head along the head's up direction
  (SMPL-X head joint orientation), ≈ 0.6 m into space; its tip leaves its own trace, showing how the head
  goes off the body axis and circles.
- Optional analytic overlay: fitted circles/helices for the current trace segment with their radius and
  period (to read the geometry as numbers).

### 3.8 Physics visualisation
- **Very translucent avatars** (alpha ≈ 0.1); skeleton limbs are recoloured by **internal axial load**:
  tension (orange) ↔ neutral (grey-white) ↔ compression (blue), width by magnitude. One shared colour
  scale for both dancers so tension and compression read as one unified system across the couple.
- **Floor forces**: ground reaction arrows at each stance foot (and the couple total), scaled per body
  weight, with centre-of-pressure dots.
- **Contact forces between the dancers**: hand–hand, hand–body and body–body contacts drawn as connectors
  with force direction/magnitude where estimated.
- **Time focus**: a scrolling strip (bottom of frame) with the selected quantities over time (e.g.
  vertical GRF per dancer, total tension, partner force interval) and a playhead.
- **Honesty requirement**: all forces are model estimates from motion. Values carry uncertainty bands from
  physics.json; partner/limb forces that the physics stage marks **not identifiable** are drawn as
  hatched/grey intervals, never as confident numbers. A legend states "estimated from video".

---

## 4. View states

A view state is a named bundle of layer parameters + a camera behaviour. All parameters blend smoothly
(default 1.0 s, configurable) when switching states, and every parameter is animatable by the director (§6).

| State | Camera | Avatars | Skeletons | Overlays |
|---|---|---|---|---|
| **Orbit** (default after load) | slow continuous orbit around the couple (≈ 1 rev / 20 s, gentle height drift), centred on the couple | 0.3, textured | opaque, beat conduction on | floor + crosses, leader axis, recent footprints |
| **Camera tour** | flies between source-camera POVs (§5.3) | 0.3 | opaque | active camera's video at 0→0.85 opacity, other cameras as glyphs, alignment QA optional |
| **Overhead floor craft** | top-down (orthographic or narrow FOV), framed to the dance area | 0.2 | opaque | footprint history, coverage trails, leader axis, step timing colours |
| **Geometry** | low 3/4 orbit, slower | 0.15 | opaque | leader axis, follower spirals + head axis, fitted circles optional |
| **Physics** | 3/4 side view, steady | 0.1 | load-coloured | GRF arrows, contact forces, time strip, legend |
| **Lesson** (existing) | user orbit | user | opaque | timing HUD, transport (beat/measure step, loop) |
| **Free** | user-controlled | user | opaque | any |

Every state also defines what is hidden (e.g. Physics hides footprints; Camera tour hides spirals).

---

## 5. Cameras

### 5.1 Virtual cameras
Built on **Cinemachine** (com.unity.cinemachine): an orbit camera, an overhead camera, a free camera, and
one camera per source phone. Transitions use Cinemachine blends (default ease-in-out 1.2 s); cuts allowed.

### 5.2 Orbit
Continuous motion by default ("the camera should generally be in motion"): orbit radius/height/speed
per state, target = couple centre (smoothed), never passing through a dancer.

### 5.3 Camera POV tour (multi-camera broadcast)
- Each source phone camera follows its **per-frame pose and zoom** (field of view from the per-frame focal
  length; principal point as lens shift).
- Tour = fly from the orbit to camera A's POV, fade A's video in (0 → 0.85 over 0.8 s), hold, fade out
  while flying to camera B, and so on — like switching cameras at a sports broadcast. Order defaults to
  walking around the room (angular order around the couple), skipping cameras without coverage.
- Hard cuts between cameras are also supported (broadcast style), with the video already faded in.
- Video time is locked to dance time through each camera's clock mapping (reference time → source time);
  the video never drifts from the avatars. Implementation: `VideoPlayer.timeReference = ExternalTime`
  with `externalReferenceTime` set from the dance clock every frame, `playbackSpeed` = dance speed, and
  explicit frame seeks when paused or stepping; in recordings the player follows game time so it stays
  frame-locked to the Recorder.

### 5.4 Overhead
Top-down view above the dance area; fits both dancers' full floor coverage; north = world +Z (fixed), with
an option to rotate to the leader's floor axis.

---

## 6. Directed sequence (auto-director)

- Each dance gets an **automated, scripted show** built on **Unity Timeline** (com.unity.timeline):
  camera tracks (Cinemachine shots), a view-state track (parameter blends), a playback-speed track, and an
  audio track.
- The script is **generated** from a JSON shot list (`direction.json`) with defaults derived from the
  dance (duration, beat grid, where turns/embraces/spins happen) and can be edited and re-rendered.
- **Default script order** (measure-aligned cuts):
  1. **Orbit** — mostly transparent avatars, opaque coloured skeletons conducting the beat.
  2. **Camera tour** — pass through every source camera POV; each video fades in and out as the camera
     passes through it.
  3. **Overhead floor craft** — floor coverage, footprint patterns, leader floor axis.
  4. **Geometry** — leader axis + follower spirals / head axis.
  5. **Physics** — limb tension/compression, floor and contact forces through time.
  6. Return to **Orbit** for the end.
- The same script drives the desktop show, the rendered videos (§8) and the VR "presentation" mode.

---

## 7. Playback and time

- Dance time is the master clock; frames are looked up by time (times.json), never by a constant fps.
- **Speeds**: continuous from slow motion up to real time, **0.1× – 1.0×**, presets 0.25×, 0.5×, 0.75×,
  1.0×; speed is animatable in the director (speed ramps into slow motion on key moments).
- Audio: at 1.0× normal; below 1.0× time-stretched without pitch change via an AudioMixer pitch-shifter
  setup, or muted below a configurable threshold (default 0.5×) where stretching degrades.
- The existing lesson transport (play/pause, beat/measure step, loop measure, seek) and CLI `hm_*`
  commands remain and gain `hm_state <name>`, `hm_camera <id>`, `hm_speed <x>`.

---

## 8. Recording pipeline (social-media video)

### 8.1 Technology
**Unity Recorder** (com.unity.recorder), Editor/Play mode only, Movie Recorder → MP4 (H.264 + AAC).
Recording is **frame-locked**: the Recorder fixes the capture frame rate, so slow-motion segments render
perfectly smooth, and frames render at full quality regardless of real-time speed.

### 8.2 Presets
| Preset | Resolution | fps | Use |
|---|---|---|---|
| Landscape HD | 1920×1080 | 30 / 60 | default 16:9 |
| Landscape 4K | 3840×2160 | 30 | high quality |
| Vertical HD | 1080×1920 | 30 / 60 | default 9:16 |
| Vertical 4K | 2160×3840 | 30 | high quality |

### 8.3 Framing rules
- Each shot defines framing for both aspect ratios; in 9:16 the couple is framed full-height (head to feet
  plus floor margin), cameras pull back/raise rather than crop the dancers.
- Title-safe areas: top 14% / bottom 20% of 9:16 frames kept free of key action (platform UI overlays);
  optional captions (dance name, state labels) placed inside safe areas.

### 8.4 Audio in recordings
- Real-time segments: original music, sample-accurate with dance time.
- Slow-motion segments: time-stretched music (pitch kept) or a music bed/fade, chosen per script. For
  renders, stretched audio for each preset speed is pre-rendered offline by the pipeline (high-quality
  stretcher such as Rubber Band) rather than the real-time mixer pitch shifter.
- Audio is captured offline in lock with the video frames (the Recorder drives Unity's `AudioRenderer`),
  so renders stay in sync even when a frame takes longer than real time to draw.

### 8.5 Automation
- `unity` CLI command `hm_render --dance <id> --script direction.json --preset vertical_hd` renders the
  directed show headlessly to `Recordings/<dance>_<preset>_<date>.mp4`; batch over presets.
- Output check: duration, resolution, fps and audio presence verified after every render.

---

## 9. Quest 3 VR (passthrough MR)

### 9.1 Mode
- Passthrough mixed reality (OpenXR + Meta OpenXR + AR Foundation passthrough; camera clear to
  transparent). The dance plays life-size on the user's real floor.
- Placement: the floor plane snaps to the detected real floor; the user grabs/rotates/moves the dance
  area; scale fixed 1:1 (optional mini "tabletop" scale).
- All visualisation layers and view states are available; the director can run as a presentation around
  the user, or the user walks freely while time plays.

### 9.2 VR recording
- **POV recording**: on-device via Quest system capture (includes passthrough). The app shows a minimal
  "recording" UI state (hides menus).
- **Session log**: every VR session logs head pose, controller/hand state, dance time, speed and view state
  (`vrsession_<date>.json`). The Editor replays a log to render:
  - a clean **POV** video (virtual content only, or composited over captured passthrough if available),
  - an **overhead** video of the same session showing the user's position relative to the dancers.
  Both through the Recorder presets of §8.

### 9.3 VR interaction
- Wrist/hand menu (palm-up) or controller panel: library, view states, speed, transport, layer toggles.
- Voice/natural-language commands through the existing TypeSafe router where available.

### 9.4 Quest 3 budgets (performance tier "Quest")
- 72 Hz minimum (90 Hz target), single-pass instanced stereo.
- Avatars: one skinned mesh + one 2048² texture per dancer; hair ≤ 200 guides × 16 segments simulated,
  rendered as one ribbon mesh.
- Video tour: at most **2** source videos decoding at once (720p H.264), others as stills.
- Room: Quest mesh (≤ 80k tris) or ≤ 150k splats; default off in passthrough (the real room is visible).
- Transparency: limit overdraw (depth pre-pass avatars, no full-screen transparent layers).

---

## 10. Data contract from the pipeline (capture.json v4)

v4 extends v3 (times, SMPL-X motion + skins, timing, physics) with:

| Key | Content |
|---|---|
| `title`, `dancers`, `bpm`, `poster` | library card data |
| `layers` | flags for every layer of §2.1 + QA numbers (reprojection px, sync residual ms) |
| `cameras/` | per source phone: `video.mp4` (rotation baked, trimmed to the take, re-encoded at a **constant** 30 fps from the phone's variable-rate PTS, Quest-friendly 720p H.264 + a full-res copy for desktop/recording), `track.json` (per frame: reference time, Unity-space position + rotation, vertical FOV, principal point, k1), `clock` (reference → video time mapping) |
| `textures/`, `hair_groom.json` | avatar albedo per dancer, hair parameters |
| `physics.json` v2 | per frame: COM, GRF per foot (+ bands), **per-segment axial load estimates** (tension/compression, + band, identifiable flag), contact forces per contact (type, points, force or interval, identifiable flag) |
| `timing.json` | beats, touchdowns, accents (with reliability), asynchrony stats |
| `direction.json` | optional authored shot list; generated if absent |
| `room/` | Quest mesh / splats + scene_to_unity |

Derived in the viewer (no export needed): leader floor axis, follower spirals and head axis, footprint
history, coverage.

---

## 11. Implementation status (2026-10-06)

| Area | Exists today | New work |
|---|---|---|
| Playback core | capture v3 loader, times-driven clock, beat grid, lesson transport, speeds, `hm_*` CLI, TypeSafe router | v4 manifest, library scan, speed track |
| Avatars | skinned SMPL-X avatars (SmplxAvatar), hair prototype | depth-prepass translucent material, texture/hair layers, opacity per state |
| Skeletons | glowing spline skeletons (Dancer.cs) | lead red / follow white, beat-conduction pulse, accent flashes |
| Floor | FloorPatterns, TimingOverlay footprints | black translucent plane + teal 1 m crosses, fading history, coverage trails |
| Floor craft / geometry | — | leader floor axis, follower spiral traces + head axis, circle fits |
| Physics | PhysicsOverlay (L0: COM, XCoM, couple GRF, impulses) | per-limb axial loads (needs a pipeline inverse-dynamics stage), contact-force connectors, time strip, uncertainty display |
| Cameras | VRTKLite orbit, VirtualCameraRig | Cinemachine rigs, per-phone 6DoF + zoom tracks, video planes, tour, cuts |
| Director | — | Timeline + `direction.json` generator |
| Recording | — | Unity Recorder presets, `hm_render`, output checks |
| VR | OpenXR + Meta OpenXR packages installed | passthrough MR scene, floor placement, hand menu, session log + replay renders |

Packages to add: `com.unity.cinemachine` (3.x), `com.unity.timeline`, `com.unity.recorder`,
`com.unity.xr.arfoundation` (passthrough + planes, if not already pulled in by Meta OpenXR).

Pipeline work this spec implies (dancecap): capture v4 export, per-camera video export (rotation, trim,
CFR, 720p + full-res) with Unity-space tracks, stretched-audio stems, physics v2 (segment inverse dynamics
with identifiability flags, contact-force intervals).

---

## 12. Acceptance tests (CLI playtests)

- Library lists every completed capture; incomplete ones show what's missing.
- Each view state switch reaches its target parameters (hm_state + hm_status).
- Camera tour: for each source camera, skeleton reprojection into that camera's video plane ≤ the
  capture's QA reprojection error + 2 px.
- Leader axis long side within 3° of the shoulder line projection, short axis perpendicular.
- Recorder: both aspect presets produce MP4s with exact resolution, fps, duration and an audio track.
- Speeds 0.1×–1.0× keep avatars, video and beats in sync (frame for time).
- Quest build: 72 Hz sustained in each state with the full layer set on the reference capture.

---

## 13. Defaults chosen and open questions

Defaults (change any of these):
- Avatar opacity 0.3 (Physics 0.1); floor alpha 0.6 (0.25 in passthrough); teal #19C3D6.
- Trace windows: footprints 4 measures, spirals 2 measures.
- Slow-motion audio muted below 0.5×.
- Default render preset: Vertical HD 1080×1920 @ 30 fps.

Open:
1. Should camera-tour videos be **full-frame** (letterboxed in the 3D plane) or **cropped to the subject**?
2. Brand/captions on rendered videos (title card, dancer names, watermark)?
3. In VR presentation mode, does the director move the **dance** around the user, or move the **user's
   viewpoint** (teleport/blink)? Default: the dance stays put, the user walks.
