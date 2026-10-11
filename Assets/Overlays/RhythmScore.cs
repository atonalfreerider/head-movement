using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The capture's rhythm layer (rhythm.json, dancecap.rhythm.for_unity; docs/RHYTHM.md in the dancecap repo): a causal LIVE rhythm score at 30 Hz
/// (a pure function of time: the score at t was computed from data up to t only), its five components, a confidence, the footsteps with the class the
/// HUD flashes (on the beat / near / off) and the final card. Times are reference seconds; the viewer adds the capture's time_to_audio.
/// The score is an ESTIMATE FROM MOTION, never an official score; the HUD says so.
/// </summary>
public class RhythmData
{
    public const int ComponentCount = 5;

    public string Title = "Rhythm score (estimated from motion, not an official score)";
    public string FinalConfidence = "low";
    public float FinalScore, FinalConfidenceValue, RangeLo = float.NaN, RangeHi = float.NaN;
    public float CardFrom = float.NaN, CardTo = float.NaN;
    public float Hz = 30f, T0;
    public int N;
    public float[] Score, Confidence;
    public readonly float[][] Comp = new float[ComponentCount][];
    public byte[] State;
    public readonly string[] CompKeys = { "lock", "accents", "tempo", "flow", "sync" };
    public readonly string[] CompLabels = { "Beat lock", "Accents", "Tempo", "Flow", "Couple sync" };
    public readonly int[] CompWeights = { 40, 20, 15, 15, 10 };
    public float[] FinalComp = new float[ComponentCount];
    public double[] StepT = new double[0];
    public byte[] StepRole = new byte[0], StepClass = new byte[0];
    public float[] StepOffset = new float[0];
    public readonly List<string> Notes = new();
    readonly Dictionary<long, byte> classByStep = new();

    public double EndT => T0 + (N - 1) / (double)Hz;

    public static RhythmData Load(string path)
    {
        JObject root = JObject.Parse(File.ReadAllText(path));
        RhythmData d = new();
        d.Title = root.Value<string>("title") ?? d.Title;
        if (root["components"] is JArray comps)
        {
            for (int i = 0; i < comps.Count && i < ComponentCount; i++)
            {
                d.CompKeys[i] = comps[i].Value<string>("key") ?? d.CompKeys[i];
                d.CompLabels[i] = comps[i].Value<string>("label") ?? d.CompLabels[i];
                d.CompWeights[i] = comps[i].Value<int?>("weight") ?? d.CompWeights[i];
            }
        }

        JObject fin = root["final"] as JObject;
        if (fin != null)
        {
            d.FinalScore = fin.Value<float?>("score") ?? 0f;
            d.FinalConfidence = fin.Value<string>("confidence") ?? "low";
            d.FinalConfidenceValue = fin.Value<float?>("confidence_value") ?? 0f;
            if (fin["range"] is JArray rg && rg.Count == 2)
            {
                d.RangeLo = rg[0].Value<float>();
                d.RangeHi = rg[1].Value<float>();
            }

            if (fin["components"] is JObject fc)
            {
                for (int i = 0; i < ComponentCount; i++) d.FinalComp[i] = fc.Value<float?>(d.CompKeys[i]) ?? 0f;
            }
        }

        if (root["final_card"] is JObject card)
        {
            d.CardFrom = card.Value<float?>("from_t") ?? float.NaN;
            d.CardTo = card.Value<float?>("to_t") ?? float.NaN;
        }

        JObject live = root["live"] as JObject ?? throw new InvalidDataException("rhythm.json has no live timeline");
        d.Hz = live.Value<float?>("hz") ?? 30f;
        d.T0 = live.Value<float?>("t0") ?? 0f;
        d.Score = Floats(live["score"]);
        d.N = d.Score.Length;
        if (d.N < 2) throw new InvalidDataException("rhythm.json live timeline is empty");
        d.Confidence = Floats(live["confidence"], d.N);
        for (int i = 0; i < ComponentCount; i++) d.Comp[i] = Floats(live[d.CompKeys[i]], d.N);
        JArray st = live["state"] as JArray;
        d.State = new byte[d.N];
        if (st != null)
        {
            for (int i = 0; i < d.N && i < st.Count; i++) d.State[i] = (byte)st[i].Value<int>();
        }

        if (float.IsNaN(d.CardFrom)) d.CardFrom = (float)d.EndT - 4f;
        if (float.IsNaN(d.CardTo)) d.CardTo = (float)d.EndT;
        if (root["steps"]?["rows"] is JArray rows)
        {
            int n = rows.Count;
            d.StepT = new double[n];
            d.StepRole = new byte[n];
            d.StepClass = new byte[n];
            d.StepOffset = new float[n];
            for (int i = 0; i < n; i++)
            {
                d.StepT[i] = rows[i][0].Value<double>();
                d.StepRole[i] = (byte)rows[i][1].Value<int>();
                d.StepClass[i] = (byte)rows[i][2].Value<int>();
                d.StepOffset[i] = rows[i][3].Type == JTokenType.Null ? 0f : rows[i][3].Value<float>();
                d.classByStep[Key(d.StepRole[i], d.StepT[i])] = d.StepClass[i];
            }
        }

        if (root["notes"] is JArray notes)
        {
            foreach (JToken n in notes) d.Notes.Add(n.Value<string>());
        }

        return d;
    }

    static float[] Floats(JToken t, int expect = -1)
    {
        JArray a = t as JArray;
        if (a == null)
        {
            if (expect > 0) return new float[expect];
            throw new InvalidDataException("rhythm.json: a live series is missing");
        }

        float[] f = new float[expect > 0 ? expect : a.Count];
        for (int i = 0; i < f.Length && i < a.Count; i++) f[i] = a[i].Value<float>();
        return f;
    }

    static long Key(int role, double t) => (long)role * 100000000L + (long)Math.Round(t * 1000.0);

    /// <summary>the class (0 on the beat, 1 near, 2 off) of the step of a role at reference time t (within a millisecond), or -1</summary>
    public int ClassOf(int role, double t)
    {
        long k = Key(role, t);
        for (long d = 0; d <= 1; d++)
        {
            if (classByStep.TryGetValue(k + d, out byte c) || classByStep.TryGetValue(k - d, out c)) return c;
        }

        return -1;
    }

    /// <summary>linear sample of a live series at a reference time (clamped to the timeline)</summary>
    public float Sample(float[] a, double refTime)
    {
        double x = (refTime - T0) * Hz;
        if (x <= 0) return a[0];
        if (x >= N - 1) return a[N - 1];
        int i = (int)x;
        float f = (float)(x - i);
        return a[i] + (a[i + 1] - a[i]) * f;
    }

    public int StateAt(double refTime)
    {
        int i = Mathf.Clamp((int)Math.Round((refTime - T0) * Hz), 0, N - 1);
        return State[i];
    }

    /// <summary>the series smoothed by a causal exponential (tau seconds) over the preceding samples: the HUD's count-up, still a pure function of time</summary>
    public float Smooth(float[] a, double refTime, float tau = 0.22f)
    {
        const int M = 20;
        double step = 1.0 / Hz;
        float sum = 0f, wsum = 0f;
        for (int k = 0; k < M; k++)
        {
            float w = Mathf.Exp(-(float)(k * step) / tau);
            sum += w * Sample(a, refTime - k * step);
            wsum += w;
        }

        return sum / wsum;
    }

    /// <summary>index of the last step at or before t of a role (-1 none)</summary>
    public int LastStep(int role, double t)
    {
        int lo = 0, hi = StepT.Length - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (StepT[mid] <= t)
            {
                best = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }

        while (best >= 0 && StepRole[best] != role) best--;
        return best;
    }
}

/// <summary>
/// The live rhythm score on screen (layer "rhythm"; a film direction turns it on with the segment layer "rhythm_score"): a compact panel with the big live
/// number (a smooth count), five small component bars, the score so far as a sparkline over the whole dance, a beat pulse and one lamp per dancer that
/// flashes at each footstep (green on the beat, amber near, red off), plus the FINAL CARD for the last seconds of the dance. A pure function of time: it
/// follows seeks exactly. Screen Space Overlay under the film's own overlay; hidden in VR (the user's head is the camera: there is no flat screen).
/// </summary>
[AddComponentMenu("")]
public class RhythmScoreHud : MonoBehaviour
{
    public static readonly Color OnColour = new(0.20f, 0.86f, 0.45f), NearColour = new(1f, 0.78f, 0.15f), OffColour = new(0.95f, 0.27f, 0.25f);
    static readonly Color[] BarColours =
    {
        new(0.96f, 0.55f, 0.15f), new(0.62f, 0.55f, 0.95f), new(0.20f, 0.78f, 0.62f), new(0.95f, 0.38f, 0.65f), new(0.55f, 0.82f, 0.30f)
    };

    RhythmData data;
    CaptureTimeline timeline;
    Canvas canvas;
    RectTransform root, panel, cardRoot;
    CanvasGroup group, cardGroup;
    Image panelBack, pulse, lampLead, lampFollow, spark, cardBack;
    Text title, subtitle, big, confidence, status, footer, lampLeadLabel, lampFollowLabel;
    readonly Image[] barBack = new Image[RhythmData.ComponentCount], barFill = new Image[RhythmData.ComponentCount];
    readonly Text[] barLabel = new Text[RhythmData.ComponentCount];
    Text cardTitle, cardNumber, cardConfidence, cardRange, cardNote, cardSmall;
    readonly Text[] cardLabel = new Text[RhythmData.ComponentCount], cardValue = new Text[RhythmData.ComponentCount];
    readonly Image[] cardBarBack = new Image[RhythmData.ComponentCount], cardBarFill = new Image[RhythmData.ComponentCount];
    Texture2D sparkTex;
    Color32[] sparkPixels;
    float[] sparkColumn;
    byte[] sparkState;
    const int SparkW = 288, SparkH = 56;

    bool visible, filmPlacement, filmVertical, laidOut;
    float filmW, filmH, filmUnit = 1f, filmSafeTop = 0.05f, filmDim = 1f;
    float laidW, laidH, laidUnit;
    int sparkCurrent = -2;
    double lastAudio = double.NaN;
    bool built;

    public bool Loaded => data != null;
    public RhythmData Data => data;
    public bool Visible { get; private set; }
    public float Shown { get; private set; } = float.NaN;
    public float CardAlpha { get; private set; }
    public int LeadFlash { get; private set; } = -1;
    public int FollowFlash { get; private set; } = -1;
    public string Placement { get; private set; } = "none";
    public Rect PanelRect { get; private set; }

    public void Init(RhythmData d, CaptureTimeline tl)
    {
        data = d;
        timeline = tl;
        BuildSpark();
    }

    /// <summary>the layer's switch (DanceLayers: the user's flag or the film's segment layer)</summary>
    public void SetVisible(bool on)
    {
        visible = on;
        if (!on) Hide();
        else if (!double.IsNaN(lastAudio)) SetTime((float)lastAudio);
    }

    /// <summary>the film overlay's placement (screen px, bottom-left origin) while a film runs: beside the beat counter, under the top platform band</summary>
    public void SetFilmPlacement(bool vertical, float w, float h, float unit, float safeTop, float dim)
    {
        filmPlacement = true;
        filmVertical = vertical;
        filmW = w;
        filmH = h;
        filmUnit = unit;
        filmSafeTop = safeTop;
        filmDim = dim;
    }

    public void ClearFilmPlacement()
    {
        filmPlacement = false;
        laidOut = false;
    }

    void Hide()
    {
        Visible = false;
        if (canvas != null && canvas.enabled) canvas.enabled = false;
        CardAlpha = 0f;
    }

    /// <summary>bring the HUD to an audio-clock time (the exact sub-frame time while a director drives the clock)</summary>
    public void SetTime(float audioTime)
    {
        lastAudio = audioTime;
        if (data == null || timeline == null) return;
        bool allowed = visible && !FilmDirector.HeadIsCamera;
        if (!allowed)
        {
            Hide();
            return;
        }

        Build();
        double t = audioTime - timeline.TimeToAudio;                 // reference seconds
        int state = data.StateAt(t);
        // the panel waits for the music: it appears one second before the first beat
        int firstBeat = FirstBeatIndex();
        double beatStart = data.T0 + firstBeat / (double)data.Hz;
        float appear = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((float)((t - (beatStart - 1.5)) / 1.0)));
        float cardIn = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((float)((t - data.CardFrom) / 0.6)));
        float cardOut = t > data.CardTo + 0.3 ? 0f : 1f;
        CardAlpha = cardIn * cardOut;
        float alpha = appear * filmDim * Mathf.Lerp(1f, 0.3f, CardAlpha);
        canvas.enabled = alpha > 0.002f || CardAlpha > 0.002f;
        Visible = canvas.enabled;
        if (!canvas.enabled) return;
        Layout();
        group.alpha = alpha;
        cardGroup.alpha = CardAlpha;
        cardRoot.gameObject.SetActive(CardAlpha > 0.002f);

        float score = data.Smooth(data.Score, t);
        Shown = score;
        big.text = Mathf.RoundToInt(score).ToString(CultureInfo.InvariantCulture);
        float conf = data.Sample(data.Confidence, t);
        string level = conf >= 0.7f ? "high" : conf >= 0.4f ? "medium" : "low";
        confidence.text = $"confidence {level}";
        confidence.color = new Color(0.78f, 0.82f, 0.88f, level == "low" ? 0.9f : 0.75f);
        for (int i = 0; i < RhythmData.ComponentCount; i++)
        {
            float v = Mathf.Clamp01(data.Smooth(data.Comp[i], t));
            SetBar(barFill[i], barBack[i], v);
        }

        status.text = state == 1 ? "no music: score paused" : state == 2 ? "DJ mix: score paused" : "";
        PaintSpark(t);
        Flashes(t);
        Pulse(audioTime, state);
        if (CardAlpha > 0.002f) PaintCard(t);
    }

    int FirstBeatIndex()
    {
        for (int i = 0; i < data.N; i++)
        {
            if (data.State[i] == 0) return i;
        }

        return 0;
    }

    void SetBar(Image fill, Image back, float v)
    {
        RectTransform r = fill.rectTransform;
        float w = back.rectTransform.sizeDelta.x * v;
        r.sizeDelta = new Vector2(Mathf.Max(0f, w), back.rectTransform.sizeDelta.y);
        r.anchoredPosition = new Vector2(back.rectTransform.anchoredPosition.x - back.rectTransform.sizeDelta.x * 0.5f + w * 0.5f, back.rectTransform.anchoredPosition.y);
    }

    void Flashes(double t)
    {
        for (int role = 0; role < 2; role++)
        {
            Image lamp = role == 0 ? lampLead : lampFollow;
            int i = data.LastStep(role, t);
            Color c = new(0.35f, 0.38f, 0.42f, 0.9f);
            int cls = -1;
            if (i >= 0)
            {
                float age = (float)(t - data.StepT[i]);
                if (age >= 0f && age < 0.9f)
                {
                    cls = data.StepClass[i];
                    Color f = cls == 0 ? OnColour : cls == 1 ? NearColour : OffColour;
                    float k = Mathf.Exp(-age / 0.32f);
                    c = Color.Lerp(c, f, Mathf.Clamp01(0.25f + 0.75f * k));
                    lamp.rectTransform.localScale = Vector3.one * (1f + 0.55f * k);
                }
                else lamp.rectTransform.localScale = Vector3.one;
            }
            else lamp.rectTransform.localScale = Vector3.one;

            lamp.color = c;
            if (role == 0) LeadFlash = cls;
            else FollowFlash = cls;
        }
    }

    void Pulse(float audioTime, int state)
    {
        BeatGrid beats = HeadMovement.Instance != null ? HeadMovement.Instance.Beats : null;
        float k = 0f;
        if (beats != null && beats.Count > 0 && state == 0)
        {
            int bi = beats.IndexAtOrBefore(audioTime + BeatGrid.SnapTolerance);
            if (bi >= 0)
            {
                float since = audioTime - beats.Times[bi];
                bool isPulse = (bi % 2) == 0;
                k = Mathf.Exp(-Mathf.Max(0f, since) / 0.16f) * (isPulse ? 1f : 0.45f);
                if (since > 0.6f) k = 0f;
            }
        }

        pulse.rectTransform.localScale = Vector3.one * (0.7f + 0.9f * k);
        pulse.color = new Color(1f, 0.84f, 0.04f, 0.25f + 0.75f * k);
    }

    // ------------------------------------------------------------------ sparkline

    void BuildSpark()
    {
        sparkColumn = new float[SparkW];
        sparkState = new byte[SparkW];
        double span = data.EndT - data.T0;
        for (int x = 0; x < SparkW; x++)
        {
            double t = data.T0 + span * x / (SparkW - 1);
            sparkColumn[x] = data.Sample(data.Score, t);
            sparkState[x] = (byte)data.StateAt(t);
        }

        sparkTex = new Texture2D(SparkW, SparkH, TextureFormat.RGBA32, false) { name = "rhythm sparkline", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        sparkPixels = new Color32[SparkW * SparkH];
    }

    void PaintSpark(double t)
    {
        double span = data.EndT - data.T0;
        int cur = Mathf.Clamp((int)Math.Round((t - data.T0) / span * (SparkW - 1)), -1, SparkW - 1);
        if (cur == sparkCurrent) return;
        sparkCurrent = cur;
        Color32 clear = new(0, 0, 0, 0), grid = new(255, 255, 255, 38), paused = new(255, 255, 255, 16);
        Color32 fill = new(255, 214, 10, 70), line = new(255, 214, 10, 255);
        for (int i = 0; i < sparkPixels.Length; i++) sparkPixels[i] = clear;
        for (int x = 0; x < SparkW; x++)
        {
            if (sparkState[x] != 0)
            {
                for (int y = 0; y < SparkH; y++) sparkPixels[y * SparkW + x] = paused;
            }

            sparkPixels[(SparkH / 2) * SparkW + x] = grid;
        }

        int prevY = -1;
        for (int x = 0; x <= cur; x++)
        {
            int y = Mathf.Clamp(Mathf.RoundToInt(sparkColumn[x] / 100f * (SparkH - 1)), 0, SparkH - 1);
            for (int yy = 0; yy < y; yy++) sparkPixels[yy * SparkW + x] = fill;
            int a = prevY < 0 ? y : Mathf.Min(prevY, y), b = prevY < 0 ? y : Mathf.Max(prevY, y);
            for (int yy = a; yy <= b; yy++) sparkPixels[Mathf.Clamp(yy, 0, SparkH - 1) * SparkW + x] = line;
            prevY = y;
        }

        if (cur >= 0)
        {
            int yc = Mathf.Clamp(Mathf.RoundToInt(sparkColumn[cur] / 100f * (SparkH - 1)), 0, SparkH - 1);
            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dy = -2; dy <= 2; dy++)
                {
                    int px = cur + dx, py = yc + dy;
                    if (px >= 0 && px < SparkW && py >= 0 && py < SparkH && dx * dx + dy * dy <= 5) sparkPixels[py * SparkW + px] = new Color32(255, 255, 255, 255);
                }
            }
        }

        sparkTex.SetPixels32(sparkPixels);
        sparkTex.Apply(false);
    }

    // ------------------------------------------------------------------ the final card

    void PaintCard(double t)
    {
        float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((float)((t - data.CardFrom - 0.2) / 1.6)));  // the count-up
        cardNumber.text = Mathf.RoundToInt(data.FinalScore * k).ToString(CultureInfo.InvariantCulture);
        cardConfidence.text = $"confidence {data.FinalConfidence}";
        if (!float.IsNaN(data.RangeLo))
        {
            cardRange.text = $"range {data.RangeLo:0} to {data.RangeHi:0} under +-30 ms audio-video offset and the DJ mix read either way";
        }
        else cardRange.text = "";
        for (int i = 0; i < RhythmData.ComponentCount; i++)
        {
            float v = Mathf.Clamp01(data.FinalComp[i]) * k;
            cardValue[i].text = Mathf.RoundToInt(data.FinalComp[i] * 100f * k).ToString(CultureInfo.InvariantCulture);
            RectTransform back = cardBarBack[i].rectTransform;
            RectTransform r = cardBarFill[i].rectTransform;
            float w = back.sizeDelta.x * v;
            r.sizeDelta = new Vector2(Mathf.Max(0f, w), back.sizeDelta.y);
            r.anchoredPosition = new Vector2(back.anchoredPosition.x - back.sizeDelta.x * 0.5f + w * 0.5f, back.anchoredPosition.y);
        }
    }

    // ------------------------------------------------------------------ build and layout

    void Build()
    {
        if (built) return;
        built = true;
        GameObject go = new("Rhythm score", typeof(RectTransform));
        go.transform.SetParent(transform, false);
        canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 66;                                    // over the dance HUD (50), under the film overlay (70: titles, fades)
        canvas.pixelPerfect = false;
        root = (RectTransform)go.transform;
        group = go.AddComponent<CanvasGroup>();
        group.interactable = group.blocksRaycasts = false;

        panel = FilmUi.Rect(root, "Panel");
        panelBack = FilmUi.Box(panel, "Back", FilmUi.Rounded, new Color(0f, 0f, 0f, 0.58f));
        title = FilmUi.Label(panel, "Title", 20, new Color(0.92f, 0.94f, 0.97f), TextAnchor.MiddleLeft, true);
        title.text = "RHYTHM SCORE";
        pulse = FilmUi.Box(panel, "Beat pulse", FilmUi.Disc, new Color(1f, 0.84f, 0.04f, 0.5f));
        subtitle = FilmUi.Label(panel, "Subtitle", 14, new Color(0.78f, 0.82f, 0.88f, 0.85f), TextAnchor.MiddleLeft);
        subtitle.text = "estimated from motion, not an official score";
        big = FilmUi.Label(panel, "Number", 80, Color.white, TextAnchor.MiddleLeft, true);
        FilmUi.Shadow(big, new Color(0f, 0f, 0f, 0.6f), 2f);
        confidence = FilmUi.Label(panel, "Confidence", 17, new Color(0.78f, 0.82f, 0.88f), TextAnchor.MiddleLeft);
        status = FilmUi.Label(panel, "Status", 16, new Color(1f, 0.84f, 0.04f, 0.9f), TextAnchor.MiddleLeft);
        for (int i = 0; i < RhythmData.ComponentCount; i++)
        {
            barLabel[i] = FilmUi.Label(panel, $"Bar label {i}", 16, new Color(0.88f, 0.9f, 0.94f), TextAnchor.MiddleLeft);
            barLabel[i].text = data.CompLabels[i];
            barBack[i] = FilmUi.Box(panel, $"Bar back {i}", FilmUi.Rounded, new Color(1f, 1f, 1f, 0.14f));
            barFill[i] = FilmUi.Box(panel, $"Bar {i}", FilmUi.Rounded, BarColours[i]);
        }

        spark = FilmUi.Box(panel, "Sparkline", null, Color.white);
        spark.sprite = Sprite.Create(sparkTex, new Rect(0, 0, SparkW, SparkH), new Vector2(0.5f, 0.5f), 100f);
        spark.type = Image.Type.Simple;
        lampLead = FilmUi.Box(panel, "Lead step", FilmUi.Disc, new Color(0.35f, 0.38f, 0.42f));
        lampFollow = FilmUi.Box(panel, "Follow step", FilmUi.Disc, new Color(0.35f, 0.38f, 0.42f));
        lampLeadLabel = FilmUi.Label(panel, "Lead label", 15, new Color(0.88f, 0.9f, 0.94f), TextAnchor.MiddleLeft);
        lampLeadLabel.text = "lead step";
        lampFollowLabel = FilmUi.Label(panel, "Follow label", 15, new Color(0.88f, 0.9f, 0.94f), TextAnchor.MiddleLeft);
        lampFollowLabel.text = "follow step";
        footer = FilmUi.Label(panel, "Footer", 14, new Color(0.7f, 0.74f, 0.8f, 0.8f), TextAnchor.MiddleLeft);
        footer.text = "green on the beat, amber near, red off";

        cardRoot = FilmUi.Rect(root, "Final card");
        cardGroup = cardRoot.gameObject.AddComponent<CanvasGroup>();
        cardGroup.interactable = cardGroup.blocksRaycasts = false;
        cardBack = FilmUi.Box(cardRoot, "Back", FilmUi.Rounded, new Color(0.02f, 0.02f, 0.04f, 0.97f));
        cardTitle = FilmUi.Label(cardRoot, "Title", 30, new Color(1f, 0.84f, 0.04f), TextAnchor.MiddleCenter, true);
        cardTitle.text = "FINAL RHYTHM SCORE";
        cardNumber = FilmUi.Label(cardRoot, "Number", 150, Color.white, TextAnchor.MiddleCenter, true);
        FilmUi.Shadow(cardNumber, new Color(0f, 0f, 0f, 0.6f), 3f);
        cardConfidence = FilmUi.Label(cardRoot, "Confidence", 24, new Color(0.85f, 0.88f, 0.92f), TextAnchor.MiddleCenter);
        cardRange = FilmUi.Label(cardRoot, "Range", 17, new Color(0.78f, 0.82f, 0.88f, 0.9f), TextAnchor.MiddleCenter);
        cardNote = FilmUi.Label(cardRoot, "Note", 17, new Color(0.78f, 0.82f, 0.88f, 0.9f), TextAnchor.MiddleCenter);
        cardNote.text = "estimated from motion, not an official score";
        cardSmall = FilmUi.Label(cardRoot, "Small", 15, new Color(0.7f, 0.74f, 0.8f, 0.85f), TextAnchor.MiddleCenter);
        cardSmall.text = "0 = what random steps would score, 100 = perfect";
        for (int i = 0; i < RhythmData.ComponentCount; i++)
        {
            cardLabel[i] = FilmUi.Label(cardRoot, $"Label {i}", 20, new Color(0.9f, 0.92f, 0.95f), TextAnchor.MiddleLeft);
            cardLabel[i].text = $"{data.CompLabels[i]}  ({data.CompWeights[i]}%)";
            cardValue[i] = FilmUi.Label(cardRoot, $"Value {i}", 20, Color.white, TextAnchor.MiddleRight, true);
            cardBarBack[i] = FilmUi.Box(cardRoot, $"Bar back {i}", FilmUi.Rounded, new Color(1f, 1f, 1f, 0.14f));
            cardBarFill[i] = FilmUi.Box(cardRoot, $"Bar {i}", FilmUi.Rounded, BarColours[i]);
        }

        laidOut = false;
        canvas.enabled = false;
    }

    void Layout()
    {
        float W = Screen.width, H = Screen.height;
        bool film = filmPlacement && filmW > 0f;
        bool vertical = film ? filmVertical : H > W;
        float u = film ? filmUnit : vertical ? W / 1080f : H / 1080f;
        if (laidOut && Mathf.Approximately(laidW, W) && Mathf.Approximately(laidH, H) && Mathf.Approximately(laidUnit, u) && placedFilm == film && placedSafe == (film ? filmSafeTop : -1f)) return;
        laidOut = true;
        laidW = W;
        laidH = H;
        laidUnit = u;
        placedFilm = film;
        placedSafe = film ? filmSafeTop : -1f;
        float pw = 384f * u, ph = 438f * u, pad = 14f * u;
        float left, top;
        if (film)
        {
            left = (vertical ? 26f : 40f) * u;
            top = H * (1f - Mathf.Max(filmSafeTop, vertical ? 0.14f : 0.05f)) - 8f * u;     // under the top platform band, level with the zouk beat counter
            Placement = vertical ? "film vertical, top left" : "film horizontal, top left";
        }
        else
        {
            left = W - pw - 24f * u;
            top = H - 24f * u;
            Placement = "viewer, top right";
        }

        panel.anchorMin = panel.anchorMax = Vector2.zero;
        panel.pivot = new Vector2(0f, 1f);
        panel.anchoredPosition = new Vector2(left, top);
        panel.sizeDelta = new Vector2(pw, ph);
        PanelRect = new Rect(left, top - ph, pw, ph);
        Place(panelBack.rectTransform, pw * 0.5f, ph * 0.5f, pw, ph);
        panelBack.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 14f * u);
        float y = ph - pad;                                          // running top edge (panel-local, y up)
        SetText(title, 20f, u, pad, y - 13f * u, pw - 2f * pad - 30f * u, 26f * u);
        Place(pulse.rectTransform, pw - pad - 10f * u, y - 13f * u, 16f * u, 16f * u);
        SetText(subtitle, 14f, u, pad, y - 40f * u, pw - 2f * pad, 20f * u);
        SetText(big, 80f, u, pad, y - 96f * u, 150f * u, 92f * u);
        SetText(confidence, 17f, u, pad + 150f * u, y - 84f * u, pw - 2f * pad - 150f * u, 22f * u);
        SetText(status, 16f, u, pad + 150f * u, y - 108f * u, pw - 2f * pad - 150f * u, 22f * u);
        float by = y - 150f * u;
        for (int i = 0; i < RhythmData.ComponentCount; i++)
        {
            float cy = by - i * 29f * u;
            SetText(barLabel[i], 16f, u, pad, cy, 120f * u, 22f * u);
            float bw = pw - 2f * pad - 128f * u;
            Place(barBack[i].rectTransform, pad + 128f * u + bw * 0.5f, cy, bw, 11f * u);
            barBack[i].pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 6f * u);
            barFill[i].rectTransform.anchoredPosition = barBack[i].rectTransform.anchoredPosition;
            barFill[i].pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 6f * u);
        }

        float sw = pw - 2f * pad, sh = SparkH * u * (sw / (SparkW * u));
        float sy = by - 5f * 29f * u - 8f * u - sh * 0.5f - 6f * u;
        Place(spark.rectTransform, pw * 0.5f, sy, sw, sh);
        float ly = sy - sh * 0.5f - 22f * u;
        Place(lampLead.rectTransform, pad + 9f * u, ly, 17f * u, 17f * u);
        SetText(lampLeadLabel, 15f, u, pad + 26f * u, ly, 100f * u, 20f * u);
        Place(lampFollow.rectTransform, pad + 148f * u, ly, 17f * u, 17f * u);
        SetText(lampFollowLabel, 15f, u, pad + 165f * u, ly, 110f * u, 20f * u);
        SetText(footer, 14f, u, pad, ly - 26f * u, pw - 2f * pad, 20f * u);

        // the final card: centred in the safe area
        float cw = Mathf.Min(W * 0.86f, 700f * u), chh = 560f * u;
        float cx = W * 0.5f, cyc = vertical ? H * 0.5f : H * 0.52f;
        cardRoot.anchorMin = cardRoot.anchorMax = Vector2.zero;
        cardRoot.pivot = new Vector2(0.5f, 0.5f);
        cardRoot.anchoredPosition = new Vector2(cx, cyc);
        cardRoot.sizeDelta = new Vector2(cw, chh);
        Place(cardBack.rectTransform, cw * 0.5f, chh * 0.5f, cw, chh);
        cardBack.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 18f * u);
        SetText(cardTitle, 30f, u, cw * 0.5f - cw * 0.45f, chh - 44f * u, cw * 0.9f, 40f * u);
        SetText(cardNumber, 150f, u, cw * 0.5f - cw * 0.45f, chh - 150f * u, cw * 0.9f, 170f * u);
        SetText(cardConfidence, 24f, u, cw * 0.5f - cw * 0.45f, chh - 246f * u, cw * 0.9f, 32f * u);
        float ry = chh - 292f * u;
        for (int i = 0; i < RhythmData.ComponentCount; i++)
        {
            float cy2 = ry - i * 34f * u;
            SetText(cardLabel[i], 20f, u, 30f * u, cy2, 250f * u, 28f * u);
            float bw2 = cw - 2f * 30f * u - 250f * u - 70f * u;
            Place(cardBarBack[i].rectTransform, 30f * u + 250f * u + bw2 * 0.5f, cy2, bw2, 13f * u);
            cardBarBack[i].pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 6f * u);
            cardBarFill[i].rectTransform.anchoredPosition = cardBarBack[i].rectTransform.anchoredPosition;
            cardBarFill[i].pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 6f * u);
            SetText(cardValue[i], 20f, u, cw - 30f * u - 60f * u, cy2, 60f * u, 28f * u);
        }

        SetText(cardNote, 17f, u, cw * 0.5f - cw * 0.45f, 62f * u, cw * 0.9f, 24f * u);
        SetText(cardRange, 15f, u, cw * 0.5f - cw * 0.45f, 38f * u, cw * 0.9f, 22f * u);
        SetText(cardSmall, 15f, u, cw * 0.5f - cw * 0.45f, 16f * u, cw * 0.9f, 20f * u);
    }

    bool placedFilm;
    float placedSafe = -2f;

    static void Place(RectTransform rt, float cx, float cy, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(cx, cy);
        rt.sizeDelta = new Vector2(w, h);
    }

    /// <summary>a text laid in the panel: x from the left edge, centre y, width, height; font size in units</summary>
    static void SetText(Text t, float size, float u, float x, float cy, float w, float h)
    {
        t.fontSize = Mathf.Max(8, Mathf.RoundToInt(size * u));
        RectTransform rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = new Vector2(x, cy);
        rt.sizeDelta = new Vector2(w, h);
    }

    void OnDestroy()
    {
        if (sparkTex != null) Destroy(sparkTex);
    }

    public Dictionary<string, object> State()
    {
        float t = float.NaN;
        int state = -1;
        if (data != null && timeline != null && !double.IsNaN(lastAudio))
        {
            double r = lastAudio - timeline.TimeToAudio;
            t = (float)r;
            state = data.StateAt(r);
        }

        return new Dictionary<string, object>
        {
            ["loaded"] = data != null, ["visible"] = Visible, ["layer"] = visible, ["shown"] = Shown, ["time"] = float.IsNaN(t) ? (object)null : Math.Round(t, 3),
            ["score"] = data != null && !float.IsNaN(t) ? Math.Round(data.Sample(data.Score, t), 2) : (object)null,
            ["finalScore"] = data != null ? data.FinalScore : float.NaN, ["state"] = state, ["card"] = Math.Round(CardAlpha, 3),
            ["placement"] = Placement, ["panel"] = new[] { Mathf.Round(PanelRect.x), Mathf.Round(PanelRect.y), Mathf.Round(PanelRect.width), Mathf.Round(PanelRect.height) },
            ["leadFlash"] = LeadFlash, ["followFlash"] = FollowFlash, ["steps"] = data != null ? data.StepT.Length : 0,
            ["confidence"] = data != null && !float.IsNaN(t) ? Math.Round(data.Sample(data.Confidence, t), 3) : (object)null
        };
    }

    /// <summary>the timing ring colour of a touchdown: green on the beat, amber near, red off; null = not a rhythm step (or the layer is off)</summary>
    public Color? RingColour(Role role, double referenceTime)
    {
        if (data == null || !visible || FilmDirector.HeadIsCamera) return null;
        int c = data.ClassOf(role == Role.Lead ? 0 : 1, referenceTime);
        if (c < 0) return null;
        return c == 0 ? OnColour : c == 1 ? NearColour : OffColour;
    }
}
