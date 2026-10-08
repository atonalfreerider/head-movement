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
  a demo take titled with its two dancers' names and the song (the real values are not published in this repository).
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
  (§9.3). Natural-language commands ("open the demo take") use the existing TypeSafe router.

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
- **1 m grid marked with small, faint teal-blue crosses** at every grid intersection (user, 2026-10-07: "the floor
  crosses need to be smaller and fainter": cross ≈ 8 cm across, line ≈ 5 mm, ≈ 45 % of the earlier brightness,
  colour light teal ≈ #19C3D6 lightened). No continuous grid lines. The grid is aligned to the floor (y = 0) and the
  origin (§3.0); it is not re-oriented by the dancers. The crosses mark the floor plane; they must not compete with
  the leader's T and the floor record (§3.5 / §3.6).
- In passthrough MR the plane alpha drops (≈ 0.25) so the real floor remains visible; crosses stay.
- Implemented (2026-10-07, user: "we still need to see light teal crosses denoting the floor plane"; smaller and fainter
  the same evening): Floor/FloorGrid.cs, layer `grid` (on by default, independent of the footprint layer and of every
  view state): light teal crosses (#19C3D6 lightened, ~(0.30, 0.86, 0.92) × `Brightness` 0.45) 8 cm across with 5 mm
  lines (were 14 cm / 7 mm at full brightness) at every 1 m intersection over the dance area + 2 m (at least ±4 m),
  one static additive glow mesh; still readable from the default orbit and the overhead state. The black plane
  (Resources/HM_FloorPlane, alpha 0.6, `hm_floor --plane`) is drawn before every other floor item. hm_state `grid`
  reports `crossArmM`, `crossWidthM`, `crossBrightness`.

### 3.2 Avatars
- **Default: 65 % transparent as displayed** (user, 2026-10-07), photoreal-textured when the texture layer exists,
  otherwise a neutral shaded material (lead warm grey, follow cool grey). Depth-correct transparency (depth
  pre-pass) so a translucent body doesn't show its own back faces.
  - "Transparent" is measured on the screen, not in the shader: the share of a background change that comes through
    one body layer (black vs grey background, the Game-view pipeline with post-processing; `hm_opacity --probe`). The
    shader blends in linear light and the display is sRGB, so one alpha looks very different on a bright and a dark
    body: alpha 0.35 showed the follower (a bright body) **48 %** and the leader (a dark body) **74 %** transparent.
  - So the avatar **opacity is the displayed opacity** (0.35 = 65 % transparent on screen; `hm_opacity --value`, view
    states scale it) and the colour pass looks its alpha up **per pixel** from the shaded colour's luminance
    (Avatar/DisplayTransparency.cs AlphaLut: the alpha at which that colour shows the opacity; the model reproduces the
    measured curves within 0.002): every part of a body - bright, mid-tone or dark - shows the same transparency.
    Measured at the default (2026-10-07, a demo take): follower 65.6 % (p10..p90 65-66 %), leader 64.7 %
    (64-67 %). `hm_opacity --mode body` (one alpha per body: bright parts more solid) and `--mode raw` (alpha =
    opacity, the old look) remain for A/B.
- **Each dancer shows through the other.** The two bodies are sorted back to front per camera, each with its own
  depth pre-pass + colour pass (the nearer dancer's translucent materials, with its shoes and hair, draw after the
  whole farther dancer). Where they overlap you see the nearer body over the farther one over the skeletons. A body
  part of the nearer dancer that is actually behind the farther one is hidden there (the usual per-object sorting
  trade-off); the order only swaps when one dancer is 5 cm nearer than the other (hysteresis), so popping is limited
  to the overlap region at that moment.
- Opacity is a per-state parameter (§4): 0.35 displayed default (65 % transparent on screen), × 2/3 Overhead,
  × 1/2 Geometry, × 1/3 Physics, up to 1.0 when requested.
- Hair (follow) inherits the avatar's opacity, slightly higher (+0.15: hair alpha 0.5 at the default; the hair
  measures 61-64 % transparent on screen, a little more solid than the 65 % body) so the hair motion reads. It is
  **styled like her portrait**, built the way games build realistic, performant hair
  (layered cards/strands, anisotropic shading, alpha-to-coverage), with a little stylisation at the tips. It
  must **match the follower's real hair in shape and colour** (length, volume, parting, layering and colour with its
  root-to-tip variation, from the video and her portrait) and **keep the glowing tip bloom** of the original
  head-movement hair: strand tips emit a soft bloom that traces the hair's whip through space.
  **Styling comes from the take's groom file, not from this repository** (user, 2026-10-07 evening: the front section
  can be brought forward over the forehead again, after an earlier "pulled back more"): the groom (part, sweep, drape of
  the front and side sections) is private per-take data read at run time; the viewer only has to support a front section
  swept across the upper forehead (the face box keeps the eyes clear) with the rest sleek and back off the face. Against
  the stray dark "blade" across the neck / collarbone (strands slipping through the trapezius and folding in
  front of the collarbone): the hair's shoulder-bar collider is sized at the 65th percentile of the mesh distance (was
  the 35th) and the inner card layer is shorter (0.86-0.90 of the strand) and lighter than the outer layers.
- **Faces**: photoreal faces from the dancers' portrait photos (identity shape + frontal face texture),
  blended with the video-baked texture, plus per-frame **facial expressions** (jaw + expression
  coefficients) estimated from the closest cameras where the face is visible enough; neutral otherwise.
- **Shoes** (sized 2026-10-07: "visibly smaller, plausible for the stature: foot ≈ 15 % of height, shoe ≈ foot + 1-2
  cm"): the last's margins over the measured foot are toe 7 mm, heel 4 mm, sides 3 mm (+1 mm toe on platforms; sole
  flare ~1 mm less than before), i.e. the sneaker is the foot + ~1.2 cm long; the dancecap shape edits narrow and lower
  the SMPL-X feet (foot_scale [length, width, height]; the length stays at the fitted ~14-15 % of stature).
- **Shoes**: the feet look like the dancers' sneakers — procedural sneaker meshes on the SMPL-X feet (bare
  feet hidden), colours from the video, soles on the floor, following the avatar's opacity.
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
  - The follower's spine (pelvis -> spine1 -> spine2 -> spine3 -> neck) is a **chain of small white beads**, not a line
    (user 2026-10-07; Overlays/SpineBeads.cs): 1.26 cm spheres every 2.7 cm along the same Catmull-Rom curve (user
    2026-10-08: smaller dots, was 2.1 cm every 3.5 cm; 18 beads on the 38 s demo; at the default 2.9 m orbit ~2.7 px
    beads with ~3 px gaps at 720p, ~9 px beads at 0.9 m), each
    coloured like the line's gradient at its place (the rhythm pulse climbs bead by bead, physics mode shows the trunk
    load), exactly as bright as the line (linear line colour x the line graph's 3.1 HDR gain), opaque in the skeleton
    queue (the translucent body blends over it like over the lines). One GPU-instanced draw per camera
    (Graphics.RenderMeshInstanced, no per-frame allocation); the beads show and fade with her other skeleton lines;
    `hm_spine` reports and tunes them (Tools/playtest_spine.ps1).
  - Known deviation (2026-10-07): the skeletons are drawn first and the translucent body is blended over them, so at
    opacity 0.35 a skeleton keeps about 65 % of its brightness and takes on some body colour (the follow's white
    skeleton inside a bright body is low-contrast). Fix when it matters: an additive skeleton top-up after the
    bodies, depth-tested against the opaque-only depth (URP depth texture) so it shows only inside its own body.
  - Option, off by default (integration review 2026-10-07): `hm_opacity --skeletons over` makes each translucent
    body skip its own skeleton's pixels (a stencil bit per dancer, written by a second material on the skeleton
    lines; the partner's body in front still blends over it; off at opacity 1). The skeleton then shows in its raw
    colour: the lead's red is crisper, but between beats the follow's "white" skeleton is a mid-grey line
    (LineRenderer colour 0.2 sRGB x the bloom material's 6.4), which reads as grey stripes on a bright body, so
    the default stays the tinted look. Making the skeletons really bright needs brighter base colours (a design
    call), not only a sorting change.
- **Beat conduction (RHYTHM mode, the default view):** on each beat a pulse of light travels through the skeleton. It
  starts where the body meets the floor (the stance foot/feet of that frame: foot near the dancer's floor level and
  slow), travels up the legs through the pelvis and spine, and out through the arms and head - the swing leg lights
  last, the pulse running down it from the hip - over TravelSeconds ≈ 0.18 s (arrival = beat + 0.18 s × the joint's
  distance along the skeleton from the stance foot / the foot-to-head distance; sharp 25 ms rise, 0.14 s decay).
  Strength by beat type: measure downbeat 1, the other zouk "1" 0.85, the accent eighths 0.6, the remaining eighths 0
  (Overlays/SkeletonStyle.cs). Per dancer and body part (legs / trunk / arms / head) the pulse is modulated by that
  dancer's own kinematic accent: the nearest |jerk| peak of the part (timing.json spline jerk, reliable joints only)
  within ±0.2 s of the beat gives a gain lerp(0.45, 1.6, exp(-(dt / 70 ms)²)) - on the beat it flares, without an
  accent it is dim - and the part's pulse arrives dt later (a late part lags visibly, an early one leads). Beats come
  from timing.json's grid, else from the capture's beat grid. Without timing.json jerk the accents come from the
  skeleton's own |jerk| (central differences of the exported joints on the real frame times, zero-phase Gaussian σ 1.5
  frames before peak picking; review 2026-10-07: the 38 s demo had no timing.json and its pulses were unmodulated);
  `hm_skeleton` reports the source (`accentSource`, per dancer too), the pulse per joint and each part's accent hit
  rate / mean lag.
- The jerk-peak flare replaces separate accent markers.

### 3.4 Source-camera video (desktop and rendered video only — never in VR)
- Each source phone is a **camera rig** in the scene, driven per frame by its tracked 6DoF pose **and
  zoom** (intrinsics per frame from the camera track). Its **original MP4** is shown **full-frame** —
  the whole frame, never cropped to the subject. Lens distortion is corrected in the exported video (or
  with the k1 term in the shader).
- **At the camera's POV the video fills the screen.** The render camera takes the phone's per-frame
  vertical field of view and principal point, so the video frame maps exactly onto the view:
  - **9:16 output**: the phone videos are portrait (all seven phones of the demo take are), so the video
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
- **Footprints** for the detected steps, filled by timing (on-beat green, near amber, off magenta — no red, which
  belongs to the lead). **Not all of them by default** (user, 2026-10-07): mode `recent` (default) shows a print only
  from its touchdown until it has faded out completely ~1.2 s later (about three eighths: the step-timing feedback stays
  at the feet, the long-term floor record is the leader-axis record below); `all` (`hm_floor --footprints all`, T key)
  is the old full history (upcoming faint, past dim, joined by a thin trail per dancer); `off`.
- **Floor record of the leader's T axis** (user, 2026-10-07: "every old position can fade to the teal blue color and be
  very faded ... a dotted line that tracks the transitions from one axis point to another ... any kind of repeated turn
  ... a dial that indicates the amount of degrees turned in that pivot point position"; layer `floorcraft`,
  Floor/FloorCraftOverlay.cs, data `floorcraft.json` version 3 from dancecap.floorcraft, §12). It accumulates to the
  end of the take; items appear when they happen and nothing from the future is drawn:
  - **Old Ts** — when a T (§3.6) ends it fades within ~1 s from the lead's red-orange to a **very faint teal-blue**
    (#19C3D6 at 5 % brightness, thinner lines, smaller arrowhead) and stays where it was as the record.
  - **Transitions** — a **dotted** straight line from each T origin to the next one, in time order (it grows from the
    old origin to the new one over the gap between the two Ts; skipped when the new T is at the same place). Brighter
    (~55 %) while recent, settling over ~2 measures to a faint permanent level (~20 %).
  - **Axis pivots** — every reorientation of his axis by more than 30°: between consecutive Ts (through the heading of
    any brief T dropped as a flash, so a turn out and back while no T holds is two pivots), **before the first T**, and
    **into and out of a counterbalance** (the counterbalance dial covers only the circling inside the pivot; review
    2026-10-07: ~700° of turning around it and before the first T was recorded nowhere). A small arc at its place (the
    old T origin when he stayed, else his chest at mid-turn) from the old to the new heading, growing over the turn,
    with an arrowhead and the angle; lead colour while it happens, then faint teal. The angle label shows only while
    recent (~2 measures); then the arc fades on to the faint teal record level of the old Ts (5-6 %), without words.
    Arcs at one spot step outward.
  - **Dials** — every **repeated turn about one point** (the §3.9 counterbalance pivot; a spin of the leader about his
    own chest; a couple turn about their combined COM; consecutive same-direction pivots adding up to ≥ 180° while one
    point stays within 0.30 m - and, for a couple turn or a turn around her, only when he really goes round that point:
    his orbit about it turns the same way by ≥ 60 % of the degrees; review 2026-10-07: his own −286° spin under the
    arms while the couple swapped places +132° the other way is no couple turn. A chain that fails as a whole is tried
    in its parts. The turning into / out of a counterbalance is never a separate dial): a gauge at the turning point - a faint face ring (radius = the leader's distance from
    the centre) with quarter-turn ticks from the start angle, a fill arc that sweeps the **cumulative degrees turned**
    (spiralling outward ~3.5 cm per full turn so every turn stays visible), a needle while it runs, an arrowhead in the
    turning direction, and the running read-out (e.g. "701°") hanging just below the gauge on screen (her feet stand on
    the centre of a counterbalance dial). The value follows the motion (the leader's angle about the centre, or his
    heading for spins / couple turns), scaled so the end reads the analysed total. Counterbalance dials are
    counterbalance yellow with the yellow pivot ring at her anchored foot at the centre and a tick per beat; other
    dials amber. After the turn the dial settles to a faint level and, ~2 measures after its end, fades to the same
    faint teal record as the old Ts; only the **counterbalance dial keeps its degree label** permanently (the others
    lose theirs). A turn while travelling (no point stays put) gets no dial, only its axis pivots.
  - In **PHYSICS mode** (§3.8) each **graded** turn shows its **balance verdict** - good / ok / needs work - as text
    under the degrees while recent plus a shape that stays (dial rim solid / dashed / dotted; pivot icon dot / ring /
    x), colour-coded subtly (green / amber / rose), never colour alone. Graded = every dial (its verdict appears at the
    dial's end: it judges the whole turn) and every axis pivot made **on one foot**. A turn **on both feet** or a
    **stepped** turn (the weight changed feet / the foot travelled) is not a foot pivot, so it gets no verdict and no
    icon: while fresh its label names how it was turned ("stepped", "both feet") instead.
  The v2 visuals (plants as small axis crosses, moves as path strips, rotation arcs) are **retired** (2026-10-07 late):
  the stable T, its record and the dials replace them; floorcraft.json still carries those keys for the pipeline
  review. Pipeline review: `work/<take>/review/floorcraft2/taxis_map.png` + `taxis_timeline.png` (dancecap.floorcraft).
- **Coverage**: an overhead heat-trail of where each dancer's feet have been (lead red, follow white),
  accumulated from the start of the take or from the current loop.
- **Counterbalance pivot marker** (see §3.9): wherever the couple performed a counterbalance pivot, the floor craft keeps
  its **dial** (above): the **yellow pivot ring** at the follower's anchored foot at the centre, the leader's circling as
  the dial's fill arc with the cumulative degrees (e.g. "701°") and a tick per beat on the face. It is part of the
  record (it settles to the faint permanent level, never vanishes).

### 3.6 Leader T axis (dancer frame, not room frame) — stable, not live
User, 2026-10-07: "we want his chest axis not to move constantly with his chest ... That arrow T should find a stable
position when the leader is occupying a certain part of the dance floor. There should be an averaged position that
represents the parallel vector and the normal vector to the leader's chest orientation, and that defines the local
axis ... The leader is allowed to walk forward and backward on that T and the T can stay in place ... The T has to
become a new T if the leader travels outside of the standard back and forth or side to side axis, or if the axis is
rotated."
- **The T** (layer `axis`, on by default): a **crossbar parallel to the leader's chest** (his shoulder / chest line
  projected to the floor, ≈ 1.0 m) and an **arrow stem along the normal to his chest** (his forward, ≈ 0.8 m, with an
  arrowhead), in the lead's red-orange, lying flat on the floor. Its origin and heading are **one averaged value per T
  episode**: the median chest-on-floor and the mean heading over **all the frames he spends on that T** (inside its
  region, heading within 30°), not just its first beat (review 2026-10-07: the first beat left the long Ts 0.28-0.33 m
  from where he danced on them; now 0.05-0.07 m). The value is **fixed**: the T does **not** follow his chest frame by
  frame.
- **Staying**: the T stays put while he dances on it - walking forward and back along the stem, or side to side along
  the crossbar: the allowed region is plus-shaped, a corridor ±0.30 m wide and ±1.2 m long along each axis through the
  origin. A single step out and back does not end it.
- **A new T** starts when he leaves that region for ≥ 1 beat (**exit**), or when his heading stays more than **30°**
  from the T's heading for ≥ 1 beat (**rotation** = an axis pivot, §3.5). A new T forms only once he is settled (one
  beat in which every frame is in the new T's own region, every heading within 20° of its mean and the drift across
  both axes ≤ 0.20 m/s - not while still arriving diagonally); between Ts (travelling / turning) there may be no current
  T. A rotated T within 0.25 m of the previous one is the same place: it **turns in place** from the old heading to the
  new one. No T forms inside a counterbalance pivot (the counterbalance dial owns that motion, §3.9). **No flashing
  Ts**: a T that would hold for less than ~1 beat after forming (he starts turning / leaving right after it formed) is
  dropped - it would only grow in and hand over at once; its heading stays a waypoint of the axis pivots (§3.5).
- **Drawing**: the current T grows in (scale ½ → 1, brightness 0 → 1) over its forming window and then holds; when it
  ends it hands over smoothly - it fades within ~1 s to the faint teal-blue record (§3.5) while the next T grows in.
- Pipeline definition (`floorcraft.json` `t_axes` / `transitions` / `axis_pivots` / `dials`, dancecap.floorcraft
  version 3, analysed on the motion the avatars play): the chest frame = spine3's global rotation (+x = his chest's
  left-right line) projected to the floor, forward = its floor perpendicular on the chest's front (a forward lean
  neither shortens nor flips it); the 15 Hz smoothed chest track (`track`) remains for spins. Yaw = angle of forward
  from +x towards world −z (= Unity +z), counter-clockwise seen from above positive, the same number in the world and
  Unity copies.
- Implemented (2026-10-07 late, FloorCraftOverlay; review fixes the same night, floorcraft.json revision 3.1): on the
  38 s demo capture 8 Ts cover 57 % of the take (every one holds ≥ 0.9 s after forming; 3 brief ones dropped), with 7
  transitions, 11 axis pivots (2 before the first T, 2 into and 1 out of the counterbalance) and 2 dials (the leader's
  −270° spin at the start, the −701° counterbalance); every T there ends by a rotation (the couple turns almost
  continuously).
  Captures without a version-3 floorcraft.json fall back to the **live** chest axis (2 m chest line + 0.8 m forward
  arrow, lightly smoothed). hm_state `floorCraft` reports the current T (id, origin, heading, grow-in alpha), the
  leader's chest relative to it (`chestInT`: along the stem / crossbar, `chestInRegion`), the old Ts / transitions /
  pivots / dials shown and the running dial (`dial`: degrees, read-out, verdict).
- The follower has **no** floor axis.
- The T lies flat on the floor; nothing rises from the leader in the default view (user, 2026-10-07: the leader never
  gets a gold up arrow axis - see §3.7 / §3.8 / §3.9; the physics-mode balance axis of §3.8 is a chest-to-foot line,
  not an up arrow).

### 3.7 Follower geometry — spirals and the head axis
- 3D **traces** of the follower's hands (wrists), feet (ankles) and head - **only for an extremity that is free**
  (user, 2026-10-07: "holding hands or feet walking on ground don't count"): a hand attached to the leader (within
  12 cm of his hands, arms, shoulders, chest, back, neck or head; released beyond 17 cm; free runs shorter than 0.1 s
  ignored) draws nothing. A foot trails **only in a leg gesture, never in a step** (review 2026-10-07: every swing
  phase of a normal step drew a trail): foot height = the lower of the foot joint and the ankle − 6 cm above her floor
  level; a candidate run is off the floor band (> 4 cm: a foot sliding or pivoting at floor height is on the floor)
  and not in stance, and it is drawn only if it peaks at ≥ 15 cm, or lasts ≥ 0.6 s and peaks at ≥ 12 cm, or is the
  free (non-pivot) leg of a counterbalance and peaks at ≥ 8 cm - on at the gesture height, off back at the floor band,
  the whole lift-off..landing kept. On the 38 s demo (04, refined motion) that leaves 1 run on the left foot and 2 on
  the right (the counterbalance whip among them; trails on 1.7 % / 14.4 % of the frames instead of 33 % / 39 % with the
  old stance rule); an ordinary step (median peak ~6 cm, < 0.6 s) never trails. The head trace shows only while her neck axis is at **full** strength
  (≥ 35° off her neutral) so it does not double the fading-in neck axis. **Very brief**: ~0.35 s behind the
  extremity, bright at it and fading to nothing at its tail; when the limb attaches the rest fades out within the same
  0.35 s. Layer `traces`, on by default. They reveal the whip / spiral of a free limb (the swinging leg and free hand
  of a pivot). hm_state `traces` lists every drawn foot run with its peak height and duration.
- **Neck axis** (layer `neck`, on by default in every state with the full-size dance; user, 2026-10-07): the follower
  gets a **white** axis from her **neck** (SMPL-X neck joint, the base of the head) along her head's up direction
  **only while her neck is off-axis by more than 15°**. Off-axis angle = the angle between her head's up axis
  (global rotation of the SMPL-X head joint) and her torso's up axis (global rotation of spine3, the chest), both
  smoothed by a zero-phase Gaussian (σ 0.1 s): the swing of neck + head relative to the chest, turning the head
  left/right does not count. **Calibrated** (default reference `neutral`, 2026-10-07): measured from HER neutral
  head-on-chest alignment, the mean of the relative head axis over calm frames (the 40 % of frames where it moves
  slowest, refined once within 25°), so her natural carriage (a fixed offset from the rest pose) reads as
  0 and the axis marks real head movements: on 04 it shows on 39.5 % of the frames (51 % against the chest's rest
  axis, `--reference torso`; median angle 12.0° vs 15.1°). `hm_neckaxis --reference vertical` measures against world
  up.
  Above 15° it **fades in and grows**: alpha = length / 0.5 m = smoothstep((angle − 15°) / (35° − 15°)); it fades out
  and shrinks the same way. `hm_neckaxis` reports angle, alpha and length (and sets threshold / full / length).
  The always-on 0.6 m head axis with its tip trace is gone. **The leader never has an up axis** (no F_net arrow,
  no plumb line, §3.8; the counterbalance marks the couple's COM on the floor only, §3.9).
- During a counterbalance (§3.9) the follower's free extremity (the swinging foot/leg and the free hand)
  traces are emphasised (brighter, wider; same brief window) — the spiral the pivot produces.
- Optional analytic overlay: fitted circles/helices for the current trace segment with their radius and
  period (to read the geometry as numbers).

### 3.8 Physics visualisation
- **Very translucent avatars** (alpha ≈ 0.1); skeleton limbs are recoloured by **internal axial load**:
  tension (orange) ↔ neutral (grey-white) ↔ compression (blue), brighter with magnitude. One shared colour
  scale for both dancers so tension and compression read as one unified system across the couple.
  **PHYSICS mode** = the physics layer / view state (P key, `hm_skeleton --mode physics`); everywhere else the skeletons
  are in RHYTHM mode (§3.3). Implemented estimate (SkeletonStyle, 1 = body weight / a full connection): legs =
  −(vertical support factor) × that leg's share (support = F_net,y / mg from physics.json, else 1 + a_y/g of the
  skeleton's torso COM; two stance feet share by the lever rule about the COM's floor point; a swing leg is neutral);
  arms = the partner-connection estimate (PartnerConnection spring-damper-inertia signal, + tension) on the arm whose
  hand holds the partner; trunk = −0.55 × support (upper-body weight), neck / head 0.4 of it. A legend box reads
  "Skeleton load - ESTIMATED from motion"; on desktop it sits top right **under** the tour title (it never covers
  "from motion"), with its bar drawn in the colours as the screen shows them. In VR the legend becomes a small
  world-space panel next to the couple (OnGUI does not reach the headset), keeping the word "ESTIMATED".
  - **Orange on screen, not gold** (review 2026-10-07: tension showed at hue ~49°, the counterbalance markers'
    yellow): the skeleton material multiplies the line colour (Color32, LDR) by ~6.4 in HDR and bloom adds to it, so
    any green in "orange" saturates towards yellow. The bases are deep - tension (1, 0.22, 0.02), compression
    (0.08, 0.28, 1) - and the magnitude only dims the colour (0.45..1 of the base, never above 1 in any channel: no
    clipped red), so the hue on screen stays orange / blue at every load.
- **Floor forces**: ground reaction arrows at each stance foot (and the couple total), scaled per body
  weight, with centre-of-pressure dots.
- **No up-arrow axis through a dancer**: the net external force F_net = m(a − g) of each body is shown as a number
  (HUD) only. Its arrow from the COM (mostly straight up: body weight) and the COM plumb line read as a gold up axis
  on the leader and a white one on the follower and were removed (user, 2026-10-07); the COM marker, XCoM and support
  polygon stay.
- **Balance axes** (user, 2026-10-07: "it's nice to see when the leader's or the follower's body weight, center of
  gravity, is directly above where they are stepping ... an axis line that lights up from their chest center to their
  grounded foot ... the more vertical that axis, the better"; layer `balance`, on by default, drawn **only in PHYSICS
  mode**; Overlays/BalanceAxisOverlay.cs, data `floorcraft.json` `balance`): per dancer, while a foot is grounded, a
  line from the **chest centre** (spine3 as the skeleton shows it) down to the **support point** of the stance foot (the
  point of that foot's heel-toe line nearest the COM), with a small ring at the foot and a dot at the chest. It
  **lights up** with the alignment score - 1 when the COM is within 4 cm of the foot and the chest-foot line within 3°
  of vertical, 0 at 12 cm / 9° (the lower of the two ramps): dim neutral grey at 0 (brightness 0.22), bright green at 1,
  a little wider; no line while no foot is grounded. Brightness = 0.22 + 0.78 × score (monotonic). Below the aligned
  score (0.5) the line is **dashed** and the ring at the foot stays visible (review 2026-10-07: a dim solid line along
  the stance leg, which physics mode colours, read like no axis at all), so "weight off the foot" and "no grounded
  foot" look different. The weight-bearing foot is the foot in contact nearest the COM's floor point; contact from
  joint heights and speeds.
- **Counterbalance axis** (user: "the center of gravity directly over the top of the follower's foot that is being
  orbited around"): inside a counterbalance pivot, in PHYSICS mode, the couple axis from the follower's **anchored
  (orbited) foot** up to the couple's **combined COM**, lighting up the same way when the combined COM is directly above
  her foot. (In the demo take the couple's COM sits 1-4 cm over her foot while the pivot is established, 15-27 cm while
  entering / leaving, and her own COM hangs 25-32 cm out: her own axis stays dim while the couple axis is bright.)
- **Turn evaluation** (user: "every turn could have an evaluation of if it was a good turn or a bad turn on that basis
  ... so there's no tension away from the axis alignment of the pivot point of the foot"): a verdict from the share of
  the turn's frames with the axis aligned (score ≥ 0.5, ≈ COM within 8 cm of the sole line and tilt ≤ 6°): **good**
  ≥ 60 %, **ok** ≥ 35 %, else **needs work**. It is given to every **dial** (for a couple turn the worse of the two
  dancers; for a counterbalance the couple's COM over her anchored foot) and to every axis pivot made **on one foot**
  (one foot in contact ≥ 80 % of the turn, the weight on it, its ball within 10 cm: a pivot; judged over the frames with
  the weight on that foot). The question is whether the axis is over the pivot point of the foot, so a turn made
  **on both feet** (both in contact ≥ 70 % of it) or a **stepped** turn (the weight changed feet / the foot travelled)
  is **not graded** (review 2026-10-07: those read "needs work" only because the COM was between the feet); its kind
  is named instead. Shown in PHYSICS mode (§3.5). All of it is an **estimate from the fitted bodies** (SMPL-X COM; foot
  contact from the motion).
- **No extra lines for tension and pressure** (user, 2026-10-07): the partner tension / compression connectors
  (PartnerConnection lines, the counterbalance's taut hand line) are not drawn; the connection's estimate colours the
  holding arms of the skeletons in physics mode, and the HUD lists it (marked "estimated") only in physics mode.
- **Time focus**: a scrolling strip (bottom of frame) with the selected quantities over time (e.g.
  vertical GRF per dancer, total tension, partner force interval, counterbalance intervals) and a playhead.
- **Honesty requirement**: all forces are model estimates from motion. Values carry uncertainty bands from
  physics.json; partner/limb forces that the physics stage marks **not identifiable** are drawn as
  hatched/grey intervals, never as confident numbers. A legend states "estimated from video".
- **Timing honesty** (final integration 2026-10-07): when timing.json's `absolute_timing.valid` is false (no
  audio-video sync event such as a clap or slate pins the phones' A/V offset), a step's offset from the beat includes
  that unknown offset. The desktop HUD then prints each step as "+N ms vs beat (unsynced)" without an on beat / late /
  early verdict, drops the hit rate from the per-dancer summary and adds a line "timing vs beat UNSYNCED (no A/V sync
  event): relative only - follow - lead +N ms". hm_state `timing.absoluteValid` reports it. A timing.json without the
  field keeps the old verdicts. Known gap: the footprint / touchdown-ring colours (on-beat green, near amber, off
  magenta) still encode the absolute offset.

### 3.9 Counterbalance (all states)
A counterbalance is when the follower's weight is offset by the leader's: they lean away from each other
and hold each other in tension through the connection, so their **shared centre of mass** sits between
them. The demo take uses it heavily: the leader anchors the follower's foot at one point on the
floor and walks a full circle around her, pivoting her in place.

- **Couple COM on the floor**: whenever the couple is in a counterbalance, a **yellow dot and ring** on the floor
  directly below the couple's combined centre of mass. It fades in/out over ≈ 0.2 s at the interval edges. Shown in
  every state, including the default Orbit. **No vertical axis** (review 2026-10-07): the combined COM stays
  0.25-0.40 m (median 0.30 m) from the leader's pelvis / chest for the whole counterbalance of the demo take, so a
  yellow vertical up to the COM stood right in front of his legs from the director camera and read as the gold up
  axis the user banned ("no gold up-arrow on the leader ever"). The axis + COM sphere remain as an option
  (`CounterbalanceOverlay.DrawCoupleAxis`, off), and even then never within 0.5 m of his torso.
- **Pivot point and dial**: the follower's anchored foot is marked with a yellow ring on the floor at the centre of
  the counterbalance's **dial** (§3.5): the leader's circling about her foot fills the dial with the cumulative degrees
  ("701°" on the demo take: almost two full turns) and a tick per beat; the floor craft keeps it afterwards.
- **Physics mode**: the couple axis from her anchored foot up to the combined COM lights up while the COM is directly
  over her foot, and the dial shows the turn's verdict (§3.8).
- **Tension**: no line is drawn (user, 2026-10-07: no extra lines for tension and pressure); in Physics mode the
  connected arms take the tension colour (§3.8).
- **Follower spiral**: the follower's extremity traces are emphasised (§3.7).
- Detection is a pipeline output (`counterbalance.json` in the capture, §12) with per-interval confidence; low
  confidence intervals (interval confidence < 0.6; a pivot's own geometry score does not dash it) are drawn dashed.
  A counterbalance = one dancer **hangs** (stands with the centre of mass beyond
  the edge of their own support, away from the partner: they would fall without the partner) while the partner is the
  **counterweight** (not leaning in towards them; a heavier, bracing partner holds the tension inside their own
  support), AND they are connected - hands joined with straight arms, or a hand on the partner's body with any elbow
  angle (an embrace frame: the lead's arm around the follow's back). Pivot = that plus the follow's anchored foot and
  the lead circling it. Checked against a human-labelled real counterbalance pivot (IoU 0.97, edges within 4 frames,
  no other interval in a 38 s take).

### 3.10 Dance graph — the zouk state machine
- The **dance graph** is a static 3D state machine of zouk moves, scaffolded from the user's
  move-graph file and grown as dances are labelled (§10). It keeps the
  original 3D layout:
  - **Height = energy.** The bottom holds grounded, stationary states (stand, hug, isolated tilts and
    body rolls); moves grow out of that grounded state into travelling steps and open moves; the top
    holds the high-kinetic-energy moves (pirouettes, spins, downswing).
  - **Icons and colours** as in the move-graph file: node shape (Cylinder, Hourglass, Diamond, Plus, Star, Tetra,
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
  "uncertain" with the top two candidates rather than a confident single name - for automatic labels and
  for narrated items whose *name* match was doubtful. A narrated name that matched exactly or by alias but
  was heard faintly by the speech recogniser (the low number is the recogniser's word probability) keeps
  the single narrated name with its low bar and says why ("28 % · heard faintly — recogniser 33 %, name
  exact"): the caption never offers a move nobody named (review 2026-10-08).
- A **graph inset** (the state machine, §3.10, as a small live minimap) sits beside the caption: the current
  node highlighted, the path so far as a fading trail, the candidate next moves glowing faintly. In the
  Dance graph and Fingerprint states the inset is hidden (the full graph is on screen).
- Layout: 16:9 — caption lower-left, inset lower-right; 9:16 — caption above the bottom safe area, inset
  below the top safe area (§8.3). VR — a small panel that follows the user's gaze at a comfortable
  distance, never in front of the dancers.

### 3.13 The dance as a reaction — "Leader + Follower × Song → fingerprint"
- Each dance is presented like a chemical reaction. The reactants are the two dancers, the song is what
  drives the reaction, and the product is the dance fingerprint (§3.11):
  **Leader + Follower × Song → [fingerprint]**.
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
| **Orbit** (default after load) | slow continuous orbit around the couple (≈ 1 rev / 20 s, gentle height drift), centred on the couple | 0.35 displayed (65 % transparent on screen), textured | opaque, beat conduction on | floor + small faint crosses, the leader's current T + the floor record (faded old Ts, dotted T-to-T path, axis pivots, dials), recent footprints, counterbalance COM dot, follower free-extremity traces, follower neck axis (> 15° from her calibrated neutral) |
| **Camera tour** (desktop/render only) | flies between source-camera POVs, video fills the frame at each POV (§5.3) | 0.35 | opaque | active camera's video, other cameras as glyphs, alignment QA optional |
| **Overhead floor craft** | top-down (orthographic or narrow FOV), framed to the dance area | 0.23 | opaque | the leader's current T + the floor record (faded old Ts, dotted path, axis pivots, dials) accumulating to the end of the take, recent footprints (full history on demand) |
| **Geometry** | low 3/4 orbit, slower | 0.175 | opaque | leader floor axis, follower spirals + neck axis (> 15°), counterbalance axis, fitted circles optional |
| **Physics** | 3/4 side view, steady | 0.12 | load-coloured (estimated; legend) | COM / XCoM / support polygon, balance axes (chest → stance foot per dancer; her anchored foot → couple COM in a counterbalance pivot) lighting up when aligned, turn verdicts on the dials, time strip, legend (no F_net up arrows, no tension / pressure connector lines) |
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

### 5.2 Orbit and follow
Continuous motion by default ("the camera should generally be in motion"): orbit radius/speed per state,
never passing through a dancer.
- **The camera never moves up and down with the dancers' centre of gravity / geometry centre.** It follows
  the centre of the two dancers in **XZ only**, slightly decoupled (damped spring with a small dead zone and
  ≈ 0.5–1 s lag), so steps and bounces do not shake the view; the look-at point also sits at a fixed height.
- **Height (Y) and XZ framing are set by the director mode** (per view state, blended on state changes).
- **Free-fly** (user control): the camera height stays fixed unless the user changes it with the keyboard.
- Implementation (desktop rig `CameraControl`, the only writer of the camera): follow point = XZ midpoint of the two
  pelvises → dead zone 0.15 m (2.5 s recentre) → critically damped springs (look 0.6 s, eye 0.9 s); seeks, loop wraps
  and loads are cuts (snap). Pausing is not a cue to move: the view coasts to a stop where it is (0.15 s); stepping or
  scrubbing while paused re-centres on the shown frame. Free-fly is cylindrical around the anchors with an
  absolute eye height and look height. Keys: **A/D** orbit, **W/S** dolly (horizontal), **E/Q** camera up/down,
  **Z/X** look lower/higher, **Shift** ×3, **O** director ↔ free. Any camera key takes the camera from the director
  (eye continuous; the tour keeps its layers); explicit tour commands and O hand it back; the tour's automatic
  measure-boundary advance does not. `hm_orbit` (azimuth/elevation/radius around the look point, or
  --height/--look/--distance) always lands in free-fly; `hm_orbit --mode toggle` is the O key.
- **Blends never pop.** The director (the tour) returns only the current state's pose and a shot number. Every shot
  change, and every hand-over to the director, blends 1 s from a snapshot of the pose **on screen** (with its motion -
  look velocity, orbit rate, dolly and crane rates - decaying over 0.25 s, so the camera does not stop dead) to the
  live state pose: cylindrical around the look point (never through the couple), the turn direction fixed at the
  blend start and then tracked continuously. When the target eye is nearly above its own look point (horizontal
  offset below 1.5 m: the Overhead state, or the graph chase camera passing over its target mid-blend) its azimuth is
  meaningless and spins, so the eye path crossfades to a straight line (fully below 0.5 m) and stays straight for the
  rest of that blend; without this a blend into the dance-graph state could whip ~65 degrees in 80 ms (integration
  review 2026-10-07). A switch during a blend continues from where the camera is; each state's
  orbit runs on its own clock from its entry; a seek or loop wrap during a blend moves the blend's start with the
  couple. `hm_camtrace` records every rendered frame for the playtest.

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
- **Graph** (`moves/graph.json`): the move-graph scaffold plus accepted additions; node ids are stable UUIDs
  from the move-graph file, with name, aliases, shape, tone, position, and energy (height).
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
| `counterbalance.json` | intervals: t0, t1, pivot point (follower's anchored foot), combined-COM track, leader circling radius and swept angle, connection, confidence + per-criterion scores, `hanger` + `overhang_m` |
| `floorcraft.json` | the leader floor axis record (§3.5 / §3.6), version 2: `track` (smoothed axis origin + yaw, 15 Hz), `plants` (centroid, established yaw = the heading held longest, forward / long axis, radius, turn), `moves` (kind step / travel, path from cross to cross, displacement, yaw change, from / to pivot), `rotations` (centre, from / to yaw, signed angle, owner plant or move, plants touched), `pivots` (from counterbalance.json, + `leader_axis`: his chest path and axis turn during the pivot - no plant / move / rotation overlaps a pivot) + `counterbalances`, `events` (all by start time); capture time + Unity coordinates like counterbalance.json, yaw ccw from above (same number in both frames) plus Unity Euler Y |
| `timing.json` | beats, touchdowns, accents (with reliability), asynchrony stats |
| `moves/` | `labels.json`, `graph.json`, `path.json`, `fingerprint.json` (§10) |
| `direction.json` | optional authored shot list; generated if absent |
| `room/` | not displayed; kept as an alignment reference only |

Derived in the viewer (no export needed): leader floor axis (from the skeleton when `floorcraft.json` is absent),
follower spirals and head axis, footprint history, coverage, the miniature isolated couple for the graph path.

---

## 13. Implementation status (2026-10-06)

| Area | Exists today | New work |
|---|---|---|
| Playback core | capture v3 loader, times-driven clock, beat grid, lesson transport, speeds, `hm_*` CLI, TypeSafe router | v4 manifest, origin offset, library scan, speed track |
| Avatars | skinned SMPL-X avatars (SmplxAvatar), hair prototype | depth-prepass translucent material, texture/hair layers, opacity per state |
| Skeletons | glowing spline skeletons (Dancer.cs) | lead red / follow white, beat-conduction pulse, accent flashes |
| Floor | FloorPatterns, TimingOverlay footprints | black translucent plane + teal 1 m crosses, fading history, coverage trails, counterbalance pivots |
| Floor craft / geometry | follower traces, neck axis (> 15°, 2026-10-07) | leader floor axis, circle fits |
| Counterbalance | — | pipeline detector, yellow COM floor dot + ring, pivot ring + leader arc |
| Dance graph | the user's move-graph file | graph import, path + fingerprint views, chase camera, labels/proposals pipeline, annotation tool |
| Physics | PhysicsOverlay (L0: COM, XCoM, couple GRF, impulses) | per-limb axial loads (needs a pipeline inverse-dynamics stage), contact-force connectors, time strip, uncertainty display |
| Cameras | VRTKLite orbit, VirtualCameraRig | Cinemachine rigs, per-phone 6DoF + zoom tracks, full-frame video planes, tour, cuts |
| Director | — | Timeline + `direction.json` generator (prototype sequencer first) |
| Recording | — | Unity Recorder presets, `hm_render`, output checks |
| VR | OpenXR + Meta OpenXR packages installed | passthrough MR scene, room-centre anchoring, hand menu |

Done 2026-10-07 (evening): skeleton beat-conduction pulse with per-part jerk-accent modulation (rhythm mode, default) and
load-coloured skeletons with an "estimated" legend (physics mode) - Overlays/SkeletonStyle.cs; black translucent floor +
light-teal 1 m crosses (Floor/FloorGrid.cs); footprints recent-only by default; leader live floor axis + the faded
plant / move / rotation / pivot record (Floor/FloorCraftOverlay.cs, floorcraft.json); follower traces on free extremities
only; neck axis against her calibrated neutral; tension / pressure connector lines removed.
Review fixes (2026-10-07, late): foot traces only in leg gestures (never a step, never at floor height); tension shows
orange on screen (deep LDR bases, no clipped red), the legend under the tour title; rhythm accents fall back to the
skeleton's own jerk without timing.json; the counterbalance's vertical COM axis is off by default (floor dot + ring).
Floor craft v2 (2026-10-07, late): the leader's T is stable (averaged per episode, a new T on an exit or a > 30°
rotation), old Ts fade to a very faint teal record joined by a dotted path, axis pivots with their angle, dials for
repeated turns (the counterbalance's "701°" about her anchored foot); physics-mode balance axes (chest → stance foot,
couple COM → anchored foot) that light up when aligned, and good / ok / needs-work verdicts on the turns; floor crosses
smaller and fainter; the v2 plant / move / rotation visuals retired (Floor/FloorCraftOverlay.cs,
Overlays/BalanceAxisOverlay.cs, Tools/playtest_floorcraft.ps1).
Floor craft v2 review fixes (2026-10-07, night; floorcraft.json revision 3.1): a dial about a shared point only when he
really goes round it (no couple-turn dial for his own spin while the couple swaps places); each T averaged over all the
frames he spends on it; no flashing Ts (< 1 beat after forming: dropped, their headings kept as pivot waypoints);
pivots before the first T and into / out of the counterbalance; only one-foot pivots (and dials) graded, the others
named "stepped" / "both feet"; dial verdicts from the dial's end; pivot / dial labels only while recent, then the
faint teal record (only the counterbalance keeps its "701°"); the misaligned balance axis dashed.
Final integration (2026-10-07): the step-timing HUD drops its absolute verdicts when timing.json says the A/V offset is
unknown (§3.8 timing honesty); FloorCraftOverlay reads the v2 move `length_m`; the whole-capture hair / shoe sweeps in
the playtests run with a 300 s CLI timeout (a 38 s capture's shoe sweep takes ~31 s, past the default 30 s, and its
checks were skipped silently). On the 38 s demo the counterbalance draws at HUD 32.00-36.05 s against the user's
32.0-36.15 s (the end is where her centre of mass comes back within ~6 cm of her standing foot).

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
- Counterbalance: on the synthetic pivot test the yellow COM floor marker appears within ±1 frame of the ground-truth
  interval and the dot sits under the combined COM (≤ 2 cm); pivot ring at the anchored foot (≤ 3 cm); no vertical
  axis is drawn on any sampled frame (default view).
- Follower traces: no foot trace on a run that peaks under 12 cm and lasts under 0.6 s (a step); the counterbalance's
  free leg trails during the interval.
- Graph path: every label segment maps to one node; the miniature couple reaches each node by the
  segment start; new links are dashed; fingerprint dwell seconds sum to the labelled duration.
- Caption: at every segment boundary the caption shows the new move within 1 frame; the confidence shown
  matches the label's provenance rule (§10); below the threshold it reads "uncertain" with two candidates.
- Reaction title: the opening card and the closing fingerprint read "Leader + Follower × Song", with level
  and category above the arrow; the library shelf matches the dance's level.
- Leader T (Tools/playtest_floorcraft.ps1): over the longest T the drawn T never moves (≤ 1 mm, ≤ 0.01°) while his chest
  walks ≥ 15 cm along its stem / crossbar inside its region; at every T boundary the old T is current to its end and a
  new T after a > 30° rotation (or an exit) once it forms, none in the gaps; by the end every old T (faint teal),
  transition, axis pivot and dial is on the floor and nothing from the future at the start; each dial is hidden before
  its start, between 0 and its total mid-way, reads its total at the end and stays; the counterbalance dial overlaps
  the human label with IoU ≥ 0.9; floor crosses ≤ 9 cm across, ≤ 6 mm lines, ≤ 0.5 brightness.
- Balance axes (same playtest): only in physics mode; none without a grounded foot; starting at the skeleton's chest
  (≤ 2 cm from the analysis); brightness a non-decreasing function of the score and non-increasing with the tilt where
  the tilt limits it; dashed exactly below the aligned score; the couple axis exactly inside the counterbalance pivot;
  verdicts only in physics mode, on a dial only from its end, as icons only on graded turns (dials + one-foot pivots).
- Floor record (same playtest, review fixes): every T but the last holds ≥ 0.7 s after forming (none only flashes); at
  the end of the take no pivot label older than ~2 measures and no dial label but the counterbalance's (and recent
  ones).
- Live-axis fallback (captures without a version-3 floorcraft.json): long side within 3° of the shoulder line
  projection, short axis perpendicular.
- Axes (Tools/playtest.ps1 + playtest_dance_layers.ps1): no F_net arrow / plumb line through either dancer; on every
  sampled frame the follower's neck axis is hidden at ≤ 15° and its alpha / length equal the 15–35° smoothstep of the
  angle; the counterbalance axis is never drawn within 0.25 m of the leader's torso.
- Recorder: both aspect presets produce MP4s with exact resolution, fps, duration and an audio track.
- Speeds 0.1×–1.0× keep avatars, video and beats in sync (frame for time).
- Quest build: 72 Hz sustained in each state with the full layer set on the reference capture.
- Camera (Tools/playtest_camera.ps1): hm_orbit 60/15/3 puts the eye at 1.726 m (look 0.95 m); while playing, eye and
  look heights span < 0.1 mm and the look anchor stays within 0.45 m of the couple; after a pause the look anchor moves
  < 3 cm; E raises the eye with the look height kept, W dollies with the eye height kept; orbit state eye 1.54–2.06 m,
  physics 1.75 m; a camera key takes over with a continuous eye and the tour keeps its state; loop wraps are cuts;
  hm_hair frame parks the camera. A per-frame trace (hm_camtrace) across every ordered pair of the six view states,
  interrupted blends, O-key hand-overs and the running tour's own advances has no one-frame camera jump > 0.5 m
  outside cuts.
- Avatars: each body 65 % ± 4 % transparent as displayed by default, every part alike (hm_opacity --probe: p10..p90
  within 6 points), skeletons visible inside; per-state opacity = default × state factor; from either side the nearer dancer's translucent queues come after the farther dancer's (back to front);
  the room and splat layers start off.

---

## 15. Decisions and open questions

Decided by the user (2026-10-06):
- Source videos: full-frame, desktop and renders only; never in VR.
- VR: POV recording only; the user walks freely; the dance is anchored at the room centre.
- Every mode starts the dance at the origin and lets it travel from there.
- Counterbalance: yellow floor dot + ring under the shared COM (the vertical axis to it was dropped 2026-10-07: it
  stood in front of the leader); pivot + leader-circle indicator in the floor craft; follower spirals emphasised.
- The zouk graph (the user's move-graph file) is the scaffold; the tour shows the path through it and the fingerprint.
- Graph icon/colour legend confirmed (§3.10).
- Move names use established Brazilian Portuguese terminology; "Lateral" is renamed **Corredor**; "cicada" was
  **Sarrada**; Little Turn is the **Viradinha**.
- Directed playback shows the move caption with a confidence score and the graph inset (§3.12).
- Each dance is a reaction "Leader + Follower × Song" producing its fingerprint (§3.13).
- Splats show the dancers only (no floor or room), off by default; the room reconstruction is not shown.
- Hair matches the follower's shape and colour and keeps the tip bloom; faces from portraits; expressions and hand
  contact refined (§3.2).
- Graph chase camera zoomed far out, may be slightly inside the graph (§5.5).
- Data siloed by level (Professional / Intermediate / Novice; only Professional so far); categories Demo,
  Lesson, Jack and Jill (§2.2).

Defaults (change any of these):
- Avatar opacity 0.35 (hm_opacity; Overhead ×2/3, Geometry ×1/2, Physics ×1/3); floor alpha 0.6 (0.25 in
  passthrough); teal #19C3D6.
- Trace windows: footprints 4 measures, spirals 2 measures.
- Slow-motion audio muted below 0.5×.
- Default render preset: Vertical HD 1080×1920 @ 30 fps.
- Miniature couple scale 0.15 in the graph; graph at true scale centred on the origin.

Open:
1. Brand/captions on rendered videos (title card, dancer names, watermark)?
