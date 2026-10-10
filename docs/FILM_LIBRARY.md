# Film library and playback bar

What the viewer shows when it starts, and how a directed film is played and navigated. Part of the viewer's front end
(VIEWER_SPEC.md section 7.1); this file is the data contract of the library and the reference for the two screens.

The viewer's start screen offers **films**, not captures: one card per film with a poster, a title, a subtitle, the length and a
Play button. Playing a film runs its directed show (the film direction of the capture: camera, speed, view states, captions, call-outs,
narration) with a **playback bar**: play / pause, a scrubber across the whole film with **chapter marks**, previous / next chapter,
the chapter's name, the time, a speed button and a button back to the library.

Every other capture folder, take or test capture stays on disk but is **not listed anywhere in the viewer**. Developer commands
(`hm_load`, `hm_film_show`, the film recorder) are unchanged and still reach every capture.

## 1. The library data

`Assets/StreamingAssets/library/library.json` (UTF-8, no byte order mark). The whole `StreamingAssets` folder is git-ignored
(`Assets/StreamingAssets/*` in `.gitignore`; only `.gitkeep` is tracked), so the file may name people and point into the
workspace. Nothing about it is published.

```json
{
  "version": 1,
  "films": [
    {
      "id": "demo",
      "title": "Title shown on the card",
      "subtitle": "One or two lines under the title",
      "capture": "05_Capture",
      "thumbnail": "thumbs/demo.jpg",
      "direction": "dancecap/work/<take>/film/direction.json",
      "durationS": 256.94,
      "chapters": [
        { "title": "Intro", "startS": 0.0, "endS": 11.115 },
        { "title": "Next part", "startS": 11.115, "endS": 28.115 }
      ],
      "available": true
    }
  ]
}
```

| Field | Type | Meaning |
|---|---|---|
| `id` | string | unique; the name used by `hm_library --action play --id` and by the preset file. Letters, digits, `-`, `_`. |
| `title`, `subtitle` | string | shown on the card (the title also on the loading screen). Titles may name people: the file is data. |
| `capture` | string | the capture folder in `StreamingAssets` the film plays on (the direction's own `capture` must match it). |
| `thumbnail` | string | poster image (JPEG or PNG), any aspect; shown whole, never cropped. Relative to `library/` (normally `thumbs/<id>.jpg`). Missing file: a placeholder poster. |
| `direction` | string | the film direction file (`direction.json`). Absolute, or relative: looked up beside `library.json`, then in the project, then in the workspace folder that holds the project. The film's `narration/timeline*.json` and `audio/<name>_mix.wav` are found beside it (the contract of the film director). |
| `durationS` | number or null | the film's length in seconds, for the card. The direction's own length is authoritative while playing. |
| `chapters` | list | `{title, startS, endS?}` in **film** seconds (the clock of the bar). Contiguous: each ends where the next begins, the last at the end of the film; the viewer re-derives the ends. Empty list: no marks, the bar still seeks. |
| `chaptersFile` | string | optional instead of `chapters`: a JSON file holding the list (or `{"chapters": [...]}`). |
| `available` | bool | optional, default true. `false` forces the card to "Processing" even when the files exist (a film held back). |
| `note` | string | optional, free text, not shown. |

A film is **ready** (Play enabled) when its capture folder has `capture.json`, the direction file exists and the film's mix
(`audio/*_mix.wav`) exists; otherwise the card shows **Processing** with a disabled Play button and says what is missing. The viewer
re-reads `library.json` when it changes and re-checks the files once a second, so a card becomes playable on its own when the
pipeline finishes: no restart, no edit. A `library.json` that does not parse shows its error on the screen and lists nothing.

When the file does not exist (a fresh checkout) the viewer behaves as it always did: no library screen, the developer HUD list of
captures.

### Writing the file

The workspace tool is the one writer (`dancecap/dancecap/film_library.py`):

```
python -m dancecap.film_library build --demo       # preset "demo" of dancecap/takes/library/films.toml
python -m dancecap.film_library build --recap      # preset "recap": a card, "Processing" until its film exists
python -m dancecap.film_library build --id x --title T --subtitle S --capture C --direction <path> [--thumbnail <img>] [--chapters-file <plan>] [--copy-film] [--hold]
python -m dancecap.film_library chapters --direction <path> [--plan <plan>]     # preview the chapters
python -m dancecap.film_library list | check
```

`build` adds or replaces one film (by id; the others keep their order), copies the thumbnail to `library/thumbs/`, reads the length and
the chapter times from the direction and writes the file atomically. Chapters come from, in this order: a plan (`{title, segment}`
anchors a chapter at the start of a direction segment, so the marks survive a rebuild of the film; or `{title, startS}`), chapters
written by the direction itself (a `chapter` field on a segment, or a top-level `chapters` list), or an automatic split by the
segment-id prefix.

## 2. The library screen

Shown when the viewer starts (and by the bar's Back button and Esc). A dark full-screen page with the title "Head Movement", the
line "Choose a film" and one card per film:

- a large poster (9:16 posters are shown at full height), the title, the subtitle, the length and the number of chapters, and a
  yellow **Play** button; the selected card has a yellow outline;
- a film that is not ready: a dimmed poster with a "Processing" tag, the line "This film is still being prepared. It will open here as
  soon as it is ready." and a grey, disabled button;
- landscape windows put the cards side by side, portrait windows stack them; everything scales with the window.

Input: the mouse (move over a card to select it, click the card or its Play button), a touch screen (tap), the keyboard (arrow keys or
Tab choose, Enter or Space plays). Clicking a film that is not ready only shows why.

Play loads the film's capture and its phone videos, preloads the soundtrack and shows a loading screen ("Esc cancels"); when the director
reports ready the film starts at 0 with the bar. If the film cannot start, the library comes back with the reason.

The library screen never appears in a recording or an automated command session: it is not created in batch mode or on a headset, it
is not drawn while the Unity Recorder runs (the recorder fixes the frame rate), and any developer command that loads a capture or starts
a film (`hm_load`, `hm_film_show`, the recorder) puts it away.

## 3. The playback bar

Visible in the viewer while a directed film plays, never in a recording (the recorder starts films without a bar, and the bar also
hides itself whenever the frame rate is locked by the recorder).

```
 3/12   Floor craft                                                              1:23 / 4:17
 |=========o---------+---------+----------+-----------+-------------+-------+---------|        <- scrubber, chapter ticks
 [< Library]        [-5 s] [|<] [ (>) ] [>|] [+5 s]                                  [1x]
```

| Element | Behaviour |
|---|---|
| Scrubber | spans the whole film. Click to seek, drag to scrub: the picture follows the pointer, the sound is held and resumes at the release, a film that was paused stays paused. |
| Chapter marks | a tick per chapter start. Hovering shows the time and the chapter's name; near a tick the tooltip snaps to it, and a click on the tick (without dragging) jumps exactly to the chapter's start. |
| Time / chapter | `m:ss / m:ss` and `n/N  chapter title` of the chapter the film is in. |
| Play / pause | also Space. At the end it plays from the start again. |
| Previous / next chapter | also `[` and `]`. Previous restarts the chapter when it is more than 2 s in, else goes to the chapter before. |
| -5 s / +5 s | also Left / Right (they repeat while held). |
| Speed | cycles 0.5x, 1x, 1.5x, 2x (the soundtrack's pitch follows). |
| Library | also Esc: stops the film and returns to the library. Home and End jump to the start and the end. |

The bar fades out three seconds after the last pointer or key activity while the film plays, and is always up while paused, scrubbing or
at the end. It is a screen-space overlay above the film's own overlay (captions, call-outs).

### Seeking

The film's picture is a pure function of the film time: the segment, the dance time, the speed ramps, the camera, the layers and role
fades, the captions and call-outs, the phones' frames and the 3D capture are all evaluated from it every frame, so a seek (forward,
backward, playing or paused) shows exactly the frame at that time. The one stateful part is the pre-mixed soundtrack: `FilmDirector.Seek`
moves it to the new time at once (held while paused or scrubbing), and the player re-syncs if it ever drifts by more than 40 ms.
`Tools/playtest_library.ps1` checks all of it.

All actions are plain public methods (`FilmLibrary.Play / Back`, `FilmPlaybackBar.SeekTo / TogglePlay / NextChapter / PreviousChapter /
CycleRate`, `FilmDirector.Seek / SetPaused / BeginScrub / EndScrub / Rate`); a later VR panel drives the same calls. Nothing in the
screens is XR specific yet: they are desktop and touch screens, drawn in screen space.

## 4. Developer interface

```
unity command hm_library --action state | show | hide | back
unity command hm_library --action play --id demo
unity command hm_library --action seek --at 93.7 | chapter --index 3 | next | prev | pause | resume | toggle | rate --value 1.5 | bar --flag true
unity command hm_film_show --action start --aspect horizontal --audio true     # a film with the soundtrack and the bar, without the library
```

`hm_library --action state` reports the library (films, ready or not and why, the cards' screen rectangles), the bar (visibility, the
scrubber's rectangle, the chapter ticks' screen x, the buttons) and the film (film time, dance time, segment, the soundtrack's position and
its drift against the film clock). `hm_film_show` without `--audio true` starts the film without the bar (review stills).

## 5. Files

| Path | Role |
|---|---|
| `Assets/FilmLibrary/LibraryData.cs` | `library.json` reader, entry probing, chapter helpers, the capture pickers' list |
| `Assets/FilmLibrary/FilmLibrary.cs` | the library screen and the Play / Back flow |
| `Assets/FilmLibrary/FilmPlaybackBar.cs` | the bar |
| `Assets/FilmLibrary/UiPointer.cs` | mouse / touch / key reading for the 2D UI (no EventSystem) |
| `Assets/FilmLibrary/Editor/FilmLibraryCli.cs` | `hm_library` |
| `Assets/Film/FilmDirector.cs`, `FilmSoundtrack.cs` | seek, pause, scrub and rate; the soundtrack's hold / seek |
| `Assets/HeadMovement.cs` | the library folder is not a capture; the HUD list and the digit keys offer the library's captures only |
| `Tools/playtest_library.ps1` | the acceptance test (VIEWER_SPEC section 14) |
