using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HmVr
{
    /// <summary>
    /// The splash and loading screen of the VR mode, all procedural (no media): the wordmark, a small animated motif - two dancers in a closed
    /// hold swaying to a slow beat, a T-axis ring turning behind them, ripples spreading on the floor under them - the lesson's title and subtitle
    /// (read from the library data), a progress bar with the stage that is being built, and a spinner that turns every frame. It fades in and
    /// out. Anchored like every panel: placed ONCE in front of the user when it appears (about 1.4 m ahead along the gaze, kept out of the
    /// dancers) and then it stays; in passthrough it floats in the room, in immersive mode a dark backdrop fills the view behind it.
    /// It only draws and animates: the lesson loads in slices behind it (HeadMovement.AsyncLoad), so it keeps moving while it loads.
    /// </summary>
    [DefaultExecutionOrder(170)]
    [AddComponentMenu("")]
    public sealed class VrSplash : MonoBehaviour
    {
        public static VrSplash Instance { get; private set; }

        public float Alpha { get; private set; }
        /// <summary>the splash is wanted (it may still be fading in or out)</summary>
        public bool Wanted { get; private set; }
        public bool Blocking => Wanted || Alpha > 0.01f;
        public Vector3 AnchorPosition => content != null ? content.transform.position : Vector3.zero;
        public string Title => titleText != null ? titleText.text : "";
        public float Progress { get; private set; }
        public int Showings { get; private set; }

        VrPanel content, backdrop;
        Text titleText, subtitleText, stageText;
        RectTransform barFill;
        Transform motif;
        LineRenderer[] lines;
        Material lineMat, lineMatSoft;
        readonly Material[] rippleMats = new Material[3];
        LineRenderer[] ripples = new LineRenderer[3];
        LineRenderer spinner;
        LineRenderer figureHeadA, figureHeadB, bodyA, bodyB, hold, tAxis;
        readonly LineRenderer[] arcs = new LineRenderer[4];
        bool placed;
        float shownAt;
        float minUntil;
        float targetProgress;
        string stage = "";
        const float W = 900f, H = 660f;
        static readonly Color Accent = new(1f, 0.839f, 0.039f), Teal = new(0.10f, 0.76f, 0.84f);

        void Awake()
        {
            Instance = this;
            backdrop = VrPanel.Create("VR splash backdrop", new Vector2(5200f, 3200f), transform);
            backdrop.Interactive = false;
            backdrop.Background.color = new Color(0.02f, 0.024f, 0.04f, 0.985f);
            backdrop.SortingOrder = 28000;
            backdrop.Alpha = 0f;

            content = VrPanel.Create("VR splash", new Vector2(W, H), transform);
            content.Interactive = false;
            content.Background.color = new Color(0.04f, 0.047f, 0.07f, 0.88f);
            content.SortingOrder = 29500;
            content.AddLabel("wordmark", "Head Movement", new Rect(-W * 0.5f + 40f, -30f, W - 80f, 100f), 84f, VrStyle.Text, TextAnchor.MiddleCenter, true);
            titleText = content.AddLabel("title", "", new Rect(-W * 0.5f + 60f, -120f, W - 120f, 62f), 40f, Accent, TextAnchor.MiddleCenter, true, true).Label;
            subtitleText = content.AddLabel("subtitle", "", new Rect(-W * 0.5f + 80f, -182f, W - 160f, 70f), 26f, VrStyle.Muted, TextAnchor.UpperCenter, false, true).Label;
            content.AddBox("bar back", new Rect(-330f, -246f, 660f, 10f), new Color(1f, 1f, 1f, 0.16f));
            VrWidget fill = content.AddBox("bar fill", new Rect(-330f, -246f, 10f, 10f), Accent);
            barFill = fill.Back.rectTransform;
            stageText = content.AddLabel("stage", "", new Rect(-W * 0.5f + 60f, -282f, W - 120f, 36f), 24f, VrStyle.Muted, TextAnchor.MiddleCenter).Label;
            content.Alpha = 0f;
            BuildMotif();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            foreach (Material m in rippleMats)
            {
                if (m != null) Destroy(m);
            }

            if (lineMat != null) Destroy(lineMat);
            if (lineMatSoft != null) Destroy(lineMatSoft);
        }

        // ------------------------------------------------------------------ motif

        LineRenderer MotifLine(string name, Material m, float width, int points, bool loop = false)
        {
            GameObject go = new(name);
            go.layer = VrStyle.Layer;
            go.transform.SetParent(motif, false);
            LineRenderer l = go.AddComponent<LineRenderer>();
            l.useWorldSpace = false;
            l.sharedMaterial = m;
            l.widthMultiplier = width;
            l.numCapVertices = 4;
            l.numCornerVertices = 3;
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            l.receiveShadows = false;
            l.alignment = LineAlignment.TransformZ;
            l.sortingOrder = 30000;
            l.positionCount = points;
            l.loop = loop;
            return l;
        }

        void BuildMotif()
        {
            motif = new GameObject("splash motif").transform; // metres, following the panel's pose (the panel's own canvas is scaled to millimetres)
            motif.SetParent(transform, false);
            motif.gameObject.layer = VrStyle.Layer;
            lineMat = VrDraw.Material(new Color(1f, 1f, 1f, 0.95f));
            lineMatSoft = VrDraw.Material(new Color(Teal.r, Teal.g, Teal.b, 0.55f));
            for (int i = 0; i < 3; i++) rippleMats[i] = VrDraw.Material(new Color(Teal.r, Teal.g, Teal.b, 0.5f));
            figureHeadA = MotifLine("head A", lineMat, 0.006f, 20, true);
            figureHeadB = MotifLine("head B", lineMat, 0.006f, 20, true);
            bodyA = MotifLine("body A", lineMat, 0.006f, 7);
            bodyB = MotifLine("body B", lineMat, 0.006f, 7);
            hold = MotifLine("hold", lineMat, 0.005f, 3);
            for (int i = 0; i < 4; i++) arcs[i] = MotifLine("T ring " + i, lineMatSoft, 0.0045f, 13);
            tAxis = MotifLine("T axis", new Material(lineMat) { name = "splash T" }, 0.007f, 5);
            tAxis.sharedMaterial.SetColor("_Tint", Accent);
            for (int i = 0; i < 3; i++) ripples[i] = MotifLine("ripple " + i, rippleMats[i], 0.004f, 49, true);
            spinner = MotifLine("spinner", new Material(lineMat) { name = "splash spinner" }, 0.008f, 17);
            spinner.sharedMaterial.SetColor("_Tint", Accent);
        }

        static Vector3 P(float x, float y) => new(x, y, -0.003f);

        void AnimateMotif(float t)
        {
            // everything in metres from the panel's centre; the motif sits in the upper half of the panel
            Vector3 c = new(0f, 0.165f, 0f);
            motif.SetPositionAndRotation(content.transform.TransformPoint(Vector3.zero) + content.transform.rotation * new Vector3(0f, 0.165f, 0f), content.transform.rotation);
            float beat = t * 0.8f; // a slow beat, 0.8 Hz
            float sway = Mathf.Sin(beat * Mathf.PI * 2f) * 0.012f;
            float lean = Mathf.Sin(beat * Mathf.PI * 2f) * 4f * Mathf.Deg2Rad;

            // the T-axis ring: four arcs turning slowly, and the T (a bar and a stem) that holds still under the couple
            for (int a = 0; a < 4; a++)
            {
                float a0 = t * 0.5f + a * Mathf.PI * 0.5f;
                for (int k = 0; k < 13; k++)
                {
                    float ang = a0 + k / 12f * Mathf.PI * 0.36f;
                    arcs[a].SetPosition(k, P(Mathf.Cos(ang) * 0.205f, Mathf.Sin(ang) * 0.205f));
                }
            }

            tAxis.positionCount = 5;
            tAxis.SetPosition(0, P(-0.075f, -0.115f));
            tAxis.SetPosition(1, P(0.075f, -0.115f));
            tAxis.SetPosition(2, P(0f, -0.115f));
            tAxis.SetPosition(3, P(0f, -0.115f));
            tAxis.SetPosition(4, P(0f, -0.19f));

            // two dancers: a head, a spine that leans toward the partner, legs, and one line across their joined arms
            DrawDancer(figureHeadA, bodyA, -0.050f + sway, +lean, c);
            DrawDancer(figureHeadB, bodyB, +0.050f + sway, -lean, c);
            hold.SetPosition(0, P(-0.050f + sway + 0.012f, 0.032f));
            hold.SetPosition(1, P(sway, 0.052f));
            hold.SetPosition(2, P(0.050f + sway - 0.012f, 0.032f));

            // floor ripples under the couple, spreading on the beat
            for (int i = 0; i < 3; i++)
            {
                float ph = Mathf.Repeat(beat * 0.5f + i / 3f, 1f);
                float r = Mathf.Lerp(0.03f, 0.19f, ph);
                for (int k = 0; k < 49; k++)
                {
                    float ang = k / 48f * Mathf.PI * 2f;
                    ripples[i].SetPosition(k, P(Mathf.Cos(ang) * r, -0.108f + Mathf.Sin(ang) * r * 0.28f));
                }

                Color col = Teal;
                col.a = 0.55f * (1f - ph) * Mathf.SmoothStep(0f, 1f, ph * 6f);
                rippleMats[i].SetColor("_Tint", col);
            }

            // the spinner: three quarters of a circle that turns every frame, next to the bar
            for (int k = 0; k < 17; k++)
            {
                float ang = -t * 6f + k / 16f * Mathf.PI * 1.5f;
                spinner.SetPosition(k, P(0.385f + Mathf.Cos(ang) * 0.02f, -0.246f - 0.165f + Mathf.Sin(ang) * 0.02f));
            }
        }

        void DrawDancer(LineRenderer head, LineRenderer body, float x, float leanRad, Vector3 centre)
        {
            Vector2 hip = new(x, -0.03f);
            Vector2 up = new(Mathf.Sin(leanRad), Mathf.Cos(leanRad));
            Vector2 neck = hip + up * 0.085f;
            Vector2 headC = hip + up * 0.118f;
            for (int k = 0; k < 20; k++)
            {
                float ang = k / 20f * Mathf.PI * 2f;
                head.SetPosition(k, P(headC.x + Mathf.Cos(ang) * 0.019f, headC.y + Mathf.Sin(ang) * 0.019f));
            }

            body.SetPosition(0, P(hip.x - 0.028f, -0.095f));
            body.SetPosition(1, P(hip.x - 0.01f, -0.06f));
            body.SetPosition(2, P(hip.x, hip.y));
            body.SetPosition(3, P(neck.x, neck.y));
            body.SetPosition(4, P(hip.x, hip.y));
            body.SetPosition(5, P(hip.x + 0.01f, -0.06f));
            body.SetPosition(6, P(hip.x + 0.028f, -0.095f));
        }

        // ------------------------------------------------------------------ show / hide

        /// <summary>fade the splash in (placed once in front of the user); `minSeconds` keeps it up at least that long</summary>
        public void Show(string title, string subtitle, float minSeconds = 0f)
        {
            if (!Wanted)
            {
                Debug.Log("VrSplash: shown" + (string.IsNullOrEmpty(title) ? " (app start)" : " for '" + title + "'"));
                Wanted = true;
                placed = false;
                shownAt = Time.unscaledTime;
                Showings++;
                Progress = 0f;
                targetProgress = 0f;
                minUntil = Time.unscaledTime + minSeconds;
            }

            if (titleText.text != (title ?? "")) titleText.text = title ?? "";
            if (subtitleText.text != (subtitle ?? "")) subtitleText.text = subtitle ?? "";
        }

        public void SetProgress(float p, string stageName)
        {
            targetProgress = Mathf.Clamp01(p);
            stage = stageName ?? "";
        }

        public void Hide()
        {
            if (Wanted) Debug.Log($"VrSplash: hidden after {Time.unscaledTime - shownAt:0.0} s");
            Wanted = false;
        }

        public void Recall() => placed = false;

        public bool MinimumElapsed => Time.unscaledTime >= minUntil;

        void LateUpdate()
        {
            VrInput input = VrInput.Instance;
            float dt = Time.unscaledDeltaTime;
            Alpha = Mathf.MoveTowards(Alpha, Wanted ? 1f : 0f, dt * (Wanted ? 4f : 3f));
            content.Alpha = Alpha;
            bool immersive = VrDisplay.Instance != null && !VrDisplay.Instance.PassthroughActive;
            backdrop.Alpha = immersive ? Alpha : 0f;
            motif.gameObject.SetActive(Alpha > 0.01f);
            if (Alpha <= 0.002f || input == null) return;
            if (!placed)
            {
                placed = true;
                InputFrame f = VrAnchor.Now(input.Frame);
                Vector3 spot = VrAnchor.Spot(f, 1.4f, 0.05f);
                content.FaceFrom(spot, f.HeadPosition, 0.35f);
                VrAnchor.Log("splash \"" + Title + "\"", spot, f);
                Vector3 away = (content.transform.position - f.HeadPosition);
                away.y = 0f;
                backdrop.FaceFrom(content.transform.position + away.normalized * 0.8f, f.HeadPosition, 0.1f);
            }

            Progress = Mathf.MoveTowards(Progress, targetProgress, dt * 0.6f);
            float w = Mathf.Max(10f, 660f * Progress);
            barFill.sizeDelta = new Vector2(w, 10f);
            barFill.anchoredPosition = new Vector2(-330f + w * 0.5f, -246f);
            string st = string.IsNullOrEmpty(stage) ? "" : stage + (Mathf.FloorToInt(Time.unscaledTime * 2f) % 3 == 0 ? " ." : Mathf.FloorToInt(Time.unscaledTime * 2f) % 3 == 1 ? " .." : " ...");
            if (stageText.text != st) stageText.text = st;
            AnimateMotif(Time.unscaledTime - shownAt);
        }

        public Dictionary<string, object> State() => new()
        {
            ["wanted"] = Wanted, ["alpha"] = Alpha, ["title"] = Title, ["progress"] = Progress, ["stage"] = stage, ["showings"] = Showings
        };
    }
}
