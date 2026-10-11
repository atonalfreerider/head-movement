# The live rhythm score (layer "rhythm")

A capture may carry `rhythm.json` (capture.json `"rhythm"`; written by the analysis pipeline, which owns the recipe, the formula and the confidence
terms). The viewer shows it as a compact HUD: **Rhythm score (estimated from motion, not an official score)**. It is an estimate from the dancers'
footsteps against the song's beat grid, not a judge's mark, and the panel says how sure it is.

## What is on screen

| Element | Meaning |
|---|---|
| big number | the live score, 0..100, a smooth count (the score smoothed over the preceding 0.2 s: still a pure function of time) |
| "confidence low / medium / high" | the analysis's confidence term at this time (grid confidence, steps seen, evidence of any lock, audio-video uncertainty) |
| five bars | beat lock, accents, tempo, flow, couple sync (0 = no better than random steps, full = perfect) |
| sparkline | the score so far over the whole dance (grey stretches: score paused, no beat) |
| pulse dot | brightens on every pulse of the beat grid |
| two lamps | flash at each footstep of the lead and the follow: green on the beat, amber near, red off (the touchdown rings on the floor use the same colours) |
| status line | "no music: score paused" / "DJ mix: score paused" while the score is frozen |
| final card | for the last 4 s of the dance: the score counting up, the five components, the confidence, the range under the audio-video uncertainty, the disclaimer |

The score is a **pure function of time**: it follows seeks and scrubbing exactly, in the free viewer, in directed playback and in the recorder
(`hm_film record` shows the same pixels at the same film time).

## Switching it on

* Free viewer: `hm_layer rhythm on` (off by default; `hm_state` has a `rhythm` block: loaded, visible, score now, shown value, card alpha, lamps).
* A film direction: the segment layer `"rhythm_score": true` (the HUD, placed top left level with the zouk beat counter and under the platform's top band, or
  top right in the free viewer) and `"rhythm_rings": true` (the touchdown rings coloured by class). Without them a film is unchanged.
* The direction's own top-level `"avatar_opacity"` (0..1, the avatars' OPACITY) replaces the viewer's default for every segment that has no `"avatars"` layer.
  Another look key already in the direction format: `"room": false` (no room mesh), `"skeleton_lines"` (stroke width factor).

## VR

The HUD is a flat screen overlay: it is hidden while `FilmDirector.HeadIsCamera` (the VR mode: the user's head is the camera). A world-space panel is not built.

## Files

`Assets/Overlays/RhythmScore.cs` (`RhythmData`: the file; `RhythmScoreHud`: the canvas), hooks in `DanceLayers` (layer, override, state), `FilmDirector`
(segment layers), `FilmOverlay` (placement), `TimingOverlay` (ring colour override), `CaptureManifest` (`rhythm`).
