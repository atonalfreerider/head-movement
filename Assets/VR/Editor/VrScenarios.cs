using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HmVr.Editor
{
    /// <summary>
    /// The scripted VR scenario (hm_vr --action selftest): on simulated hands, in Play mode, it walks the whole experience the way a
    /// user would - the menu, picking a lesson by pointing and pinching, the hand panel (palm up), play / pause, slow motion, seeking by
    /// 5 s, chapters, the scrubber, the chapter list, viewpoints, teleport, snap turn, the wrist menu button, back to the library - and
    /// records a pass / fail per check with the measured numbers. It is a frame-by-frame state machine on the editor's update loop.
    /// </summary>
    public static class VrScenarios
    {
        sealed class Check
        {
            public string Name, Detail;
            public bool Ok;
        }

        static readonly List<Check> Checks = new();
        static readonly List<string> Log = new();
        static IEnumerator<float> routine;
        static double waitUntil;
        static bool running, done;
        static string current = "";
        static string film;

        static VrSession S => VrMode.Instance?.Session;
        static SimInputSource Sim => VrRig.Instance.Sim;
        static VrPointers P => S.Pointers;

        static string wantFilm;

        public static void Start(string filmId = null)
        {
            if (running) return;
            wantFilm = string.IsNullOrEmpty(filmId) ? null : filmId;
            Checks.Clear();
            Log.Clear();
            done = false;
            running = true;
            routine = Scenario().GetEnumerator();
            waitUntil = 0;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (!running || EditorApplication.timeSinceStartup < waitUntil) return;
            try
            {
                if (!EditorApplication.isPlaying) throw new InvalidOperationException("left Play mode");
                if (routine.MoveNext()) waitUntil = EditorApplication.timeSinceStartup + Mathf.Max(0f, routine.Current);
                else Finish();
            }
            catch (Exception e)
            {
                Add("scenario error in '" + current + "'", false, e.GetType().Name + ": " + e.Message);
                Finish();
            }
        }

        static void Finish()
        {
            running = false;
            done = true;
            EditorApplication.update -= Tick;
            try
            {
                if (VrMode.Instance != null && VrRig.Instance != null && VrRig.Instance.Sim != null)
                {
                    Sim.Rest(Hand.Left);
                    Sim.Rest(Hand.Right);
                }
            }
            catch (Exception)
            {
                // finishing
            }
        }

        public static Dictionary<string, object> Report() => new()
        {
            ["running"] = running, ["done"] = done, ["stage"] = current, ["film"] = film,
            ["passed"] = Checks.Count(c => c.Ok), ["failed"] = Checks.Count(c => !c.Ok),
            ["failures"] = Checks.Where(c => !c.Ok).Select(c => $"{c.Name}: {c.Detail}").ToList(),
            ["checks"] = Checks.Select(c => $"{(c.Ok ? "ok  " : "FAIL")} {c.Name} - {c.Detail}").ToList(), ["log"] = Log
        };

        static void Add(string name, bool ok, string detail = "")
        {
            Checks.Add(new Check { Name = name, Ok = ok, Detail = detail });
            Log.Add($"{(ok ? "ok" : "FAIL")} {name} {detail}");
        }

        static void Stage(string s)
        {
            current = s;
            Log.Add("== " + s);
        }

        // ------------------------------------------------------------------ helpers

        static IEnumerable<float> Wait(float s)
        {
            yield return s;
        }

        static IEnumerable<float> Until(Func<bool> cond, float timeout, string what)
        {
            float t = 0f;
            while (!cond() && t < timeout)
            {
                yield return 0.1f;
                t += 0.1f;
            }

            Add(what, cond(), $"after {t:0.0} s");
        }

        static VrWidget W(string id) => VrCli.FindWidget(id);

        static IEnumerable<float> FarClick(string id, Hand h = Hand.Right)
        {
            Sim.AimAt(h, W(id).WorldCentre, 0f);
            yield return 0.55f;
            string hover = P.Get(h).Hover?.Id;
            Add($"ray hovers {id}", hover == id, $"hover={hover}" + (hover == id ? "" : $" menuOpen={S.Menu.IsOpen} over={S.Menu.OverLesson} rayHit={P.Get(h).RayHit} near={P.Get(h).NearPanel} ({P.Get(h).PokePanel?.name} at {(P.Get(h).PokePanel != null ? P.Get(h).PokePanel.Plane.Depth(VrInput.Instance.Get(h).IndexTip) : 0f):0.00} m) clicks=[{string.Join(" | ", P.ClickLog)}] livePanels=[{string.Join(",", VrPanel.All.Where(p => p.IsLive).Select(p => p.name))}]"));
            Sim.SetPinch(h, 1f);
            yield return 0.3f;
            Sim.SetPinch(h, 0f);
            yield return 0.35f;
        }

        static IEnumerable<float> PokeClick(string id, Hand h = Hand.Right)
        {
            VrWidget w = W(id);
            Sim.PokeAt(h, w.WorldCentre, w.Panel.Plane.Normal, 0.07f);
            yield return 0.5f;
            w = W(id);
            Sim.PokeAt(h, w.WorldCentre, w.Panel.Plane.Normal, -0.012f);
            yield return 0.3f;
            w = W(id);
            Sim.PokeAt(h, w.WorldCentre, w.Panel.Plane.Normal, 0.07f);
            yield return 0.3f;
            // draw the hand back out of the way (in front of the surface) so it does not sweep through it on its next trip
            w = W(id);
            Sim.PokeAt(h, w.WorldCentre, w.Panel.Plane.Normal, 0.22f);
            yield return 0.35f;
        }

        /// <summary>a poke that keeps its widget (for a button that hides its own panel: the widget cannot be looked up again after the press)</summary>
        static IEnumerable<float> PokeClickStable(string id, Hand h = Hand.Right)
        {
            // the press aims at where the widget is NOW (the wrist panel settles as the palm comes up); the retreat reuses that spot because
            // the click may remove the widget
            VrWidget w = W(id);
            Sim.PokeAt(h, w.WorldCentre, w.Panel.Plane.Normal, 0.07f);
            yield return 0.5f;
            w = W(id);
            Vector3 centre = w.WorldCentre, normal = w.Panel.Plane.Normal;
            Sim.PokeAt(h, centre, normal, 0.07f);
            yield return 0.25f;
            w = W(id);
            centre = w.WorldCentre;
            normal = w.Panel.Plane.Normal;
            Sim.PokeAt(h, centre, normal, -0.012f);
            yield return 0.3f;
            Sim.PokeAt(h, centre, normal, 0.22f);
            yield return 0.35f;
        }

        static float FilmRate => FilmDirector.Instance != null ? FilmDirector.Instance.Rate : -1f;

        // ------------------------------------------------------------------ the scenario

        static IEnumerable<float> Scenario()
        {
            Stage("enter VR (simulated hands)");
            FilmLibrary lib = FilmLibrary.Instance;
            Add("library exists", lib != null);
            if (lib == null) yield break;
            if (!VrMode.Active) VrMode.Instance.Enter(true);
            foreach (float w in Until(() => VrMode.Active && S != null && S.Menu != null, 5f, "VR mode active")) yield return w;
            if (!VrMode.Active) yield break;
            Add("the head camera is the main camera", Camera.main == VrRig.Instance.Cam, "Camera.main = " + (Camera.main != null ? Camera.main.name : "none"));
            Add("film directed camera disabled", FilmDirector.HeadIsCamera);
            yield return 1.2f;
            Add("flat library canvas is not drawn", FindFlatCanvases() == 0, "enabled overlay canvases: " + FindFlatCanvases());

            Stage("menu");
            IReadOnlyList<LibraryEntry> films = lib.Films;
            foreach (float w in Until(() => S.Menu.CardPanels.Count == films.Count && S.Menu.CardPanels.Count > 0 && S.Menu.CardPanels[0].Alpha > 0.9f, 5f, "one card per library film")) yield return w;
            Add("menu cards = library entries", S.Menu.CardPanels.Count == films.Count, $"{S.Menu.CardPanels.Count} cards, {films.Count} films");
            foreach (LibraryEntry e in films)
            {
                VrWidget play = VrPanel.All.SelectMany(p => p.Widgets).FirstOrDefault(x => x.Id == "play:" + e.Id);
                Add($"card {e.Id}: button >= 40 mm high", play != null && play.RectMm.height >= 40f && play.RectMm.width >= 40f, play != null ? $"{play.RectMm.width}x{play.RectMm.height} mm" : "missing");
                if (play != null) Add($"card {e.Id}: {(e.Ready ? "playable" : "Processing disabled")}", play.Enabled() == e.Ready, $"ready={e.Ready} enabled={play.Enabled()} label={play.Label.text}");
            }

            // spacing between neighbouring cards: the gap between their edges
            List<VrPanel> cp = S.Menu.CardPanels.ToList();
            for (int i = 0; i + 1 < cp.Count; i++)
            {
                float gap = Vector3.Distance(cp[i].transform.position, cp[i + 1].transform.position) - VrMenu.CardW * 0.001f;
                Add($"cards {i}-{i + 1} gap >= 25 mm", gap >= 0.025f, $"{gap * 1000f:0} mm");
            }

            LibraryEntry pick = films.FirstOrDefault(e => e.Ready && (wantFilm == null || e.Id == wantFilm)) ?? films.FirstOrDefault(e => e.Ready);
            if (pick == null)
            {
                Add("a ready film to play", false, "none in the library");
                yield break;
            }

            // the dominant hand: the footer's switch swaps the pointing and the panel hand
            foreach (float w in FarClick("handedness")) yield return w;
            Add("Pointing: left hand switches the roles", S.Input.Dominant == Hand.Left, "dominant=" + S.Input.Dominant);
            foreach (float w in FarClick("handedness")) yield return w;
            Add("and back to the right hand", S.Input.Dominant == Hand.Right, "dominant=" + S.Input.Dominant);

            film = pick.Id;
            // hover a card with the ray, pinch: starts loading
            foreach (float w in FarClick("play:" + pick.Id)) yield return w;
            Add("a click sound played", VrAudio.Clicks > 0, $"clicks={VrAudio.Clicks}");
            foreach (float w in Until(() => lib.State == FilmLibrary.Mode.Loading || lib.State == FilmLibrary.Mode.Playing, 8f, "pinch on Play starts the lesson")) yield return w;
            foreach (float w in Until(() => lib.State == FilmLibrary.Mode.Playing && FilmDirector.Instance != null && S.Locomotion.Viewpoints.Count > 0, 90f, "the lesson is playing")) yield return w;
            if (lib.State != FilmLibrary.Mode.Playing) yield break;
            yield return 2f;

            Stage("loading did not freeze");
            VrPerf perf = S.Perf;
            Add("a splash with the lesson's title was shown while it loaded", S.Splash.Showings >= 1, $"showings {S.Splash.Showings}, title '{S.Splash.Title}'");
            Add("the loading screen kept moving: no frame over 1000 ms during the load (editor; the headset target is 100 ms)", perf.LoadMaxFrameMs < 1000f, $"{perf.LoadFrames} frames, worst {perf.LoadMaxFrameMs:0} ms, {perf.LoadFramesOver100} over 100 ms, {perf.LoadFramesOver40} over 40 ms; longest build step '{LoadTrace.LongestStep}' {LoadTrace.LongestStepMs:0} ms");
            Add("the capture was built in slices (async load)", HeadMovement.AsyncLoad && !HeadMovement.Instance.IsLoadingCapture);

            Stage("lesson: POV only");
            foreach (float w in Until(() => S.Display.PassthroughActive && S.Placement.Active, 6f, "passthrough is on from the start and the dance is placed")) yield return w;
            Add("passthrough is always on (no immersive wish)", S.Display.Wanted == DisplayMode.Passthrough && MixedReality.Passthrough, $"wanted {S.Display.Wanted} state {S.Display.Current}");
            yield return 0.5f;
            Vector3 headBefore = VrRig.Instance.HeadReal;
            Quaternion rotBefore = VrRig.Instance.Head.localRotation;
            yield return 1.5f;
            float drift = Vector3.Distance(headBefore, VrRig.Instance.HeadReal) + Quaternion.Angle(rotBefore, VrRig.Instance.Head.localRotation);
            Add("the film never moves the head camera (in the room)", drift < 0.01f, $"drift {drift:0.0000} over 1.5 s of film (shot: {FilmDirector.Instance.State()["cameraMode"]})");
            Add("the director's camera mode does not apply", Camera.main == VrRig.Instance.Cam, "Camera.main is the head");
            Add("no flat overlay in the view", FindFlatCanvases() == 0, "overlay canvases enabled: " + FindFlatCanvases());
            Vector3 couple = new(S.Placement.StageCentre.x, 0f, S.Placement.StageCentre.y);
            Vector3 hp = VrRig.Instance.Head.position;
            float dist = new Vector2(hp.x - couple.x, hp.z - couple.z).magnitude;
            Vector3 toCouple = new(couple.x - hp.x, 0f, couple.z - hp.z);
            Vector3 fwd = VrRig.Instance.Head.forward;
            fwd.y = 0f;
            float facing = Vector3.Angle(fwd, toCouple);
            Add("the centre of the whole dance stands just outside its travel from the user", Mathf.Abs(dist - S.Placement.StartDistance) < 0.05f && dist >= VrPlacement.MinStartDistance - 0.05f, $"{dist:0.00} m (start distance {S.Placement.StartDistance:0.00})");
            Add("straight ahead of the user (centre-front)", facing < 6f, $"{facing:0.0} deg off");
            Add("the world is visible in the lesson", VrRig.Instance.Cam.cullingMask == ~0, "cull mask " + VrRig.Instance.Cam.cullingMask);
            Add("viewpoint presets exist (start, front, side, overhead + phones)", S.Locomotion.Viewpoints.Count >= 4, string.Join(", ", S.Locomotion.Viewpoints.Select(v => v.Id)));
            Add("captions panel follows the film's transcript capability", S.Captions.HasTranscript == (VrFilm.CaptionChunks() != null), "transcript=" + S.Captions.HasTranscript);

            Stage("hand panel (palm up)");
            Sim.PalmUp(Hand.Left, true);
            foreach (float w in Until(() => S.HandPanel.Shown, 3f, "the hand panel shows when the left palm is raised")) yield return w;
            yield return 0.6f;
            Vector3 panelPos = S.HandPanel.Main.transform.position;
            Vector3 wristP = S.Input.Left.State.Wrist;
            Add("the hand panel rides on the LEFT wrist (not in front of the user)", Vector3.Distance(panelPos, wristP) < 0.25f, $"{Vector3.Distance(panelPos, wristP):0.00} m from the left wrist");
            Add("it faces the eyes", Vector3.Angle(S.HandPanel.Main.transform.forward, panelPos - VrRig.Instance.Head.position) < 35f, $"{Vector3.Angle(S.HandPanel.Main.transform.forward, panelPos - VrRig.Instance.Head.position):0.0} deg");

            // play / pause by poke
            bool pausedBefore = VrFilm.Paused;
            foreach (float w in PokeClick("play")) yield return w;
            Add("poke on play/pause toggles", VrFilm.Paused != pausedBefore, $"paused {pausedBefore} -> {VrFilm.Paused}");
            foreach (float w in PokeClick("play")) yield return w;
            Add("poke again toggles back", VrFilm.Paused == pausedBefore, $"paused={VrFilm.Paused}");
            Add("the soundtrack is playing with the film", FilmSoundtrack.Current == null || FilmSoundtrack.Current.IsPlaying || VrFilm.Paused, FilmSoundtrack.Current != null ? FilmSoundtrack.Current.Describe() : "no soundtrack");

            // 5 s forward / back
            float t0 = VrFilm.Time;
            foreach (float w in PokeClick("forward 5")) yield return w;
            Add("+5 s", VrFilm.Time - t0 > 4.5f && VrFilm.Time - t0 < 8.5f, $"{t0:0.0} -> {VrFilm.Time:0.0}");
            t0 = VrFilm.Time;
            foreach (float w in PokeClick("back 5")) yield return w;
            Add("-5 s", t0 - VrFilm.Time > 3f && t0 - VrFilm.Time < 7f, $"{t0:0.0} -> {VrFilm.Time:0.0}");

            // chapters
            if (VrFilm.Chapters.Count > 1)
            {
                int c0 = VrFilm.ChapterIndex;
                foreach (float w in PokeClick("next chapter")) yield return w;
                Add("next chapter", VrFilm.ChapterIndex == c0 + 1 || VrFilm.ChapterIndex > c0, $"chapter {c0} -> {VrFilm.ChapterIndex}");
                int c1 = VrFilm.ChapterIndex;
                foreach (float w in PokeClick("next chapter")) yield return w;
                foreach (float w in PokeClick("prev chapter")) yield return w;
                Add("previous chapter", VrFilm.ChapterIndex <= c1 + 1 && VrFilm.ChapterIndex >= c1, $"chapter {c1} -> {VrFilm.ChapterIndex}");
            }

            // slow motion: a music lesson's sound slows with the picture (pitch = the rate); a dialogue lesson's is faded out instead (no speech two
            // octaves down) while its captions and the film clock go on
            Stage("slow motion");
            if (VrFilm.Paused) VrFilm.Resume();
            bool muteSlow = FilmDirector.Instance != null && FilmDirector.Instance.SlowAudio == SlowAudioMode.Mute;
            foreach (float w in PokeClick("rate 0.25x")) yield return w;
            Add("slow motion 0.25x sets the film rate", Mathf.Approximately(FilmRate, 0.25f), $"rate={FilmRate}");
            yield return 0.4f;
            float ta = VrFilm.Time;
            yield return 3f;
            float advanced = VrFilm.Time - ta;
            Add("the film clock runs at a quarter speed", advanced > 0.45f && advanced < 1.1f, $"{advanced:0.00} film s in 3 real s (rate {FilmRate})");
            if (FilmSoundtrack.Current != null)
            {
                FilmSoundtrack st = FilmSoundtrack.Current;
                if (muteSlow)
                {
                    Add("dialogue: the soundtrack is silent in slow motion (faded out, not pitched down)", st.Gain < 0.01f && st.Volume * st.Gain < 0.01f, $"gain={st.Gain:0.00}");
                    Add("dialogue: the hand panel says the dialogue is paused", VrFilm.SlowAudioHint.Length > 0 && VrHandPanel.Instance.SlowNoteText == VrFilm.SlowAudioHint, $"'{VrHandPanel.Instance.SlowNoteText}'");
                    Add("dialogue: the player keeps its place against the film clock", Mathf.Abs(st.AudioTime - VrFilm.Time) < 0.25f, $"audio {st.AudioTime:0.00} vs film {VrFilm.Time:0.00}");
                }
                else
                {
                    float pitch = st.Pitch;
                    Add("the soundtrack follows the rate (pitch, as the flat speed button)", Mathf.Abs(pitch - 0.25f) < 0.02f, $"pitch={pitch:0.00}");
                    Add("the soundtrack stays with the film", Mathf.Abs(st.AudioTime - VrFilm.Time) < 0.25f, $"audio {st.AudioTime:0.00} vs film {VrFilm.Time:0.00}");
                    Add("music: the sound stays audible in slow motion (no hint, full level)", st.Gain > 0.99f && VrFilm.SlowAudioHint.Length == 0, $"gain={st.Gain:0.00}");
                }
            }

            foreach (float w in PokeClick("rate 0.1x")) yield return w;
            Add("slow motion 0.1x", Mathf.Approximately(FilmRate, 0.1f), $"rate={FilmRate}");
            if (muteSlow && FilmSoundtrack.Current != null) Add("dialogue: still silent at 0.1x", FilmSoundtrack.Current.Gain < 0.01f, $"gain={FilmSoundtrack.Current.Gain:0.00}");
            foreach (float w in PokeClick("rate 1x")) yield return w;
            Add("back to 1x", Mathf.Approximately(FilmRate, 1f), $"rate={FilmRate}");
            yield return 0.4f;
            if (FilmSoundtrack.Current != null)
            {
                Add("back at 1x the sound is back at full level, normal pitch", FilmSoundtrack.Current.Gain > 0.99f && Mathf.Abs(FilmSoundtrack.Current.EffectivePitch - 1f) < 0.01f,
                    $"gain={FilmSoundtrack.Current.Gain:0.00} pitch={FilmSoundtrack.Current.EffectivePitch:0.00}");
                Add("back at 1x the hand panel's note is gone", VrFilm.SlowAudioHint.Length == 0 && VrHandPanel.Instance.SlowNoteText.Length == 0, $"'{VrHandPanel.Instance.SlowNoteText}'");
            }

            // scrubber: pinch-drag with the pointing ray from a step back (the panel pinned in the air), poke-drag otherwise
            Stage("scrubber");
            float dur = VrFilm.Duration;
            VrWidget scrub = W("scrubber");
            Sim.PokeAt(Hand.Right, scrub.Panel.Content.TransformPoint(new Vector3(scrub.RectMm.xMin + 0.5f * scrub.RectMm.width, scrub.RectMm.center.y, 0f)), scrub.Panel.Plane.Normal, 0.07f);
            yield return 0.5f;
            Sim.PokeAt(Hand.Right, scrub.Panel.Content.TransformPoint(new Vector3(scrub.RectMm.xMin + 0.5f * scrub.RectMm.width, scrub.RectMm.center.y, 0f)), scrub.Panel.Plane.Normal, -0.012f);
            yield return 0.35f;
            Add("poking the scrubber holds the film (scrubbing)", FilmDirector.Instance.Scrubbing, "scrubbing=" + FilmDirector.Instance.Scrubbing);
            Add("scrub to the middle", Mathf.Abs(VrFilm.Time - 0.5f * dur) < 0.04f * dur, $"time {VrFilm.Time:0.0} of {dur:0.0}");
            // drag to 25 %
            for (int i = 1; i <= 6; i++)
            {
                float u = Mathf.Lerp(0.5f, 0.25f, i / 6f);
                scrub = W("scrubber");
                Sim.PokeAt(Hand.Right, scrub.Panel.Content.TransformPoint(new Vector3(scrub.RectMm.xMin + u * scrub.RectMm.width, scrub.RectMm.center.y, 0f)), scrub.Panel.Plane.Normal, -0.012f);
                yield return 0.12f;
            }

            yield return 0.3f;
            Add("drag the scrubber to a quarter", Mathf.Abs(VrFilm.Time - 0.25f * dur) < 0.05f * dur, $"time {VrFilm.Time:0.0} of {dur:0.0}");
            scrub = W("scrubber");
            Sim.PokeAt(Hand.Right, scrub.WorldCentre, scrub.Panel.Plane.Normal, 0.07f);
            yield return 0.5f;
            Add("letting go ends the scrub and the film plays on", !FilmDirector.Instance.Scrubbing, "scrubbing=" + FilmDirector.Instance.Scrubbing);
            if (FilmSoundtrack.Current != null)
            {
                yield return 1f;
                Add("audio re-synced after the seek", Mathf.Abs(FilmSoundtrack.Current.AudioTime - VrFilm.Time) < 0.4f, $"audio {FilmSoundtrack.Current.AudioTime:0.00} vs film {VrFilm.Time:0.00}");
            }

            // chapter list
            Stage("chapter list");
            if (VrFilm.Chapters.Count > 3)
            {
                foreach (float w in PokeClick("tab chapters")) yield return w;
                Add("the chapter list opens", S.HandPanel.ShowChapters && S.HandPanel.ChaptersList.Panel.IsShown, "shown=" + S.HandPanel.ChaptersList.Panel.IsShown);
                yield return 0.5f;
                // a row of the page that is showing (the list opens on the current chapter's page): the last one
                string rowId = S.HandPanel.ChaptersList.Panel.Widgets.Where(x => x.Id.StartsWith("row:")).Select(x => x.Id).Last();
                int target = int.Parse(rowId.Substring(4));
                foreach (float w in PokeClick(rowId)) yield return w;
                float want = VrFilm.Chapters[target].StartS;
                Add("poking a chapter row jumps to it", Mathf.Abs(VrFilm.Time - want) < 4f, $"time {VrFilm.Time:0.0}, chapter {target} starts {want:0.0}");
                foreach (float w in PokeClick("tab chapters")) yield return w;
            }

            // viewpoints
            Stage("viewpoints (none in passthrough)");
            Add("no viewpoint jumps in passthrough: the Views tab is disabled (the floor stays fixed)", !W("tab views").Enabled(), "enabled=" + W("tab views").Enabled());
            Sim.PalmUp(Hand.Left, false);
            yield return 1.5f;
            Add("the wrist panel goes when the palm is lowered (it appears nowhere but on the wrist)", !S.HandPanel.Shown, $"alpha {S.HandPanel.Alpha:0.00}");

            // the anchored panel: from a step back it is out of arm's reach and works with the pointing ray and a pinch
            Stage("wrist panel: poke and poke-drag");
            Sim.PalmUp(Hand.Left, true);
            yield return 1.2f;
            Add("the panel is on the wrist, never anchored in the room", !S.HandPanel.Anchored && S.HandPanel.Shown);
            if (VrFilm.Time > VrFilm.Duration - 14f)
            {
                VrFilm.SeekTo(VrFilm.Duration * 0.3f); // a short film may have played to its end by now: +5 s needs room
                yield return 1.0f;
            }

            float tFar = VrFilm.Time;
            foreach (float w in PokeClick("forward 5")) yield return w;
            Add("poke on +5 s (the wrist panel is within reach)", VrFilm.Time - tFar > 4.5f && VrFilm.Time - tFar < 9f, $"{tFar:0.0} -> {VrFilm.Time:0.0}");
            // the scrubber: press the fingertip in and slide it along (a slider follows a pressed fingertip)
            float durFar = VrFilm.Duration;
            Vector3 AtU(float u)
            {
                VrWidget fs = W("scrubber");
                return fs.Panel.Content.TransformPoint(new Vector3(fs.RectMm.xMin + u * fs.RectMm.width, fs.RectMm.center.y, 0f));
            }

            Vector3 Nrm() => W("scrubber").Panel.Plane.Normal;
            Sim.PokeAt(Hand.Right, AtU(0.7f), Nrm(), 0.07f);
            yield return 0.45f;
            Sim.PokeAt(Hand.Right, AtU(0.7f), Nrm(), -0.01f);
            yield return 0.35f;
            Add("pressing the scrubber holds the film", FilmDirector.Instance.Scrubbing, "scrubbing=" + FilmDirector.Instance.Scrubbing);
            for (int i = 1; i <= 8; i++)
            {
                Sim.PokeAt(Hand.Right, AtU(Mathf.Lerp(0.7f, 0.3f, i / 8f)), Nrm(), -0.01f);
                yield return 0.15f;
            }

            yield return 0.3f;
            Add("sliding the fingertip drags the scrubber to 30 %", Mathf.Abs(VrFilm.Time - 0.3f * durFar) < 0.05f * durFar, $"time {VrFilm.Time:0.0} of {durFar:0.0}");
            Sim.PokeAt(Hand.Right, AtU(0.3f), Nrm(), 0.12f);
            yield return 0.6f;
            Add("lifting the fingertip ends the scrub", !FilmDirector.Instance.Scrubbing, "scrubbing=" + FilmDirector.Instance.Scrubbing);
            Sim.Rest(Hand.Right);
            Sim.PalmUp(Hand.Left, false);
            yield return 1.5f;

            foreach (float w in PinchStage()) yield return w;

            foreach (float w in PassthroughStage()) yield return w;

            // the menu lives only on the left wrist: no back-of-wrist button, no menu arc over the lesson
            Stage("menu only on the left wrist, back to the library");
            Sim.Rest(Hand.Left);
            Sim.HoldOut(Hand.Left, -0.05f, 0f);
            yield return 1.0f;
            Add("no back-of-wrist menu button in a lesson (the menu is the wrist panel)", S.Wrist.Alpha < 0.05f, $"alpha {S.Wrist.Alpha:0.00}");
            Add("no menu over the lesson", !S.Menu.IsOpen, $"open={S.Menu.IsOpen}");
            Sim.Rest(Hand.Left);
            yield return 0.6f;
            Sim.PalmUp(Hand.Left, true);
            foreach (float w in Until(() => S.HandPanel.Shown, 4f, "the wrist panel shows for Back to library")) yield return w;
            yield return 0.4f;
            foreach (float w in PokeClickStable("library")) yield return w; // the click ends the lesson: the panel goes with it
            foreach (float w in Until(() => lib.State == FilmLibrary.Mode.Library, 6f, "Back to library returns to the library")) yield return w;
            yield return 1.0f;
            Add("the lesson is stopped cleanly", FilmDirector.Instance == null && FilmPlaybackBar.Instance == null, $"director={(FilmDirector.Instance != null)} bar={(FilmPlaybackBar.Instance != null)}");
            Add("the soundtrack is stopped", FilmSoundtrack.Current == null || !FilmSoundtrack.Current.IsPlaying, FilmSoundtrack.Current != null ? FilmSoundtrack.Current.Describe() : "none");
            Add("the VR menu is back", S.Menu.IsOpen && !S.Menu.OverLesson, $"open={S.Menu.IsOpen}");
            Add("the lobby is a quiet empty space (world culled)", VrRig.Instance.Cam.cullingMask == 1 << VrStyle.Layer, "cull mask " + VrRig.Instance.Cam.cullingMask);
            Add("the rig's camera pose is untouched by the stop", Camera.main == VrRig.Instance.Cam, "main camera is the head");

            Stage("exit VR");
            Sim.Rest(Hand.Left);
            Sim.Rest(Hand.Right);
            yield return 1.5f; // the right hand comes back from poking the wrist panel: its pointing ray settles before it aims
            foreach (float w in FarClick("exit vr")) yield return w;
            foreach (float w in Until(() => !VrMode.Active, 5f, "Exit VR leaves VR mode")) yield return w;
            yield return 1.0f;
            Camera view = DanceText.ViewCamera;
            Add("the desktop camera is back", VrRig.Instance == null && view != null && view.name == "Camera", "view camera: " + (view != null ? view.name : "none"));
            Add("the flat library is shown again", lib.State == FilmLibrary.Mode.Library, "library " + lib.State);
            Add("no VR objects remain", GameObject.Find("VR Rig") == null && GameObject.Find("VR Session") == null && VrPanel.All.Count == 0, $"panels left: {VrPanel.All.Count}");
            Add("the director's camera is free again", !FilmDirector.HeadIsCamera);
            Stage("done");
        }

        // ------------------------------------------------------------------ pinches: one hand never moves the floor; two hands slide and turn it; the god's eye

        static IEnumerable<float> OneHandDrag(Hand side, Vector3 from, Vector3 to, int steps = 8)
        {
            Sim.HandAtHead(side, from, 0f, 0f);
            yield return 0.5f;
            Sim.HandAtHead(side, from, 0f, 1f);
            yield return 0.3f;
            for (int i = 1; i <= steps; i++)
            {
                Sim.HandAtHead(side, Vector3.Lerp(from, to, i / (float)steps), 0f, 1f);
                yield return 0.06f;
            }

            yield return 0.2f;
            Sim.SetPinch(side, 0f);
            yield return 0.3f;
        }

        static IEnumerable<float> TwoHandDrag(Vector3 l0, Vector3 r0, Vector3 l1, Vector3 r1, bool release = true)
        {
            Sim.HandAtHead(Hand.Left, l0, 0f, 0f);
            Sim.HandAtHead(Hand.Right, r0, 0f, 0f);
            yield return 0.5f;
            Sim.HandAtHead(Hand.Left, l0, 0f, 1f);
            Sim.HandAtHead(Hand.Right, r0, 0f, 1f);
            yield return 0.3f;
            for (int i = 1; i <= 10; i++)
            {
                Sim.HandAtHead(Hand.Left, Vector3.Lerp(l0, l1, i / 10f), 0f, 1f);
                Sim.HandAtHead(Hand.Right, Vector3.Lerp(r0, r1, i / 10f), 0f, 1f);
                yield return 0.06f;
            }

            yield return 0.25f;
            if (!release) yield break;
            Sim.SetPinch(Hand.Left, 0f);
            Sim.SetPinch(Hand.Right, 0f);
            yield return 0.3f;
        }

        static IEnumerable<float> PinchStage()
        {
            Stage("pinches: one hand never moves the floor");
            VrPlacement pl = S.Placement;
            VrRig rig = VrRig.Instance;
            Sim.Rest(Hand.Left);
            Sim.Rest(Hand.Right);
            if (VrFilm.Paused) VrFilm.Resume();
            yield return 0.8f;
            Vector3 origin0 = pl.OriginReal;
            float yaw0 = pl.Current.YawDeg;
            int changes0 = pl.Changes;
            foreach (float w in OneHandDrag(Hand.Right, new Vector3(0.05f, -0.3f, 0.4f), new Vector3(0.25f, -0.3f, 0.25f))) yield return w;
            foreach (float w in OneHandDrag(Hand.Left, new Vector3(-0.05f, -0.3f, 0.4f), new Vector3(-0.3f, -0.3f, 0.5f))) yield return w;
            Sim.HandAtHead(Hand.Right, new Vector3(0.1f, -0.3f, 0.4f), 0f, 0f);
            yield return 0.4f;
            Sim.SetPinch(Hand.Right, 1f);
            yield return 0.2f;
            Sim.SetPinch(Hand.Right, 0f);
            yield return 0.4f;
            Add("one-hand drags and taps leave the floor exactly where it is", Vector3.Distance(pl.OriginReal, origin0) < 1e-5f && Mathf.Abs(pl.Current.YawDeg - yaw0) < 1e-4f && pl.Changes == changes0,
                $"origin moved {Vector3.Distance(pl.OriginReal, origin0):0.000000} m, yaw {yaw0:0.00} -> {pl.Current.YawDeg:0.00}, rig changes {changes0}->{pl.Changes}, one-hand pinches {pl.OneHandPinches}");

            Stage("two-hand pinch: slide and turn the floor");
            // slide: both hands move the same way
            int moves0 = pl.Moves;
            Vector2 a0 = pl.Current.Anchor;
            Vector3 l0 = new(-0.15f, -0.3f, 0.4f), r0 = new(0.15f, -0.3f, 0.4f), move = new(0.2f, 0f, -0.1f);
            foreach (float w in TwoHandDrag(l0, r0, l0 + move, r0 + move)) yield return w;
            Vector2 slid = pl.Current.Anchor - a0;
            Vector3 moveReal = Quaternion.Euler(0f, rig.HeadRealYawDeg, 0f) * move;
            Add("both hands dragging slide the floor with them", (slid - new Vector2(moveReal.x, moveReal.z)).magnitude < 0.03f, $"slid ({slid.x:0.00}, {slid.y:0.00}) for a hand move of ({moveReal.x:0.00}, {moveReal.z:0.00})");
            Add("a slide does not turn it", Mathf.Abs(Mathf.DeltaAngle(yaw0, pl.Current.YawDeg)) < 0.5f, $"yaw {yaw0:0.0} -> {pl.Current.YawDeg:0.0}");
            Add("one floor move counted", pl.Moves == moves0 + 1, $"moves {moves0}->{pl.Moves}");
            // turn: the hands circle about their midpoint by 30 degrees (seen from above, clockwise)
            float yaw1 = pl.Current.YawDeg;
            Vector3 mid = (l0 + r0) * 0.5f;
            float LineDeg()
            {
                Vector3 la = rig.ToReal(S.Input.Left.PinchPoint), ra = rig.ToReal(S.Input.Right.PinchPoint);
                return Mathf.Atan2(ra.x - la.x, ra.z - la.z) * Mathf.Rad2Deg;
            }

            Sim.HandAtHead(Hand.Left, l0, 0f, 0f);
            Sim.HandAtHead(Hand.Right, r0, 0f, 0f);
            yield return 0.5f;
            Sim.HandAtHead(Hand.Left, l0, 0f, 1f);
            Sim.HandAtHead(Hand.Right, r0, 0f, 1f);
            yield return 0.3f;
            float line0 = LineDeg();
            for (int i = 1; i <= 10; i++)
            {
                Quaternion qi = Quaternion.Euler(0f, 30f * i / 10f, 0f);
                Sim.HandAtHead(Hand.Left, mid + qi * (l0 - mid), 0f, 1f);
                Sim.HandAtHead(Hand.Right, mid + qi * (r0 - mid), 0f, 1f);
                yield return 0.06f;
            }

            yield return 0.25f;
            float turned = Mathf.DeltaAngle(line0, LineDeg());
            Add("turning the hands turns the floor by the same angle as the line between the pinches", turned > 15f && Mathf.Abs(Mathf.DeltaAngle(yaw1, pl.Current.YawDeg) - turned) < 1f,
                $"pinch line turned {turned:0.0} deg, floor yaw {yaw1:0.0} -> {pl.Current.YawDeg:0.0}");
            Sim.SetPinch(Hand.Left, 0f);
            Sim.SetPinch(Hand.Right, 0f);
            yield return 0.3f;
            // spread: no scaling ever, never lifted
            foreach (float w in TwoHandDrag(l0, r0, l0 + new Vector3(-0.15f, 0f, 0f), r0 + new Vector3(0.15f, 0f, 0f))) yield return w;
            Add("spreading the hands never scales the floor, nor lifts it", pl.Current.Scale == 1f && rig.RigScale == 1f && rig.transform.lossyScale == Vector3.one && Mathf.Abs(rig.FloorY) < 1e-4f,
                $"scale {pl.Current.Scale} rig {rig.RigScale} floor y {rig.FloorY:0.0000}");
            Sim.Rest(Hand.Left);
            Sim.Rest(Hand.Right);
            yield return 0.4f;

            Stage("god's eye: an invisible button 10 ft above the heads");
            VrSkyTarget sky = S.Sky;
            yield return 0.8f;
            Add("the god's eye button is live but not drawn", sky != null && sky.Shown && !sky.Lit, sky != null ? $"shown {sky.Shown} lit {sky.Lit}" : "missing");
            if (sky == null || !sky.Shown) yield break;
            HeadMovement hm = HeadMovement.Instance;
            int f = hm.CurrentFrame;
            float heads = Mathf.Max(hm.LeadDancer.Joint(f, SmplJoint.Head).y, hm.FollowDancer.Joint(f, SmplJoint.Head).y);
            Add("about 10 ft above the dancers' heads", Mathf.Abs(sky.Position.y - heads - VrSkyTarget.AboveHeadsM) < 0.35f, $"{sky.Position.y - heads:0.00} m above the heads");
            DancePlacement before = pl.Current.Clone();
            Sim.AimAt(Hand.Right, sky.Position, 0f);
            yield return 0.6f;
            Add("the ray finds it and it lights up", ReferenceEquals(S.Pointers.Get(Hand.Right).WorldHover, sky) && sky.Lit, $"hover {(S.Pointers.Get(Hand.Right).WorldHover != null)} lit {sky.Lit}");
            Sim.SetPinch(Hand.Right, 1f);
            yield return 0.25f;
            Sim.SetPinch(Hand.Right, 0f);
            yield return 1.3f;
            Vector3 head = rig.Head.position;
            Vector2 c = pl.StageCentre;
            Add("a pinch on it lifts the user above the dance", pl.GodsEyeOn && Mathf.Abs(head.y - VrSkyTarget.EyeAboveFloorM) < 0.2f, $"god's eye {pl.GodsEyeOn}, eyes {head.y:0.00} m above the dance floor");
            Add("over the centre of the dance", new Vector2(head.x - c.x, head.z - c.y).magnitude < 0.6f, $"{new Vector2(head.x - c.x, head.z - c.y).magnitude:0.00} m off");
            Add("the button is gone up there", !sky.Shown && !sky.Lit);
            // a short pinch aimed at the dance floor returns to the ground: exactly the placement before
            Vector3 fwd = rig.Head.forward;
            fwd.y = 0f;
            Vector3 floorAim = new Vector3(head.x, 0f, head.z) + (fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward) * 1.2f;
            Sim.AimAt(Hand.Right, floorAim, 0f);
            yield return 0.6f;
            Sim.SetPinch(Hand.Right, 1f);
            yield return 0.25f;
            Sim.SetPinch(Hand.Right, 0f);
            yield return 1.3f;
            Add("a pinch at the floor returns to the ground", !pl.GodsEyeOn && Mathf.Abs(rig.FloorY) < 1e-4f, $"god's eye {pl.GodsEyeOn}, floor y {rig.FloorY:0.0000}");
            Add("with the floor exactly where it was", (pl.Current.Anchor - before.Anchor).magnitude < 1e-4f && Mathf.Abs(pl.Current.YawDeg - before.YawDeg) < 1e-3f,
                $"anchor moved {(pl.Current.Anchor - before.Anchor).magnitude:0.000000} m, yaw {before.YawDeg:0.00} -> {pl.Current.YawDeg:0.00}");
            Sim.Rest(Hand.Right);
            yield return 0.5f;
        }

        // ------------------------------------------------------------------ passthrough mixed reality (simulated: no headset, no camera feed)

        static Vector3 PelvisOf(Role role, int frame)
        {
            HeadMovement hm = HeadMovement.Instance;
            Dancer d = role == Role.Lead ? hm.LeadDancer : hm.FollowDancer;
            return d.Joint(frame, SmplJoint.Pelvis);
        }

        static IEnumerable<float> PassthroughStage()
        {
            Stage("passthrough: mixed reality, fixed origin");
            VrDisplay d = S.Display;
            VrPlacement pl = S.Placement;
            VrRig rig = VrRig.Instance;
            HeadMovement hm = HeadMovement.Instance;
            Add("the display controller and the placement exist", d != null && pl != null);
            if (d == null || pl == null) yield break;
            Sim.Rest(Hand.Left);
            Sim.Rest(Hand.Right);
            // the room mesh on (if the capture has one) and the floor grid, so the passthrough has something to hide
            bool hasRoom = false;
            try
            {
                hm.SetLayerVisible("room", true);
                hm.SetLayerVisible("grid", true);
                GameObject room0 = GameObject.Find("Room");
                hasRoom = room0 != null && room0.transform.Find("Room Mesh") != null;
            }
            catch (Exception)
            {
                // a capture without a room mesh
            }

            yield return 0.8f;
            Add("passthrough is on (simulated)", d.PassthroughActive && pl.Active && pl.Current != null);
            Add("mixed-reality flag is set for the scene layers", MixedReality.Passthrough);
            Add("the camera clears to alpha 0 on a solid colour", rig.Cam.clearFlags == CameraClearFlags.SolidColor && rig.Cam.backgroundColor.a == 0f, $"flags {rig.Cam.clearFlags} alpha {rig.Cam.backgroundColor.a}");
            GameObject room = GameObject.Find("Room");
            Transform rm = room != null ? room.transform.Find("Room Mesh") : null;
            Add("no room mesh in passthrough", rm == null || !rm.gameObject.activeInHierarchy, hasRoom ? "room mesh object " + (rm != null && rm.gameObject.activeInHierarchy ? "ACTIVE" : "hidden / not loaded") : "this capture has no room mesh");
            GameObject plane = GameObject.Find("Floor plane");
            Add("no floor fill in passthrough", plane == null || !plane.activeInHierarchy, plane == null ? "no plane" : "plane active=" + plane.activeInHierarchy);
            Add("no teleport or snap turn in passthrough (the user walks; only two hands move the floor)", !S.Locomotion.Enabled);
            Add("the hands are drawn minimal (the real hands are in view)", S.Hands.Minimal);
            Add("the real floor is the capture's floor (rig on y = 0)", Mathf.Abs(rig.FloorY) < 1e-4f, $"rig floor y {rig.FloorY:0.0000}");
            Add("scale is exactly 1.0", pl.Current.Scale == 1f && rig.RigScale == 1f && rig.transform.lossyScale == Vector3.one, $"placement {pl.Current.Scale:0.000000} rig {rig.RigScale:0.000000}");

            // the origin is FIXED: over playback and seeks the floor never moves, drifts, re-centres or follows; the dancers travel in the room
            if (VrFilm.Paused) VrFilm.Resume();
            Vector3 origin0 = pl.OriginReal;
            Vector3 rigPos0 = rig.transform.position;
            Quaternion rigRot0 = rig.transform.rotation;
            float tPlay0 = VrFilm.Time;
            int fa0 = hm.CurrentFrame;
            yield return 10f;
            Add("the film played for 10 s", VrFilm.Time - tPlay0 > 7f || VrFilm.Time >= VrFilm.Duration - 0.5f, $"{tPlay0:0.0} -> {VrFilm.Time:0.0}");
            Add("the origin did not move over 10 s of playback", Vector3.Distance(pl.OriginReal, origin0) < 1e-5f && Vector3.Distance(rig.transform.position, rigPos0) < 1e-5f && Quaternion.Angle(rig.transform.rotation, rigRot0) < 1e-3f,
                $"origin moved {Vector3.Distance(pl.OriginReal, origin0):0.000000} m, rig {Vector3.Distance(rig.transform.position, rigPos0):0.000000} m");
            float dur = VrFilm.Duration;
            VrFilm.SeekTo(dur * 0.25f);
            yield return 1.2f;
            int fa = hm.CurrentFrame;
            Vector3 la = PelvisOf(Role.Lead, fa);
            VrFilm.SeekTo(dur * 0.75f);
            yield return 1.2f;
            int fb = hm.CurrentFrame;
            Vector3 lb = PelvisOf(Role.Lead, fb);
            Add("seeks: the origin stayed", Vector3.Distance(pl.OriginReal, origin0) < 1e-5f && Vector3.Distance(rig.transform.position, rigPos0) < 1e-5f);
            Add("the dancers keep their captured paths (not re-centred)", new Vector2(lb.x - la.x, lb.z - la.z).magnitude > 0.05f || new Vector2(la.x, la.z).magnitude > 0.05f,
                $"frame {fa} -> {fb}: lead pelvis ({la.x:0.00},{la.z:0.00}) -> ({lb.x:0.00},{lb.z:0.00}) (start frame {fa0})");

            // skeletons at full opacity
            Stage("skeletons at full opacity");
            // the solid skeleton (Dancer.SetSolidSkeleton): alpha 1, opaque-queue depth-correct material with ZWrite on, plus the over-the-bodies pass; the bodies' stencil skip is off
            string skeletonReport = Dancer.SkeletonReport(out bool solidOk);
            Add("the skeleton is SOLID whatever the avatar opacity (alpha 1, no blending, opaque queue + ZWrite, second pass over the bodies)", Dancer.SolidSkeletonOn && solidOk, skeletonReport);
            foreach (SmplxAvatar a in hm.Avatars.Values) Add($"{a.DancerRole}: the body is translucent ({a.Opacity:0.00}) and the skeleton does not depend on it", Dancer.SolidSkeletonOn && !SmplxAvatar.SkeletonsOverBodies, $"opacity {a.Opacity:0.00}");
            Add("the HUD, floor craft and overlays keep their own opacity (no global alpha)", true);

            // the wrist panel tracks the left wrist: it moves with the hand, not with the head
            Stage("wrist panel tracks the left wrist");
            Sim.HeadYawLocalDeg = 0f;
            Sim.PalmUp(Hand.Left, true);
            foreach (float w in Until(() => S.HandPanel.Shown, 3f, "raising the left palm shows the wrist panel")) yield return w;
            yield return 0.8f;
            Vector3 wr0 = S.Input.Left.State.Wrist, hp0 = S.HandPanel.AnchorPosition;
            Sim.HeadYawLocalDeg = 25f;
            yield return 1.2f;
            Vector3 wr1 = S.Input.Left.State.Wrist, hp1 = S.HandPanel.AnchorPosition;
            Add("the panel keeps its place on the wrist while the head turns", Vector3.Distance(hp1 - wr1, hp0 - wr0) < 0.06f, $"offset changed {Vector3.Distance(hp1 - wr1, hp0 - wr0):0.000} m");
            Add("the panel stays within reach of the wrist", Vector3.Distance(hp1, wr1) < 0.25f, $"{Vector3.Distance(hp1, wr1):0.00} m");
            Sim.HeadYawLocalDeg = 0f;
            Sim.PalmUp(Hand.Left, false);
            yield return 1.6f;
            Add("and it is gone with the palm down", !S.HandPanel.Shown, $"alpha {S.HandPanel.Alpha:0.00}");

            // physics / rhythm: a permanent toggle on the wrist that no lesson segment overrides
            Stage("physics / rhythm toggle (permanent)");
            bool phys0 = VrSkeletonPref.Physics;
            Sim.PalmUp(Hand.Left, true);
            yield return 1.0f;
            foreach (float w in PokeClick("skeleton mode")) yield return w;
            yield return 0.4f;
            Add("the wrist toggle switches the mode", VrSkeletonPref.Physics != phys0 && hm.SkeletonMode == (VrSkeletonPref.Physics ? SkeletonStyle.Mode.Physics : SkeletonStyle.Mode.Rhythm), $"pref {VrSkeletonPref.Physics}, viewer {hm.SkeletonMode}");
            Add("it is remembered", PlayerPrefs.GetString(VrSkeletonPref.PrefKey, "rhythm") == (VrSkeletonPref.Physics ? "physics" : "rhythm"));
            VrFilm.SeekTo(VrFilm.Duration * 0.6f);
            yield return 1.5f;
            VrFilm.SeekTo(VrFilm.Duration * 0.15f);
            yield return 1.5f;
            Add("no lesson segment overrides it", hm.SkeletonMode == (VrSkeletonPref.Physics ? SkeletonStyle.Mode.Physics : SkeletonStyle.Mode.Rhythm), $"viewer {hm.SkeletonMode}");
            foreach (float w in PokeClick("skeleton mode")) yield return w;
            yield return 0.4f;
            Add("toggling again restores it", VrSkeletonPref.Physics == phys0, $"pref {VrSkeletonPref.Physics}");
            Sim.PalmUp(Hand.Left, false);
            yield return 0.8f;

            Add("there is no chapter ring", UnityEngine.Object.FindObjectsByType<LineRenderer>(FindObjectsSortMode.None).All(l => !l.name.ToLowerInvariant().Contains("chapter")));

            // captions: off by default, remembered, anchored
            Stage("captions off by default");
            Add("captions are off by default in VR", !S.Captions.Enabled || PlayerPrefs.GetInt("hm_vr_captions", 0) == 1, $"enabled={S.Captions.Enabled}");
            S.Menu.Close();
            yield return 0.6f;

            // the headset HUD items; the menu's view button only reports passthrough (always on)
            Stage("passthrough: HUD and the view button");
            Add("the VR HUD exists (beat counter / rhythm score where the lesson turns them on)", S.Hud != null);
            Sim.Rest(Hand.Right);
            Sim.Rest(Hand.Left);
            S.Menu.Open(true);
            yield return 1.2f;
            VrWidget view = W("view mode");
            Add("the menu's view button is a disabled 'Passthrough' label", view != null && !view.Enabled() && view.Label.text.Contains("Passthrough"), view != null ? $"enabled={view.Enabled()} label '{view.Label.text}'" : "missing");
            d.Set(DisplayMode.Immersive, false);
            yield return 0.4f;
            Add("asking for immersive is refused: passthrough stays on", d.PassthroughActive && MixedReality.Passthrough, $"wanted {d.Wanted} state {d.Current}");
            foreach (float w in FarClick("resume")) yield return w;

            // the hair tier on the Quest settings (a PC run of the Quest tier)
            Stage("quest tier: hair");
            S.Perf.Adaptive = false; // an editor frame is slower than a headset's budget: the adaptive step-down is exercised on the device
            VrQuality.Apply(false, VrQuality.Tier.Quest);
            HairStrands hair = UnityEngine.Object.FindAnyObjectByType<HairStrands>();
            if (hair == null)
            {
                Add("(this film has no groomed hair: the hair tier is not exercised)", true);
            }
            else
            {
                yield return 0.5f;
                Add("hair stays ON in the Quest tier (Full)", hair.enabled && hair.Visible && VrQuality.Hair == HairLevel.Full, $"enabled={hair.enabled} level={VrQuality.Hair}");
                VrQuality.SetHair(HairLevel.Light, false);
                Add("Light: cheaper levels sooner, no tip glow", hair.enabled && hair.LodScale > 1.2f && hair.MinLod >= 1 && hair.TipGlow == 0f, $"lodScale {hair.LodScale} minLod {hair.MinLod} tips {hair.TipGlow}");
                VrQuality.SetHair(HairLevel.Off, false);
                yield return 0.3f;
                if (VrQuality.SplatHairNeedsSimulation(hair))
                    Add("Off: a splat dancer's bound hair keeps its simulation (the splats swing with it)", hair.enabled, $"enabled={hair.enabled}");
                else
                    Add("Off: nothing simulated or drawn", !hair.enabled && !hair.RendererVisible && !hair.GlowVisible, $"enabled={hair.enabled} drawn={hair.RendererVisible}");
                VrQuality.SetHair(HairLevel.Full, false);
                yield return 0.5f;
                Add("Full again: hair back", hair.enabled && hair.Visible, $"enabled={hair.enabled}");
            }

            VrQuality.Restore();
            Add("the PC tier is restored", VrQuality.Current == VrQuality.Tier.Pc);
            hm.SetLayerVisible("room", false);
            yield return 0.3f;
        }

        static int FindFlatCanvases() => UnityEngine.Object.FindObjectsByType<Canvas>().Count(c => c.renderMode == RenderMode.ScreenSpaceOverlay && c.enabled);
    }
}
