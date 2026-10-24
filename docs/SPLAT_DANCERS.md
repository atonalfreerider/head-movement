# Splat dancers: skinned Gaussian-splat dancers in the viewer (PC and Quest)

Written 2026-10-20. VIEWER_SPEC 3.2a (the splat layer shows the dancers only and is off by default on the desktop, on by default
for a capture whose capture.json asks for it: "Default per capture" below). The data contract is the
workspace's `dancecap/docs/SPLAT_AVATAR_FORMAT.md` (`splats/<role>.splatav` + `<role>.json` in a capture folder; LHM++ skinned
Gaussians, canonical pose = the shaped SMPL-X zero pose, Unity coordinates). The capture data is likeness data and lives only in the
git-ignored `Assets/StreamingAssets/<capture>/splats/`.

## Use it

    unity command hm_splatdancers --action on | off | state      # the "splat dancers" toggle by hand (wins for the editor session)
    unity command hm_splatdancers --action auto                  # forget the hand choice: each capture's default again
    unity command hm_splatdancers --action hair --mode skinned | hidden | only
    unity command hm_splatdancers --action hairmotion --mode strands | rigid   # bound hair on the simulated strands (default) / LHM++ LBS
    unity command hm_splatdancers --action haircheck [--from <dance s> --to <dance s>]   # hair pass vs CPU reference, swing, intrusion, stray hair
    unity command hm_splatdancers --action hairshot --out C:/abs/prefix --azimuths 0,90,180,270 [--radius 1.2]   # stills around her head
    unity command hm_splatdancers --action opacity --mode opaque | avatars | scale
    unity command hm_splatdancers --action path --mode composite | direct
    unity command hm_splatdancers --action skeleton --mode physics | always | never   # the skeleton lines with the opaque splats (default physics)
    unity command hm_splatdancers --action check                 # GPU skinning vs the CPU reference + splat joints vs avatar bones
    unity command hm_splatdancers --action perf_start | perf_report

While on, each dancer whose capture has a splat avatar is drawn as Gaussians instead of the mesh avatar: every Renderer under the avatar
(body, shoes, head props, hair cards, tip glows) gets `forceRenderingOff`, and a `SkinnedSplatAvatar` is posed every frame from the
avatar's own bones, so playback, seeks, slow motion, the film director's sub-frame poses and the film recorder all drive it. The toggle
set by hand survives Play-mode restarts for the rest of the editor session (SessionState, so `hm_film record` records splats when it
was on); a new editor session starts with it unset: each capture's default (below). Splats hide with the avatars layer, inside
role-hidden spans, when the avatar opacity is 0 and while the dancer is parked (below).

## Default per capture (2026-10-23)

A capture shows its splat dancers by default when its `capture.json` asks for it: `"splat_dancers": "on"` (`CaptureManifest.splat_dancers`;
written by dancecap `export_unity` from `takes/<take>.toml [export] splat_dancers = "on"`; absent / `"off"` = the viewer default). The
decision is one pure function, `SplatDancersDefault.Decide` (`Core/SplatDancersDefault.cs`, EditMode tests
`Tests/Editor/SplatDancersDefaultTests.cs`), applied by `SplatDancers` at every capture load (`HeadMovement.AvatarsLoaded`):

| where | the splat dancers at a capture load |
|---|---|
| Editor | set by hand this editor session (`hm_splatdancers --action on` / `off`): that, for every capture, as before. Otherwise ON for a flagged capture that has `splats/<role>.json` when the device can draw them (D3D12 / Vulkan / Metal with compute shaders), else OFF |
| Editor, flagged capture on D3D11 / OpenGL / no compute | OFF (the mesh avatars) and one warning per capture: `splat dancers: capture default refused: ... D3D11: the renderer needs D3D12 - start the editor with -force-d3d12; avatars` |
| Windows player | `"splats"` / `-hmsplats` when given; otherwise ON in VR (as before) and, for a flagged capture with `splats/`, ON on the desktop too; never without a drawing path (the `HM-SPLATS tier` line says why) |
| Quest | unchanged: on unless `"splats": false` / `-hmsplats off` (every capture with `splats/`) |

Loading an unflagged capture after a flagged one turns the splats off again (the avatars come back, the Gaussians feature leaves the URP
renderer). A capture loaded while the splats are off reads none of its splat files (`state`: `count` 0, `loadSeconds` 0); switching
them on later loads them then (before 2026-10-23 every capture load after the first flagged one read both files, 1.5-4.3 s, with the
splats off). `hm_splatdancers --action state` reports `why` (e.g. `capture default (capture.json splat_dancers on)` or `viewer default (off
on the desktop)`), `handSet` and `captureFlag`; `--action auto` forgets the hand choice and applies the loaded capture's default. A film
recorded from a flagged capture records splats without any `hm_splatdancers` call (`FilmRecorder` reads `SplatDancers.Enabled`).

**Parked dancers.** dancecap `export_unity` parks a dancer that is not yet in any camera's view (capture.json `absent`) 30 m under its
partner's pelvis. The opaque splats of a parked dancer showed through the empty floor in overhead views (checked 2026-10-23 on the first
flagged capture: a tiny figure under the couple at 60 deg elevation). While the avatar's pelvis is more than `ParkedBelowM` (5 m) under
the floor in the avatar's own space, its splats are drawn at opacity 0 (`OpacityScale`; the avatar stays hidden under them, `state`
reports `parked`), and the film's face sidecar counts that dancer as not shown.

**Graphics API.** The renderer needs D3D12 (or Vulkan / Metal). On this 4-thread PC the editor's automatic choice is D3D11 (Unity's
built-in D3D12 device filter rejects 4 CPUs or fewer; the log says "D3D12 API denied by user filter"), so open the editor with
`-force-d3d12` (`unity open . --editor-version 6000.6.5f1 --args "-force-d3d12"`). No project setting changes for it.

## How it is built

| piece | file |
|---|---|
| renderer | package `net.kleinbeck.gaussians` 0.4.0 (ninjamode/Unity-VR-Gaussian-Splatting, MIT, derived from aras-p/UnityGaussianSplatting, MIT), git URL pinned to commit `9675b7a60ab359f233f0e95671ae699bdf8b1e69` in `Packages/manifest.json`; its `LICENSE.md` and `THIRD PARTY NOTICES.md` ship with the package |
| reader of the format | `Assets/Splats/Avatar/Core/SplatAvatarData.cs` |
| posing maths (CPU reference) | `Assets/Splats/Avatar/Core/SplatSkinning.cs` |
| GPU skinning | `Assets/Splats/Avatar/Core/Shaders/HM_SkinSplats.compute` (55 skin matrices, 4 joints per Gaussian, writes the renderer's FP32 frame) |
| deformation provider | `Assets/Splats/Avatar/Core/SkinnedSplatAvatar.cs` (`IGaussianSplat3DDeformation`; run-time `GaussianSplat3DAsset.InitializeFloat`; skins once per frame at `beginContextRendering`) |
| composite with a fade | `Assets/Splats/Avatar/Core/Shaders/HM_SplatComposite.shader` (the package composite + `_HmSplatOpacity`) |
| URP feature at run time | `Assets/Splats/Avatar/Core/SplatUrpFeature.cs` (adds `GaussiansURPFeature` to the URP renderer in memory only; `Editor/SplatFeatureGuard.cs` takes it out on Play-mode exit, script reload and before any asset save: `Assets/Bloom_Renderer.asset` is never changed on disk) |
| skeleton over the splats | `Assets/Splats/Avatar/Core/SplatOverlayFeature.cs` (run-time URP feature next to the Gaussians one: draws `SplatUrpFeature.OverSplats` right after the splat composite and depth, with a depth texture of its own cleared to far, so the camera depth is neither tested nor written) |
| viewer toggle | `Assets/Splats/Avatar/SplatDancers.cs` (hooks `HeadMovement.AvatarsLoaded`; both dancers in one `GaussiansGroup` = one global sort) |
| hair binding (posing maths, CPU reference) | `Assets/Splats/Avatar/Core/SplatHairBinding.cs` (`ISplatHairSource`, segment frames, lever / attach / face box) |
| hair pass on the GPU | kernels `HairSegments` + `SkinHair` of `HM_SkinSplats.compute` (after `Skin`: per-segment frames from the uploaded simulated points, then the bound Gaussians) |
| hair source | `Assets/Splats/Avatar/HairStrandsSplatSource.cs` (a new partial-class file of `HairStrands`: read-only rest / points / face box; `HairStrands.cs` is unchanged) |
| CLI | `Assets/Splats/Avatar/Editor/SplatDancersCli.cs` (`hm_splatdancers`) |
| tests | `Assets/Splats/Avatar/Tests/Editor/SplatAvatarTests.cs` (EditMode: reader, canonical round trip, single-joint pose, quaternion, GPU vs CPU), `SplatHairTests.cs` (binding round trip, rest = head-rigid, rigid swing, attach, face box, GPU hair pass vs CPU) |

Assembly `HmSplats` (`Core/`) is built for the Editor, Windows 64-bit and Android, and `SplatDancers.cs` /
`HairStrandsSplatSource.cs` compile there (2026-10-21; before, the Quest build had no splat code). A player finds the shaders through
`Assets/Splats/Avatar/Resources/HmSplatShaders.asset` (`SplatShaderSet`: the package's `RenderGaussianSplats`, `GaussianComposite`,
the two debug shaders the renderer refuses to start without, `SplatUtilities.compute`, and our `HM_SkinSplats.compute` /
`HM_SplatComposite.shader`); the Editor fills the same set from the AssetDatabase. Refresh the asset with
`unity command eval --code 'return HmSplats.SplatShaderSet.WriteAsset();'` when a shader moves. It holds shader references only, no
likeness data.

Posing (format section 4): G_0 = the avatar's pelvis bone world transform, G_j = G_parent [R_j | J_j - J_parent] with the splat file's
own rest joints J and the avatar bones' local rotations, S_j = G_j T(-J_j); per Gaussian M = sum w_k S_k, centre = M mean, rotation =
q(M) rotation. The GPU result equals the CPU reference to < 1e-5 m (test); the splat joints sit 1.3-5.3 mm from the avatar's bones on
this export (10 vs 16 betas).

## Display

- **Opaque by default.** Path `composite`: the splats are accumulated off-screen and composited before URP's transparent queue, then
  their approximate depth is written (the package's depth pass, moved from after to before the transparents by reflection on the
  package's internal pass: `SplatUrpFeature.DepthBeforeTransparents`), so floor marks, arrows and labels depth-test against the dancers
  (a floor-craft line behind a shoe is hidden by it). Path `direct` (the package default) draws the splats inside the transparent queue
  without depth for the overlays: floor rings draw over the feet.
- **Overlays inside the opaque splats** (`skeleton --mode`, default `physics`, kept across Play-mode restarts like the
  toggle). The skeletons, the follower's spine beads, the balance axes, the centre-of-mass spheres and the hand-contact
  lights lie INSIDE the bodies, and the leader's T lies on the floor under them; they showed through the 75 %-transparent
  mesh avatars of the film. With opaque splats alone (first splat film excerpts, 2026-10-20) they were hidden except where
  a line reached the surface (a red band across the lead's collar, pieces on the jeans), the physics view showed its
  legend ("Skeleton load - ESTIMATED from motion") over bodies whose load-coloured skeleton could not be seen, and the
  overhead shots lost the T's crossbar under the dancers. `SplatOverlayFeature` (a run-time URP feature next to the
  Gaussians one) now draws such renderers again right after the splat composite and depth, with a depth texture of its
  own cleared to far (the camera depth is neither tested nor written: a colour-only pass was merged by the render graph
  into the previous native pass with the camera depth still bound, and the lines stayed hidden). The same renderers and
  materials are drawn, so colours, widths, fades and bloom are the viewer's own; an additive glow (balance axes, floor
  craft) is switched off for the regular loop while the pass draws it, so it is not doubled. `physics` mode:
  - physics view: skeleton lines, spine beads, balance axes, COM spheres and hand lights over the splats;
  - every other view: the skeleton lines of a dancer whose splats show are switched off (`forceRenderingOff`, given back
    as soon as the splats hide), so the photoreal bodies stay clean; the hand lights are over the splats;
  - seen from above (the camera looks down steeper than about 53 deg: the overhead floor-craft shots) the floor craft
    (the leader's T, the floor record) is drawn over the dancers; in other views floor marks stay depth-correct;
  - on the phones' video the splats are hidden and every overlay is drawn as before.
  `always`: all of it in every view (the avatar film's look: the lines dominate the bodies). `never`: depth-correct only
  (the first excerpts). Private viewer members are read by reflection (`Dancer.SkeletonLines()`, `followSpineBeads`,
  `SpineBeads` buffers, `ContactDetection` hand orbs); `state` reports `overSplats` and `overSplatsDrawnLastFrame`.
- `opacity --mode avatars` fades the whole composited splat image uniformly to the avatars' opacity (one layer: the inside and the back
  of the bodies never show). VIEWER_SPEC 3.2's "transparent AS DISPLAYED" needs more: a per-pixel alpha from the composited colour's
  luminance like `DisplayTransparency.AlphaLut`. `--mode scale` scales every Gaussian's alpha (the inside shows; review only).
- **Fade into and out of a phone's video** (`SplatDancers.FilmVideoFade`, 2026-10-21). In a film segment with the layer
  `avatars_fade_with_video` (the recap's flights into a phone and its camera tours) the director fades the mesh avatars with
  1 - the phone video's opacity; the opaque splats now get the same factor through the composite (`_HmSplatOpacity`), so they
  dissolve into the video like the avatars (before: both bodies, splats and video dancers, for the 9 frames of the 0.3 s fade-in,
  then the splats gone on one frame; on the way out they came back at full opacity). The skeleton lines (off while the splats
  show, outside the physics view) come back on the frame the splats are hidden, as before. The splat depth stays on through the
  fade: switching the group's Write Depth off below half opacity crashed the D3D12 device at the first fade ("failed to Close a
  command list (80070057)", four editor crashes on 2026-10-21) - never toggle `GaussiansGroup.m_WriteDepth` at run time.
- **Darker caption and chapter-card backing over the bodies** (`FilmUi.OpaqueBodies`, set by `SplatDancers`; never set on the
  Quest). While splats are drawn, the caption backing goes from alpha 0.38 to 0.85 (blended with the composite opacity) and also
  covers the speaker tag, the chapter / title card backing from 0.62 to 0.9 and fades in and out more slowly than its title
  (backing x sqrt(card alpha) instead of x card alpha): the speaker-coloured words were hard to read over a white shirt or light
  jeans, a white title over a photoreal head. With the splats off or hidden (the phones' video) the value is 0 and the overlays
  are exactly as before (an avatar excerpt recorded with this code matches the 2026-10-11b avatar film frame for frame).
- **Film recording with the splats** (`hm_film record`, head-movement `Assets/Film/Editor/`): with the splat dancers on (or on a
  D3D12 / Vulkan device) the recorder keeps the frames in film order (`--readback auto`, `OrderedReadback.cs`: the Movie Recorder
  writes each frame from an AsyncGPUReadback callback and takes its timestamp from a FIFO; on D3D12 with the GPU 2+ frames behind,
  two callbacks in one update came the other way round and two neighbouring frames were swapped in the MP4, 6-7 pairs per splat
  film; a player-loop step right before `EarlyUpdate.UpdateAsyncReadbackManager` now polls the Recorder's request of the previous
  frame until it is done, so Unity dispatches one at a time), and after its checks it replaces the recorder's AAC track with the
  film mix (`--audiofix auto`: `python -m dancecap.film_audiofix`, the raw file kept as `<name>_rawaudio.mp4`; the recorder's track
  has a burst at film 56.876 s). `--readback observe` records with the Recorder's own order and counts the frames that waited two
  deep (118.5-123 s: up to 4 frames unwritten, 134 of 136 frames with more than one; ordered: never more than one in 14
  excerpts). Cost: the same excerpt rendered at 15.9 frames/s ordered against 19.0 with the Recorder's own order (excerpts
  11-19 frames/s; a full film about 12 min instead of 6.5).
  Excerpts (`--start`) run their film clock from the start time while a full film accumulates `Time.deltaTime` in float32 from
  0 (15 ms ahead at 300 s): in some phone stretches an excerpt shows the phone frame one before the full film's. Compare
  excerpts with excerpts, or full films with full films.

## Cost (measured 2026-10-20, editor Play mode, RTX 2080 Ti, D3D12, 2 x 160,000 Gaussians)

| view | frame ms, avatars | frame ms, splats | splat GPU passes (median ms): prepare + accumulate + composite + depth |
|---|---|---|---|
| free orbit 3.2 m, 1920x1080 | 10.8 | 10.2 | 0.35 + 4.08 + 0.03 + 1.38 |
| film feature zoom, 1080x1920 | 9.6 | 25.0 | 0.34 + 10.33 + 0.03 + 3.85 |
| film physics 3/4 view, 1080x1920 | 9.3 | 20.8 | 0.35 + 4.64 + 0.03 + 1.65 |

Fill sets the cost (the close-up is twice the 3/4 view). A 3 s `hm_film record` excerpt rendered at 21.1 fps with splats and 18.3 fps
with avatars (frame-locked recorder; short excerpts are dominated by warm-up).

## Hair (2026-10-20)

The follow's hanging hair moves with the viewer's own hair simulation, the one that moves the mesh avatar's hair cards
(`HairStrands`: 160 guides x 17 points from `hair_groom.json`, capsules, face box, comb, the background bake). The cards are
hidden with the rest of the avatar while the splats show; the simulation keeps running. Data: the free hair Gaussians of the
splat file are bound offline to the guides' rest shape (dancecap `mono4d/recap_splat_hair.py`, format section 6: strand,
segment, local offset / rotation); scalp-level hair keeps LHM++'s LBS (rigid with the head). Each rendered frame
(`SkinnedSplatAvatar.SkinNow`, after the LBS pass): the 2,720 simulated points are read (0.17 ms CPU in the editor), uploaded,
kernel `HairSegments` re-anchors them to the splat skeleton's head and computes one frame per segment, kernel `SkinHair` poses
the 12,152 bound Gaussians: on their segment (blended with the neighbour segment along the strand), the across-strand part of
far offsets turned with the head instead of the segment (no lever arm through the head), out of the simulation's face box,
blended to head-rigid at the top. GPU = CPU reference within 0.0002 mm.

Checked (2026-10-20, in the class-recap take's git-ignored `work/<take>/splat_hair_unity/`): `hm_film record --aspect vertical --start 355
--duration 6` (film 355-361 = dance 347.8-353.8 s, turns up to 393 deg/s) with splats + swinging hair, with splats + rigid LHM++
hair and with the avatars, and `--start 349.5` (the close feature zoom) with splats and with avatars: all PASS, 1080x1920, 180
frames; side-by-side `rec/compare_t355-361.mp4`, `rec/compare_t349.5-355.5.mp4`. The hair swings with the cards' simulation, is
attached at the scalp, does not flicker frame to frame; numbers in the format doc section 9.

Known limits: the guides drape behind the shoulders while LHM++'s side hair hangs beside the face / in front of the shoulders
(5-12 cm from any guide): in fast turns that side hair swings forward along the cheek and can hide the profile from the side (the
cards, on the guides, do not); a bound Gaussian is on ONE segment of ONE guide (no blend across guides), so neighbouring locks on
different guides can part; the simulation has no partner colliders (the cards neither); hair the guides press onto her back
partly enters the body (hidden by the opaque body).

**Stray hair (2026-10-23).** A guide that swings out alone carries its bound Gaussians away from the hair mass. LHM++ can draw dark
hair with few large, opaque Gaussians, often several near-copies stacked within 2 mm (composite opacity up to 0.99): on a flagged
capture they showed as round dark discs on the partner's white top and on her sleeve during head throws. The fix is in the data
(dancecap `mono4d/recap_splat_hair.py`, a per-take "look"; the format is unchanged): bound hair Gaussians are made strand-shaped
(longest axis along their rest segment, at most 3 mm across, the projected area kept) and each stack of near-copies is capped at a
composite opacity of 0.25, so a lone strand draws as a thin translucent wisp; the binding itself is untouched. `haircheck` reports
the stray bound hair per frame (posed centres read back): `isolated` (no other hair Gaussian within 5 cm), `sparse` (fewer than 8),
each also with opacity > 0.5 (`isolatedOpaque`, `sparseOpaque`), and `inOpaqueClumps` / `clumpOpacityMax` (the sparse Gaussians
within 1 cm, composite opacity 1 - prod(1 - a)); a sweep adds `stray` (maxima, frames with any, the worst clump frame) and the
per-frame columns. The per-Gaussian "opaque" counts are 0 by construction once the data caps every bound Gaussian at 0.25, so they
are no evidence on such a file. What is drawn is scored by `projPeakAlpha` / `projOpaqueAreaMm2` / `projOpaqueCorePx` (sweep:
`stray.projected`): the sparse bound hair is EWA-splatted orthographically along the 3 world axes (side, front, top; posed centres,
rotations and scales; 2 mm pixels; alpha = 1 - prod(1 - a g(x))); per frame the worst axis gives the peak alpha, the opaque area
(alpha > 0.5) and the opaque CORE = opaque pixels whose 8 neighbours are opaque too, i.e. a solid patch at least 6 mm across (a
disc; a lone 3 mm strand has none, crossing strands can make a few). On the first flagged capture (the same file posed by the same
simulation; only the look differs):

| span | file | peak alpha | opaque area max | opaque core max | frames with a core |
|---|---|---|---|---|---|
| 10 s of head throws (301 frames) | LHM++ hair as exported | 1.0 | 5,896 mm2 | 1,101 px | 213 |
| | strand look | 0.83 | 896 mm2 | 44 px | 75 |
| 5 s spin (151 frames) | LHM++ hair as exported | 1.0 | 5,896 mm2 | 966 px | 102 |
| | strand look | 0.82 | 660 mm2 | 35 px | 29 |
| | Quest `quest_25k` fitted without the look | 1.0 | 44,536 mm2 | 9,370 px | 151 |
| | Quest `quest_25k` with the look | 0.83 | 772 mm2 | 67 px | 38 |

The worst remaining frames show thin strands crossing (looked at, four sides); the round discs are gone. The projected pass costs
about 3.5 ms per frame; a 301-frame sweep takes about 2 min in the editor (run it with `unity command --timeout 800 ...`).

**Her top under the hair (2026-10-23).** LHM++ cannot see the clothes under long hair and paints the hidden layer itself: on the
first flagged capture a dark hair "paint" layer about 1.5 cm over the top's back, and under it the top's own surface guessed GREY
(Lab about 23 / 13 / 4 against the top's red, C* 31-35). With the hair swung off her back, that showed as a dark smear and, once the
paint was faded, as a pale grey-mauve blotch between the shoulder blades. The data fix (the same per-take look) fades the paint
(opacity x 0.03) and gives the exposed grey and brown-black Gaussians, and the faded paint, the top's LOCAL colour: the
opacity-weighted median of the clearly red Gaussians nearby (C* >= 25, hue 15-40 deg, within 3-12 cm), each keeping its own shading
(L* ratio to its group, clipped to 0.7-1.3). Only colours change (844 Gaussians); positions, scales, opacities and the binding do not.
In pixel-aligned editor shots of her back at 7 moments x 3 sides, the blotch area went from C* 11-15 to C* 26-28 (the top around it
31-35) and its share of greyish pixels (C* < 15) from 0.53-0.67 to 0.03-0.12.

## Quest (Android build, 2026-10-21)

The owner chose **splats on by default** on the headset (2026-10-20, over "avatars + splat test switch" and "avatars only"). In a
player build for Android (`SplatDancers.QuestPlayer`) the toggle starts ON; every lesson whose capture has `splats/` shows the
splat dancers (`HasSplatData`): the class recap, and also the Jack & Jill lesson because its `splats/` folder was pushed too (the
owner decides whether it stays); every other lesson shows its mesh avatars exactly as before (no Gaussians feature is added to the
URP renderer for them). The PC default is unchanged (off; `hm_splatdancers --action on`), except a capture whose capture.json asks for
its splat dancers ("Default per capture" above). The Quest needs no flag: deploy a capture's distilled `quest_*` sets together with its
`splats/` (the shipped `quest_settings.json` asks for `quest_15k`; a capture without the set falls back to the full files). A
follow whose `follow.json` carries a hair look (`hair.look`, "Stray hair" above) must be distilled with dancecap's `quest_distill`
of 2026-10-23 or later: it keeps the look on the students over bound hair (needle-shaped, opacity <= the stack cap, while fitting and
in the written set; `quest_distill.hair.look` in the set's json). Sets fitted before that re-grow the bound hair into large opaque
Gaussians (the round dark discs on a swinging strand) - do not deploy them.

- **Data**: `Tools/deploy_data.ps1` copies the whole capture folder without `.meta`, `*.hmj`, `*.mp4`, so `splats/lead.*`,
  `splats/follow.*` (with the hair binding in `follow.json`) and `hair_groom.json` reach
  `/sdcard/Android/data/com.headmovement.viewer/files/hm_data/<capture>/` (checked with `adb shell ls -la` and
  `sha256sum`: identical to StreamingAssets). Never in the APK: `Assets/StreamingAssets` is moved out for the build (QUEST.md 7).
- **The tier is data** (`hm_data/quest_settings.json`, keys in `VrSettings.cs`; command-line switches for a test run):

  | key | switch | default | meaning |
  |---|---|---|---|
  | `splats` | `-hmsplats on\|off` | true | the splat dancers (false: the mesh avatars) |
  | `splat_max` | `-hmsplatmax N` | 0 = `QuestDefaultMax` (all) | at most N Gaussians per dancer, the most opaque kept (`SplatAvatarData.Reduce`, load time); -1 = all |
  | `splat_max_sigma` | `-hmsplatsigma M` | 0 = `QuestDefaultSigma` (none) | clamp every standard deviation to M metres (fill cost); -1 = none |
  | `splat_path` | `-hmsplatpath composite\|direct` | composite | composite = as on the PC (accumulate, composite, splat depth before the transparents); direct = drawn in the transparent queue (no intermediate texture; the splat depth goes after the transparents) |
  | `splat_depth` | `-hmsplatdepth on\|off` | on | the splats write their depth (one more splat raster pass) |
  | `splat_set` | `-hmsplatset NAME\|full` | `quest_25k` (`QuestDefaultSet`) | a Quest-only file set `splats/<NAME>/<role>.json` (the distilled `quest_40k`, `quest_25k`, `quest_15k`, below); a capture without it uses `splats/<role>.json` (logged) |
  | `splat_scale` | `-hmsplatscale S` | 1 (`QuestDefaultScale`) | the splat layer's resolution per side, a fraction of the eye buffer: 1 = the package's composite (full resolution, the eye buffer's 4x MSAA, splat depth); below 1 = `SplatLowResFeature` (below) |
  | `splat_alpha_cutoff` | `-hmsplatcutoff A` | 0 = the package's 1/255 | larger = smaller quads (opacity-aware bounds), less fill, softer edges lost |
  | `splat_sort_every` | `-hmsplatsort N` | 1 | sort the Gaussians every N rendered frames (sort amortisation: `SkinnedSplatAvatar.HoldPositionRevision` + the group's Sort Nth Frame; compaction off) |

  `Reduce` keeps the N Gaussians with the largest opacity x (sigma_mid x sigma_max)^0.25 (faint ones go first, size breaks ties), keeps
  record order, joints, weights and the hair binding (`SplatHairBinding` is rebuilt on the kept records), and logs how much of the summed
  opacity it kept (EditMode test `Reduce_KeepsTheMostOpaqueAndTheHairBinding`).
- **GLES3 / no compute**: the renderer has no OpenGL path; on a GLES3 device the toggle goes off and the avatars stay.
- **Safety net**: when the renderer reports for 3 s that it cannot draw a shown dancer (`GaussiansGroup.RenderingError` or a
  `MemberRenderingStatus` warning: an unsupported shader or kernel, resources that never become ready), the mesh avatars come back for
  that capture (`HM-SPLATS FALLBACK ...` in logcat). It does not catch a picture that is drawn but wrong.
- **Overlays in the headset**: no over-the-splats pass in XR (`SplatOverlayFeature` is not verified for single-pass stereo). The VR
  skeleton draws over the bodies by itself (its second material, ZTest Always, queue 3100), so in the physics view the lines show
  over the splats; in the other views the lines of a dancer whose splats show are switched off, as in the PC films (`skeleton
  --mode physics`). Hand lights, COM spheres and the floor craft keep their own depth-correct draws in VR.
- **Logcat** (`adb logcat -s Unity | grep HM-SPLATS`): `HM-SPLATS tier: ...` at each capture load (on/off, thinning, path, depth,
  graphics API, settings file), `HM-SPLATS splat dancers: <role> N Gaussians ... in s; thinned: ...` per dancer, and every 5 s
  `HM-SPLATS status: feature in the URP renderer, depth patch, path, group status, participating Gaussians, MB, over-splats, per
  dancer shown / avatar visible / skinned frames / hair live`.
- **Build**: the Vulkan and GLES3 variants of the package shaders and `HM_SkinSplats.compute` compile (warnings only); the APK is
  55 MB (the compiled `SplatUtilities.compute` is 17.8 MB of it); 25 min for the first build with the splat shaders, 3 min warm.
  The PC path was re-checked after the change (Editor Play mode, D3D12, recap: 320,000 participating Gaussians, hair live, the
  image as before; EditMode tests 15/15, GPU vs CPU included).
- **Measured on the headset** (2026-10-21, the build installed at 00:15, Quest 3 `2G0YC1ZF8F063B` on the desk, `Tools/quest_splat_bench.py`:
  `-hmplay recap -hmseek 120 -hmvr-display immersive -hmview start` = the floor edge 3 m from the couple, a fresh `logcat -G 16M`, VrApi
  of the app over 15 s from 7 s after the vantage was taken; "app GPU ms" = VrApi `App=`; CPU/GPU level 2-3 / 3, GPU 640 MHz in every
  splat run; GPU% 0.99 = GPU bound). The owner's test ("framerate extremely low but splat looked correct") is this: **5 fps**.

  | recap, floor edge (start) | fps | stale frames / 15 s | app GPU ms |
  |---|---|---|---|
  | splats off (mesh avatars, hair) | 72-73 | 2 | 6.3 |
  | as installed (2 x 160,000, composite, depth, eye buffer 4x MSAA) | 5 | 1086 | 184-197 (4 runs) |
  | `-hmsplatmax 100000` / `60000` / `30000` / `15000` | 6 / 7 / 9 / 12 | 1086 / 1071 / 1084 / 926 | 156 / 125 / 97 / 69 |
  | `-hmsplatsigma 0.02` / `0.01` | 5 / 6 | 1087 / 1087 | 194 / 155 |
  | `-hmsplatpath direct` | 4 | 1085 | 219 |
  | `-hmsplatdepth off` | 6 | 1088 | 147 |
  | `-hmsplatmax 15000 -hmsplatdepth off` | 16 | 862 | 62 |
  | `"msaa": 1` (quest_settings.json: the whole eye buffer without MSAA) | 10 | 747 | 97 |

  In passthrough with the headset lying on the desk (the dancers mostly out of view) the same build ran at 43 fps, app GPU 19.5 ms.
  **Bottleneck: fill.** The cost follows the Quest renderer's fragment count (quad area per Gaussian, opacity-aware, summed over the
  splats; `dancecap work/quest_distill/fill_estimate.json`: 2 x 160k at the floor edge = 37 M fragments per eye, `-hmsplatmax 15000` =
  14 M) - a fit to the five thinning runs gives app GPU ms = 4.8 x (M fragments per eye) + 31 x (M Gaussians), residuals under 1 ms -
  and every fragment is blended into a 4x-MSAA RGBA16F accumulation (the package's accumulation texture inherits the eye buffer's MSAA:
  MSAA off halves the time) and drawn once more by the depth pass (a quarter of the time). Clamping sigma barely helps (the opaque
  surface keeps its overdraw), thinning keeps the large Gaussians (2 x 15k still costs 69 ms and loses the arms and the head, below),
  the direct path is slower. Sort, LBS and the hair pass are inside the count term (~10 ms at 320k).
- **Distilled Quest sets** (`splats/quest_40k|quest_25k|quest_15k/<role>.*`, dancecap `mono4d/quest_distill.py`, format doc section 10):
  a compact set per dancer fitted with gsplat to renders of the full avatar (100 canonical + 144 posed views, max-scale and fill
  penalties); held-out views vs the full 160k (PSNR / LPIPS on the dancer crop; the viewer's `-hmsplatmax` thinning at the same count
  for comparison; fill = fragments relative to the full avatar):

  | dancer | quest_40k | quest_25k | quest_15k | thinned 25k / 15k |
  |---|---|---|---|---|
  | recap lead | 45.7 dB / 0.008 / fill 0.074 | 44.4 / 0.010 / 0.064 | 42.5 / 0.012 / 0.058 | 21.4 / 0.25 / 0.52 - 19.6 / 0.34 / 0.38 |
  | recap follow | 42.8 / 0.012 / 0.077 | 42.4 / 0.012 / 0.067 | 41.1 / 0.014 / 0.060 | 21.2 / 0.25 / 0.40 - 20.2 / 0.33 / 0.32 |
  | John & Lula lead | 43.1 / 0.012 / 0.059 | 41.6 / 0.017 / 0.051 | 39.4 / 0.026 / 0.045 | 18.6 / 0.34 / 0.42 - 17.6 / 0.38 / 0.28 |
  | John & Lula follow | 45.4 / 0.013 / 0.078 | 44.9 / 0.015 / 0.069 | 43.5 / 0.018 / 0.063 | 24.8 / 0.24 / 0.43 - 23.7 / 0.29 / 0.32 |

  The full avatar's 160k Gaussians are mostly hidden layers (about 50 fragments per covered pixel at the floor edge); the distilled
  sets cover the same surface with 6-8 % of the fragments (2 x 25k at the floor edge: 2.5 M per eye instead of 37 M). Sheets (teacher |
  d40 | t40 | d25 | t25 | d15 | t15, held-out canonical and posed views): `dancecap/work/quest_distill/sheets/`. The follow's hair
  binding is carried over (quest_25k: 636 Gaussians on strands). Pod session `20261021-022537-batch` (A40, 20 min, $0.20).
- **Low-res splat layer** (`SplatLowResFeature`, `"splat_scale"` below 1): replaces the package's URP feature: the same prepare
  (view data + sort) but the splats accumulate into a texture of their own at S x the eye buffer per side, 1x MSAA, RGBA16F, with a
  low-res depth attachment of its own, composited bilinearly into the eye buffer (`HM_SplatComposite` pass 1: premultiplied, gamma to
  linear, the fade); the camera is not forced to an intermediate texture, so on the Quest the composite writes the XR eye texture itself.
  The package's internals are reached by reflection (`HM-SPLATS status: ... low-res layer 'ok: WxH per eye (S of ...), ..., target back
  buffer|intermediate, composite flip|no flip (...), splat depth ...'` or the reason).
  - **Upside down on the Quest (2026-10-21 bench), fixed**: the renderer's view data always uses the render-texture projection
    (`GL.GetGPUProjectionMatrix(p, true)`), so the layer holds the image in texture order (UV origin bottom-left); the package's composite
    copies in memory order, right only into its own forced intermediate. Written straight into the eye texture (top-left origin on
    Vulkan) the splats came out mirrored vertically over an upright floor (`Builds/quest_bench_d25/tune1/recap_d25_s05_front_shots`).
    The composite now asks the render graph for the target's UV origin (`RasterGraphContext.GetTextureUVOrigin`, the test URP's own
    final blit uses) and flips through `_BlitScaleBias` when it differs from the layer's: the eye texture (Vulkan) and a D3D12 back buffer
    are flipped, an intermediate (the PC editor's camera) is not; OpenGL-like APIs report bottom-left for both.
  - **Splat depth in the low-res layer** (`"splat_depth"` on, the default; `SplatUrpFeature.LowResDepth`): after the colour the
    renderer's SplatDepth pass writes the layer's own depth (approximate Gaussian-centre depth, at the layer's resolution), and the
    composite (`HM_SplatComposite` pass 2) writes it into the camera depth as SV_Depth with ZTest LEqual: splats behind nearer scene
    geometry are hidden, and the floor marks, the contact disc and the rest of the transparent queue depth-test against the dancers as
    on the package's composite path (before: no splat depth, the floor marks drew over the feet). The depth is dilated by one texel
    (the nearest of the 2x2 point samples) so the bilinear colour at the silhouette keeps a depth. `"splat_depth": "off"` = the old
    behaviour (ZTest Always, no depth). The colour pass always has the (cleared) depth attachment bound, as the package's pass binds
    the camera depth: its ZTest LEqual never runs without a depth buffer.
  - The D3D12 editor crash of 03:57 (the other agent's `--action quest --set full --scale 0.99`: "d3d12: failed to Close a command list
    (80070057)", the error of the m_WriteDepth toggle above) is still not isolated: the low-res layer has not been switched on in the
    PC editor since (the film path, scale 1, is unchanged and was re-checked on D3D12 after this change). One candidate the change
    removes: the old low-res colour pass drew the splat material (ZTest LEqual) with no depth buffer bound.
- **Quest default (2026-10-21)**: the distilled `quest_25k` on the package's composite path with splat depth (the path the owner
  saw work): `SplatDancers.QuestDefaultSet = "quest_25k"` in the code and `Tools/quest_settings.json` pushed as
  `hm_data/quest_settings.json`. Expected (e, from the fit above and the fill of the distilled sets; not measured): the floor edge
  ~13 ms of app GPU (2.5 M fragments per eye + 50k Gaussians) = about 72 fps, the front row (1.9 m) ~20 ms = 45-50 fps, overhead
  ~9 ms. The low-res layer at 0.5 would take the fill part to about a quarter (e): the candidate if the front row must hold 72 fps.
- **Not measured after the change**: the headset's tracking was lost while it lay still on the desk (logcat `VioInitialization:
  Skipping: Device is static`, launches held at "Finding position in room"), and the Android rebuild with this code is not done (the
  Editor sat at its "Scene Backup Detected" dialog after the 03:57 crash). Once both are cleared: build (QUEST.md 0), install, then
  `python Tools/quest_splat_bench.py plan --only recap --shots 1` (and `--only john_lula`): `installed` (= the 00:15 build's tier),
  `off`, `d25`, `d15`, `d25_s07`, `d25_s05`, `d15_s05`, `d40_s05`, `d25_s05_sort2` at the floor edge, the front row and overhead.

## PC VR over Meta Quest Link (2026-10-21)

The owner's other route after the 5 fps Quest test ("try deep compression or let's try running over quest link on PC"): the same
viewer as a Windows player on this PC (RTX 2080 Ti, i5-6500), streamed to the Quest 3 by Meta Horizon Link. Steps for the owner:
QUEST.md section 2a.

- **Build**: `Builds/vr/HeadMovement.exe` (`hm_vr_build --target windows --output Builds/vr --development false`, 625 s), its
  `HeadMovement_Data/StreamingAssets` replaced by the laid-out data (`Tools/deploy_data.ps1 -Out Builds/data`, 651 MB, then
  `robocopy Builds/data ... /MIR`), so the exe also runs without `HM_DATA`. Start it with `Tools/pcvr_link.ps1` or
  `Builds/vr/Start Head Movement (Link).cmd`: `-force-d3d12` (the renderer needs D3D12; this 4-thread PC picks D3D11 by itself),
  `-hmvr on`, `-hmvr-display immersive` (passthrough is a Quest-standalone feature), `-hmsplats on`, HM_DATA, `-logFile
  Builds/vr/logs/pcvr_<time>.log`, and a summary of the `HM-SPLATS` / `HM-PERF` / `VrMode` lines when the app closes.
- **Default**: a Windows player (`SplatDancers.PcPlayer`) applies the player tier at each capture load like the Quest: without a
  `"splats"` key in `<data>/quest_settings.json` and without `-hmsplats`, the splat dancers are ON while the viewer is in VR
  (`VrMode.Active`: a headset through OpenXR, or `-hmvr sim`) and OFF on the desktop (the PC default is unchanged), except ON for a
capture whose capture.json asks for it (2026-10-23, "Default per capture"; not yet run in a player build). Lessons with
  `splats/`: the class recap and John & Lula; the others keep their avatars. Every `-hmsplat*` switch applies; D3D11 keeps the avatars
  and says why (`HM-SPLATS tier: ... (D3D11: ... -force-d3d12; avatars)`). The Player log gets the same `HM-SPLATS` lines as logcat.
- **Smoke test without a headset** (the Quest 3 was in use standalone): `-force-d3d12 -hmvr sim -hmvr-display immersive -hmplay recap
  -hmseek 23 -hmview start -hmshots 3` with `HM_DATA=Builds/data`: D3D12 12.2, `HM-SPLATS tier: pc vr: splats on (pc default: on in VR,
  off on the desktop) ...`, 320,000 participating, hair live, no fallback; the stills show the photoreal couple at the floor edge
  (`%USERPROFILE%/AppData/LocalLow/DefaultCompany/Head Movement/hmshots/`). **Not run**: a real OpenXR session over Link (single-pass
  instanced stereo through the package on D3D12 is untested; the 3-s fallback to the avatars is the safety net) and a lesson loaded
  on the desktop (the off path is code-reviewed only).
- **Link's default eye buffer on this PC** (Meta Horizon Link service logs `%LOCALAPPDATA%/Oculus/Service_2026-10-1*.txt`, Quest 3 =
  `CapiReportedHmdType 23`): `display fixed fov res: 1824x1952` per eye, `VsyncToNextVsync 0.013889` (72 Hz), encode 4096 x 2240.
  The owner's Graphics preferences (refresh rate, render resolution) change it; nothing here changed them.

**Splat layer GPU time at the Link eye buffer** (editor Play mode, D3D12; `hm_splatdancers --action eyes`: two eye cameras 1824 x
1952, vertical FOV 96 deg, IPD 63 mm, eyes 1.6 m above the floor, the URP asset's MSAA 4x as an XR eye texture gets it on the PC tier,
side-on to the couple at 2.5 m = the VR seat and 1.0 m = walking up close; the recap film at film 23, 39 and 273 s (the moments of the
2026-10-20 splat comparison), played 1x for 3.5 s from each moment; `Profiling.Recorder` GPU ms of the Gaussians passes, prepare +
accumulate + composite + depth, summed over BOTH eyes; multi-pass, so the sort runs per eye: a single-pass runtime saves about half of
"prepare"). Median over the moments (range); rows marked t23 measured at film 23 s only. Work folder (git-ignored, likeness):
the class-recap take's `work/<take>/pcvr_link/run1/` (`results.jsonl`, `table.md`, every eye still, `sheet_*.jpg`).

| splat setting | Gaussians | 2.5 m | 1.0 m | look (left eye stills, sheets) |
|---|---|---|---|---|
| full, as installed (MSAA 4x accumulation) | 2 x 160k | 16.0 (14.7-16.0) | 38.4 (35.5-42.6) | reference |
| `-hmsplatmax 80000` | 2 x 80k | 11.1 (10.7-11.1) | 29.1 (26.9-30.9) | faces smeared (his face, her cheek) |
| `-hmsplatsigma 0.01` | 2 x 160k | 8.5 (8.2-10.5) | 22.3 (21.3-23.6) | as full |
| `-hmsplatsigma 0.006` | 2 x 160k | 6.7 (5.9-6.8) | 17.4 (16.8-17.7) | gaps: streaks on his arm, grainy jeans |
| `-hmsplatmax 80000 -hmsplatsigma 0.01` | 2 x 80k | 7.9 (7.6-8.0) | 18.6 (15.4-20.2) | faces smeared |
| `-hmsplatmax 40000 -hmsplatsigma 0.006` | 2 x 40k | 3.5 (3.4-3.8) | 9.2 (9.0-10.2) | broken (disc holes, heads in blobs) |
| full, accumulation at 1x MSAA (t23) | 2 x 160k | 4.4 | 18.3 | as full (no visible difference) |
| `-hmsplatsigma 0.01`, 1x MSAA (t23) | 2 x 160k | - | 12.1 | as full |
| full at 0.8 x the Link resolution (1459 x 1562, t23) | 2 x 160k | 9.1 | - | softer |
| distilled `-hmsplatset quest_40k` (t23) | 2 x 40k | 1.4 | 4.0 | close to full at 1 m (jeans edges a little fuzzy) |
| distilled `-hmsplatset quest_25k` (t23) | 2 x 25k | 1.2 | 5.3 | close to full |

(The 1x-MSAA rows set the eye targets' MSAA to 1: in the player the accumulation inherits the eye buffer's MSAA, so 1x needs either
the PC tier's eye buffer at 1x MSAA or a 1x splat layer like `SplatLowResFeature`; neither is in this build for the PC.)

- **The rest of the frame**: the editor's synced timing (8 stereo pairs, then a GPU readback) is CPU-bound in the editor (3-5 ms of
  CPU per stereo pair without splats, 6-10 with) and reads 7-11 ms per stereo frame with no dancers at all and 8-9.5 ms with the mesh
  avatars: an upper bound, not a GPU number. The scene's own draws are small (opaque / transparent queues 0.03 / 0.4 ms GPU); about
  1-3 ms of GPU for the rest at the Link eye buffer including post (e), plus Link's compositor and encoder on the same GPU, about 1-2 ms
  (e, not measured). The 4-core i5-6500 has to fit the frame too (the Link run's `HM-PERF ... cpu=` will tell).
- **Verdict (e)**: room for the splat layer about 9-12 ms at 72 Hz, 7.5-10.5 at 80 Hz, 6-9 at 90 Hz. The build as installed (full
  set, MSAA 4x) holds none of them (16 ms at the seat, 38 close: expect Link's ASW at 36 fps or less). `-hmsplatsigma 0.01` (looks as
  full) can hold 72 Hz at the seat, perhaps 80, not 90, and not up close. The distilled `quest_40k` set fits 90 Hz at the seat and up
  close (4 ms at 1 m) - for the recap; John & Lula has no distilled set yet and falls back to its full files (logged). Thinning by
  count costs the faces for little gain.
- **Editor crash**: switching the recap to `--action quest --set full --scale 0.99` (the low-res layer) in Play mode with the eye
  cameras on D3D12 crashed the Editor (2026-10-21 03:57, `%TEMP%/Unity/Editor/Crashes/Crash_2026-10-21_105735789`, the NVIDIA D3D12
  driver module in the dump); cause not isolated. `-hmsplatscale` below 1 is untested in the Windows player: not for the first Link test.

## Not done yet

- A run over Link with the headset: frame rate, the look in both eyes, the hair (PC VR over Link above; QUEST.md 2a).
- The Quest frame rate, the look in both eyes and the hair on the headset (above).
