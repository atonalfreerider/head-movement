# Head Movement viewer — front-end specification

Version 3 · 2026-10-06 · supersedes the keyboard/lesson notes in README.md where they conflict.

The viewer turns a **completed dance** (the output of the dancecap capture pipeline) into an explorable,
directed and recordable 3D experience: on desktop, as rendered social-media video, and on Quest 3 in
passthrough mixed reality. This document is the contract for everything that happens in the Unity viewer.

Changes in v3: dance placed at the origin in every mode (§3.0); source videos are desktop/render only and
always shown full-frame (§3.4, §5.3); Quest records the POV only and the user walks freely (§9);
counterbalance indicators (§3.9); the zouk dance graph — path through the state machine and the
time-independent fingerprint (§3.10, §3.11); new tour order (§6); dance-move labels (§10, §11).
v3.1: every dance is titled as a reaction "Leader + Follower × Song" that produces its fingerprint (§3.13);
move caption with a confidence score and a graph inset throughout directed playback (§3.12); library
silos by level (Professional / Intermediate / Novice) and categories (Demo / Lesson / Jack and Jill) (§2).

---

## 1. Output targets

| Target | Purpose | Notes |
|---|---|---|
| **Desktop / Editor** | authoring, review, and the only place videos are rendered | Unity Recorder runs in the Editor only (Play mode) |
| **Rendered video, 16:9** | YouTube, desktop sharing | 1920×1080 (default) or 3840×2160, 30 or 60 fps, H.264 MP4 + AAC |
| **Rendered video, 9:16** | Reels / TikTok / Shorts | 1080×1920 (default) or 2160×3840, 30 or 60 fps, H.264 MP4 + AAC, vertical framing rules (§8.3) |
| **Quest 3 standalone** | immersive viewing in **passthrough MR**: the dance plays life-size in the user's room, the user walks freely around it | every layer **except the source-camera videos** (§9) |

Every view state, overlay and directed sequence works in all targets unless stated; differences are
limited to framing, UI and performance tiers. The one deliberate exception: **the original phone videos
are never shown in VR**.

---

## 2. Dance library

### 2.1 What counts as a completed dance

A dance is listed when its capture folder (StreamingAssets/&lt;Capture&gt;/, capture.json v4, §12) contains at
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
| Counterbalance intervals (pivot, shared centre of mass) | yes | physics / counterbalance |
| Floor craft (footprints, coverage) | derived in viewer | — |
| Dance-move timeline + graph path + fingerprint | optional until the dance is labelled | moves (§10) |
| Photoreal avatar textures | optional | texture bake |
| Hair groom + simulation parameters | optional | hair |
| Source camera videos + per-frame camera tracks | optional (desktop camera tour only) | ingest + cameras |
| Gaussian splats of the **dancers only** (4D sequence) | optional, future; layer **off by default** | — |
| Room reconstruction (mesh / splats) | not displayed (kept only as alignment reference) | room |

### 2.2 Dance metadata, silos and categories
- Every dance carries: **leader**, **follower**, **song** (title, artist), **level**, **category**, date,
  venue (optional). Its title is the reaction label **"Leader + Follower × Song"** (§3.13), e.g.
  "Kadu + Larissa × Ficar Sem Você" (Davi Sabbag feat. Urias).
- **Level silos: Professional, Intermediate, Novice.** Data is kept siloed by level end to end: the
  library shows one shelf per level, and analysis (move classifiers, fingerprint comparisons, averages)
  never mixes levels unless a comparison across levels is explicitly requested. Today only Professional
  dances exist.
- **Categories: Demo, Lesson, Jack and Jill competition.** Shown as a badge and a library filter; carried
  into the analysis as metadata (lessons include talking and demonstration pauses; Jack and Jill dances are
  improvised with a random partner).

### 2.3 Library UI

- One shelf per level silo (Professional / Intermediate / Novice), filterable by category.
- Grid of dance cards: the reaction title (§3.13), thumbnail (poster frame), category badge, duration, BPM,
  date, the **dance fingerprint** miniature when labelled (§3.11), **layer badges** (textures, hair, camera
  tour, room, moves, 4DGS) and a **quality badge** from the capture's QA numbers (reprojection error, sync
  residual).
- Select → loading screen (progress per layer) → default view (§3, state **Orbit**).
- Desktop: mouse/keyboard. VR: hand-tracked or controller-pointed panel, anchored in passthrough
  (§9.3). Natural-language commands ("open the Larissa and Kadu demo") use the existing TypeSafe router.

---

## 3. Visual language (the canonical look)

### 3.0 Dance placement — centred at the origin (all modes)
- Every dance starts **at the origin**: the couple centre (midpoint of the two pelvis positions projected
  to the floor) at the **first frame of the take** is placed at world (0, 0, 0) on the floor.
- One fixed **dance offset** (a horizontal translation, computed once per dance) is applied to everything
  from the capture — dancers, cameras, room, splats, traces — so they stay mutually aligned. From there the
  dance **travels naturally** as the dancers move around the floor; looping a measure never re-centres.
- Yaw is kept as captured (option: rotate so the leader faces +Z at the start).
- The floor grid (§3.1) stays aligned to the origin.
- Desktop and renders: the origin is the scene centre. VR: the origin is anchored at the **centre of the
  user's room** (§9.1).

### 3.1 Floor
- **Black, translucent** floor plane (default alpha ≈ 0.6) so the floor is legible without hiding the
  room/passthrough below it.
- **1 m grid marked with teal-blue crosses** at every grid intersection (cross arm ≈ 6 cm, line ≈ 6 mm,
  colour teal ≈ #19C3D6). No continuous grid lines. The grid is aligned to the floor (y = 0) and the
  origin (§3.0); it is not re-oriented by the dancers.
- In passthrough MR the plane alpha drops (≈ 0.25) so the real floor remains visible; crosses stay.

### 3.2 Avatars
- **Default: semi-transparent** (alpha ≈ 0.3), photoreal-textured when the texture layer exists, otherwise
  a neutral shaded material (lead warm grey, follow cool grey). Depth-correct transparency (depth
  pre-pass) so a translucent body doesn't show its own back faces.
- Opacity is a per-state parameter (§4): ≈ 0.3 default, ≈ 0.1 in Physics, up to 1.0 when requested.
- Hair (follow) inherits the avatar's opacity, slightly higher (+0.15) so the hair motion reads. It must
  **match Larissa's real hair in shape and colour** (length, volume, parting and layering from the video and
  her portrait; dyed red with its root-to-tip variation) and **keep the glowing tip bloom** of the original
  head-movement hair: strand tips emit a soft bloom that traces the hair's whip through space.
- **Faces**: photoreal faces from the dancers' portrait photos (identity shape + frontal face texture),
  blended with the video-baked texture, plus per-frame **facial expressions** (jaw + expression
  coefficients) estimated from the closest cameras where the face is visible enough; neutral otherwise.
- **Hands**: articulated fingers from multi-view hand estimates, and **hand contact** that actually touches
  (hand-hand and hand-body connections between the dancers are refined so contacting hands meet).

### 3.2a Gaussian splats — dancers only
- Splats show **only the dancers** — literally nothing else: no floor, no room, no spectators. The splat
  layer is **off by default**; when switched on it replaces or overlays the avatars (4D dancer splats are
  future work). The room reconstruction is not displayed in any mode.

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

### 3.4 Source-camera video (desktop and rendered video only — never in VR)
- Each source phone is a **camera rig** in the scene, driven per frame by its tracked 6DoF pose **and
  zoom** (intrinsics per frame from the camera track). Its **original MP4** is shown **full-frame** —
  the whole frame, never cropped to the subject. Lens distortion is corrected in the exported video (or
  with the k1 term in the shader).
- **At the camera's POV the video fills the screen.** The render camera takes the phone's per-frame
  vertical field of view and principal point, so the video frame maps exactly onto the view:
  - **9:16 output**: the phone videos are portrait (all seven Larissa/Kadu phones are), so the video
    **fills the whole screen**.
  - **16:9 output**: the whole portrait frame is shown centred at full height; to its left and right the
    3D world continues seamlessly beyond the phone's field of view (no black bars, no cropping).
  - Minor aspect differences (e.g. 480×848 vs 9:16) fit to height; the 3D world fills the sliver.
- **Transitions:** fly into a camera's POV → video fades in until it fills the frame → hold → video fades
  out, revealing the 3D world with the avatars dancing in the same place → fly on.
- **Video and 3D see each other** during fades: the video has its own opacity (0–1). Two compositing orders:
  - **3D over video** (default): the video is drawn first as a background layer (no depth write), the
    translucent avatars and opaque skeletons composite over it, so a correct reconstruction is visibly
    confirmed: the skeleton lands on the dancer in the video.
  - **Video over 3D**: the video is drawn last at partial opacity, so you see the avatars *through* the
    video. Skeletons can optionally stay on top in both orders.
- Seen from elsewhere, each camera shows as a small frustum glyph with its video thumbnail, coloured by
  phone; inactive cameras are hidden or dimmed.
- **Alignment QA:** the viewer can show the reprojection error of the skeleton in the active camera
  (pixels, rolling median) — a direct check of the whole capture.

### 3.5 Floor craft
- **Footprints** for every detected step (timing.json touchdowns), outlined in the dancer's colour and
  filled by timing (on-beat green, near amber, off magenta — no red, which belongs to the lead), each with
  a **fading history** (recent steps bright, older steps fade over a configurable window, e.g. 4 measures),
  so the floor pattern of the figure reads.
- **Coverage**: an overhead heat-trail of where each dancer's feet have been (lead red, follow white),
  accumulated from the start of the take or from the current loop.
- **Counterbalance pivot marker** (see §3.9): wherever the couple performed a counterbalance pivot, the
  floor craft keeps a **yellow pivot ring** at the follower's anchored foot, the **leader's circling path**
  around it as a yellow arc with the swept angle (e.g. "360°"), and a short tick per beat along the arc.
  Pivot markers fade with the footprint history but are kept in the full-dance coverage view.

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
- During a counterbalance (§3.9) the follower's free extremity (the swinging foot/leg and the free hand)
  traces are emphasised (brighter, longer window) — the spiral the pivot produces.
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
  vertical GRF per dancer, total tension, partner force interval, counterbalance intervals) and a playhead.
- **Honesty requirement**: all forces are model estimates from motion. Values carry uncertainty bands from
  physics.json; partner/limb forces that the physics stage marks **not identifiable** are drawn as
  hatched/grey intervals, never as confident numbers. A legend states "estimated from video".

### 3.9 Counterbalance (all states)
A counterbalance is when the follower's weight is offset by the leader's: they lean away from each other
and hold each other in tension through the connection, so their **shared centre of mass** sits between
them. The Larissa/Kadu take uses it heavily: the leader anchors the follower's foot at one point on the
floor and walks a full circle around her, pivoting her in place.

- **Yellow axis**: whenever the couple is in a counterbalance, a **yellow dot** on the floor directly
  below the couple's combined centre of mass, with a **yellow vertical axis** rising from it to the
  combined centre of mass (a small sphere marks the COM). It fades in/out over ≈ 0.2 s at the interval
  edges. Shown in every state, including the default Orbit.
- **Pivot point**: the follower's anchored foot is marked with a yellow ring on the floor; the floor craft
  keeps it (§3.5) with the leader's circling arc.
- **Tension**: the connection between the dancers (hand–hand/hand–body) is drawn as a taut line during
  the counterbalance; in Physics mode it takes the tension colour with its estimated interval.
- **Follower spiral**: the follower's extremity traces are emphasised (§3.7).
- Detection is a pipeline output (`physics/counterbalance.json`, §12) with per-interval confidence; low
  confidence intervals are drawn dashed.

### 3.10 Dance graph — the zouk state machine
- The **dance graph** is a static 3D state machine of zouk moves, scaffolded from the user's
  `Zouk1.json` (143 nodes, 181 directed links) and grown as dances are labelled (§10). It keeps the
  original 3D layout:
  - **Height = energy.** The bottom holds grounded, stationary states (stand, hug, isolated tilts and
    body rolls); moves grow out of that grounded state into travelling steps and open moves; the top
    holds the high-kinetic-energy moves (pirouettes, spins, downswing).
  - **Icons and colours** as in Zouk1.json: node shape (Cylinder, Hourglass, Diamond, Plus, Star, Tetra,
    Ball) and tone (blue, green, yellow, orange). Legend (confirmed by the user 2026-10-06): Cylinder =
    standing rest, Hourglass = holds / embraces / isolations, Diamond = steps and basics, Plus = open /
    hand-connection moves, Star = turns and spins, Tetra = head movements (howls) and dips, Ball =
    special wave moves; blue = connection/position states, green = moves, yellow and orange = advanced /
    high-commitment moves.
  - **Names in Brazilian Portuguese**: each move is labelled with its established Brazilian Portuguese
    term (researched, with sources), the user's original English name kept as an alias for search and
    narration. Where no Portuguese term is attested, the user's name stays. **Corredor** replaces
    "Lateral" (the follower walking a repeating line back and forth in front of the leader).
  - Directed links drawn as thin lines with arrowheads; move names as billboard labels (fade with distance).
  - Displayed **in the dance environment**, centred on the origin at true scale (the graph spans ≈ 4 × 4 m
    and ≈ 3 m high, bottom ≈ 0.2 m above the floor); in VR the user walks through it.
- **Path through the graph ("Dance graph" state):** the couple's motion is **isolated** — travel removed,
  body rotation and limb motion kept — and shown as a **miniature couple** (≈ 0.15 scale, skeletons +
  translucent avatars) standing at the node of the current move. When the move changes, the couple glides
  along the link to the next node over the transition time; a fading trail marks the path taken. The
  **camera follows the couple through the graph** (chase camera zoomed far out above and behind, looking ahead along the
  path). The current move name and the next move are shown as a caption.
  - Duplicate move names (e.g. "Lateral" appears twice) are different contexts: the path picks the node
    instance linked from the previous node; a transition with no link in the graph is drawn as a **dashed
    new link** (a candidate to add to the graph).
  - Moves not yet in the graph (e.g. Corredor) appear as **proposed nodes** placed at the height of their
    measured energy, outlined dashed until accepted.
- **Live move ribbon**: in all states (optional layer), a caption shows the current move; the time strip
  (§3.8) can show the move timeline as coloured segments.

### 3.11 Dance fingerprint — the time-independent graph
- The whole dance shown at once on the static graph as a **heat map of dwell time**: each node's glow and
  size by the seconds (and share of the dance) spent in that state; links thickened by how often each
  transition was taken; unvisited nodes dim; dashed new links and proposed nodes included.
- Side panel: total time per energy band (graph height), number of distinct moves, transitions, the
  share of time in counterbalance, and the longest phrase.
- This is the **dance fingerprint**: the same picture for two dances compares their content at a glance.
  It is exported as an image for the library card (§2.3) and is the closing shot of the directed tour —
  the **product** of the dance's reaction (§3.13).

### 3.12 Move caption and graph inset (directed playback)
- Throughout directed playback (desktop, renders and the VR presentation) a **move caption** shows the
  current move: the Brazilian Portuguese name, the English alias smaller beneath, and a **confidence
  score**. It changes at segment boundaries with a short cross-fade; the next move can be previewed in the
  last beat of a segment.
- **Confidence** is shown as a percentage with a small bar and is defined by the label's provenance
  (§10): human-confirmed labels show "labelled" instead of a number; narration-mapped labels show the
  mapping confidence; automatic labels show the classifier's **calibrated** probability (top alternatives
  on demand); placeholder timelines show "placeholder". Below a threshold (default 50 %) the caption reads
  "uncertain" with the top two candidates rather than a confident single name.
- A **graph inset** (the state machine, §3.10, as a small live minimap) sits beside the caption: the current
  node highlighted, the path so far as a fading trail, the candidate next moves glowing faintly. In the
  Dance graph and Fingerprint states the inset is hidden (the full graph is on screen).
- Layout: 16:9 — caption lower-left, inset lower-right; 9:16 — caption above the bottom safe area, inset
  below the top safe area (§8.3). VR — a small panel that follows the user's gaze at a comfortable
  distance, never in front of the dancers.

### 3.13 The dance as a reaction — "Leader + Follower × Song → fingerprint"
- Each dance is presented like a chemical reaction. The reactants are the two dancers, the song is what
  drives the reaction, and the product is the dance fingerprint (§3.11):
  **Kadu + Larissa × Ficar Sem Você → [fingerprint]**.
- Typeset like an equation: dancer names in their skeleton colours (lead red, follow white), "×" and the
  song in the accent colour, and **reaction conditions above the arrow** — level and category (e.g.
  "Professional · Demo").
- **Opening title card** of the directed tour shows the left-hand side with an empty product slot; the
  **closing fingerprint shot** completes the equation with the fingerprint as the product. The same label
  titles the library card and the rendered video file names.

---

## 4. View states

A view state is a named bundle of layer parameters + a camera behaviour. All parameters blend smoothly
(default 1.0 s, configurable) when switching states, and every parameter is animatable by the director (§6).

| State | Camera | Avatars | Skeletons | Overlays |
|---|---|---|---|---|
| **Orbit** (default after load) | slow continuous orbit around the couple (≈ 1 rev / 20 s, gentle height drift), centred on the couple | 0.3, textured | opaque, beat conduction on | floor + crosses, leader axis, recent footprints, counterbalance axis |
| **Camera tour** (desktop/render only) | flies between source-camera POVs, video fills the frame at each POV (§5.3) | 0.3 | opaque | active camera's video, other cameras as glyphs, alignment QA optional |
| **Overhead floor craft** | top-down (orthographic or narrow FOV), framed to the dance area | 0.2 | opaque | footprint history, coverage trails, leader axis, counterbalance pivots + arcs, step timing colours |
| **Geometry** | low 3/4 orbit, slower | 0.15 | opaque | leader axis, follower spirals + head axis, counterbalance axis, fitted circles optional |
| **Physics** | 3/4 side view, steady | 0.1 | load-coloured | GRF arrows, contact forces, counterbalance axis, time strip, legend |
| **Dance graph** | chase camera following the miniature couple along its path through the graph | 0.3 (miniature) | opaque (miniature) | graph, path trail, move caption; full-size dance and floor overlays faded out |
| **Fingerprint** | slow orbit of the whole graph | — | — | dwell-time heat map, transition weights, side panel |
| **Lesson** (existing) | user orbit | user | opaque | timing HUD, transport (beat/measure step, loop) |
| **Free** | user-controlled | user | opaque | any |

Every state also defines what is hidden (e.g. Physics hides footprints; Camera tour hides spirals).
In VR the Camera tour state does not exist, and Dance graph/Fingerprint are walk-around displays (no
camera moves).

---

## 5. Cameras

### 5.1 Virtual cameras
Built on **Cinemachine** (com.unity.cinemachine): an orbit camera, an overhead camera, a graph chase
camera, a free camera, and one camera per source phone. Transitions use Cinemachine blends (default
ease-in-out 1.2 s); cuts allowed. Desktop and renders only — in VR the user's head is the camera.

### 5.2 Orbit
Continuous motion by default ("the camera should generally be in motion"): orbit radius/height/speed
per state, target = couple centre (smoothed), never passing through a dancer.

### 5.3 Camera POV tour (multi-camera broadcast) — desktop/render only
- Each source phone camera follows its **per-frame pose and zoom** (field of view from the per-frame focal
  length; principal point as lens shift).
- Tour = fly from the orbit into camera A's POV, fade A's video in until the **full-frame video fills the
  screen** (§3.4), hold, fade it out to reveal the 3D avatars, fly to camera B, and so on — like switching
  cameras at a sports broadcast. Order defaults to walking around the room (angular order around the
  couple), skipping cameras without coverage.
- Hard cuts between cameras are also supported (broadcast style), with the video already faded in.
- Video time is locked to dance time through each camera's clock mapping (reference time → source time);
  the video never drifts from the avatars. Implementation: `VideoPlayer.timeReference = ExternalTime`
  with `externalReferenceTime` set from the dance clock every frame, `playbackSpeed` = dance speed, and
  explicit frame seeks when paused or stepping; in recordings the player follows game time so it stays
  frame-locked to the Recorder.

### 5.4 Overhead
Top-down view above the dance area; fits both dancers' full floor coverage; north = world +Z (fixed), with
an option to rotate to the leader's floor axis.

### 5.5 Graph chase
Follows the miniature couple through the dance graph (§3.10) **zoomed far out** (≈ 2.6–3.4 m behind and
1.1–1.5 m above the couple, so a whole neighbourhood of the graph is in frame), looking ahead to the next
node; the camera may sit a little **inside the graph** among the nodes; it pulls back further while the
couple dwells.

---

## 6. Directed sequence (auto-director)

- Each dance gets an **automated, scripted show** built on **Unity Timeline** (com.unity.timeline):
  camera tracks (Cinemachine shots), a view-state track (parameter blends), a playback-speed track, and an
  audio track.
- The script is **generated** from a JSON shot list (`direction.json`) with defaults derived from the
  dance (duration, beat grid, counterbalance intervals, move timeline) and can be edited and re-rendered.
- **Throughout**: the move caption with its confidence and the graph inset (§3.12).
- **Default script order** (measure-aligned cuts):
  0. **Title card** — the reaction "Leader + Follower × Song →" with the level and category above the
     arrow and an empty product slot (§3.13), over the opening orbit.
  1. **Orbit** — mostly transparent avatars, opaque coloured skeletons conducting the beat.
  2. **Camera tour** (desktop/renders) — pass through every source camera POV; each video fills the frame
     and fades in and out as the camera passes through it.
  3. **Overhead floor craft** — floor coverage, footprint patterns, leader floor axis, counterbalance
     pivots with the leader's circling arcs.
  4. **Geometry** — leader axis + follower spirals / head axis; counterbalance spirals emphasised.
  5. **Physics** — limb tension/compression, floor and contact forces through time.
  6. **Dance graph** — the couple travels through the zouk state machine, the camera following its path.
  7. **Fingerprint** — the time-independent heat map of the whole dance; the closing shot completes the
     reaction: the fingerprint lands in the product slot of "Leader + Follower × Song → [fingerprint]".
- The same script drives the desktop show and the rendered videos (§8). In VR the director changes view
  states and layers around the user (the user walks freely; the director never moves the user).
- Prototype stage: until Timeline lands, a lightweight sequencer (`hm_tour`) plays the same state list.

---

## 7. Playback and time

- Dance time is the master clock; frames are looked up by time (times.json), never by a constant fps.
- **Speeds**: continuous from slow motion up to real time, **0.1× – 1.0×**, presets 0.25×, 0.5×, 0.75×,
  1.0×; speed is animatable in the director (speed ramps into slow motion on key moments).
- Audio: at 1.0× normal; below 1.0× time-stretched without pitch change via an AudioMixer pitch-shifter
  setup, or muted below a configurable threshold (default 0.5×) where stretching degrades.
- The existing lesson transport (play/pause, beat/measure step, loop measure, seek) and CLI `hm_*`
  commands remain and gain `hm_state <name>`, `hm_camera <id>`, `hm_speed <x>`, `hm_graph`, `hm_tour`.

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
- Camera tour: source videos are always full-frame (§3.4) — filling a 9:16 frame, centred at full height
  in 16:9 with the 3D world continuing at the sides.
- Title-safe areas: top 14% / bottom 20% of 9:16 frames kept free of key action (platform UI overlays);
  the reaction title, move caption and graph inset (§3.12, §3.13) and any state labels sit inside the safe
  areas.
- File names: `<Leader>+<Follower>x<Song>_<preset>_<date>.mp4` (ASCII-folded).

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
  transparent). The dance plays life-size in the user's room and the **user walks freely** around and
  through it.
- **Placement**: the dance origin (§3.0) is anchored at the **centre of the user's room** on the real
  floor — the room's scene-model bounds when available, else the centre of the play area (boundary), else
  1.5 m in front of the user. The user can re-anchor (grab, move, rotate), but the default is the room
  centre. Scale is 1:1.
- All visualisation layers and view states are available **except the source-camera videos** (no camera
  tour, no video planes, no camera glyphs in VR). The dance graph and fingerprint are room-scale displays
  the user walks through.
- The director (§6) can run as a presentation: it switches view states and layers around the user; it
  never moves the user's viewpoint.

### 9.2 VR recording
- **POV only**: the user records what they see, on-device, via Quest system capture (includes
  passthrough). The app offers a "recording" UI state that hides menus and panels.

### 9.3 VR interaction
- Wrist/hand menu (palm-up) or controller panel: library, view states, speed, transport, layer toggles,
  re-anchor.
- Voice/natural-language commands through the existing TypeSafe router where available.

### 9.4 Quest 3 budgets (performance tier "Quest")
- 72 Hz minimum (90 Hz target), single-pass instanced stereo.
- Avatars: one skinned mesh + one 2048² texture per dancer; hair ≤ 200 guides × 16 segments simulated,
  rendered as one ribbon mesh.
- No video decoding (no source videos in VR).
- Dance graph: nodes GPU-instanced per icon shape, labels pooled; ≤ 300 nodes at full rate.
- No room reconstruction (the real room is visible in passthrough); dancer splats, when they exist, are off
  by default.
- Transparency: limit overdraw (depth pre-pass avatars, no full-screen transparent layers).

---

## 10. Dance moves — labels, graph path, fingerprint

The viewer consumes a **move timeline** per dance; how it is produced (automatic proposals + the user's
narrated labels, then a trained classifier) is specified in `dancecap/docs/MOVES.md`.

- **Move timeline** (`moves/labels.json`): segments `[t0, t1)` in dance time with the move id (graph
  node or proposed move), provenance (`human`, `narration`, `auto`, `placeholder`), confidence, and notes.
  Moves are **collective** — one timeline for the couple.
- **Graph** (`moves/graph.json`): the Zouk1 scaffold plus accepted additions; node ids are stable UUIDs
  from Zouk1.json, with name, aliases, shape, tone, position, and energy (height).
- **Path** (`moves/path.json`): the sequence of graph nodes and links the dance took (resolved node
  instances, transition times, new links flagged).
- **Fingerprint** (`moves/fingerprint.json`): dwell seconds per node, transition counts per link, energy
  band totals, counterbalance share.
- Until a dance has human labels, any displayed timeline is marked on screen: **"placeholder — not an
  analysis"** (provenance `placeholder`) or **"automatic — unreviewed"** (provenance `auto`).
- **Confidence per segment** (feeds the caption, §3.12): `human` → none shown ("labelled");
  `narration` → the phrase-to-move mapping confidence; `auto` → the classifier's calibrated probability,
  with `alternatives: [{move, p}]` (top 3) and the calibration record it came from; `placeholder` → none.
  Automatic labels come from the classifier plan in `dancecap/docs/TMR_SOMA_PLAN.md` (TMR-SOMA motion
  embeddings + our own small, per-silo classifier, decoded along the graph).

---

## 11. Viewer integration of the move workflow
- The viewer is a **display** of labels, not the annotation tool. Narration and labelling happen in the
  dancecap annotation tool (MOVES.md), which shares the same files; the viewer reloads labels on change.
- `hm_graph mode=path|fingerprint|off` and `hm_tour start|stop` expose the new states to the CLI.

---

## 12. Data contract from the pipeline (capture.json v4)

v4 extends v3 (times, SMPL-X motion + skins, timing, physics) with:

| Key | Content |
|---|---|
| `title`, `dancers`, `bpm`, `poster` | library card data |
| `dance` | `{leader, follower, song: {title, artist}, level: professional\|intermediate\|novice, category: demo\|lesson\|jack_and_jill, date, venue}`; `title` = "Leader + Follower × Song" |
| `layers` | flags for every layer of §2.1 + QA numbers (reprojection px, sync residual ms) |
| `origin` | the dance offset of §3.0 (computed by the exporter from the first frame, applied by the viewer) |
| `cameras/` | per source phone: `video.mp4` (rotation baked, trimmed to the take, re-encoded at a **constant** 30 fps from the phone's variable-rate PTS, full resolution for desktop/recording; not packaged for Quest builds), `track.json` (per frame: reference time, Unity-space position + rotation, vertical FOV, principal point, k1), `clock` (reference → video time mapping) |
| `textures/`, `hair_groom.json` | avatar albedo per dancer, hair parameters |
| `physics.json` v2 | per frame: COM, GRF per foot (+ bands), **per-segment axial load estimates** (tension/compression, + band, identifiable flag), contact forces per contact (type, points, force or interval, identifiable flag) |
| `counterbalance.json` | intervals: t0, t1, pivot point (follower's anchored foot), combined-COM track, leader circling radius and swept angle, connection, confidence + per-criterion scores |
| `timing.json` | beats, touchdowns, accents (with reliability), asynchrony stats |
| `moves/` | `labels.json`, `graph.json`, `path.json`, `fingerprint.json` (§10) |
| `direction.json` | optional authored shot list; generated if absent |
| `room/` | not displayed; kept as an alignment reference only |

Derived in the viewer (no export needed): leader floor axis, follower spirals and head axis, footprint
history, coverage, the miniature isolated couple for the graph path.

---

## 13. Implementation status (2026-10-06)

| Area | Exists today | New work |
|---|---|---|
| Playback core | capture v3 loader, times-driven clock, beat grid, lesson transport, speeds, `hm_*` CLI, TypeSafe router | v4 manifest, origin offset, library scan, speed track |
| Avatars | skinned SMPL-X avatars (SmplxAvatar), hair prototype | depth-prepass translucent material, texture/hair layers, opacity per state |
| Skeletons | glowing spline skeletons (Dancer.cs) | lead red / follow white, beat-conduction pulse, accent flashes |
| Floor | FloorPatterns, TimingOverlay footprints | black translucent plane + teal 1 m crosses, fading history, coverage trails, counterbalance pivots |
| Floor craft / geometry | — | leader floor axis, follower spiral traces + head axis, circle fits |
| Counterbalance | — | pipeline detector, yellow COM axis, pivot ring + leader arc |
| Dance graph | Zouk1.json (user's graph) | graph import, path + fingerprint views, chase camera, labels/proposals pipeline, annotation tool |
| Physics | PhysicsOverlay (L0: COM, XCoM, couple GRF, impulses) | per-limb axial loads (needs a pipeline inverse-dynamics stage), contact-force connectors, time strip, uncertainty display |
| Cameras | VRTKLite orbit, VirtualCameraRig | Cinemachine rigs, per-phone 6DoF + zoom tracks, full-frame video planes, tour, cuts |
| Director | — | Timeline + `direction.json` generator (prototype sequencer first) |
| Recording | — | Unity Recorder presets, `hm_render`, output checks |
| VR | OpenXR + Meta OpenXR packages installed | passthrough MR scene, room-centre anchoring, hand menu |

Packages to add: `com.unity.cinemachine` (3.x), `com.unity.timeline`, `com.unity.recorder`,
`com.unity.xr.arfoundation` (passthrough + planes, if not already pulled in by Meta OpenXR).

Pipeline work this spec implies (dancecap): capture v4 export (origin, moves, counterbalance), per-camera
video export (rotation, trim, CFR) with Unity-space tracks, stretched-audio stems, physics v2 (segment
inverse dynamics with identifiability flags, contact-force intervals), counterbalance detector, moves
pipeline (MOVES.md).

---

## 14. Acceptance tests (CLI playtests)

- Library lists every completed capture; incomplete ones show what's missing.
- Each view state switch reaches its target parameters (hm_state + hm_status).
- Origin: at the first frame the couple centre is within 1 cm of (0, 0) on the floor in every mode;
  dancers, cameras and room keep their relative alignment (camera reprojection unchanged).
- Camera tour (desktop): at each POV the video fills a 9:16 frame edge to edge (portrait phones) and is
  centred at full height in 16:9; skeleton reprojection into the video ≤ the capture's QA reprojection
  error + 2 px.
- VR build contains no video players or source videos.
- Counterbalance: on the synthetic pivot test the yellow axis appears within ±1 frame of the ground-truth
  interval and the dot sits under the combined COM (≤ 2 cm); pivot ring at the anchored foot (≤ 3 cm).
- Graph path: every label segment maps to one node; the miniature couple reaches each node by the
  segment start; new links are dashed; fingerprint dwell seconds sum to the labelled duration.
- Caption: at every segment boundary the caption shows the new move within 1 frame; the confidence shown
  matches the label's provenance rule (§10); below the threshold it reads "uncertain" with two candidates.
- Reaction title: the opening card and the closing fingerprint read "Leader + Follower × Song", with level
  and category above the arrow; the library shelf matches the dance's level.
- Leader axis long side within 3° of the shoulder line projection, short axis perpendicular.
- Recorder: both aspect presets produce MP4s with exact resolution, fps, duration and an audio track.
- Speeds 0.1×–1.0× keep avatars, video and beats in sync (frame for time).
- Quest build: 72 Hz sustained in each state with the full layer set on the reference capture.

---

## 15. Decisions and open questions

Decided by the user (2026-10-06):
- Source videos: full-frame, desktop and renders only; never in VR.
- VR: POV recording only; the user walks freely; the dance is anchored at the room centre.
- Every mode starts the dance at the origin and lets it travel from there.
- Counterbalance: yellow floor dot + vertical axis to the shared COM; pivot + leader-circle indicator in
  the floor craft; follower spirals emphasised.
- The zouk graph (Zouk1.json) is the scaffold; the tour shows the path through it and the fingerprint.
- Graph icon/colour legend confirmed (§3.10).
- Move names use established Brazilian Portuguese terminology; "Lateral" is renamed **Corredor**; "cicada" was
  **Sarrada**; Little Turn is the **Viradinha**.
- Directed playback shows the move caption with a confidence score and the graph inset (§3.12).
- Each dance is a reaction "Leader + Follower × Song" producing its fingerprint (§3.13).
- Splats show the dancers only (no floor or room), off by default; the room reconstruction is not shown.
- Hair matches Larissa's shape and colour and keeps the tip bloom; faces from portraits; expressions and hand
  contact refined (§3.2).
- Graph chase camera zoomed far out, may be slightly inside the graph (§5.5).
- Data siloed by level (Professional / Intermediate / Novice; only Professional so far); categories Demo,
  Lesson, Jack and Jill (§2.2).

Defaults (change any of these):
- Avatar opacity 0.3 (Physics 0.1); floor alpha 0.6 (0.25 in passthrough); teal #19C3D6.
- Trace windows: footprints 4 measures, spirals 2 measures.
- Slow-motion audio muted below 0.5×.
- Default render preset: Vertical HD 1080×1920 @ 30 fps.
- Miniature couple scale 0.15 in the graph; graph at true scale centred on the origin.

Open:
1. Brand/captions on rendered videos (title card, dancer names, watermark)?
