using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HmVr
{
    public static class VrStyle
    {
        /// <summary>the unnamed layer the VR objects live on (hands, panels, rays, reticles): the menu shows only this layer, so the lobby is a quiet void</summary>
        public const int Layer = 30;

        public static readonly Color Accent = new(1f, 0.839f, 0.039f);
        public static readonly Color Ink = new(0.039f, 0.047f, 0.067f);
        public static readonly Color PanelColour = new(0.07f, 0.082f, 0.115f, 0.94f);
        public static readonly Color Button = new(1f, 1f, 1f, 0.13f);
        public static readonly Color ButtonHot = new(1f, 1f, 1f, 0.30f);
        public static readonly Color Muted = new(0.66f, 0.71f, 0.80f);
        public static readonly Color Amber = new(1f, 0.72f, 0.25f);
        public static readonly Color Text = new(0.96f, 0.97f, 1f);

        static Material uiMaterial;

        /// <summary>the default UI material drawn after the avatars (their translucent passes run up to queue 3022), so a panel in front of a dancer is not painted over by it</summary>
        public static Material UiMaterial
        {
            get
            {
                if (uiMaterial != null) return uiMaterial;
                uiMaterial = new Material(Canvas.GetDefaultCanvasMaterial()) { name = "VR UI", renderQueue = 3900, hideFlags = HideFlags.DontSave };
                return uiMaterial;
            }
        }
    }

    public enum WidgetKind
    {
        Static,
        Button,
        Slider
    }

    /// <summary>One thing on a panel that the hands can act on (or just a label). Rectangles are in millimetres from the panel's centre.</summary>
    public sealed class VrWidget
    {
        public string Id;
        public VrPanel Panel;
        public WidgetKind Kind;
        public Rect RectMm;
        public Func<bool> Enabled = () => true;
        public Action OnClick;
        public Action OnDragBegin, OnDragEnd;
        public Action<float> OnDrag;
        public Image Back, Fill, Head;
        public Text Label;
        public RawImage Picture;
        public readonly List<Graphic> Glyphs = new();
        public readonly List<Image> Ticks = new();
        public Color Normal = VrStyle.Button, Hot = VrStyle.ButtonHot, Pressed = VrStyle.Accent;
        public Color LabelNormal = VrStyle.Text, LabelDisabled = new(1f, 1f, 1f, 0.35f);
        public bool Toggled;
        public Func<string> LabelFunc;
        public float Value; // slider position 0..1

        // set by the pointer system each frame
        public bool Hovered, Pushed;
        public float HoverAmount, PushAmount;
        public int ClickCount;

        public bool Live => Panel != null && Panel.IsLive && Enabled();

        public Vector3 WorldCentre => Panel.Content.TransformPoint(new Vector3(RectMm.center.x, RectMm.center.y, 0f));

        public bool HitTest(Vector2 localMm, float marginMm) =>
            localMm.x >= RectMm.xMin - marginMm && localMm.x <= RectMm.xMax + marginMm && localMm.y >= RectMm.yMin - marginMm && localMm.y <= RectMm.yMax + marginMm;

        public void Click()
        {
            if (!Enabled()) return;
            ClickCount++;
            OnClick?.Invoke();
        }

        public void Tick(float dt)
        {
            if (Kind == WidgetKind.Static) return;
            bool enabled = Enabled();
            HoverAmount = Mathf.MoveTowards(HoverAmount, Hovered && enabled ? 1f : 0f, dt * 10f);
            PushAmount = Mathf.MoveTowards(PushAmount, Pushed && enabled ? 1f : 0f, dt * 14f);
            if (Kind == WidgetKind.Button && Back != null)
            {
                Color c = Toggled ? Color.Lerp(new Color(VrStyle.Accent.r, VrStyle.Accent.g, VrStyle.Accent.b, 0.85f), Pressed, HoverAmount) : Color.Lerp(Normal, Hot, HoverAmount);
                c = Color.Lerp(c, Pressed, PushAmount);
                if (!enabled) c = new Color(1f, 1f, 1f, 0.07f);
                Back.color = c;
                bool dark = Toggled || PushAmount > 0.5f || (Back.color.r > 0.6f && Back.color.g > 0.6f && Back.color.a > 0.7f);
                Color lc = !enabled ? LabelDisabled : dark ? VrStyle.Ink : LabelNormal;
                if (Label != null && Label.color != lc) Label.color = lc;
                foreach (Graphic g in Glyphs)
                {
                    if (g != null && g.color != lc) g.color = lc;
                }
            }

            if (Label != null && LabelFunc != null)
            {
                string t = LabelFunc();
                if (Label.text != t) Label.text = t;
            }

            if (Head != null)
            {
                float s = 1f + 0.25f * Mathf.Max(HoverAmount, PushAmount);
                Head.rectTransform.localScale = new Vector3(s, s, 1f);
            }
        }
    }

    /// <summary>
    /// A flat panel in the world: a world-space canvas drawn with the film's procedural sprites (FilmUi), 1 canvas unit = 1 mm, with a
    /// list of widgets the hand pointers hit-test analytically (no event system, no colliders). Panels live on the VR layer.
    /// </summary>
    public sealed class VrPanel : MonoBehaviour
    {
        public static readonly List<VrPanel> All = new();

        public Vector2 SizeMm;
        public bool Interactive = true;
        /// <summary>a panel attached to a hand is never poked or pointed at by that same hand</summary>
        public Hand? AttachedTo;
        public readonly List<VrWidget> Widgets = new();
        public RectTransform Content { get; private set; }
        public Image Background { get; private set; }
        CanvasGroup group;
        Canvas canvas;
        float alpha = 1f;

        public float Alpha
        {
            get => alpha;
            set
            {
                alpha = Mathf.Clamp01(value);
                if (group != null) group.alpha = alpha;
                if (canvas != null && canvas.enabled != alpha > 0.002f) canvas.enabled = alpha > 0.002f;
            }
        }

        public bool IsLive => isActiveAndEnabled && Interactive && alpha > 0.35f;
        public bool IsShown => isActiveAndEnabled && alpha > 0.002f;
        public RectPlane Plane => RectPlane.OfCanvas(Content, SizeMm * MetersPerMm);
        public float MetersPerMm { get; private set; } = 0.001f;

        public static VrPanel Create(string name, Vector2 sizeMm, Transform parent, bool background = true, float scale = 1f)
        {
            GameObject go = new(name, typeof(RectTransform));
            go.layer = VrStyle.Layer;
            go.transform.SetParent(parent, false);
            VrPanel p = go.AddComponent<VrPanel>();
            p.SizeMm = sizeMm;
            p.canvas = go.AddComponent<Canvas>();
            p.canvas.renderMode = RenderMode.WorldSpace;
            p.canvas.overrideSorting = true;
            p.canvas.sortingOrder = 29000; // over the translucent avatars and hair (sorted by order, then queue)
            CanvasScaler scaler = go.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 3f;
            scaler.referencePixelsPerUnit = 100f;
            p.group = go.AddComponent<CanvasGroup>();
            p.group.interactable = false;
            p.group.blocksRaycasts = false;
            RectTransform rt = (RectTransform)go.transform;
            rt.sizeDelta = sizeMm;
            rt.localScale = Vector3.one * (0.001f * scale);
            p.MetersPerMm = 0.001f * scale;
            rt.pivot = new Vector2(0.5f, 0.5f);
            p.Content = rt;
            if (background)
            {
                p.Background = NewImage(rt, "Background", FilmUi.Rounded, VrStyle.PanelColour);
                Fill(p.Background.rectTransform);
                p.Background.pixelsPerUnitMultiplier = 24f / 14f;
            }

            return p;
        }

        void OnEnable()
        {
            if (!All.Contains(this)) All.Add(this);
        }

        void OnDisable()
        {
            All.Remove(this);
        }

        void Update()
        {
            if (!IsShown) return;
            float dt = Time.unscaledDeltaTime;
            foreach (VrWidget w in Widgets) w.Tick(dt);
        }

        // ------------------------------------------------------------------ building (millimetres from the panel's centre)

        public static Rect R(float cx, float cy, float w, float h) => new(cx - w * 0.5f, cy - h * 0.5f, w, h);

        public static RectTransform NewRect(Transform parent, string name)
        {
            GameObject go = new(name, typeof(RectTransform));
            go.layer = VrStyle.Layer;
            go.transform.SetParent(parent, false);
            RectTransform rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            return rt;
        }

        static void Fill(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        public static void Place(RectTransform rt, Rect mm)
        {
            rt.anchoredPosition = mm.center;
            rt.sizeDelta = mm.size;
        }

        public static Image NewImage(Transform parent, string name, Sprite sprite, Color colour)
        {
            RectTransform rt = NewRect(parent, name);
            Image img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.type = sprite != null && sprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple;
            img.color = colour;
            img.material = VrStyle.UiMaterial;
            img.raycastTarget = false;
            return img;
        }

        public static Text NewText(Transform parent, string name, string text, float fontMm, Color colour, TextAnchor anchor = TextAnchor.MiddleCenter, bool bold = false, bool wrap = false)
        {
            RectTransform rt = NewRect(parent, name);
            Text t = rt.gameObject.AddComponent<Text>();
            t.font = DanceText.Font;
            t.fontSize = Mathf.Max(6, Mathf.RoundToInt(fontMm));
            t.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            t.alignment = anchor;
            t.color = colour;
            t.text = text;
            t.raycastTarget = false;
            t.supportRichText = true;
            t.horizontalOverflow = wrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
            t.verticalOverflow = wrap ? VerticalWrapMode.Truncate : VerticalWrapMode.Overflow;
            t.material = VrStyle.UiMaterial;
            t.lineSpacing = 1.05f;
            return t;
        }

        VrWidget Add(VrWidget w)
        {
            w.Panel = this;
            Widgets.Add(w);
            return w;
        }

        public VrWidget AddButton(string id, string label, Rect mm, Action click, float fontMm = 26f, bool bold = true)
        {
            VrWidget w = new() { Id = id, Kind = WidgetKind.Button, RectMm = mm, OnClick = click };
            w.Back = NewImage(Content, id, FilmUi.Rounded, w.Normal);
            Place(w.Back.rectTransform, mm);
            w.Back.pixelsPerUnitMultiplier = 24f / Mathf.Min(14f, mm.height * 0.45f);
            if (label != null)
            {
                w.Label = NewText(w.Back.transform, "Label", label, fontMm, VrStyle.Text, TextAnchor.MiddleCenter, bold);
                Fill(w.Label.rectTransform);
            }

            return Add(w);
        }

        public VrWidget AddLabel(string id, string text, Rect mm, float fontMm, Color colour, TextAnchor anchor = TextAnchor.MiddleLeft, bool bold = false, bool wrap = false)
        {
            VrWidget w = new() { Id = id, Kind = WidgetKind.Static, RectMm = mm };
            w.Label = NewText(Content, id, text, fontMm, colour, anchor, bold, wrap);
            Place(w.Label.rectTransform, mm);
            return Add(w);
        }

        public VrWidget AddPicture(string id, Rect mm, Texture texture)
        {
            VrWidget w = new() { Id = id, Kind = WidgetKind.Static, RectMm = mm };
            GameObject go = new(id, typeof(RectTransform));
            go.layer = VrStyle.Layer;
            go.transform.SetParent(Content, false);
            w.Picture = go.AddComponent<RawImage>();
            w.Picture.raycastTarget = false;
            w.Picture.material = VrStyle.UiMaterial;
            w.Picture.texture = texture;
            RectTransform rt = w.Picture.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            Place(rt, mm);
            return Add(w);
        }

        public VrWidget AddBox(string id, Rect mm, Color colour, bool rounded = true)
        {
            VrWidget w = new() { Id = id, Kind = WidgetKind.Static, RectMm = mm };
            w.Back = NewImage(Content, id, rounded ? FilmUi.Rounded : null, colour);
            Place(w.Back.rectTransform, mm);
            if (rounded) w.Back.pixelsPerUnitMultiplier = 24f / Mathf.Min(10f, mm.height * 0.4f);
            return Add(w);
        }

        /// <summary>a slider: a track (the hit area is the whole rectangle, which should be tall enough to aim at), a fill, a head and tick marks</summary>
        public VrWidget AddSlider(string id, Rect mm, Action<float> onDrag, Action begin, Action end)
        {
            VrWidget w = new() { Id = id, Kind = WidgetKind.Slider, RectMm = mm, OnDrag = onDrag, OnDragBegin = begin, OnDragEnd = end };
            float th = 9f;
            Rect track = new(mm.xMin, mm.center.y - th * 0.5f, mm.width, th);
            w.Back = NewImage(Content, id + " track", FilmUi.Rounded, new Color(1f, 1f, 1f, 0.28f));
            Place(w.Back.rectTransform, track);
            w.Back.pixelsPerUnitMultiplier = 24f / 4f;
            w.Fill = NewImage(Content, id + " played", FilmUi.Rounded, VrStyle.Accent);
            w.Fill.pixelsPerUnitMultiplier = 24f / 4f;
            w.Head = NewImage(Content, id + " head", FilmUi.Disc, Color.white);
            Place(w.Head.rectTransform, new Rect(0, 0, 24f, 24f));
            SetSlider(w, 0f);
            return Add(w);
        }

        public static void SetSlider(VrWidget w, float u)
        {
            u = Mathf.Clamp01(u);
            w.Value = u;
            float th = 9f;
            float x0 = w.RectMm.xMin, x = x0 + u * w.RectMm.width;
            Place(w.Fill.rectTransform, new Rect(x0, w.RectMm.center.y - th * 0.5f, Mathf.Max(0.5f, x - x0), th));
            w.Head.rectTransform.anchoredPosition = new Vector2(x, w.RectMm.center.y);
        }

        public void SetTicks(VrWidget w, IList<float> u)
        {
            int n = u?.Count ?? 0;
            while (w.Ticks.Count < n)
            {
                Image tick = NewImage(Content, w.Id + " tick", null, new Color(1f, 1f, 1f, 0.95f));
                tick.transform.SetSiblingIndex(w.Head.transform.GetSiblingIndex());
                w.Ticks.Add(tick);
            }

            for (int i = 0; i < w.Ticks.Count; i++)
            {
                bool on = i < n;
                w.Ticks[i].gameObject.SetActive(on);
                if (on) Place(w.Ticks[i].rectTransform, new Rect(w.RectMm.xMin + u[i] * w.RectMm.width - 1.5f, w.RectMm.center.y - 9f, 3f, 18f));
            }
        }

        // ------------------------------------------------------------------ glyphs for buttons (drawn, no font symbols needed)

        public static void GlyphPlay(VrWidget w, float size)
        {
            Image tri = NewImage(w.Back.transform, "Play glyph", FilmUi.Triangle, VrStyle.Ink);
            tri.rectTransform.sizeDelta = new Vector2(size, size);
            tri.rectTransform.anchoredPosition = new Vector2(size / 6f, 0f);
            w.Glyphs.Add(tri);
        }

        public static (Image a, Image b) GlyphPause(VrWidget w, float size)
        {
            Image a = NewImage(w.Back.transform, "Pause glyph A", null, VrStyle.Ink);
            Image b = NewImage(w.Back.transform, "Pause glyph B", null, VrStyle.Ink);
            a.rectTransform.sizeDelta = b.rectTransform.sizeDelta = new Vector2(size * 0.2f, size * 0.8f);
            a.rectTransform.anchoredPosition = new Vector2(-size * 0.22f, 0f);
            b.rectTransform.anchoredPosition = new Vector2(size * 0.22f, 0f);
            w.Glyphs.Add(a);
            w.Glyphs.Add(b);
            return (a, b);
        }

        /// <summary>skip-to-previous / next: a bar and a triangle</summary>
        public static void GlyphSkip(VrWidget w, bool previous, float size)
        {
            Image bar = NewImage(w.Back.transform, "Skip bar", null, Color.white);
            bar.rectTransform.sizeDelta = new Vector2(size * 0.16f, size * 0.9f);
            bar.rectTransform.anchoredPosition = new Vector2((previous ? -0.42f : 0.42f) * size, 0f);
            Image tri = NewImage(w.Back.transform, "Skip triangle", FilmUi.Triangle, Color.white);
            tri.rectTransform.sizeDelta = new Vector2(size, size);
            tri.rectTransform.anchoredPosition = new Vector2((previous ? 0.1f : -0.1f) * size, 0f);
            if (previous) tri.rectTransform.localEulerAngles = new Vector3(0f, 0f, 180f);
            w.Glyphs.Add(bar);
            w.Glyphs.Add(tri);
        }

        public static void GlyphChevron(VrWidget w, float size, float angleDeg)
        {
            Image tri = NewImage(w.Back.transform, "Chevron", FilmUi.Triangle, Color.white);
            tri.rectTransform.sizeDelta = new Vector2(size, size);
            tri.rectTransform.localEulerAngles = new Vector3(0f, 0f, angleDeg);
            w.Glyphs.Add(tri);
        }

        public static void ClearWidgets(VrPanel p)
        {
            for (int i = p.Content.childCount - 1; i >= 0; i--)
            {
                Transform c = p.Content.GetChild(i);
                if (p.Background != null && c == p.Background.transform) continue;
                Destroy(c.gameObject);
            }

            p.Widgets.Clear();
        }

        // ------------------------------------------------------------------ placement

        /// <summary>stand the panel at a point, its face turned toward the viewer (upright: only the yaw and a little pitch follow)</summary>
        public void FaceFrom(Vector3 position, Vector3 viewer, float pitchFollow = 0.5f)
        {
            Vector3 toViewer = viewer - position;
            Vector3 flat = new(toViewer.x, 0f, toViewer.z);
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.back;
            Quaternion yaw = Quaternion.LookRotation(-flat.normalized, Vector3.up); // the canvas's forward points away from the reader
            float pitch = Mathf.Atan2(toViewer.y, flat.magnitude) * Mathf.Rad2Deg * pitchFollow;
            transform.SetPositionAndRotation(position, yaw * Quaternion.Euler(pitch, 0f, 0f));
        }
    }
}
