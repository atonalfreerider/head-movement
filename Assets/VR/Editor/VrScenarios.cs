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
            Add($"ray hovers {id}", hover == id, $"hover={hover}" + (hover == id ? "" : $" menuOpen={S.Menu.IsOpen} over={S.Menu.OverLesson} rayHit={P.Get(h).RayHit} near={P.Get(h).NearPanel} clicks=[{string.Join(" | ", P.ClickLog)}] livePanels=[{string.Join(",", VrPanel.All.Where(p => p.IsLive).Select(p => p.name))}]"));
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

            Stage("lesson: POV only");
            Vector3 headBefore = VrRig.Instance.Head.position;
            Quaternion rotBefore = VrRig.Instance.Head.rotation;
            yield return 1.5f;
            float drift = Vector3.Distance(headBefore, VrRig.Instance.Head.position) + Quaternion.Angle(rotBefore, VrRig.Instance.Head.rotation);
            Add("the film never moves the head camera", drift < 0.01f, $"drift {drift:0.0000} over 1.5 s of film (shot: {FilmDirector.Instance.State()["cameraMode"]})");
            Add("the director's camera mode does not apply", Camera.main == VrRig.Instance.Cam, "Camera.main is the head");
            Add("no flat overlay in the view", FindFlatCanvases() == 0, "overlay canvases enabled: " + FindFlatCanvases());
            Vector3 couple = VrViewpoints.CoupleCentre(HeadMovement.Instance);
            Vector3 hp = VrRig.Instance.Head.position;
            float dist = new Vector2(hp.x - couple.x, hp.z - couple.z).magnitude;
            Vector3 toCouple = new(couple.x - hp.x, 0f, couple.z - hp.z);
            Vector3 fwd = VrRig.Instance.Head.forward;
            fwd.y = 0f;
            float facing = Vector3.Angle(fwd, toCouple);
            Add("start vantage 2-5.5 m from the dancers", dist >= 2f && dist <= 5.5f, $"{dist:0.00} m");
            Add("start vantage faces the dancers", facing < 25f, $"{facing:0.0} deg off");
            Add("the world is visible in the lesson", VrRig.Instance.Cam.cullingMask == ~0, "cull mask " + VrRig.Instance.Cam.cullingMask);
            Add("viewpoint presets exist (start, front, side, overhead + phones)", S.Locomotion.Viewpoints.Count >= 4, string.Join(", ", S.Locomotion.Viewpoints.Select(v => v.Id)));
            Add("captions panel follows the film's transcript capability", S.Captions.HasTranscript == (VrFilm.CaptionChunks() != null), "transcript=" + S.Captions.HasTranscript);

            Stage("hand panel (palm up)");
            Sim.PalmUp(Hand.Left, true);
            foreach (float w in Until(() => S.HandPanel.Shown, 3f, "the hand panel shows when the left palm is raised")) yield return w;
            yield return 0.6f;
            Vector3 palm = VrInput.Instance.Left.State.Palm;
            Vector3 panelPos = S.HandPanel.Main.transform.position;
            Add("the hand panel floats above the palm", panelPos.y > palm.y + 0.08f && Vector3.Distance(panelPos, palm) < 0.5f, $"{Vector3.Distance(panelPos, palm):0.00} m above/near the palm");

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

            // slow motion
            Stage("slow motion");
            if (VrFilm.Paused) VrFilm.Resume();
            foreach (float w in PokeClick("rate 0.25x")) yield return w;
            Add("slow motion 0.25x sets the film rate", Mathf.Approximately(FilmRate, 0.25f), $"rate={FilmRate}");
            yield return 0.4f;
            float ta = VrFilm.Time;
            yield return 3f;
            float advanced = VrFilm.Time - ta;
            Add("the film clock runs at a quarter speed", advanced > 0.45f && advanced < 1.1f, $"{advanced:0.00} film s in 3 real s (rate {FilmRate})");
            if (FilmSoundtrack.Current != null)
            {
                float pitch = FilmSoundtrack.Current.Pitch;
                Add("the soundtrack follows the rate (pitch, as the flat speed button)", Mathf.Abs(pitch - 0.25f) < 0.02f, $"pitch={pitch:0.00}");
                Add("the soundtrack stays with the film", Mathf.Abs(FilmSoundtrack.Current.AudioTime - VrFilm.Time) < 0.25f, $"audio {FilmSoundtrack.Current.AudioTime:0.00} vs film {VrFilm.Time:0.00}");
            }

            foreach (float w in PokeClick("rate 0.1x")) yield return w;
            Add("slow motion 0.1x", Mathf.Approximately(FilmRate, 0.1f), $"rate={FilmRate}");
            foreach (float w in PokeClick("rate 1x")) yield return w;
            Add("back to 1x", Mathf.Approximately(FilmRate, 1f), $"rate={FilmRate}");

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
            Stage("viewpoints and locomotion");
            foreach (float w in PokeClick("tab views")) yield return w;
            Add("the viewpoint list opens", S.HandPanel.ShowViews, "views=" + S.HandPanel.ShowViews);
            yield return 0.5f;
            foreach (string vp in new[] { "overhead", "front", "start" })
            {
                foreach (float w in PokeClick("row:" + vp)) yield return w;
                yield return 0.6f;
                Viewpoint v = S.Locomotion.Viewpoints.First(x => x.Id == vp);
                Vector3 h = VrRig.Instance.Head.position;
                float err = new Vector2(h.x - v.Head.x, h.z - v.Head.z).magnitude;
                Add($"viewpoint {vp}", err < 0.15f && S.Locomotion.CurrentViewpoint == vp, $"head {h.x:0.00},{h.y:0.00},{h.z:0.00} want {v.Head.x:0.00},{v.Head.z:0.00} floor {v.FloorY:0.0}");
            }

            Viewpoint phone = S.Locomotion.Viewpoints.FirstOrDefault(x => x.Kind == "phone");
            if (phone.Id != null)
            {
                foreach (float w in PokeClick("row:" + phone.Id)) yield return w;
                yield return 0.6f;
                Vector3 h = VrRig.Instance.Head.position;
                Add($"viewpoint {phone.Id} (a phone's real position)", new Vector2(h.x - phone.Head.x, h.z - phone.Head.z).magnitude < 0.15f, $"head {h.x:0.00},{h.z:0.00} want {phone.Head.x:0.00},{phone.Head.z:0.00} yaw {VrRig.Instance.HeadWorldYawDeg:0} want {phone.YawDeg:0}");
            }

            foreach (float w in PokeClick("row:start")) yield return w;
            yield return 0.5f;
            foreach (float w in PokeClick("tab views")) yield return w;
            Sim.PalmUp(Hand.Left, false);
            yield return 1.5f;
            Add("the hand panel hides when the palm is lowered", !S.HandPanel.Shown, $"alpha {S.HandPanel.Alpha:0.00}");

            // Pin: the panel hangs in the air; from a step back it is out of arm's reach and works with the pointing ray and a pinch
            Stage("pinned panel, far ray and pinch");
            Sim.PalmUp(Hand.Left, true);
            yield return 1.2f;
            foreach (float w in PokeClick("pin")) yield return w;
            Add("Pin keeps the panel", S.HandPanel.Pinned, "pinned=" + S.HandPanel.Pinned);
            Sim.PalmUp(Hand.Left, false);
            yield return 1.5f;
            Add("a pinned panel stays when the palm is lowered", S.HandPanel.Shown, $"alpha {S.HandPanel.Alpha:0.00}");
            Sim.Rest(Hand.Right);
            Vector3 stepBack = -VrRig.Instance.Head.forward;
            stepBack.y = 0f;
            VrRig.Instance.transform.position += stepBack.normalized * 1.0f; // the user walks back a metre
            yield return 0.8f;
            float tFar = VrFilm.Time;
            foreach (float w in FarClick("forward 5")) yield return w;
            Add("far ray + pinch on +5 s", VrFilm.Time - tFar > 4.5f && VrFilm.Time - tFar < 9f, $"{tFar:0.0} -> {VrFilm.Time:0.0}");
            VrWidget fs = W("scrubber");
            Vector3 AtU(float u) => fs.Panel.Content.TransformPoint(new Vector3(fs.RectMm.xMin + u * fs.RectMm.width, fs.RectMm.center.y, 0f));
            float durFar = VrFilm.Duration;
            Sim.AimAt(Hand.Right, AtU(0.7f), 0f);
            yield return 0.7f;
            Sim.SetPinch(Hand.Right, 1f);
            yield return 0.35f;
            Add("a pinch on the scrubber holds the film", FilmDirector.Instance.Scrubbing, "scrubbing=" + FilmDirector.Instance.Scrubbing);
            for (int i = 1; i <= 8; i++)
            {
                fs = W("scrubber");
                Sim.AimAt(Hand.Right, AtU(Mathf.Lerp(0.7f, 0.3f, i / 8f)), 1f);
                yield return 0.15f;
            }

            yield return 0.3f;
            Add("pinch-dragging the scrubber to 30 %", Mathf.Abs(VrFilm.Time - 0.3f * durFar) < 0.05f * durFar, $"time {VrFilm.Time:0.0} of {durFar:0.0}");
            Sim.SetPinch(Hand.Right, 0f);
            yield return 0.6f;
            Add("opening the pinch ends the scrub", !FilmDirector.Instance.Scrubbing, "scrubbing=" + FilmDirector.Instance.Scrubbing);
            Sim.Rest(Hand.Right);
            VrRig.Instance.transform.position -= stepBack.normalized * 1.0f;
            Sim.PalmUp(Hand.Left, true);
            yield return 1.2f;
            foreach (float w in PokeClick("pin")) yield return w;
            Add("Pin again lets the panel go back to the hand", !S.HandPanel.Pinned, "pinned=" + S.HandPanel.Pinned);
            Sim.PalmUp(Hand.Left, false);
            yield return 1.5f;

            // teleport: pinch with the dominant hand where no panel is, hold, release
            Vector3 from = VrRig.Instance.Head.position;
            Vector3 aheadFlat = VrRig.Instance.Head.forward;
            aheadFlat.y = 0f;
            Vector3 floorTarget = new Vector3(from.x, 0f, from.z) + aheadFlat.normalized * 1.8f + Vector3.Cross(Vector3.up, aheadFlat.normalized) * -0.3f;
            Sim.AimAt(Hand.Right, floorTarget + Vector3.up * 0.0f, 0f);
            yield return 0.7f;
            int tp0 = S.Locomotion.Teleports;
            Sim.SetPinch(Hand.Right, 1f);
            yield return 0.5f;
            Add("teleport arc shows while pinching", S.Locomotion.TeleportAiming, $"aiming={S.Locomotion.TeleportAiming} valid={S.Locomotion.AimValid}");
            Vector3 landing = S.Locomotion.AimTarget;
            Sim.SetPinch(Hand.Right, 0f);
            yield return 0.9f;
            Vector3 to = VrRig.Instance.Head.position;
            Add("opening the pinch teleports there", S.Locomotion.Teleports == tp0 + 1 && new Vector2(to.x - landing.x, to.z - landing.z).magnitude < 0.2f, $"teleports {tp0}->{S.Locomotion.Teleports}, landed {to.x:0.00},{to.z:0.00} aimed {landing.x:0.00},{landing.z:0.00}");
            Sim.Rest(Hand.Right);
            yield return 0.6f;

            // snap turn: the other hand pinches and moves sideways
            float yaw0 = VrRig.Instance.HeadWorldYawDeg;
            Sim.HoldOut(Hand.Left, 0.0f, 0f);
            yield return 0.6f;
            Sim.SetPinch(Hand.Left, 1f);
            yield return 0.3f;
            Sim.HoldOut(Hand.Left, 0.17f, 1f);
            yield return 0.7f;
            float yaw1 = VrRig.Instance.HeadWorldYawDeg;
            Add("pinch and move right: snap turn of 45 degrees", Mathf.Abs(Mathf.DeltaAngle(yaw0, yaw1) - 45f) < 1.5f, $"yaw {yaw0:0.0} -> {yaw1:0.0}");
            Sim.HoldOut(Hand.Left, 0.0f, 1f);
            yield return 0.8f;
            Sim.HoldOut(Hand.Left, -0.17f, 1f);
            yield return 0.8f;
            float yaw2 = VrRig.Instance.HeadWorldYawDeg;
            Add("move back and left: 45 degrees the other way", Mathf.Abs(Mathf.DeltaAngle(yaw1, yaw2) + 45f) < 1.5f || Mathf.Abs(Mathf.DeltaAngle(yaw1, yaw2) + 90f) < 1.5f, $"yaw {yaw1:0.0} -> {yaw2:0.0}");
            Sim.SetPinch(Hand.Left, 0f);
            Sim.Rest(Hand.Left);
            yield return 0.8f;

            foreach (float w in PassthroughStage()) yield return w;

            // wrist button
            Stage("wrist menu button and back to the library");
            Sim.Rest(Hand.Left);
            Sim.HoldOut(Hand.Left, -0.05f, 0f);
            yield return 1.0f;
            Add("the wrist menu button is shown on the back of the left hand", S.Wrist.Alpha > 0.5f, $"alpha {S.Wrist.Alpha:0.00}");
            if (S.Wrist.Alpha > 0.5f)
            {
                bool wasPlaying = VrFilm.Playing;
                foreach (float w in PokeClick("menu")) yield return w;
                Add("poking the wrist button opens the menu over the lesson", S.Menu.IsOpen && S.Menu.OverLesson, $"open={S.Menu.IsOpen} over={S.Menu.OverLesson}");
                Add("the lesson pauses behind the menu", VrFilm.Paused || !wasPlaying, $"paused={VrFilm.Paused}");
                yield return 0.8f;
                yield return 0.6f;
                foreach (float w in FarClick("resume")) yield return w;
                Add("Resume closes the menu and plays on", !S.Menu.IsOpen && (VrFilm.Playing || !wasPlaying), $"open={S.Menu.IsOpen} playing={VrFilm.Playing}");
                foreach (float w in PokeClick("menu")) yield return w;
                yield return 0.8f;
            }

            if (S.Menu.IsOpen)
            {
                foreach (float w in FarClick("back to library")) yield return w;
            }
            else
            {
                Sim.PalmUp(Hand.Left, true);
                yield return 1.0f;
                foreach (float w in PokeClick("library")) yield return w;
            }

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
            yield return 0.5f;
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

        // ------------------------------------------------------------------ passthrough mixed reality (simulated: no headset, no camera feed)

        static Vector2 RightOfHead(VrRig rig)
        {
            float a = rig.HeadRealYawDeg * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(a), -Mathf.Sin(a)); // the head's right on the real floor (x, z)
        }

        static IEnumerable<float> PassthroughStage()
        {
            Stage("passthrough: mixed reality");
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
            d.Set(DisplayMode.Passthrough, false);
            foreach (float w in Until(() => d.PassthroughActive && pl.Active && pl.Current != null, 4f, "switching to passthrough (simulated)")) yield return w;
            yield return 1.0f;
            Add("mixed-reality flag is set for the scene layers", MixedReality.Passthrough);
            Add("the camera clears to alpha 0 on a solid colour", rig.Cam.clearFlags == CameraClearFlags.SolidColor && rig.Cam.backgroundColor.a == 0f, $"flags {rig.Cam.clearFlags} alpha {rig.Cam.backgroundColor.a}");
            GameObject room = GameObject.Find("Room");
            Transform rm = room != null ? room.transform.Find("Room Mesh") : null;
            Add("no room mesh in passthrough", rm == null || !rm.gameObject.activeInHierarchy, hasRoom ? "room mesh object " + (rm != null && rm.gameObject.activeInHierarchy ? "ACTIVE" : "hidden / not loaded") : "this capture has no room mesh");
            GameObject plane = GameObject.Find("Floor plane");
            Add("no floor fill in passthrough", plane == null || !plane.activeInHierarchy, plane == null ? "no plane" : "plane active=" + plane.activeInHierarchy);
            Add("no teleport or snap turn in passthrough", !S.Locomotion.Enabled);
            Add("the hands are drawn minimal (the real hands are in view)", S.Hands.Minimal);
            Add("the real floor is the capture's floor (rig on y = 0)", Mathf.Abs(rig.FloorY) < 1e-4f, $"rig floor y {rig.FloorY:0.0000}");
            Add("life size by default", Mathf.Abs(pl.Current.Scale - 1f) < 1e-4f && Mathf.Abs(rig.RigScale - 1f) < 1e-4f, $"scale {pl.Current.Scale:0.00} rig {rig.RigScale:0.00}");
            Vector3 centre = pl.Current.Pivot; // the placement's pivot: the couple's centre when it was placed
            Vector3 headReal = rig.HeadReal;
            Vector2 pivotReal = pl.Current.ToReal(new Vector2(centre.x, centre.z));
            float ahead = (pivotReal - new Vector2(headReal.x, headReal.z)).magnitude;
            Add("the couple stands about 2.2 m in front of the user", Mathf.Abs(ahead - VrPlacement.DefaultDistance) < 0.1f, $"{ahead:0.00} m");
            Vector3 headFwdReal = rig.DirToReal(rig.Head.forward);
            headFwdReal.y = 0f;
            Vector3 toPivot = new(pivotReal.x - headReal.x, 0f, pivotReal.y - headReal.z);
            Add("and straight ahead of where the user looks", Vector3.Angle(headFwdReal, toPivot) < 8f, $"{Vector3.Angle(headFwdReal, toPivot):0.0} deg off");
            Add("Place mode is on with its panel", pl.PlaceMode && pl.PanelShown, $"place={pl.PlaceMode} panel={pl.PanelShown}");
            yield return 0.4f;

            // the hand panel's Views tab becomes Place
            Sim.PalmUp(Hand.Left, true);
            foreach (float w in Until(() => S.HandPanel.Shown, 3f, "hand panel shows in passthrough")) yield return w;
            yield return 0.4f;
            Add("the hand panel's Views tab is the Place tab", W("tab views").Label.text == "Place", "label " + W("tab views").Label.text);
            Sim.PalmUp(Hand.Left, false);
            yield return 0.8f;

            // a pinch in the air holds the dance: slide it 30 cm to the right
            Stage("passthrough: pinch-drag to move");
            Vector2 a0 = pl.Current.Anchor;
            float yaw0 = pl.Current.YawDeg;
            Sim.HandAtHead(Hand.Right, new Vector3(0.45f, -0.25f, 0.45f), 0f, 0f);
            yield return 1.0f;
            Sim.SetPinch(Hand.Right, 1f);
            yield return 0.7f;
            a0 = pl.Current.Anchor;
            Add("a pinch in the air (not on a panel) holds the dance", pl.Holding, $"holding={pl.Holding} grabs={pl.Grabs}");
            Sim.HandAtHead(Hand.Right, new Vector3(0.75f, -0.25f, 0.45f), 0f, 1f);
            yield return 1.0f;
            Vector2 moved = pl.Current.Anchor - a0;
            Vector2 right = RightOfHead(rig);
            float along = Vector2.Dot(moved, right), across = Mathf.Abs(Vector2.Dot(moved, new Vector2(-right.y, right.x)));
            Add("sliding the hand 30 cm slides the dance 30 cm", Mathf.Abs(along - 0.30f) < 0.07f && across < 0.07f, $"moved {along:0.000} along, {across:0.000} across");
            Add("sliding does not turn it", Mathf.Abs(Mathf.DeltaAngle(yaw0, pl.Current.YawDeg)) < 3f, $"yaw {yaw0:0.0} -> {pl.Current.YawDeg:0.0}");
            Add("the rig carries the move (the pivot stands at the anchor)", Vector2.Distance(pl.Current.ToReal(new Vector2(centre.x, centre.z)), pl.Current.Anchor) < 0.02f);
            Sim.HandAtHead(Hand.Right, new Vector3(0.75f, -0.25f, 0.45f), 0f, 0f);
            yield return 0.6f;
            Add("opening the pinch lets go", !pl.Holding);

            // twist the hand: the dance turns (after the dead zone)
            Stage("passthrough: twist to turn");
            Sim.HandAtHead(Hand.Right, new Vector3(0.45f, -0.25f, 0.45f), 0f, 0f);
            yield return 0.8f;
            Sim.HandAtHead(Hand.Right, new Vector3(0.45f, -0.25f, 0.45f), 0f, 1f);
            yield return 0.8f;
            float yawBefore = pl.Current.YawDeg;
            Sim.HandAtHead(Hand.Right, new Vector3(0.45f, -0.25f, 0.45f), 50f, 1f);
            yield return 1.2f;
            float turned = Mathf.DeltaAngle(yawBefore, pl.Current.YawDeg);
            Add("turning the hand 50 degrees turns the dance past the dead zone", turned > 24f && turned < 44f, $"turned {turned:0.0} deg (expect about {50f - VrPlacement.TwistDeadDeg:0})");
            Sim.HandAtHead(Hand.Right, new Vector3(0.45f, -0.25f, 0.45f), 50f, 0f);
            yield return 0.6f;

            // two hands: spread to make it larger
            Stage("passthrough: two-hand spread to resize");
            Sim.HandAtHead(Hand.Left, new Vector3(-0.15f, -0.25f, 0.45f), 0f, 0f);
            Sim.HandAtHead(Hand.Right, new Vector3(0.15f, -0.25f, 0.45f), 0f, 0f);
            yield return 0.9f;
            Sim.SetPinch(Hand.Left, 1f);
            yield return 0.25f;
            Sim.SetPinch(Hand.Right, 1f);
            yield return 0.7f;
            Add("pinching with both hands holds with both", pl.Holding && pl.TwoHandGrabs >= 1, $"two-hand grabs {pl.TwoHandGrabs}");
            Sim.HandAtHead(Hand.Left, new Vector3(-0.30f, -0.25f, 0.45f), 0f, 1f);
            Sim.HandAtHead(Hand.Right, new Vector3(0.30f, -0.25f, 0.45f), 0f, 1f);
            yield return 1.4f;
            Add("doubling the spread doubles the size (limit 2x)", pl.Current.Scale > 1.85f && pl.Current.Scale <= 2.001f, $"scale {pl.Current.Scale:0.00}, rig scale {rig.RigScale:0.00}");
            Add("the rig carries the inverse scale", Mathf.Abs(rig.RigScale * pl.Current.Scale - 1f) < 0.02f, $"rig {rig.RigScale:0.000} x scale {pl.Current.Scale:0.000}");
            Add("the pinch thresholds follow the scaled hands", Mathf.Abs(HandState.WorldScale - rig.RigScale) < 1e-4f, $"world scale {HandState.WorldScale:0.00}");
            Sim.HandAtHead(Hand.Left, new Vector3(-0.30f, -0.25f, 0.45f), 0f, 0f);
            Sim.HandAtHead(Hand.Right, new Vector3(0.30f, -0.25f, 0.45f), 0f, 0f);
            yield return 0.8f;
            Sim.Rest(Hand.Left);
            Sim.Rest(Hand.Right);
            yield return 0.8f;

            // the panel's buttons
            Stage("passthrough: Place panel buttons and Lock");
            yield return 0.8f;
            foreach (float w in FarClick("size 1:1")) yield return w;
            yield return 0.8f;
            Add("Size 1:1 resets the size", Mathf.Abs(pl.Current.Scale - 1f) < 1e-3f && Mathf.Abs(rig.RigScale - 1f) < 1e-3f, $"scale {pl.Current.Scale:0.00}");
            float yawT = pl.Current.YawDeg;
            foreach (float w in FarClick("turn right")) yield return w;
            yield return 0.6f;
            Add("Turn right turns it 15 degrees", Mathf.Abs(Mathf.DeltaAngle(yawT, pl.Current.YawDeg) - VrPlacement.TurnStepDeg) < 0.5f, $"yaw {yawT:0.0} -> {pl.Current.YawDeg:0.0}");
            foreach (float w in FarClick("in front of me")) yield return w;
            Vector3 centre2 = pl.Current.Pivot; // "in front of me" re-pivots on the couple where it is now
            Vector2 pivotAgain = pl.Current.ToReal(new Vector2(centre2.x, centre2.z));
            Add("In front of me puts it back 2.2 m ahead", Mathf.Abs((pivotAgain - new Vector2(headReal.x, headReal.z)).magnitude - VrPlacement.DefaultDistance) < 0.1f, $"{(pivotAgain - new Vector2(headReal.x, headReal.z)).magnitude:0.00} m");
            foreach (float w in FarClick("lock")) yield return w;
            Add("Lock leaves Place mode", !pl.PlaceMode);
            yield return 0.6f;
            Add("the Place panel goes away when locked", !pl.PanelShown, $"alpha {pl.Panel.Alpha:0.00}");
            Vector2 lockedAnchor = pl.Current.Anchor;
            Sim.HandAtHead(Hand.Right, new Vector3(0.45f, -0.25f, 0.45f), 0f, 1f);
            yield return 0.8f;
            Sim.HandAtHead(Hand.Right, new Vector3(0.80f, -0.25f, 0.45f), 0f, 1f);
            yield return 0.8f;
            Add("a locked dance ignores pinch-drags", !pl.Holding && Vector2.Distance(lockedAnchor, pl.Current.Anchor) < 1e-4f, $"moved {Vector2.Distance(lockedAnchor, pl.Current.Anchor):0.0000}");
            Sim.HandAtHead(Hand.Right, new Vector3(0.80f, -0.25f, 0.45f), 0f, 0f);
            yield return 0.4f;
            Sim.Rest(Hand.Right);
            yield return 0.4f;
            Sim.PalmUp(Hand.Left, true);
            yield return 1.2f;
            foreach (float w in PokeClick("tab views")) yield return w;
            Add("the Place tab turns Place mode back on", pl.PlaceMode);
            Sim.PalmUp(Hand.Left, false);
            yield return 1.0f;

            // the headset HUD items and the menu's view toggle
            Stage("passthrough: HUD and the view toggle");
            Add("the VR HUD exists (beat counter / rhythm score where the lesson turns them on)", S.Hud != null);
            S.Menu.Open(true);
            yield return 1.0f;
            foreach (float w in FarClick("view mode")) yield return w;
            Add("the menu's View button switches to immersive and remembers it", d.Wanted == DisplayMode.Immersive && !d.PassthroughActive, $"wanted {d.Wanted} state {d.Current}");
            Add("scene layers are back to immersive", !MixedReality.Passthrough && rig.Cam.backgroundColor.a > 0.99f, $"mr={MixedReality.Passthrough} alpha {rig.Cam.backgroundColor.a:0.00}");
            Add("the rig is back at scale 1", Mathf.Abs(rig.RigScale - 1f) < 1e-4f && Mathf.Abs(HandState.WorldScale - 1f) < 1e-4f);
            GameObject room2 = GameObject.Find("Room");
            Transform rm2 = room2 != null ? room2.transform.Find("Room Mesh") : null;
            if (hasRoom)
            {
                hm.SetLayerVisible("room", true); // the film's own segment may have switched the room layer off meanwhile
                yield return 0.6f;
                room2 = GameObject.Find("Room");
                rm2 = room2 != null ? room2.transform.Find("Room Mesh") : null;
                Add("the room mesh loads again in immersive", rm2 != null && rm2.gameObject.activeInHierarchy, "room mesh " + (rm2 != null ? "active=" + rm2.gameObject.activeInHierarchy : "missing"));
            }
            GameObject plane2 = GameObject.Find("Floor plane");
            Add("the floor plane comes back in immersive (when the film shows the grid)", plane2 != null ? plane2.activeInHierarchy : !hm.Grid.Visible, plane2 != null ? "plane active" : "grid layer visible=" + hm.Grid.Visible);
            yield return 0.6f;
            Add("viewpoints are back", S.Locomotion.Viewpoints.Count > 0, $"viewpoints {S.Locomotion.Viewpoints.Count}");
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
