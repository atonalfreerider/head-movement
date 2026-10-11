# The live rhythm score (layer "rhythm")

A capture may carry `rhythm.json` (capture.json `"rhythm"`; written by the analysis pipeline, which owns the recipe, the formula and the confidence
terms). The viewer shows it as a compact HUD: **Rhythm entrainment (estimated from motion, not an official score)**. It is an estimate of how
strongly the dancers' body sway and footsteps are phase-locked to the song's beat grid, not a judge's mark, and the panel says how sure it is. Nothing
validates a mapping from it to judges' scores.

## What is on screen

| Element | Meaning |
|---|---|
| big number | the live score, 0..100, a smooth count (the score smoothed over the preceding 0.2 s: still a pure function of time). It is **greyed** while the beat-lock evidence is thin (`live.evidence` under 0.3) |
| subtitle | "estimated from motion, not an official score"; while the evidence is thin it says "insufficient beat-lock evidence (provisional)" |
| "confidence low / medium / high" | the analysis's confidence term at this time (grid confidence, evidence of a lock against the wrong-tempo baseline, time seen, audio-video uncertainty) |
| five bars | body sway, footwork, recent form, couple sync, accents (0 = what a metronome at the wrong tempo gives, full = the reference level); labels and weights come from the file |
| sparkline | the score so far over the whole dance (grey stretches: score paused, no beat) |
| pulse dot | brightens on every pulse of the beat grid |
| two lamps | flash at each footstep of the lead and the follow: green when the step sits on the dancer's own locked step rhythm, amber near, red off, pale blue (neutral) while no lock is detected (the touchdown rings on the floor use the same colours) |
| status line | "no music: score paused" / "DJ mix: score paused" while the score is frozen |
| final card | for the last 4 s of the dance: the score counting up (greyed, with "insufficient beat-lock evidence", when the whole-dance evidence is thin), the five components, the confidence, the range (estimate noise, audio-video uncertainty, DJ seam), the disclaimer |

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
