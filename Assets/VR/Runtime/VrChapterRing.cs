using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HmVr
{
    /// <summary>
    /// The chapter timeline as a RING around the whole dance (owner 2026-11-15): radius 3.5 m about the dance's footprint centre, 3.5 m above
    /// the floor, in a very faded grey. The whole lesson runs once around it, one arc per chapter with a small gap between chapters. The
    /// CURRENT POSITION marker stands on the ring directly opposite the user across the dancers' centre (it follows the user as they walk,
    /// smoothed so a snap turn does not whip it), and the timeline turns through the marker as the lesson plays. Pointing at the ring with the
    /// far ray lights the chapter under the ray and shows its name and time; pinching seeks there (snapped to a chapter start when close to
    /// one). Under the marker a small panel shows the chapter and the time and carries a pause button and a speed button (1, 1/2, 1/4, 1/10,
    /// back to 1). The ring lives in the dance's world (not on the rig), so it moves with the dance when the floor is dragged or turned.
    /// </summary>
    [DefaultExecutionOrder(140)]
    [AddComponentMenu("")]
    public sealed class VrChapterRing : MonoBehaviour, IRayTarget
    {
        public static VrChapterRing Instance { get; private set; }

        public const float Radius = 3.5f, Height = 3.5f, BandM = 0.35f, ReachDeg = 115f, LineWidth = 0.03f, ControlsScale = 6f;
        /// <summary>the very faded grey of the ring (owner: "a very faded gray color")</summary>
        public static readonly Color RingColour = new(0.78f, 0.78f, 0.8f, 0.16f);
        static readonly Color HoverColour = new(0.9f, 0.9f, 0.92f, 0.42f), MarkerColour = new(1f, 1f, 1f, 0.5f), TickColour = new(0.8f, 0.8f, 0.82f, 0.22f);

        public bool Visible { get; private set; }
        public float MarkerBearingDeg => markerBearing;
        public Vector3 Centre => centre;
        public int Seeks { get; private set; }
        public float LastSeekTo { get; private set; } = -1f;
        public float HoverTime => hoverTime;

        Transform ring;
        readonly List<LineRenderer> arcs = new();
        readonly List<LineRenderer> ticks = new();
        LineRenderer hoverArc, marker, hoverTick;
        Material ringMat, hoverMat, markerMat, tickMat;
        VrPanel controls;
        VrWidget title, preview, pauseBtn, speedBtn;
        Image playTri, pauseA, pauseB;
        Vector3 centre;
        float markerBearing, markerVel;
        bool markerSet;
        float hoverTime = -1f, hoverUntil;
        int hoverChapter = -1, builtFor = -1;
        float builtDuration = -1f;
        readonly List<float> starts = new();
        static readonly Vector3[] Scratch = new Vector3[2];

        void Awake()
        {
            Instance = this;
            ring = new GameObject("Chapter ring").transform;
            ring.SetParent(transform, false);
            ringMat = VrDraw.Material(RingColour);
            hoverMat = VrDraw.Material(HoverColour);
            markerMat = VrDraw.Material(MarkerColour);
            tickMat = VrDraw.Material(TickColour);
            hoverArc = Line(ring, "ring hover", hoverMat, LineWidth * 1.6f, false);
            hoverTick = Line(transform, "ring hover tick", markerMat, LineWidth * 0.8f, true);
            marker = Line(transform, "ring marker", markerMat, LineWidth * 1.2f, true);
            BuildControls();
            VrPointers.Targets.Add(this);
            SetVisible(false);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            VrPointers.Targets.Remove(this);
            foreach (Material m in new[] { ringMat, hoverMat, markerMat, tickMat })
            {
                if (m != null) Destroy(m);
            }
        }

        static LineRenderer Line(Transform parent, string name, Material m, float width, bool world)
        {
            LineRenderer l = VrDraw.Line(parent, name, m, width);
            l.useWorldSpace = world;
            l.numCapVertices = 0;
            return l;
        }

        void BuildControls()
        {
            controls = VrPanel.Create("Ring controls", new Vector2(300f, 118f), transform, false, ControlsScale);
            controls.WorldFixed = true;
            title = controls.AddLabel("ring chapter", "", VrPanel.R(0f, 38f, 300f, 34f), 22f, VrStyle.Muted, TextAnchor.MiddleCenter, true);
            preview = controls.AddLabel("ring preview", "", VrPanel.R(0f, 8f, 300f, 24f), 17f, VrStyle.Text, TextAnchor.MiddleCenter);
            pauseBtn = controls.AddButton("ring pause", "", VrPanel.R(-45f, -36f, 70f, 46f), VrFilm.TogglePlay);
            VrPanel.GlyphPlay(pauseBtn, 22f);
            playTri = (Image)pauseBtn.Glyphs[0];
            (pauseA, pauseB) = VrPanel.GlyphPause(pauseBtn, 22f);
            speedBtn = controls.AddButton("ring speed", "1x", VrPanel.R(45f, -36f, 70f, 46f), VrFilm.CycleRate, 20f);
        }

        void SetVisible(bool on)
        {
            Visible = on;
            if (ring.gameObject.activeSelf != on) ring.gameObject.SetActive(on);
            marker.enabled = on;
            if (!on)
            {
                hoverTick.enabled = false;
                hoverArc.enabled = false;
            }

            controls.Alpha = on ? 1f : 0f;
        }

        // ------------------------------------------------------------------ per frame

        void LateUpdate()
        {
            HeadMovement hm = HeadMovement.Instance;
            VrRig rig = VrRig.Instance;
            float dur = VrFilm.Duration;
            bool on = VrFilm.InLesson && hm != null && rig != null && dur > 0.5f && hm.LeadDancer != null;
            if (!on)
            {
                if (Visible) SetVisible(false);
                markerSet = false;
                return;
            }

            if (!Visible) SetVisible(true);
            centre = VrStage.Centre(hm);
            centre.y = 0f;
            Rebuild(dur);
            float now = VrFilm.Time;
            Vector3 head = rig.Head.position;
            float target = RingMath.MarkerBearing(head, centre);
            Vector2 flat = new(head.x - centre.x, head.z - centre.z);
            if (!markerSet)
            {
                markerBearing = target;
                markerSet = true;
            }
            else if (flat.magnitude >= 0.3f)
            {
                markerBearing = Mathf.SmoothDampAngle(markerBearing, target, ref markerVel, 0.3f, Mathf.Infinity, Time.unscaledDeltaTime);
            }

            // the timeline turns so that 'now' sits at the marker
            float zero = RingMath.BearingOfTime(0f, now, dur, markerBearing);
            ring.SetPositionAndRotation(new Vector3(centre.x, Height, centre.z), Quaternion.Euler(0f, zero, 0f));
            Vector3 m = RingPoint(markerBearing);
            Scratch[0] = m + Vector3.down * 0.28f;
            Scratch[1] = m + Vector3.up * 0.28f;
            marker.positionCount = 2;
            marker.SetPositions(Scratch);
            PlaceControls(m, head);
            UpdateHover(now, dur);
            UpdateControls(now, dur);
        }

        Vector3 RingPoint(float bearingDeg) => new Vector3(centre.x, Height, centre.z) + ViewpointMath.Dir(bearingDeg) * Radius;

        void PlaceControls(Vector3 markerPos, Vector3 head)
        {
            Vector3 at = markerPos + Vector3.down * 0.75f;
            controls.FaceFrom(at, head, 0.6f);
        }

        /// <summary>the arcs: one per chapter (local bearings, time 0 at local bearing 0), rebuilt when the chapters or the length change</summary>
        void Rebuild(float dur)
        {
            IReadOnlyList<LibraryChapter> chs = VrFilm.Chapters;
            if (builtFor == chs.Count && Mathf.Abs(builtDuration - dur) < 0.01f) return;
            builtFor = chs.Count;
            builtDuration = dur;
            starts.Clear();
            foreach (LibraryChapter c in chs) starts.Add(Mathf.Clamp(c.StartS, 0f, dur));
            if (starts.Count == 0 || starts[0] > 0.01f) starts.Insert(0, 0f);
            foreach (LineRenderer l in arcs) Destroy(l.gameObject);
            foreach (LineRenderer l in ticks) Destroy(l.gameObject);
            arcs.Clear();
            ticks.Clear();
            float gapDeg = 0.8f;
            for (int i = 0; i < starts.Count; i++)
            {
                float sign = Delta(dur);
                float a = LocalBearing(starts[i], dur) + sign * gapDeg * 0.5f;
                float b = LocalBearing(i + 1 < starts.Count ? starts[i + 1] : dur, dur) - sign * gapDeg * 0.5f;
                LineRenderer l = Line(ring, "ring arc " + i, ringMat, LineWidth, false);
                SetArc(l, a, b, 0f);
                l.enabled = true;
                arcs.Add(l);
                LineRenderer t = Line(ring, "ring tick " + i, tickMat, LineWidth * 0.7f, false);
                Vector3 p = Local(LocalBearing(starts[i], dur));
                t.positionCount = 2;
                t.SetPosition(0, p + Vector3.down * 0.12f);
                t.SetPosition(1, p + Vector3.up * 0.12f);
                t.enabled = true;
                ticks.Add(t);
            }

            hoverChapter = -1;
        }

        /// <summary>a time's bearing in the ring's own frame (time 0 at bearing 0); the ring's rotation puts 'now' at the marker</summary>
        static float LocalBearing(float t, float dur) => Delta(dur) * 360f * Mathf.Clamp01(t / Mathf.Max(dur, 1e-3f));

        /// <summary>which way the ring runs (+1: later times at larger bearings)</summary>
        static float Delta(float dur) => Mathf.DeltaAngle(0f, RingMath.BearingOfTime(dur * 0.25f, 0f, dur, 0f)) >= 0f ? 1f : -1f;

        static Vector3 Local(float bearingDeg) => ViewpointMath.Dir(bearingDeg) * Radius;

        static void SetArc(LineRenderer l, float fromDeg, float toDeg, float lift)
        {
            float span = Mathf.Abs(toDeg - fromDeg);
            int n = Mathf.Clamp(Mathf.CeilToInt(span / 2f) + 1, 2, 182);
            l.positionCount = n;
            for (int k = 0; k < n; k++)
            {
                float b = Mathf.Lerp(fromDeg, toDeg, k / (float)(n - 1));
                l.SetPosition(k, Local(b) + Vector3.up * lift);
            }
        }

        int ChapterAt(float t)
        {
            int c = 0;
            for (int i = 0; i < starts.Count; i++)
            {
                if (starts[i] <= t + 1e-3f) c = i;
            }

            return c;
        }

        void UpdateHover(float now, float dur)
        {
            bool hovering = hoverTime >= 0f && Time.unscaledTime <= hoverUntil;
            if (!hovering)
            {
                hoverTime = -1f;
                if (hoverArc.enabled) hoverArc.enabled = false;
                if (hoverTick.enabled) hoverTick.enabled = false;
                hoverChapter = -1;
                return;
            }

            int c = ChapterAt(hoverTime);
            if (c != hoverChapter)
            {
                hoverChapter = c;
                float a = LocalBearing(starts[c], dur), b = LocalBearing(c + 1 < starts.Count ? starts[c + 1] : dur, dur);
                SetArc(hoverArc, a, b, 0f);
                VrAudio.Tick(RingPoint(RingMath.BearingOfTime(hoverTime, now, dur, markerBearing)));
            }

            hoverArc.enabled = true;
            Vector3 p = RingPoint(RingMath.BearingOfTime(hoverTime, now, dur, markerBearing));
            Scratch[0] = p + Vector3.down * 0.2f;
            Scratch[1] = p + Vector3.up * 0.2f;
            hoverTick.positionCount = 2;
            hoverTick.SetPositions(Scratch);
            hoverTick.enabled = true;
        }

        string lastTitle, lastPreview, lastSpeed;

        void UpdateControls(float now, float dur)
        {
            int ci = VrFilm.ChapterIndex;
            IReadOnlyList<LibraryChapter> chs = VrFilm.Chapters;
            string t = (ci >= 0 && ci < chs.Count ? chs[ci].Title + "   " : "") + LibraryData.Clock(now) + " / " + LibraryData.Clock(dur);
            if (t != lastTitle)
            {
                lastTitle = t;
                title.Label.text = t;
            }

            string pv = "";
            if (hoverTime >= 0f)
            {
                int hc = ChapterAt(hoverTime);
                string name = hc - (starts.Count - chs.Count) >= 0 && hc - (starts.Count - chs.Count) < chs.Count ? chs[hc - (starts.Count - chs.Count)].Title : "";
                pv = "Pinch to go to  " + (name.Length > 0 ? name + "  " : "") + LibraryData.Clock(hoverTime);
            }

            if (pv != lastPreview)
            {
                lastPreview = pv;
                preview.Label.text = pv;
            }

            bool playing = VrFilm.Playing;
            playTri.gameObject.SetActive(!playing);
            pauseA.gameObject.SetActive(playing);
            pauseB.gameObject.SetActive(playing);
            float r = VrFilm.Rate;
            string sp = Mathf.Abs(r - 1f) < 0.001f ? "1x" : Mathf.Abs(r - 0.5f) < 0.001f ? "1/2x" : Mathf.Abs(r - 0.25f) < 0.001f ? "1/4x" : Mathf.Abs(r - 0.1f) < 0.001f ? "1/10x" : r.ToString("0.##") + "x";
            if (sp != lastSpeed)
            {
                lastSpeed = sp;
                speedBtn.Label.text = sp;
            }

            speedBtn.Toggled = Mathf.Abs(r - 1f) > 0.001f;
        }

        // ------------------------------------------------------------------ the far ray (IRayTarget)

        public bool RayLive => Visible;

        public bool RayHit(Vector3 origin, Vector3 direction, float maxDistance, out float t, out Vector3 point)
        {
            point = default;
            if (!RingMath.Raycast(origin, direction, centre, Radius, Height, BandM, out float bearing, out point, out t) || t > maxDistance) return false;
            return RingMath.Reachable(bearing, markerBearing, ReachDeg);
        }

        float TimeAt(Vector3 point)
        {
            float dur = VrFilm.Duration;
            float bearing = ViewpointMath.Bearing(centre, point);
            float t = RingMath.TimeAtBearing(bearing, VrFilm.Time, dur, markerBearing);
            return RingMath.SnapTime(t, starts, dur, Mathf.Max(0.8f, dur * 0.008f));
        }

        public void RayHover(Hand side, Vector3 point)
        {
            hoverTime = TimeAt(point);
            hoverUntil = Time.unscaledTime + 0.08f;
        }

        public void RayPress(Hand side, Vector3 point)
        {
            hoverTime = TimeAt(point);
            hoverUntil = Time.unscaledTime + 0.08f;
            VrAudio.Click(point);
        }

        public void RayRelease(Hand side, Vector3 point, bool stillOver)
        {
            if (!stillOver) return; // moved off the ring before opening the pinch: cancelled, as a button is
            float t = TimeAt(point);
            Seeks++;
            LastSeekTo = t;
            Debug.Log($"VrChapterRing: seek to {t:0.00} s ({side} hand)");
            VrFilm.SeekTo(t);
            VrAudio.Confirm(point);
        }

        public void RayCancel(Hand side)
        {
        }

        public Dictionary<string, object> State() => new()
        {
            ["visible"] = Visible, ["centre"] = new[] { centre.x, centre.z }, ["radius"] = Radius, ["height"] = Height, ["markerBearingDeg"] = Mathf.Round(markerBearing * 10f) / 10f,
            ["chapters"] = starts.Count, ["hoverTime"] = hoverTime, ["seeks"] = Seeks, ["lastSeekTo"] = LastSeekTo,
            ["controls"] = controls != null ? new[] { controls.transform.position.x, controls.transform.position.y, controls.transform.position.z } : null
        };
    }
}
