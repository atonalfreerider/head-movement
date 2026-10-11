using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace HmVr
{
    /// <summary>
    /// The two flat-screen HUD items a lesson can turn on, as one small world-space panel that glides to the lower right of the gaze (never
    /// head-locked): the ZOUK COUNT (eight dots for the eighths of the measure, the current one lit and pulsing, "measure N"; where the
    /// lesson's segment has the layer "beat_counter") and the live RHYTHM SCORE (the number, greyed while the beat-lock evidence is thin;
    /// where the rhythm layer is on). Both are pure functions of the film clock, like the flat versions, so seeks and slow motion follow.
    /// The flat versions are screen-space overlays and never reach the headset.
    /// </summary>
    [DefaultExecutionOrder(110)]
    [AddComponentMenu("")]
    public sealed class VrHud : MonoBehaviour
    {
        public static VrHud Instance { get; private set; }

        public bool Enabled = true;
        public float Alpha { get; private set; }
        public bool BeatShown { get; private set; }
        public bool RhythmShown { get; private set; }
        public int Eighth { get; private set; } = -1;
        public int Measure { get; private set; } = -1;
        public float Score { get; private set; } = float.NaN;

        VrPanel panel;
        readonly Image[] dots = new Image[8];
        Text measureLabel, scoreLabel, scoreNote;
        const float W = 560f, H = 190f;

        void Awake()
        {
            Instance = this;
            panel = VrPanel.Create("VR HUD", new Vector2(W, H), transform);
            panel.Interactive = false;
            panel.Background.color = new Color(0f, 0f, 0f, 0.55f);
            panel.Background.pixelsPerUnitMultiplier = 24f / 26f;
            for (int i = 0; i < 8; i++)
            {
                dots[i] = VrPanel.NewImage(panel.Content, $"eighth {i + 1}", FilmUi.Disc, Color.white);
            }

            measureLabel = VrPanel.NewText(panel.Content, "Measure", "", 26f, new Color(0.85f, 0.88f, 0.92f), TextAnchor.MiddleCenter);
            scoreLabel = VrPanel.NewText(panel.Content, "Rhythm score", "", 66f, Color.white, TextAnchor.MiddleLeft, true);
            scoreNote = VrPanel.NewText(panel.Content, "Rhythm note", "", 20f, new Color(0.78f, 0.82f, 0.88f, 0.9f), TextAnchor.MiddleLeft, false, true);
            panel.Alpha = 0f;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void LateUpdate()
        {
            VrInput input = VrInput.Instance;
            FilmDirector d = VrFilm.InLesson ? VrFilm.Director : null;
            HeadMovement hm = HeadMovement.Instance;
            BeatShown = RhythmShown = false;
            if (d == null || hm == null || input == null || !Enabled)
            {
                Fade(0f);
                return;
            }

            FilmDirector.SegLayers l = d.LayersOf(d.SegmentIndex);
            float audio = float.IsNaN(d.DanceTime) || hm.Timeline == null ? float.NaN : d.DanceTime + hm.Timeline.TimeToAudio;
            BeatShown = l != null && l.BeatCounter && hm.Beats != null && hm.Beats.Count > 0 && float.IsFinite(audio);
            RhythmScoreHud rh = DanceLayers.Instance != null ? DanceLayers.Instance.Rhythm : null;
            RhythmShown = rh != null && rh.Loaded && rh.WantedVisible && rh.Timeline != null && float.IsFinite(audio);
            if (!BeatShown && !RhythmShown)
            {
                Fade(0f);
                return;
            }

            Layout(BeatShown, RhythmShown);
            if (BeatShown) UpdateBeat(hm.Beats, audio);
            if (RhythmShown) UpdateRhythm(rh, audio);
            Fade(1f);
            if (!placed)
            {
                placed = true; // anchored, like every panel: placed once a little to the right of the gaze, then it stays (a recall places it again)
                InputFrame f = VrAnchor.Now(input.Frame);
                Vector3 hudSpot = VrAnchor.Spot(f, 1.45f, 0.42f, 24f);
                panel.FaceFrom(hudSpot, f.HeadPosition, 0.5f);
                VrAnchor.Log("beat / rhythm HUD", hudSpot, f);
            }
        }

        bool placed;

        public void Recall() => placed = false;

        void Fade(float target)
        {
            Alpha = Mathf.MoveTowards(Alpha, target, Time.unscaledDeltaTime * 5f);
            panel.Alpha = Alpha;
            if (Alpha <= 0.002f && target <= 0f) placed = false;
        }

        bool laidBeat, laidRhythm, laidOnce;

        void Layout(bool beat, bool rhythm)
        {
            if (laidOnce && laidBeat == beat && laidRhythm == rhythm) return;
            laidOnce = true;
            laidBeat = beat;
            laidRhythm = rhythm;
            float h = (beat ? 96f : 0f) + (rhythm ? 100f : 0f) + 26f;
            panel.SizeMm = new Vector2(W, h);
            panel.Content.sizeDelta = panel.SizeMm;
            float y = h * 0.5f - 13f;
            if (beat)
            {
                float spacing = 52f, x0 = -spacing * 3.5f;
                for (int i = 0; i < 8; i++) dots[i].gameObject.SetActive(true);
                measureLabel.gameObject.SetActive(true);
                VrPanel.Place(measureLabel.rectTransform, new Rect(-W * 0.5f + 20f, y - 34f, W - 40f, 34f));
                y -= 34f;
                for (int i = 0; i < 8; i++) VrPanel.Place(dots[i].rectTransform, VrPanel.R(x0 + i * spacing, y - 31f, 24f, 24f));
                y -= 62f;
            }
            else
            {
                for (int i = 0; i < 8; i++) dots[i].gameObject.SetActive(false);
                measureLabel.gameObject.SetActive(false);
            }

            scoreLabel.gameObject.SetActive(rhythm);
            scoreNote.gameObject.SetActive(rhythm);
            if (rhythm)
            {
                VrPanel.Place(scoreLabel.rectTransform, new Rect(-W * 0.5f + 30f, y - 86f, 190f, 84f));
                VrPanel.Place(scoreNote.rectTransform, new Rect(-W * 0.5f + 230f, y - 86f, W - 260f, 84f));
            }
        }

        void UpdateBeat(BeatGrid beats, float audioTime)
        {
            int bi = beats.IndexAtOrBefore(audioTime + BeatGrid.SnapTolerance);
            int m = bi >= 0 ? bi / BeatGrid.BeatsPerMeasure : -1;
            int eighth = bi >= 0 ? bi % BeatGrid.BeatsPerMeasure : -1;
            float since = bi >= 0 ? audioTime - beats.Times[bi] : 99f;
            float pulse = Mathf.Exp(-Mathf.Max(0f, since) / 0.16f);
            int first = m * BeatGrid.BeatsPerMeasure;
            for (int i = 0; i < 8; i++)
            {
                int idx = first + i;
                int type = idx >= 0 && idx < beats.Count ? beats.Types[idx] : 3;
                float size = type == 1 ? 34f : type == 2 ? 27f : 18f;
                bool cur = i == eighth;
                dots[i].color = cur ? new Color(1f, 0.84f, 0.04f, 1f) : new Color(1f, 1f, 1f, type == 1 ? 0.75f : type == 2 ? 0.6f : 0.38f);
                if (cur) size *= 1f + 0.55f * pulse;
                dots[i].rectTransform.sizeDelta = new Vector2(size, size);
            }

            if (m != Measure)
            {
                Measure = m;
                measureLabel.text = m >= 0 ? "zouk count   ·   measure " + (m + 1).ToString(CultureInfo.InvariantCulture) : "zouk count";
            }

            Eighth = eighth;
        }

        void UpdateRhythm(RhythmScoreHud rh, float audioTime)
        {
            RhythmData data = rh.Data;
            double t = audioTime - rh.Timeline.TimeToAudio;
            float score = data.Smooth(data.Score, t);
            Score = score;
            int state = data.StateAt(t);
            float evidence = data.Sample(data.Evidence, t);
            bool low = state == 0 && evidence < RhythmScoreHud.MinEvidence;
            scoreLabel.text = Mathf.RoundToInt(score).ToString(CultureInfo.InvariantCulture);
            scoreLabel.color = low ? new Color(1f, 1f, 1f, 0.42f) : Color.white;
            scoreNote.text = low ? "rhythm: not enough beat-lock evidence yet (provisional)" : "rhythm, estimated from the motion (not an official score)";
        }

        public Dictionary<string, object> State() => new()
        {
            ["alpha"] = Alpha, ["beat"] = BeatShown, ["rhythm"] = RhythmShown, ["eighth"] = Eighth, ["measure"] = Measure, ["score"] = float.IsNaN(Score) ? -1f : Score
        };
    }
}
