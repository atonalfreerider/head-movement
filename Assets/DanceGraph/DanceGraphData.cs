using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// The dance-move layer of a capture (VIEWER_SPEC 10, dancecap.moves Unity copies in &lt;capture&gt;/moves/):
/// graph.json (the zouk state machine scaffolded from the user's Zouk1.json + proposed additions), labels.json (the
/// move timeline), path.json (the graph nodes/links the dance took) and fingerprint.json (dwell time per node).
/// Times are converted to the viewer's audio clock (time_base "capture"; "reference" adds time_to_audio). Graph
/// positions stay as authored (Unity axes); World() applies display.offset (centred on the origin, bottom 0.2 m
/// above the floor) and display.scale.
/// </summary>
public class DanceGraphData
{
    public class Node
    {
        public int Index;
        public string Id, Name, AuthoredName, Slug, Move, Shape, ToneName, Family, Status;
        public Color Tone;
        public Vector3 Position; // as authored
        public Quaternion Facing;
        public float Energy;
        public int Band;
        public bool Blank, Isolated;
        public Vector3 World;
    }

    public class Link
    {
        public string Id, From, To, Status;
        public bool Accepted;
        public int FromIndex = -1, ToIndex = -1;
    }

    public class Band
    {
        public int Index;
        public float Lo, Hi, YLo, YHi;
        public string Label;
    }

    public class Segment
    {
        public string Id, Move, Node, Provenance, Notes;
        public float T0, T1;
        public float? Confidence;
    }

    public class Step
    {
        public float T0, T1;
        public string Node, Move, Link, Segment;
        public bool NewLink, Reverse, BreakBefore, Stay;
        public string[] Via = Array.Empty<string>();
        public string[] Links = Array.Empty<string>();
        public int NodeIndex = -1;
        public int[] ViaIndex = Array.Empty<int>();
    }

    public class Fingerprint
    {
        public string Provenance, Banner;
        public float LabelledSeconds;
        public readonly Dictionary<string, (float seconds, float share, int visits)> Nodes = new();
        public readonly Dictionary<string, int> Links = new(); // "from|to" -> count
        public readonly List<(int band, string label, float seconds, float share)> Bands = new();
        public readonly HashSet<string> NewLinkKeys = new();
        public int DistinctMoves, Transitions;
        public float? CounterbalanceShare;
        public float CounterbalanceAnalysedSeconds = float.NaN;
        public float LongestPhraseSeconds = float.NaN;
        public string[] LongestPhraseMoves = Array.Empty<string>();
    }

    public readonly List<Node> Nodes = new();
    public readonly List<Link> Links = new();
    public readonly List<Band> Bands = new();
    public readonly Dictionary<string, Node> NodeById = new();
    public readonly Dictionary<string, string> MoveNames = new(); // slug -> display name
    public Vector3 DisplayOffset;
    public float DisplayScale = 1f;
    public string GraphSource;

    public readonly List<Segment> Segments = new();
    public string LabelsProvenance, LabelsBanner;
    public readonly List<Step> Steps = new();
    public readonly List<(string from, string to, int count)> NewLinks = new();
    public string PathProvenance, PathBanner;
    public Fingerprint Print;
    public readonly List<string> Warnings = new();
    public string Folder;

    public bool HasPath => Steps.Count > 0;

    public Vector3 World(Vector3 authored) => (authored + DisplayOffset) * DisplayScale;

    public static DanceGraphData Load(string movesFolder, CaptureTimeline timeline)
    {
        DanceGraphData d = new() { Folder = movesFolder };
        string graphPath = Path.Combine(movesFolder, "graph.json");
        if (!File.Exists(graphPath)) return null;
        d.ReadGraph(JObject.Parse(File.ReadAllText(graphPath)));

        string labels = Path.Combine(movesFolder, "labels.json");
        if (File.Exists(labels)) d.Guard("labels.json", () => d.ReadLabels(JObject.Parse(File.ReadAllText(labels)), timeline));
        string path = Path.Combine(movesFolder, "path.json");
        if (File.Exists(path)) d.Guard("path.json", () => d.ReadPath(JObject.Parse(File.ReadAllText(path)), timeline));
        string print = Path.Combine(movesFolder, "fingerprint.json");
        if (File.Exists(print)) d.Guard("fingerprint.json", () => d.ReadFingerprint(JObject.Parse(File.ReadAllText(print))));
        return d;
    }

    void Guard(string what, Action read)
    {
        try
        {
            read();
        }
        catch (Exception e)
        {
            Warnings.Add($"{what} unreadable: {e.Message}");
            Debug.LogWarning($"moves/{what} unreadable: {e.Message}");
        }
    }

    static float Num(JToken t, float fallback = float.NaN) =>
        t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? t.Value<float>() : fallback;

    static Vector3 V3(JToken t) => t is JArray a && a.Count >= 3
        ? new Vector3(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>())
        : Vector3.zero;

    void ReadGraph(JObject root)
    {
        GraphSource = root.Value<string>("source");
        if (root["display"] is JObject display)
        {
            DisplayOffset = V3(display["offset"]);
            DisplayScale = Num(display["scale"], 1f);
        }

        if (root["energy_bands"] is JArray bands)
        {
            foreach (JToken b in bands)
            {
                Bands.Add(new Band
                {
                    Index = b.Value<int>("band"), Lo = Num(b["lo"]), Hi = Num(b["hi"]), YLo = Num(b["y_lo"]),
                    YHi = Num(b["y_hi"]), Label = b.Value<string>("label")
                });
            }
        }

        if (root["moves"] is JArray moves)
        {
            foreach (JToken m in moves)
            {
                string slug = m.Value<string>("slug");
                if (slug != null) MoveNames[slug] = m.Value<string>("name") ?? slug;
            }
        }

        foreach (JToken n in root["nodes"] ?? new JArray())
        {
            JArray tone = n["tone"] as JArray;
            JArray facing = n["facing"] as JArray;
            Node node = new()
            {
                Index = Nodes.Count, Id = n.Value<string>("id"), Name = n.Value<string>("name") ?? "",
                AuthoredName = n.Value<string>("authored_name"), Slug = n.Value<string>("slug"),
                Move = n.Value<string>("move"), Shape = n.Value<string>("shape") ?? "Ball",
                ToneName = n.Value<string>("tone_name"), Family = n.Value<string>("family"),
                Status = n.Value<string>("status") ?? "scaffold",
                Tone = tone != null && tone.Count >= 3
                    ? new Color(tone[0].Value<float>(), tone[1].Value<float>(), tone[2].Value<float>())
                    : Color.gray,
                Position = V3(n["position"]),
                Facing = facing != null && facing.Count == 4
                    ? new Quaternion(facing[0].Value<float>(), facing[1].Value<float>(), facing[2].Value<float>(), facing[3].Value<float>())
                    : Quaternion.identity,
                Energy = Num(n["energy"], 0f), Band = n["band"]?.Type == JTokenType.Integer ? n.Value<int>("band") : 0,
                Blank = n.Value<bool?>("blank") ?? string.IsNullOrEmpty(n.Value<string>("name")),
                Isolated = n.Value<bool?>("isolated") ?? false
            };
            if (Mathf.Abs(Quaternion.Dot(node.Facing, node.Facing) - 1f) > 0.1f) node.Facing = Quaternion.identity;
            node.World = World(node.Position);
            Nodes.Add(node);
            if (node.Id != null) NodeById[node.Id] = node;
        }

        foreach (JToken l in root["links"] ?? new JArray())
        {
            Link link = new()
            {
                Id = l.Value<string>("id"), From = l.Value<string>("from"), To = l.Value<string>("to"),
                Status = l.Value<string>("status") ?? "scaffold", Accepted = l.Value<bool?>("accepted") ?? false
            };
            if (link.From != null && NodeById.TryGetValue(link.From, out Node a)) link.FromIndex = a.Index;
            if (link.To != null && NodeById.TryGetValue(link.To, out Node b)) link.ToIndex = b.Index;
            if (link.FromIndex >= 0 && link.ToIndex >= 0) Links.Add(link);
        }
    }

    static float TimeShift(JObject root, CaptureTimeline timeline) =>
        string.Equals(root.Value<string>("time_base"), "reference", StringComparison.OrdinalIgnoreCase) && timeline != null
            ? timeline.TimeToAudio
            : 0f;

    void ReadLabels(JObject root, CaptureTimeline timeline)
    {
        if (string.Equals(root.Value<string>("time_base"), "video", StringComparison.OrdinalIgnoreCase))
        {
            Warnings.Add("labels.json is in video time - not shown (export it in capture time)");
            return;
        }

        float dt = TimeShift(root, timeline);
        LabelsBanner = root.Value<string>("banner");
        foreach (JToken s in root["segments"] ?? new JArray())
        {
            Segments.Add(new Segment
            {
                Id = s.Value<string>("id"), Move = s.Value<string>("move"), Node = s.Value<string>("node"),
                Provenance = s.Value<string>("provenance"), Notes = s.Value<string>("notes"),
                T0 = Num(s["t0"]) + dt, T1 = Num(s["t1"]) + dt,
                Confidence = s["confidence"]?.Type is JTokenType.Float or JTokenType.Integer ? s.Value<float>("confidence") : null
            });
        }

        Segments.Sort((a, b) => a.T0.CompareTo(b.T0));
        LabelsProvenance = Segments.Select(s => s.Provenance).Distinct().Count() == 1 ? Segments[0].Provenance
            : Segments.Count == 0 ? null : "mixed";
    }

    void ReadPath(JObject root, CaptureTimeline timeline)
    {
        float dt = TimeShift(root, timeline);
        PathProvenance = root.Value<string>("provenance");
        PathBanner = root.Value<string>("banner");
        foreach (JToken s in root["steps"] ?? new JArray())
        {
            Step step = new()
            {
                T0 = Num(s["t0"]) + dt, T1 = Num(s["t1"]) + dt, Node = s.Value<string>("node"),
                Move = s.Value<string>("move"), Link = s.Value<string>("link"), Segment = s.Value<string>("segment"),
                NewLink = s.Value<bool?>("new_link") ?? false, Reverse = s.Value<bool?>("reverse") ?? false,
                BreakBefore = s.Value<bool?>("break_before") ?? false, Stay = s.Value<bool?>("stay") ?? false,
                Via = s["via"] is JArray via ? via.Select(v => v.Value<string>()).ToArray() : Array.Empty<string>(),
                Links = s["links"] is JArray ls ? ls.Select(v => v.Value<string>()).Where(v => v != null).ToArray()
                    : s.Value<string>("link") is string one ? new[] { one } : Array.Empty<string>()
            };
            step.NodeIndex = step.Node != null && NodeById.TryGetValue(step.Node, out Node n) ? n.Index : -1;
            step.ViaIndex = step.Via.Select(v => NodeById.TryGetValue(v, out Node vn) ? vn.Index : -1).Where(i => i >= 0).ToArray();
            if (step.NodeIndex < 0) Warnings.Add($"path step {step.Move} at {step.T0:0.00}s: node {step.Node} not in graph.json");
            Steps.Add(step);
        }

        Steps.Sort((a, b) => a.T0.CompareTo(b.T0));
        foreach (JToken l in root["new_links"] ?? new JArray())
        {
            NewLinks.Add((l.Value<string>("from"), l.Value<string>("to"), l.Value<int?>("count") ?? 1));
        }
    }

    void ReadFingerprint(JObject root)
    {
        Fingerprint p = new()
        {
            Provenance = root.Value<string>("provenance"), Banner = root.Value<string>("banner"),
            LabelledSeconds = Num(root["labelled_seconds"], 0f),
            DistinctMoves = root.Value<int?>("distinct_moves") ?? 0, Transitions = root.Value<int?>("transitions") ?? 0,
            CounterbalanceShare = root["counterbalance_share"]?.Type is JTokenType.Float or JTokenType.Integer
                ? root.Value<float>("counterbalance_share")
                : null,
            CounterbalanceAnalysedSeconds = Num(root["counterbalance_analysed_seconds"])
        };
        if (root["nodes"] is JObject nodes)
        {
            foreach (JProperty prop in nodes.Properties())
            {
                p.Nodes[prop.Name] = (Num(prop.Value["seconds"], 0f), Num(prop.Value["share"], 0f), prop.Value.Value<int?>("visits") ?? 0);
            }
        }

        if (root["links"] is JObject links)
        {
            foreach (JProperty prop in links.Properties()) p.Links[prop.Name] = prop.Value.Value<int>();
        }

        foreach (JToken b in root["energy_bands"] ?? new JArray())
        {
            p.Bands.Add((b.Value<int>("band"), b.Value<string>("label"), Num(b["seconds"], 0f), Num(b["share"], 0f)));
        }

        foreach (JToken k in root["new_link_keys"] ?? new JArray()) p.NewLinkKeys.Add(k.Value<string>());
        if (root["longest_phrase"] is JObject phrase)
        {
            p.LongestPhraseSeconds = Num(phrase["seconds"]);
            p.LongestPhraseMoves = phrase["moves"] is JArray mv ? mv.Select(m => m.Value<string>()).ToArray() : Array.Empty<string>();
        }

        Print = p;
    }

    public string MoveName(string slug) => slug != null && MoveNames.TryGetValue(slug, out string name) ? name : slug;

    /// <summary>"PLACEHOLDER - not an analysis" / "AUTOMATIC - unreviewed" badge text for a provenance (null = none)</summary>
    public static string Badge(string provenance, string banner)
    {
        if (string.Equals(provenance, "placeholder", StringComparison.OrdinalIgnoreCase)) return "PLACEHOLDER - not an analysis";
        if (string.Equals(provenance, "auto", StringComparison.OrdinalIgnoreCase)) return "AUTOMATIC - unreviewed";
        if (string.Equals(provenance, "mixed", StringComparison.OrdinalIgnoreCase)) return "MIXED - partly unreviewed";
        return string.IsNullOrEmpty(banner) ? null : banner.ToUpperInvariant();
    }
}
