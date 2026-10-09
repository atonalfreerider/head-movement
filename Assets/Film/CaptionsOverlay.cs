using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Narration captions with word-level karaoke (the film's narration timeline: ElevenLabs word timings on the film
/// clock). One caption chunk at a time, fading in and out; the word being spoken sits on a yellow box (#FFD60A, dark
/// text) that slides to the next word over ~60 ms; the other words are white with a dark outline and shadow, on a
/// subtle dark backing.
/// - 9:16: the lower section of the frame, above the bottom title-safe band (block bottom at 79 % of the height),
///   centred, at most 2 short lines (the chunker's 24 characters), 62 px at 1080 wide.
/// - 16:9: one long line at the bottom (baseline at 92.5 %), 44 px at 1080 high; a line wider than 64 % of the frame
///   wraps to two (the block grows upwards).
/// Pixel sizes follow Screen (the Recorder sets the Game view to the preset size). Deterministic: a pure function of
/// film time.
/// </summary>
public class CaptionsOverlay : MonoBehaviour
{
    class WordView
    {
        public RectTransform Rt;
        public Text Text;
        public Outline Outline;
        public Shadow Shadow;
        public Rect Box; // word rect in screen px (no padding)
    }

    RectTransform root;
    Image backing, highlight;
    readonly List<WordView> pool = new();
    List<FilmNarration.Chunk> chunks = new();
    FilmCaptionStyle style = new();
    FilmAspect aspect = new();
    int laidOut = -1, laidW, laidH, wordCount;
    float fontPx, unit;

    public string CurrentWord { get; private set; }
    public string CurrentText { get; private set; }
    public int CurrentChunk { get; private set; } = -1;
    public float Alpha { get; private set; }
    public int Lines { get; private set; }
    public Rect BlockRect { get; private set; } // screen px (bottom-left origin)
    public Rect HighlightRect { get; private set; }
    public float FontPx => fontPx;

    public void Init(RectTransform canvasRoot, List<FilmNarration.Chunk> list, FilmCaptionStyle s, FilmAspect a)
    {
        chunks = list ?? new List<FilmNarration.Chunk>();
        style = s ?? new FilmCaptionStyle();
        aspect = a ?? new FilmAspect();
        if (root == null)
        {
            root = FilmUi.Rect(canvasRoot, "Narration captions");
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.offsetMin = root.offsetMax = Vector2.zero;
            backing = FilmUi.Box(root, "Caption backing", FilmUi.Rounded, new Color(0f, 0f, 0f, 0.38f));
            highlight = FilmUi.Box(root, "Current word", FilmUi.Rounded, style.Highlight);
        }

        highlight.color = style.Highlight;
        laidOut = -1;
        Hide();
    }

    public void Hide()
    {
        if (root == null) return;
        backing.enabled = false;
        highlight.enabled = false;
        foreach (WordView w in pool) w.Text.enabled = false;
        CurrentChunk = -1;
        CurrentWord = null;
        CurrentText = null;
        Alpha = 0f;
    }

    public void SetVisible(bool on)
    {
        if (root != null) root.gameObject.SetActive(on);
    }

    int ChunkAt(float t)
    {
        for (int i = 0; i < chunks.Count; i++)
        {
            if (t >= chunks[i].Show && t < chunks[i].Hide) return i;
        }

        return -1;
    }

    public void SetTime(float t)
    {
        if (root == null) return;
        int i = ChunkAt(t);
        if (i < 0)
        {
            if (CurrentChunk >= 0 || backing.enabled) Hide();
            return;
        }

        FilmNarration.Chunk c = chunks[i];
        if (i != laidOut || Screen.width != laidW || Screen.height != laidH) Layout(i);
        CurrentChunk = i;
        float a = Mathf.Min(Mathf.SmoothStep(0f, 1f, (t - c.Show) / 0.12f), Mathf.SmoothStep(0f, 1f, (c.Hide - t) / 0.15f));
        Alpha = a;
        CurrentText = string.Join(" / ", c.Lines);

        // the word being spoken: the last one started, until the next starts (0.3 s after the last word ends: none)
        int cur = -1;
        for (int k = 0; k < c.Words.Length; k++)
        {
            if (c.Words[k].Start <= t + 1e-4f) cur = k;
        }

        if (cur == c.Words.Length - 1 && cur >= 0 && t > c.Words[cur].End + 0.3f) cur = -1;
        CurrentWord = cur >= 0 ? c.Words[cur].Text : null;

        backing.enabled = true;
        FilmUi.SetAlpha(backing, 0.38f * a);
        for (int k = 0; k < wordCount; k++)
        {
            WordView w = pool[k];
            bool isCur = k == cur;
            w.Text.enabled = true;
            Color tc = isCur ? style.HighlightText : style.Text;
            tc.a = a;
            w.Text.color = tc;
            Color oc = style.Outline;
            oc.a = isCur ? 0f : style.Outline.a;
            w.Outline.effectColor = oc;
            Color sc = new(0f, 0f, 0f, isCur ? 0f : 0.55f);
            w.Shadow.effectColor = sc;
        }

        for (int k = wordCount; k < pool.Count; k++) pool[k].Text.enabled = false;

        if (cur < 0)
        {
            highlight.enabled = false;
            HighlightRect = default;
            return;
        }

        // the box slides from the previous word over the transition time (deterministic in t)
        Rect to = Pad(pool[cur].Box);
        Rect r = to;
        float tr = Mathf.Max(0.001f, style.TransitionMs / 1000f);
        float since = t - c.Words[cur].Start;
        if (cur > 0 && since < tr && pool[cur - 1].Box.y > to.y - 1f && pool[cur - 1].Box.y < to.y + 1f)
        {
            Rect from = Pad(pool[cur - 1].Box);
            float k = Mathf.SmoothStep(0f, 1f, since / tr);
            r = new Rect(Mathf.Lerp(from.x, to.x, k), to.y, Mathf.Lerp(from.width, to.width, k), to.height);
        }

        highlight.enabled = true;
        Color hc = style.Highlight;
        hc.a = a;
        highlight.color = hc;
        FilmUi.Place(highlight.rectTransform, r.center, r.size);
        HighlightRect = r;
        // keep the box behind the words
        highlight.rectTransform.SetSiblingIndex(1);
    }

    Rect Pad(Rect b)
    {
        Vector2 p = style.BoxPadding * unit;
        return new Rect(b.x - p.x, b.y - p.y, b.width + 2 * p.x, b.height + 2 * p.y);
    }

    void Layout(int index)
    {
        laidOut = index;
        laidW = Screen.width;
        laidH = Screen.height;
        FilmNarration.Chunk c = chunks[index];
        float W = laidW, H = laidH;
        unit = aspect.Vertical ? W / 1080f : H / 1080f;
        fontPx = Mathf.Max(10f, aspect.CaptionFontPx * unit);
        int size = Mathf.RoundToInt(fontPx);
        float maxW = aspect.CaptionWidthFrac * W;
        float space = FilmUi.Width(" ", size, FontStyle.Bold);

        // lines of word indices: the chunker's lines, re-wrapped when one is wider than the block
        List<List<int>> lines = new();
        int nLines = Mathf.Max(1, c.Lines.Length);
        for (int l = 0; l < nLines; l++) lines.Add(new List<int>());
        for (int k = 0; k < c.Words.Length; k++) lines[Mathf.Clamp(c.Words[k].Line, 0, nLines - 1)].Add(k);
        float[] ww = new float[c.Words.Length];
        for (int k = 0; k < c.Words.Length; k++) ww[k] = FilmUi.Width(c.Words[k].Text, size, FontStyle.Bold);
        List<List<int>> wrapped = new();
        foreach (List<int> line in lines)
        {
            List<int> cur = new();
            float x = 0f;
            foreach (int k in line)
            {
                float add = (cur.Count > 0 ? space : 0f) + ww[k];
                if (cur.Count > 0 && x + add > maxW)
                {
                    wrapped.Add(cur);
                    cur = new List<int>();
                    x = 0f;
                    add = ww[k];
                }

                cur.Add(k);
                x += add;
            }

            if (cur.Count > 0) wrapped.Add(cur);
        }

        Lines = wrapped.Count;
        float lineH = fontPx * 1.24f;
        Vector2 pad = new Vector2(26f, 14f) * unit;
        float blockH = Lines * lineH + 2f * pad.y;
        float bottom = aspect.Vertical
            ? (1f - aspect.CaptionBottomY) * H
            : (1f - aspect.CaptionBaselineY) * H - 0.32f * fontPx - pad.y;
        bottom = Mathf.Max(bottom, 4f);

        while (pool.Count < c.Words.Length)
        {
            Text t = FilmUi.Label(root, $"Word {pool.Count}", size, Color.white, TextAnchor.MiddleCenter, true);
            FilmUi.Outline(t, style.Outline, Mathf.Max(1f, style.OutlinePx * unit));
            Shadow sh = t.gameObject.AddComponent<Shadow>();
            sh.useGraphicAlpha = true;
            pool.Add(new WordView { Rt = t.rectTransform, Text = t, Outline = t.GetComponent<Outline>(), Shadow = sh });
        }

        wordCount = c.Words.Length;
        float widest = 0f;
        for (int l = 0; l < wrapped.Count; l++)
        {
            float lw = 0f;
            foreach (int k in wrapped[l]) lw += ww[k];
            lw += space * Mathf.Max(0, wrapped[l].Count - 1);
            widest = Mathf.Max(widest, lw);
            float y = bottom + pad.y + (wrapped.Count - 1 - l) * lineH + lineH * 0.5f; // line 0 on top
            float x = W * 0.5f - lw * 0.5f;
            foreach (int k in wrapped[l])
            {
                WordView w = pool[k];
                w.Text.text = c.Words[k].Text;
                w.Text.fontSize = size;
                w.Outline.effectDistance = new Vector2(1f, -1f) * Mathf.Max(1f, style.OutlinePx * unit * 0.66f);
                w.Shadow.effectDistance = new Vector2(1.2f, -2f) * unit;
                Rect box = new(x, y - fontPx * 0.56f, ww[k], fontPx * 1.12f);
                w.Box = box;
                FilmUi.Place(w.Rt, box.center, new Vector2(ww[k] + 4f, lineH));
                x += ww[k] + space;
            }
        }

        Rect block = new(W * 0.5f - widest * 0.5f - pad.x, bottom, widest + 2f * pad.x, blockH);
        BlockRect = block;
        FilmUi.Place(backing.rectTransform, block.center, block.size);
        backing.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 16f * unit);
        highlight.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, style.CornerPx * unit);
    }
}
