# Splat dancers: skinned Gaussian-splat dancers in the viewer (PC and Quest)

Written 2026-10-20. VIEWER_SPEC 3.2a (the splat layer shows the dancers only and is off by default). The data contract is the
workspace's `dancecap/docs/SPLAT_AVATAR_FORMAT.md` (`splats/<role>.splatav` + `<role>.json` in a capture folder; LHM++ skinned
Gaussians, canonical pose = the shaped SMPL-X zero pose, Unity coordinates). The capture data is likeness data and lives only in the
git-ignored `Assets/StreamingAssets/<capture>/splats/`.

## Use it

    unity command hm_splatdancers --action on | off | state      # the "splat dancers" toggle (default OFF)
    unity command hm_splatdancers --action hair --mode skinned | hidden | only
    unity command hm_splatdancers --action hairmotion --mode strands | rigid   # bound hair on the simulated strands (default) / LHM++ LBS
    unity command hm_splatdancers --action haircheck [--from <dance s> --to <dance s>]   # hair pass vs CPU reference, swing, intrusion
    unity command hm_splatdancers --action hairshot --out C:/abs/prefix --azimuths 0,90,180,270 [--radius 1.2]   # stills around her head
    unity command hm_splatdancers --action opacity --mode opaque | avatars | scale
    unity command hm_splatdancers --action path --mode composite | direct
    unity command hm_splatdancers --action skeleton --mode physics | always | never   # the skeleton lines with the opaque splats (default physics)
    unity command hm_splatdancers --action check                 # GPU skinning vs the CPU reference + splat joints vs avatar bones
    unity command hm_splatdancers --action perf_start | perf_report

While on, each dancer whose capture has a splat avatar is drawn as Gaussians instead of the mesh avatar: every Renderer under the avatar
(body, shoes, head props, hair cards, tip glows) gets `forceRenderingOff`, and a `SkinnedSplatAvatar` is posed every frame from the
avatar's own bones, so playback, seeks, slow motion, the film director's sub-frame poses and the film recorder all drive it. The toggle
survives Play-mode restarts for the rest of the editor session (SessionState, so `hm_film record` records splats when it was on); a new
editor session starts with it off. Splats hide with the avatars layer, inside role-hidden spans and when the avatar opacity is 0.

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

## Quest (Android build, 2026-10-21)

The owner chose **splats on by default** on the headset (2026-10-20, over "avatars + splat test switch" and "avatars only"). In a
player build for Android (`SplatDancers.QuestPlayer`) the toggle starts ON; every lesson whose capture has `splats/` shows the
splat dancers (`HasSplatData`): the class recap, and also the Jack & Jill lesson because its `splats/` folder was pushed too (the
owner decides whether it stays); every other lesson shows its mesh avatars exactly as before (no Gaussians feature is added to the
URP renderer for them). The PC default is unchanged (off; `hm_splatdancers --action on`).

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
- **Measured on the headset**: see the table below; the first desk test was blocked (the Quest 3 lay still on the desk in a dark room:
  the system's "Finding position in room" dialog held every launch, logcat `VioInitialization: Skipping: Device is static`). Until a
  run on the headset, the frame rate of the splat recap on a Quest 3 and the look in both eyes are **untested**; switch the splats off
  with `-hmsplats off` or `"splats": false` if they misbehave.

| moment (recap) | splats | Gaussians | fps (VrApi) | GPU ms |
|---|---|---|---|---|
| (not measured yet: see above) | | | | |

## Not done yet

- A Windows player build was not tried; the shader set above should give it the shaders too.
- The Quest frame rate, the look in both eyes and the hair on the headset (above).
