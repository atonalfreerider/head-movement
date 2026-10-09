using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// roleHidden (user 2026-10-08: "mute the lead's avatar and skeleton in the first 4 seconds ... we should fade in on his avatar and
/// skeleton starting at four seconds"): per-role hidden spans with a fade, in CAPTURE seconds - seconds from the capture's first
/// frame, the clock the viewer's HUD prints (HUD 0.00 s = the first frame; audio clock = capture clock + timeline.First). Nothing about
/// the reference clock (negative before the music's zero) leaks in here.
///
/// A span { from, to, fade_in, fade_out } hides the role fully for from &lt;= t &lt; to (alpha 0), fades it IN over fade_in seconds
/// starting exactly at `to` (alpha 0 at `to`, 1 at to + fade_in, smoothstep: no pop), and fades it OUT over fade_out seconds ending at
/// `from` (alpha 1 at from - fade_out, 0 at `from`). With several spans the alpha is the lowest of them. fade_in / fade_out default to
/// 0 (a hard cut). A span with from <= 0 covers the very start of the capture whatever the rounding of the clock.
///
/// What the alpha scales: the role's avatar (hair, shoes) on top of the viewer's avatar opacity (0.35 stays 0.35 once faded in), the
/// skeleton lines (and the follower's bead chain) and every overlay derived from its pose (floor-craft T and record, balance axes, the
/// couple's counterbalance dot / axis, hand-contact lights, the miniature couple in the graph view). Nothing else changes - poses, clock,
/// camera, narration and the other dancer are untouched (a pure visibility switch). A point event of the role (a footprint, a touchdown
/// ring) is scaled by the alpha at its own time; a floor-craft record entry wholly inside the fully hidden part is never shown.
///
/// Per capture: capture.json "role_hidden": { "lead": [ { "from": 0.0, "to": 4.0, "fade_in": 1.0 } ] } (also [from, to, fade_in, fade_out]
/// arrays; written by dancecap export_unity from the take's [export.role_hidden]); hm_hide sets / clears it for a session; hm_state
/// "roleHidden" reports it.
/// </summary>
public sealed class RoleHiddenSpans
{
    /// <summary>alpha at or below this counts as fully hidden (nothing drawn at all, no pose work for the lines)</summary>
    public const float HiddenBelow = 0.002f;

    public struct Span
    {
        public float From, To, FadeIn, FadeOut;

        public override string ToString() => $"[{From:0.###}, {To:0.###}) in {FadeIn:0.###} out {FadeOut:0.###}";
    }

    readonly List<Span>[] spans = { new(), new() };

    /// <summary>audio clock time of capture second 0 (the first frame)</summary>
    public float Origin { get; private set; }

    /// <summary>bumped on every change (consumers that cache a per-frame result re-evaluate when it moves)</summary>
    public int Version { get; private set; }

    public bool Any => spans[0].Count > 0 || spans[1].Count > 0;

    public IReadOnlyList<Span> Of(Role role) => spans[(int)role & 1];

    /// <summary>load a capture's spans (the manifest's role_hidden); an absent block means no spans. origin = timeline.First</summary>
    public void Reset(CaptureManifest manifest, float origin)
    {
        Origin = origin;
        foreach (List<Span> s in spans) s.Clear();
        if (manifest?.role_hidden is JObject root)
        {
            foreach (KeyValuePair<string, JToken> kv in root)
            {
                if (!TryRole(kv.Key, out Role role) || kv.Value is not JArray list) continue;
                foreach (JToken entry in list)
                {
                    if (TryEntry(entry, out Span span)) spans[(int)role].Add(span);
                }
            }
        }

        foreach (Role role in new[] { Role.Lead, Role.Follow }) Normalise(role);
        Version++;
    }

    static bool TryEntry(JToken entry, out Span span)
    {
        span = default;
        if (entry is JArray a && a.Count >= 2)
        {
            span = new Span { From = F(a[0], 0f), To = F(a[1], 0f), FadeIn = a.Count > 2 ? F(a[2], 0f) : 0f, FadeOut = a.Count > 3 ? F(a[3], 0f) : 0f };
            return true;
        }

        if (entry is JObject o)
        {
            span = new Span
            {
                From = F(o["from"] ?? o["t0"], float.NegativeInfinity), To = F(o["to"] ?? o["t1"], float.NaN),
                FadeIn = F(o["fade_in"] ?? o["fadeIn"], 0f), FadeOut = F(o["fade_out"] ?? o["fadeOut"], 0f)
            };
            return true;
        }

        return false;
    }

    static float F(JToken t, float fallback) =>
        t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? (float)t : fallback;

    public void Set(Role role, IEnumerable<Span> list)
    {
        spans[(int)role & 1].Clear();
        if (list != null) spans[(int)role & 1].AddRange(list);
        Normalise(role);
        Version++;
    }

    public void Add(Role role, Span span)
    {
        spans[(int)role & 1].Add(span);
        Normalise(role);
        Version++;
    }

    public void Clear(Role role) => Set(role, null);

    /// <summary>drop empty / non-finite spans (an open start is allowed: -infinity = from the very start), clamp the fades, sort</summary>
    void Normalise(Role role)
    {
        List<Span> list = spans[(int)role & 1];
        List<Span> kept = list
            .Where(s => !float.IsNaN(s.From) && float.IsFinite(s.To) && s.To > s.From)
            .Select(s => new Span { From = s.From, To = s.To, FadeIn = Mathf.Max(0f, s.FadeIn), FadeOut = Mathf.Max(0f, s.FadeOut) })
            .OrderBy(s => s.From).ToList();
        list.Clear();
        list.AddRange(kept);
    }

    static float Smooth(float x) => x <= 0f ? 0f : x >= 1f ? 1f : x * x * (3f - 2f * x);

    /// <summary>the role's visibility at this capture second: 0 inside a span, fading to 1 around it</summary>
    public float AlphaAtCapture(Role role, double captureTime)
    {
        List<Span> list = spans[(int)role & 1];
        float alpha = 1f;
        for (int i = 0; i < list.Count; i++)
        {
            Span s = list[i];
            float a;
            // a span that starts at (or before) the first frame covers everything before it too: no float rounding of the clock origin can
            // leave a frame at the very start drawn
            if ((s.From <= 0f || captureTime >= s.From) && captureTime < s.To) return 0f;
            if (captureTime >= s.To) a = s.FadeIn > 1e-6f ? Smooth((float)((captureTime - s.To) / s.FadeIn)) : 1f;
            else a = s.FadeOut > 1e-6f ? 1f - Smooth((float)((captureTime - (s.From - s.FadeOut)) / s.FadeOut)) : 1f;
            if (a < alpha) alpha = a;
        }

        return alpha;
    }

    /// <summary>the role's visibility at this audio-clock time (capture second = audio - Origin)</summary>
    public float AlphaAtAudio(Role role, double audioTime) =>
        spans[(int)role & 1].Count == 0 ? 1f : AlphaAtCapture(role, audioTime - Origin);

    public bool HiddenAtAudio(Role role, double audioTime) => AlphaAtAudio(role, audioTime) <= HiddenBelow;

    /// <summary>an entry lasting [a0, a1] (audio seconds) lies wholly inside the fully hidden part of one span: it was derived from a
    /// pose that is never shown</summary>
    public bool WhollyInsideAudio(Role role, double a0, double a1)
    {
        List<Span> list = spans[(int)role & 1];
        if (list.Count == 0 || double.IsNaN(a0) || double.IsNaN(a1)) return false;
        double c0 = a0 - Origin, c1 = a1 - Origin;
        for (int i = 0; i < list.Count; i++)
        {
            if ((list[i].From <= 0f || c0 >= list[i].From) && c1 <= list[i].To) return true;
        }

        return false;
    }

    public static bool TryRole(string name, out Role role)
    {
        role = Role.Lead;
        if (string.Equals(name, "lead", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(name, "follow", StringComparison.OrdinalIgnoreCase))
        {
            role = Role.Follow;
            return true;
        }

        return false;
    }

    /// <summary>"0:4:1;10:12:0.5:0.5" (capture seconds; from:to[:fade_in[:fade_out]]; ';' or ',' between spans) -> spans</summary>
    public static List<Span> Parse(string text)
    {
        List<Span> list = new();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (string part in text.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] p = part.Split(':');
            if (p.Length < 2 || p.Length > 4) throw new ArgumentException($"span '{part}': use from:to[:fade_in[:fade_out]] in capture seconds, e.g. 0:4:1");
            float N(int i) => float.Parse(p[i], CultureInfo.InvariantCulture);
            list.Add(new Span { From = N(0), To = N(1), FadeIn = p.Length > 2 ? N(2) : 0f, FadeOut = p.Length > 3 ? N(3) : 0f });
        }

        return list;
    }

    /// <summary>{ "lead": [[from, to, fade_in, fade_out]], "follow": [...] } in capture seconds, or audio seconds (hm_state, hm_hide)</summary>
    public Dictionary<string, object> State(bool audio = false)
    {
        float shift = audio ? Origin : 0f;
        object Of2(int role) => spans[role].Select(v => new[] { v.From + shift, v.To + shift, v.FadeIn, v.FadeOut }).ToList();
        return new Dictionary<string, object> { ["lead"] = Of2(0), ["follow"] = Of2(1) };
    }
}
