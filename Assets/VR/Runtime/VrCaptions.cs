using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace HmVr
{
    /// <summary>
    /// A panel that stays where it is until you look away from it, then glides to where you now look (never glued to the head):
    /// held at a distance and a height below the eyes, turned toward the user and upright.
    /// </summary>
    public sealed class LazyFollow
    {
        public float Distance = 1.5f, DropM = 0.5f, ReengageDeg = 32f, SettleDeg = 4f, SmoothS = 0.4f, MaxOffsetM = 0.55f;
        public bool Moving { get; private set; }
        Vector3 vel;
        bool placed;

        public void Reset() => placed = false;

        public Vector3 Target(Vector3 headPos, Quaternion headYaw) => headPos + headYaw * Vector3.forward * Distance + Vector3.down * DropM;

        /// <summary>moves the panel's position toward where the head is looking (yaw only) when it has drifted out of view; returns the new position</summary>
        public Vector3 Step(Vector3 current, Vector3 headPos, Quaternion headRot, Quaternion headYaw, float dt)
        {
            Vector3 target = Target(headPos, headYaw);
            if (!placed)
            {
                placed = true;
                vel = Vector3.zero;
                return target;
            }

            Vector3 toPanel = current - headPos;
            Vector3 flatPanel = new(toPanel.x, 0f, toPanel.z);
            Vector3 flatLook = headYaw * Vector3.forward;
            float off = flatPanel.sqrMagnitude > 1e-4f ? Vector3.Angle(flatPanel, flatLook) : 0f;
            float far = (new Vector3(target.x - current.x, 0f, target.z - current.z)).magnitude;
            if (!Moving && (off > ReengageDeg || far > MaxOffsetM)) Moving = true;
            if (Moving && off < SettleDeg && far < 0.08f) Moving = false;
            if (!Moving) return current;
            return Vector3.SmoothDamp(current, target, ref vel, SmoothS, 4f, dt);
        }
    }

    /// <summary>
    /// The lesson's transcript as a world-space panel: the chunk being spoken, the word on the beat in yellow (the flat captions' karaoke),
    /// a speaker tag. A pure function of the film clock, so a seek or slow motion needs no handling. It exists only for films that have
    /// a transcript; it follows the user's gaze lazily (LazyFollow) and fades in and out with each chunk.
    /// </summary>
    [DefaultExecutionOrder(100)]
    [AddComponentMenu("")]
    public sealed class VrCaptions : MonoBehaviour
    {
        public static VrCaptions Instance { get; private set; }

        public bool Enabled = true;
        public string CurrentText { get; private set; } = "";
        public string CurrentWord { get; private set; }
        public string CurrentSpeaker { get; private set; }
        public float Alpha { get; private set; }
        public bool HasTranscript => chunks != null;
        public VrPanel Panel => panel;
        [System.NonSerialized] public LazyFollow Follow = new() { Distance = 1.6f, DropM = 0.52f };

        VrPanel panel;
        Text body, speakerTag;
        FilmNarration.Chunk[] chunks;
        FilmDirector bound;
        int shownChunk = -1, shownWord = -2;
        readonly StringBuilder sb = new();
        const float WidthMm = 1200f;
        public bool High;

        void Awake()
        {
            Instance = this;
            panel = VrPanel.Create("VR captions", new Vector2(WidthMm, 190f), transform);
            panel.Interactive = false;
            panel.Background.color = new Color(0f, 0f, 0f, 0.55f);
            panel.Background.pixelsPerUnitMultiplier = 24f / 26f;
            body = VrPanel.NewText(panel.Content, "Caption", "", 60f, Color.white, TextAnchor.MiddleCenter, true, true);
            VrPanel.Place(body.rectTransform, new Rect(-WidthMm * 0.5f + 40f, -95f + 18f, WidthMm - 80f, 150f));
            speakerTag = VrPanel.NewText(panel.Content, "Speaker", "", 30f, Color.white, TextAnchor.MiddleLeft, true);
            VrPanel.Place(speakerTag.rectTransform, new Rect(-WidthMm * 0.5f + 40f, 70f, 500f, 40f));
            panel.Alpha = 0f;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        static Color Hex(Color c) => c;

        void LateUpdate()
        {
            FilmDirector d = VrFilm.InLesson ? VrFilm.Director : null;
            if (d != bound)
            {
                bound = d;
                chunks = d != null ? VrFilm.CaptionChunks() : null;
                shownChunk = -1;
                shownWord = -2;
                Follow.Reset();
            }

            VrInput input = VrInput.Instance;
            if (d == null || chunks == null || !Enabled || input == null)
            {
                Alpha = Mathf.MoveTowards(Alpha, 0f, Time.unscaledDeltaTime * 6f);
                panel.Alpha = Alpha;
                if (Alpha <= 0f) CurrentText = "";
                return;
            }

            float t = d.FilmTime;
            int ci = ChunkAt(t);
            float target = 0f;
            if (ci >= 0)
            {
                FilmNarration.Chunk c = chunks[ci];
                target = Mathf.Min(Mathf.SmoothStep(0f, 1f, (t - c.Show) / 0.12f), Mathf.SmoothStep(0f, 1f, (c.Hide - t) / 0.15f));
                int word = CurrentWordIndex(c, t);
                if (ci != shownChunk || word != shownWord)
                {
                    shownChunk = ci;
                    shownWord = word;
                    Render(c, word);
                }
            }
            else
            {
                shownChunk = -1;
                CurrentText = "";
                CurrentWord = null;
                CurrentSpeaker = null;
            }

            Alpha = Mathf.MoveTowards(Alpha, target, Time.unscaledDeltaTime * 8f);
            panel.Alpha = Alpha;
            if (Alpha > 0.002f)
            {
                InputFrame f = input.Frame;
                Follow.DropM = High ? -0.42f : 0.52f;
                Vector3 p = Follow.Step(panel.transform.position, f.HeadPosition, f.HeadRotation, f.HeadYaw, Time.unscaledDeltaTime);
                panel.FaceFrom(p, f.HeadPosition, 0.6f);
            }
        }

        int ChunkAt(float t)
        {
            // chunks are in time order; the last one that started and has not ended
            int lo = 0, hi = chunks.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (chunks[mid].Show <= t)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            for (int i = found; i >= 0 && i > found - 3; i--)
            {
                if (t >= chunks[i].Show && t < chunks[i].Hide) return i;
            }

            return -1;
        }

        static int CurrentWordIndex(FilmNarration.Chunk c, float t)
        {
            int cur = -1;
            for (int k = 0; k < c.Words.Length; k++)
            {
                if (c.Words[k].Start <= t + 1e-4f) cur = k;
            }

            if (cur == c.Words.Length - 1 && cur >= 0 && t > c.Words[cur].End + 0.3f) cur = -1;
            return cur;
        }

        void Render(FilmNarration.Chunk c, int cur)
        {
            sb.Clear();
            int line = c.Words.Length > 0 ? c.Words[0].Line : 0;
            for (int k = 0; k < c.Words.Length; k++)
            {
                FilmNarration.Word w = c.Words[k];
                if (w.IsEmoji) continue; // the built-in font has no emoji; the flat captions draw them as images
                if (k > 0)
                {
                    sb.Append(w.Line != line ? '\n' : ' ');
                    line = w.Line;
                }

                string txt = w.Text ?? "";
                if (k == cur) sb.Append("<color=#FFD60A>").Append(txt).Append("</color>");
                else sb.Append(txt);
            }

            body.text = sb.ToString();
            CurrentText = string.Join(" / ", c.Lines ?? new string[0]);
            CurrentWord = cur >= 0 ? c.Words[cur].Text : null;
            CurrentSpeaker = string.IsNullOrEmpty(c.Speaker) ? null : c.Speaker;
            speakerTag.text = CurrentSpeaker != null ? CurrentSpeaker.ToUpperInvariant() : "";
            speakerTag.color = c.Tint ?? Color.white;
            int lines = Mathf.Max(1, body.text.Split('\n').Length);
            float h = 64f + lines * 72f + (CurrentSpeaker != null ? 36f : 0f);
            panel.SizeMm = new Vector2(WidthMm, h);
            panel.Content.sizeDelta = panel.SizeMm;
            VrPanel.Place(body.rectTransform, new Rect(-WidthMm * 0.5f + 40f, -h * 0.5f + 20f, WidthMm - 80f, h - 40f - (CurrentSpeaker != null ? 34f : 0f)));
            VrPanel.Place(speakerTag.rectTransform, new Rect(-WidthMm * 0.5f + 40f, h * 0.5f - 46f, 600f, 36f));
        }
    }
}
