using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Owner of the VIEWER_SPEC v3 dance layers that sit on top of HeadMovement: counterbalance (3.9/3.5), follower
/// traces (3.7: on by default since 2026-10-07, only her free extremities, ~0.35 s), the follower's neck axis (3.7, layer
/// "neck": on by default, shown only while her neck is off-axis), the leader's stable T axis (3.6, layer "axis"), the
/// floor-craft record of old Ts / dotted T-to-T path / axis pivots / dials (3.5, layer "floorcraft"; FloorCraftOverlay),
/// the physics-mode balance axes (3.8, layer "balance": chest -> stance foot per dancer and the counterbalance's couple
/// axis, lighting up when the weight is over the foot; BalanceAxisOverlay, from floorcraft.json balance),
/// the dance graph with its path / fingerprint modes (3.10/3.11), the move caption (3.12, layer "moves": on by default,
/// a placeholder timeline only in directed playback; the tour forces it on and adds the graph inset outside the graph
/// states) and the tour director (6).
/// Created automatically next to HeadMovement; rebuilds its layers whenever HeadMovement loads a capture and
/// follows the frame HeadMovement shows (frame time on the audio clock), so overlays and poses never disagree.
/// HeadMovement routes hm_layer counterbalance|traces|neck|graph here and appends State() to hm_state.
/// </summary>
public class DanceLayers : MonoBehaviour
{
    public static DanceLayers Instance { get; private set; }

    // user layer flags (hm_layer); view states override them while active
    static bool counterbalanceOn = true, tracesOn = true, graphOn, neckOn = true, axisOn = true, floorcraftOn = true, balanceOn = true, movesOn = true;
    public bool? CounterbalanceOverride, TracesOverride, NeckOverride, AxisOverride, FloorcraftOverride;
    public DanceGraphLayer.Mode? GraphOverride;
    public bool? MovesOverride, InsetOverride; // the tour: move caption throughout, graph inset outside the graph states
    public bool ShowAllPivots;

    HeadMovement hm;
    CaptureManifest loaded;
    Dancer loadedLead;
    Transform root;
    CounterbalanceOverlay counterbalance;
    FollowerTraces traces;
    NeckAxisOverlay neckAxis;
    FloorCraftOverlay floorCraft;
    BalanceAxisOverlay balance;
    DanceGraphLayer graph;
    DanceHud hud;
    DanceTour tour;
    readonly List<string> warnings = new();

    public CounterbalanceOverlay Counterbalance => counterbalance;
    public FollowerTraces Traces => traces;
    public NeckAxisOverlay NeckAxis => neckAxis;
    public FloorCraftOverlay FloorCraft => floorCraft;
    public BalanceAxisOverlay Balance => balance;
    public DanceGraphLayer Graph => graph;
    public DanceHud Hud => hud;
    public DanceTour Tour => tour;
    public HeadMovement Head => hm;
    public float MeasureSeconds { get; private set; } = 3.14f;
    public Rect DanceArea { get; private set; } // world xz bounds of both dancers' feet over the take
    public float FrameTime { get; private set; } = float.NaN;
    /// <summary>the audio-clock time the layers were last given: the exact sub-frame time while a director drives the
    /// clock (FrameTime = the nearer pose frame's time otherwise)</summary>
    public float SubFrameTime { get; private set; } = float.NaN;
    public int Frame { get; private set; } = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        Ensure();
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Ensure();

    public static DanceLayers Ensure()
    {
        if (Instance != null) return Instance;
        Instance = Object.FindAnyObjectByType<DanceLayers>(); // statics are lost on a script reload, the object is not
        if (Instance != null) return Instance;
        HeadMovement head = HeadMovement.Instance != null ? HeadMovement.Instance : Object.FindAnyObjectByType<HeadMovement>();
        if (head == null) return null;
        GameObject go = new("Dance Layers");
        Instance = go.AddComponent<DanceLayers>();
        Instance.hm = head;
        Instance.tour = go.AddComponent<DanceTour>();
        Instance.hud = DanceHud.Create(go.transform);
        return Instance;
    }

    void OnEnable()
    {
        if (Instance == null) Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ---------------------------------------------------------------- layer switches (hm_layer)

    public static bool Handles(string layer) => layer is "counterbalance" or "traces" or "graph" or "neck" or "axis" or "floorcraft" or "balance" or "moves";

    /// <summary>hm_layer counterbalance | traces | neck | graph | axis | floorcraft | balance (balance: physics mode only)</summary>
    public static void SetLayer(string layer, bool visible)
    {
        switch (layer)
        {
            case "counterbalance": counterbalanceOn = visible; break;
            case "traces": tracesOn = visible; break;
            case "neck": neckOn = visible; break;
            case "graph": graphOn = visible; break;
            case "axis": axisOn = visible; break;
            case "floorcraft": floorcraftOn = visible; break;
            case "balance": balanceOn = visible; break;
            case "moves": movesOn = visible; break;
            default: throw new ArgumentException($"unknown dance layer '{layer}'");
        }

        DanceLayers layers = Ensure();
        if (layers != null) layers.ApplyVisibility();
    }

    public static bool LayerOn(string layer) => layer switch
    {
        "counterbalance" => counterbalanceOn,
        "traces" => tracesOn,
        "neck" => neckOn,
        "graph" => graphOn,
        "axis" => axisOn,
        "floorcraft" => floorcraftOn,
        "balance" => balanceOn,
        "moves" => movesOn,
        _ => false
    };

    public void ApplyVisibility()
    {
        if (counterbalance != null)
        {
            counterbalance.SetVisible(CounterbalanceOverride ?? counterbalanceOn);
            counterbalance.SetShowAllPivots(ShowAllPivots);
        }

        if (traces != null) traces.SetVisible(TracesOverride ?? tracesOn);
        if (neckAxis != null) neckAxis.SetVisible(NeckOverride ?? neckOn);
        if (floorCraft != null) floorCraft.SetVisible(AxisOverride ?? axisOn, FloorcraftOverride ?? floorcraftOn);
        if (balance != null) balance.SetVisible(balanceOn && hm != null && hm.SkeletonMode == SkeletonStyle.Mode.Physics);
        if (graph != null)
        {
            DanceGraphLayer.Mode want = GraphOverride ?? (graphOn ? DanceGraphLayer.Mode.Static : DanceGraphLayer.Mode.Off);
            if (graph.Current != want) graph.SetMode(want);
            // the move caption (VIEWER_SPEC 3.12): layer "moves", forced on in directed playback; a placeholder timeline
            // is not captioned in the free view
            bool placeholder = string.Equals(graph.Data.LabelsProvenance, "placeholder", StringComparison.OrdinalIgnoreCase);
            graph.SetCaptionVisible(MovesOverride ?? (movesOn && !placeholder));
            graph.SetInsetVisible(InsetOverride ?? false);
        }
        else if (hud != null)
        {
            hud.Clear();
        }
    }

    // ---------------------------------------------------------------- per frame

    void LateUpdate()
    {
        if (hm == null)
        {
            hm = HeadMovement.Instance;
            if (hm == null) return;
        }

        Refresh();
    }

    /// <summary>bring every layer to the frame HeadMovement shows now (also called by hm_state before reporting)</summary>
    public void Refresh()
    {
        if (hm == null || hm.Manifest == null || hm.Timeline == null || hm.LeadDancer == null || hm.FollowDancer == null) return;
        if (!ReferenceEquals(hm.Manifest, loaded) || !ReferenceEquals(hm.LeadDancer, loadedLead)) Rebuild();
        int frame = hm.CurrentFrame;
        if (frame < 0) return;
        Frame = frame;
        FrameTime = hm.Timeline.AudioTimeOf(frame);
        // directed playback (the film's slow motion): the poses blend between frames, so the overlays that sample their
        // data by time (the couple COM dot, the floor-craft dials) and the balance axes get the exact sub-frame time
        float exact = FrameTime;
        int other = -1;
        float otherWeight = 0f;
        if (hm.ExternallyDriven)
        {
            double[] ts = hm.Timeline.AudioTimes;
            if (ts.Length > 1 && frame < ts.Length)
            {
                double te = Math.Min(Math.Max(hm.ExternalAudioTime, ts[0]), ts[ts.Length - 1]);
                exact = (float)te;
                int o = te >= ts[frame] ? frame + 1 : frame - 1;
                if (o >= 0 && o < ts.Length && ts[o] != ts[frame])
                {
                    other = o;
                    otherWeight = (float)Math.Min(0.5, Math.Abs(te - ts[frame]) / Math.Abs(ts[o] - ts[frame]));
                }
            }
        }

        SubFrameTime = exact;
        Camera cam = DanceText.ViewCamera;
        if (counterbalance != null) counterbalance.SetTime(exact, frame, cam);
        if (traces != null)
        {
            CounterbalanceData.Interval active = counterbalance != null ? counterbalance.Active : null;
            if (!traces.EmphasisMatches(active)) traces.SetEmphasis(active);
            traces.SetFrame(frame, other, otherWeight);
        }

        if (neckAxis != null) neckAxis.SetFrame(frame, cam);
        bool physics = hm.SkeletonMode == SkeletonStyle.Mode.Physics; // the physics layer / view state (P key)
        if (floorCraft != null)
        {
            floorCraft.SetPhysicsMode(physics);
            floorCraft.SetTime(exact, frame, cam);
        }

        if (balance != null)
        {
            balance.SetVisible(balanceOn && physics);
            balance.SetTime(exact, frame, cam, other, otherWeight);
        }

        if (graph != null)
        {
            graph.SetDebugHud(hm.LayerVisible("hud")); // the caption moves clear of HeadMovement's debug HUD
            graph.SetTime(FrameTime, frame);
        }
    }

    void Rebuild()
    {
        loaded = hm.Manifest;
        loadedLead = hm.LeadDancer;
        warnings.Clear();
        if (root != null) Destroy(root.gameObject);
        counterbalance = null;
        traces = null;
        neckAxis = null;
        floorCraft = null;
        balance = null;
        graph = null;
        hud.Clear();
        if (loaded == null || hm.LeadDancer == null || hm.FollowDancer == null || hm.Timeline == null) return;

        root = new GameObject($"Dance layers ({loaded.DisplayName})").transform;
        root.SetParent(transform, false);
        MeasureSeconds = Measure(hm.Beats);
        DanceArea = Area(hm.LeadDancer, hm.FollowDancer);
        JObject capture = ReadCaptureJson(loaded);

        string cbPath = FileFromCapture(capture, "counterbalance", "counterbalance.json");
        if (cbPath != null)
        {
            try
            {
                CounterbalanceData data = CounterbalanceData.Load(cbPath, hm.Timeline, DanceOrigin.Offset);
                counterbalance = new GameObject("Counterbalance").AddComponent<CounterbalanceOverlay>();
                counterbalance.transform.SetParent(root, false);
                counterbalance.Init(data, hm.LeadDancer, hm.FollowDancer, MeasureSeconds);
            }
            catch (Exception e)
            {
                Warn($"counterbalance.json unreadable ({e.Message}) - counterbalance layer disabled");
            }
        }

        try
        {
            traces = new GameObject("Follower traces").AddComponent<FollowerTraces>();
            traces.transform.SetParent(root, false);
            traces.Init(hm.FollowDancer, hm.LeadDancer, loaded, hm.Timeline, MeasureSeconds, counterbalance?.Data); // counterbalance: her free leg
        }
        catch (Exception e)
        {
            Warn($"follower traces failed ({e.Message})");
        }

        try
        {
            neckAxis = new GameObject("Follower neck axis").AddComponent<NeckAxisOverlay>();
            neckAxis.transform.SetParent(root, false);
            SmplxData.Motion motion = hm.Avatars.TryGetValue(Role.Follow, out SmplxAvatar fa) && fa != null ? fa.Motion : null;
            string motionPath = motion == null ? loaded.RolePath(loaded.smplx, Role.Follow) : null;
            if (motionPath != null && File.Exists(motionPath)) motion = SmplxData.ReadMotion(motionPath);
            neckAxis.Init(hm.FollowDancer, motion, hm.Timeline);
            if (traces != null) traces.SetHeadGate(neckAxis.Gate()); // the head trail shows with the neck axis only
        }
        catch (Exception e)
        {
            Warn($"follower neck axis failed ({e.Message})");
        }

        try
        {
            string fcPath = FileFromCapture(capture, "floorcraft", "floorcraft.json");
            FloorCraftData fc = null;
            if (fcPath != null)
            {
                try
                {
                    fc = FloorCraftData.Load(fcPath, hm.Timeline, DanceOrigin.Offset);
                }
                catch (Exception e)
                {
                    Warn($"floorcraft.json unreadable ({e.Message}) - live leader axis and counterbalance dials only");
                }
            }

            floorCraft = new GameObject("Floor craft").AddComponent<FloorCraftOverlay>();
            floorCraft.transform.SetParent(root, false);
            floorCraft.Init(fc, counterbalance?.Data, hm.LeadDancer, hm.Timeline, MeasureSeconds);
            if (counterbalance != null) counterbalance.DrawPivots = false; // the floor record draws them (as dials)
            if (fc?.Balance != null)
            {
                balance = new GameObject("Balance axes").AddComponent<BalanceAxisOverlay>();
                balance.transform.SetParent(root, false);
                balance.Init(fc.Balance, hm.LeadDancer, hm.FollowDancer);
            }
        }
        catch (Exception e)
        {
            Warn($"floor craft failed ({e.Message})");
        }

        string movesDir = FolderFromCapture(capture, "moves", "moves");
        if (movesDir != null && File.Exists(Path.Combine(movesDir, "graph.json")))
        {
            try
            {
                DanceGraphData data = DanceGraphData.Load(movesDir, hm.Timeline);
                graph = new GameObject("Dance graph layer").AddComponent<DanceGraphLayer>();
                graph.transform.SetParent(root, false);
                graph.Init(data, hm.LeadDancer, hm.FollowDancer, loaded, hm.Timeline, hm.Beats, hud);
                foreach (string w in data.Warnings) Warn($"moves: {w}");
            }
            catch (Exception e)
            {
                Warn($"moves/graph.json unreadable ({e.Message}) - graph layer disabled");
                if (graph != null) Destroy(graph.gameObject);
                graph = null;
            }
        }

        ApplyVisibility();
        tour.OnCaptureLoaded();
    }

    void Warn(string message)
    {
        warnings.Add(message);
        Debug.LogWarning($"{loaded?.DisplayName}: {message}");
    }

    static JObject ReadCaptureJson(CaptureManifest manifest)
    {
        string path = Path.Combine(manifest.Folder, "capture.json");
        try
        {
            return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    string FileFromCapture(JObject capture, string key, string fallback)
    {
        string rel = capture?[key]?.Type == JTokenType.String ? capture.Value<string>(key) : fallback;
        string path = Path.Combine(loaded.Folder, rel);
        return File.Exists(path) ? path : null;
    }

    string FolderFromCapture(JObject capture, string key, string fallback)
    {
        string rel = capture?[key]?.Type == JTokenType.String ? capture.Value<string>(key) : fallback;
        string path = Path.Combine(loaded.Folder, rel);
        return Directory.Exists(path) ? path : null;
    }

    static float Measure(BeatGrid beats)
    {
        if (beats == null || beats.MeasureCount < 2) return 3.14f;
        int n = Mathf.Min(beats.MeasureCount - 1, 64);
        return (beats.MeasureStart(n) - beats.MeasureStart(0)) / n;
    }

    static Rect Area(Dancer lead, Dancer follow)
    {
        float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
        int n = Mathf.Min(lead.FrameCount, follow.FrameCount);
        for (int f = 0; f < n; f += 2)
        {
            foreach (Dancer d in new[] { lead, follow })
            {
                foreach (SmplJoint j in new[] { SmplJoint.L_Ankle, SmplJoint.R_Ankle })
                {
                    Vector3 p = d.Joint(f, j);
                    if (float.IsNaN(p.x)) continue;
                    x0 = Mathf.Min(x0, p.x);
                    x1 = Mathf.Max(x1, p.x);
                    z0 = Mathf.Min(z0, p.z);
                    z1 = Mathf.Max(z1, p.z);
                }
            }
        }

        return x0 > x1 ? new Rect(-1, -1, 2, 2) : Rect.MinMaxRect(x0, z0, x1, z1);
    }

    // ---------------------------------------------------------------- hm_state

    /// <summary>called from HeadMovement.State(): origin, layer flags, counterbalance, traces, graph, tour</summary>
    public static void AppendState(Dictionary<string, object> state)
    {
        DanceLayers layers = Ensure();
        if (state.TryGetValue("layers", out object l) && l is Dictionary<string, bool> flags)
        {
            flags["counterbalance"] = counterbalanceOn;
            flags["traces"] = tracesOn;
            flags["neck"] = neckOn;
            flags["graph"] = graphOn;
            flags["axis"] = axisOn;
            flags["floorcraft"] = floorcraftOn;
            flags["balance"] = balanceOn;
            flags["moves"] = movesOn;
        }

        HeadMovement head = HeadMovement.Instance;
        state["origin"] = DanceOrigin.State(head?.LeadDancer, head?.FollowDancer, head != null ? head.CurrentFrame : -1);
        if (layers == null) return;
        layers.Refresh();
        state["counterbalance"] = layers.counterbalance != null
            ? layers.counterbalance.State()
            : new Dictionary<string, object> { ["loaded"] = false, ["visible"] = counterbalanceOn };
        state["traces"] = layers.traces != null && layers.traces.FrameCount > 0 ? layers.traces.State() : null;
        state["neckAxis"] = layers.neckAxis != null && layers.neckAxis.FrameCount > 0 ? layers.neckAxis.State() : null;
        state["floorCraft"] = layers.floorCraft != null ? layers.floorCraft.State() : null;
        state["balanceAxes"] = layers.balance != null ? layers.balance.State() : null;
        state["graph"] = layers.graph != null
            ? layers.graph.State()
            : new Dictionary<string, object> { ["loaded"] = false, ["mode"] = "off" };
        state["tour"] = layers.tour.State();
        if (layers.warnings.Count > 0) state["danceLayerWarnings"] = new List<string>(layers.warnings);
        // the move being danced now (the move caption's span: move | uncertain | unlabelled) - the live move ribbon
        if (layers.graph != null && !float.IsNaN(layers.FrameTime))
        {
            state["move"] = layers.graph.CaptionAt(layers.FrameTime);
        }
    }
}
