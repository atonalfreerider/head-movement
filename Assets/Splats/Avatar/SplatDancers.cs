#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Gaussians.ThreeD;
using HmSplats;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// The splat dancers. Owner rule (2026-10-23): "I don't ever want to see the basic avatars if gauss splat avatars are available for a
/// dance. fully archive them when they are replaced with gauss splats." A dancer whose capture has <c>splats/&lt;role&gt;.json</c> +
/// <c>.splatav</c> (dancecap SPLAT_AVATAR_FORMAT.md; LHM++ skinned Gaussians) is ALWAYS drawn as splats - the Editor (every view, Play
/// mode, the film recorder and director), a Windows player (desktop and VR) and the Quest - and its mesh avatar (body, shoes, head
/// props, hair cards and tip glows: every Renderer under the avatar) is never drawn (Renderer.forceRenderingOff from the capture load
/// on, also while the splats load). The avatar stays as an invisible rig: a SkinnedSplatAvatar is posed every frame from the avatar's
/// own bones (so playback, seeks, slow motion and the film director's sub-frame poses all drive it), the hair simulation and physics
/// ride on them too. A device that cannot draw splats (D3D11, GLES, no compute, the players' 3-s rendering watchdog) shows the
/// dancers' skeleton lines instead - never the mesh avatar - and one log line + an on-screen note (SplatDancersDefault.Note). A dancer
/// without splat data keeps its mesh avatar. Nothing switches a splat capture back to its mesh avatars (hm_splatdancers --action off
/// is refused; a player's "splats": false is ignored): SplatDancersDefault, docs/SPLAT_DANCERS.md "Splats always". Both dancers sit in
/// one GaussiansGroup: one global sort.
///
/// Display (VIEWER_SPEC 3.2): OPAQUE. The splats show at full opacity whatever the avatars' opacity says (only 0 =
/// hidden is honoured), they are hidden with the avatars layer, inside role-hidden spans and when the avatar is not
/// drawn. The 65 % / 75 % transparent look of the mesh avatars needs the renderer's Composite Texture path with a
/// composite that scales the accumulated alpha (and, for "transparent as displayed", a per-pixel alpha from the
/// composited luminance like DisplayTransparency's AlphaLut); OpacityMode.ScaleSplats is a crude review stand-in
/// (scales every Gaussian's alpha, so the inside and the back of the body show through).
///
/// The renderer's URP feature is added to the URP renderer at run time only while splat dancers are drawn
/// (SplatUrpFeature); nothing in the project's render or XR settings changes. Editor, Windows and Android (the Quest).
/// A player (the Quest, a Windows player for PC VR over Meta Quest Link: TierPlayer) applies its tier at each capture load (data:
/// quest_settings.json "splat_max", "splat_max_sigma", "splat_path", "splat_depth", "splat_set" ...; the -hmsplat* switches,
/// VrSettings); on the Quest the Gaussians come from the distilled set / are thinned at load (QuestDefaultSet, QuestDefaultMax /
/// QuestDefaultSigma: SplatAvatarData.Reduce). Its "splats" key / -hmsplats switch no longer turns a capture with splat dancers back
/// to its mesh avatars (ignored, logged). GLES (no compute path in the renderer) and D3D11 (start the exe / editor with -force-d3d12)
/// show the skeleton lines. Hair: when the splat file carries a hair binding
/// (dancecap mono4d/recap_splat_hair.py: the free hair Gaussians bound to the HairStrands guides) the dancer's simulated
/// HairStrands (the same simulation that moves the hair cards, which are hidden with the rest of the avatar) drive the
/// bound Gaussians every frame (HairStrandsSplatSource, SkinnedSplatAvatar's SkinHair pass); scalp-level hair and an
/// unbound file keep LHM++'s rigid skinning. HairMotion = false shows the rigid LHM++ hair (A/B).
/// CLI: unity command hm_splatdancers (Assets/Splats/Avatar/Editor/SplatDancersCli.cs).
/// </summary>
[DefaultExecutionOrder(31000)]
public sealed class SplatDancers : MonoBehaviour
{
    /// <summary>Opaque (default): the splats at full opacity. FollowAvatars: the whole splat image faded uniformly to the
    /// avatars' current opacity (the composite's _HmSplatOpacity; one layer, no inside / back of the bodies; needs the
    /// Composite path). ScaleSplats: every Gaussian's alpha scaled (crude review stand-in: the inside shows).</summary>
    public enum OpacityModes { Opaque, FollowAvatars, ScaleSplats }

    /// <summary>Composite (default): splats accumulated off-screen and composited before the transparent queue, then their
    /// approximate depth written (SplatUrpFeature.DepthBeforeTransparents), so floor marks, arrows and labels depth-test
    /// against the dancers. Direct: the package's default Direct Transparent path (splats in the transparent queue, no
    /// depth for the overlays: floor rings draw over the feet).</summary>
    public enum Paths { Composite, Direct }

    /// <summary>The viewer's skeleton lines with the opaque splat bodies (SplatOverlayFeature draws them over the splats without a
    /// depth test, as they show through the translucent mesh avatars and over the phones' video). Physics (default): in the
    /// physics view (skeletons coloured by the estimated load, the legend) the lines, the follower's spine beads, the balance
    /// axes and the centre-of-mass spheres are drawn over the splats; in every other view the lines of a dancer whose splats
    /// show are not drawn at all, so the photoreal bodies stay clean (no piece of a line poking out of the shirt); on the
    /// phones' video, where the splats are hidden, the lines are as before. In both, the hand-contact lights are drawn over
    /// the splats, and seen from above the floor craft (the leader's T) is too. Always: everything over the splats in every
    /// view. Never: the depth-correct overlays only (hidden inside the bodies; a piece of a line pokes out where it reaches
    /// the surface: the first splat recordings).</summary>
    public enum SkeletonOverModes { Always, Physics, Never }

    public const string CompositeShaderPath = "Assets/Splats/Avatar/Core/Shaders/HM_SplatComposite.shader";
    static readonly int OpacityId = Shader.PropertyToID("_HmSplatOpacity");
    static Paths renderPath = Paths.Composite;

    public static Paths RenderPath
    {
        get => renderPath;
        set => renderPath = value;
    }

    /// <summary>the opacity the composite applies now (1 = opaque)</summary>
    public static float DisplayOpacity { get; private set; } = 1f;

    static bool splatsOn;
    static OpacityModes opacityMode = OpacityModes.Opaque;
    static SkinnedSplatAvatar.HairModes hairMode = SkinnedSplatAvatar.HairModes.Skinned;
    static bool hairMotion = true;

    /// <summary>bound hair Gaussians follow the simulated strands (default) or keep their LBS (rigid LHM++ hair); in the
    /// Editor it survives Play-mode restarts like the toggle (so hm_film record can record the rigid A/B)</summary>
    public static bool HairMotion
    {
        get => hairMotion;
        set
        {
            hairMotion = value;
#if UNITY_EDITOR
            UnityEditor.SessionState.SetBool(HairMotionKey, value);
#endif
        }
    }

    const string HairMotionKey = "HM.SplatDancers.HairMotion";

    static SkeletonOverModes skeletonOver = SkeletonOverModes.Physics;
    const string SkeletonOverKey = "HM.SplatDancers.SkeletonOver";

    /// <summary>the skeleton lines / hand lights over the splats (see SkeletonOverModes); in the Editor it survives Play-mode
    /// restarts like the toggle (so hm_film record records the chosen mode)</summary>
    public static SkeletonOverModes SkeletonOver
    {
        get => skeletonOver;
        set
        {
            skeletonOver = value;
#if UNITY_EDITOR
            UnityEditor.SessionState.SetInt(SkeletonOverKey, (int)value);
#endif
        }
    }

    public static SplatDancers Instance { get; private set; }

    /// <summary>the splat dancers are drawn for the loaded capture: it has splat data and the device can draw it
    /// (SplatDancersDefault.Decide; false for a capture without splats/ and on a device without a splat path)</summary>
    public static bool Enabled => splatsOn;

    /// <summary>why the splat dancers are on / off at the last capture load (SplatDancersDefault.Decision.Why; the CLI state)</summary>
    public static string DefaultWhy { get; private set; } = "";

    /// <summary>"" while the loaded capture's dancers are drawn as they should be; else the note shown on screen and logged once:
    /// the capture has splat dancers this device cannot draw (SplatDancersDefault.Note: "splat dancers need D3D12: start the editor with
    /// -force-d3d12 - skeleton lines shown ..."), or the players' watchdog gave up, or a splat avatar failed to build</summary>
    public static string CannotDrawNote { get; private set; } = "";

    /// <summary>the reason the device cannot draw splats at the last capture load (SplatDancersDefault.NoPath; "" = it can)</summary>
    static string noPathNow = "";

    // SessionState keys of the hand toggle of before 2026-10-23 (hm_splatdancers --action on | off): erased at Play-mode start, never read
    const string HandKey = "HM.SplatDancers.Explicit";
    static string refusedLogged;

    public static OpacityModes OpacityMode
    {
        get => opacityMode;
        set => opacityMode = value;
    }

    public static SkinnedSplatAvatar.HairModes HairMode
    {
        get => hairMode;
        set => hairMode = value;
    }

    sealed class Entry
    {
        public Role Role;
        public SmplxAvatar Avatar;
        public string JsonPath;
        public Task<SplatAvatarData> Loading;
        public SkinnedSplatAvatar Splat;
        public string Error;
        public readonly List<Renderer> Hidden = new();
        public float NextHairScan;
        public double LoadSeconds;
        public volatile string Reduction;
    }

    readonly List<Entry> entries = new();
    GameObject root;
    GaussiansGroup group;
    HeadMovement hm;
    string capture;
    ComputeShader compute;
    readonly System.Diagnostics.Stopwatch loadWatch = new();

    /// <summary>the SessionState key of the hand toggle of before 2026-10-23 (erased at Play-mode start, never read: an "off" an editor
    /// session still held must not bring the mesh avatars back)</summary>
    const string SessionKey = "HM.SplatDancers.On";

    /// <summary>a player build for the Quest (Android): splats on by default, thinned to the Quest budget</summary>
    public static readonly bool QuestPlayer =
#if UNITY_ANDROID && !UNITY_EDITOR
        true;
#else
        false;
#endif

    /// <summary>a Windows player (HeadMovement.exe: PC VR over Meta Quest Link / Air Link, or the desktop viewer). Its splat tier is
    /// data like the Quest's (quest_settings.json in HmData.Root, the -hmsplat* switches), applied at each capture load; a capture with
    /// splat data is drawn with its splat dancers on the desktop and in VR. D3D11 (the automatic choice on a 4-thread PC: start the exe
    /// with -force-d3d12) and GLES have no path in the renderer: the skeleton lines are shown.</summary>
    public static readonly bool PcPlayer =
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        true;
#else
        false;
#endif

    /// <summary>a player whose splat tier is applied at each capture load (VrSettings): the Quest and a Windows player</summary>
    public static bool TierPlayer => QuestPlayer || PcPlayer;

    /// <summary>the Quest's Gaussians per dancer (load-time thinning, SplatAvatarData.Reduce; 0 = all), measured on a Quest 3
    /// (docs/SPLAT_DANCERS.md "Quest"); quest_settings.json "splat_max" / -hmsplatmax override it</summary>
    public const int QuestDefaultMax = 0;

    /// <summary>the Quest's clamp of every Gaussian's standard deviation in metres (0 = none); "splat_max_sigma" / -hmsplatsigma</summary>
    public const float QuestDefaultSigma = 0f;

    /// <summary>the Quest's render path and splat depth (see Paths; "splat_path" / "splat_depth")</summary>
    public const Paths QuestDefaultPath = Paths.Composite;
    public const bool QuestDefaultDepth = true;

    /// <summary>the Quest's splat file set (splats/&lt;set&gt;/&lt;role&gt;.json, dancecap mono4d/quest_distill.py; "" = splats/&lt;role&gt;.json),
    /// its splat layer resolution (SplatUrpFeature.LowResScale: 1 = the package's full-res composite), alpha cutoff (0 = the package's
    /// 1/255) and sort cadence (sort every N frames); "splat_set", "splat_scale", "splat_alpha_cutoff", "splat_sort_every" override them.
    /// A Windows player keeps the full files and the package's rendering unless its settings name them (docs/SPLAT_DANCERS.md "Quest").</summary>
    public const string QuestDefaultSet = "quest_25k";
    public const float QuestDefaultScale = 1f;
    public const float QuestDefaultAlphaCutoff = 0f;
    public const int QuestDefaultSortEvery = 1;

    /// <summary>what the Quest tier applied at the last capture load (State, logcat)</summary>
    public static string QuestTier { get; private set; } = "";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
#if UNITY_EDITOR
        // the hand toggle of before 2026-10-23: whatever this editor session still holds ("off" included) must not matter any more
        UnityEditor.SessionState.EraseBool(HandKey);
        UnityEditor.SessionState.EraseBool(SessionKey);
#endif
        splatsOn = false; // decided at every capture load (SplatDancersDefault.Decide: the capture's splat data + the device)
        DefaultWhy = "";
        CannotDrawNote = "";
        noPathNow = "";
        refusedLogged = null;
        reduceMax = 0;
        reduceSigma = 0f;
        splatSet = "";
        alphaCutoff = 0f;
        sortEvery = 1;
        SplatUrpFeature.LowResScale = 1f;
        SplatUrpFeature.LowResDepth = true;
        opacityMode = OpacityModes.Opaque;
        renderPath = Paths.Composite;
        DisplayOpacity = 1f;
        hairMode = SkinnedSplatAvatar.HairModes.Skinned;
#if UNITY_EDITOR
        hairMotion = UnityEditor.SessionState.GetBool(HairMotionKey, true);
        skeletonOver = (SkeletonOverModes)Mathf.Clamp(UnityEditor.SessionState.GetInt(SkeletonOverKey, (int)SkeletonOverModes.Physics), 0, 2);
#else
        hairMotion = true;
        skeletonOver = SkeletonOverModes.Physics;
#endif
        Instance = null;
        HeadMovement.AvatarsLoaded -= OnAvatarsLoaded;
        HeadMovement.AvatarsLoaded += OnAvatarsLoaded;
        // 0 while the splat dancers are off or hidden: the overlays look exactly as with the mesh avatars
        FilmUi.OpaqueBodies = OpaqueBodiesNow;
    }

    static void OnAvatarsLoaded(HeadMovement viewer)
    {
        bool wasOn = splatsOn;
        if (TierPlayer) ApplyPlayerTier(viewer);
        else ApplyEditorDefault(viewer);
        // a capture with splat data needs the instance even when the device cannot draw the splats: it keeps the mesh avatars hidden
        // (the skeleton lines show instead)
        if (CaptureHasSplats(viewer.Manifest)) Ensure();
        if (Instance != null) Instance.Rebuild(viewer);
        if (!TierPlayer)
        {
            if (splatsOn) SplatUrpFeature.Ensure();
            else if (wasOn) TurnedOff(); // the previous capture's default had them on, this capture's is off
        }
        else if (!splatsOn || Instance == null || !Instance.HasSplatData) SplatUrpFeature.Remove(); // a player lesson without splat data renders as before
        // (in a player the Gaussians feature is added when the first splat dancer is built: Build)
    }

    /// <summary>the capture has a splat avatar for at least one dancer: splats/&lt;role&gt;.json, or any named set's
    /// splats/&lt;set&gt;/&lt;role&gt;.json (CaptureManifest.HasSplatAvatar: the same test the avatar loading uses)</summary>
    static bool CaptureHasSplats(CaptureManifest m) => m != null && (m.HasSplatAvatar(Role.Lead) || m.HasSplatAvatar(Role.Follow));

    /// <summary>the Editor at each capture load (SplatDancersDefault.Decide): the splat dancers are drawn for every capture with splat
    /// data when the API can draw them; a capture with splat data on D3D11 / GLES / no compute shows the skeleton lines, with one log
    /// line and the on-screen note</summary>
    static void ApplyEditorDefault(HeadMovement viewer)
    {
        CaptureManifest m = viewer.Manifest;
        noPathNow = SplatDancersDefault.NoPath(SystemInfo.graphicsDeviceType, SystemInfo.supportsComputeShaders, SplatDancersDefault.Host.Editor);
        SplatDancersDefault.Decision d = SplatDancersDefault.Decide(new SplatDancersDefault.Inputs
        {
            Host = SplatDancersDefault.Host.Editor,
            CaptureFlag = m != null && m.SplatDancersDefaultOn,
            HasSplatData = CaptureHasSplats(m),
            NoPath = noPathNow
        });
        splatsOn = d.On;
        DefaultWhy = d.Why;
        CannotDrawNote = d.Refused ? SplatDancersDefault.Note(noPathNow, SplatDancersDefault.Host.Editor) : "";
        string name = Path.GetFileName(m?.Folder ?? "?");
        if (d.Refused)
        {
            if (refusedLogged != m?.Folder) Debug.LogWarning($"splat dancers: {CannotDrawNote} (capture {name}; {d.Why})");
            refusedLogged = m?.Folder;
        }
        else if (d.On) Debug.Log($"splat dancers: on ({d.Why}; {SystemInfo.graphicsDeviceType}; capture {name})");
    }

    /// <summary>thinning for the next capture load / rebuild (SplatAvatarData.Reduce): at most max Gaussians per dancer (0 = all), every
    /// standard deviation clamped to sigma metres (0 = none). A player sets it from VrSettings at each capture load; in the Editor it is
    /// off unless set here (hm_splatdancers --action thin: measure the Quest / PC VR reductions on the desktop) and resets with Play mode.</summary>
    public static void SetReduction(int max, float sigma)
    {
        reduceMax = Math.Max(0, max);
        reduceSigma = Math.Max(0f, sigma);
        if (Instance != null && HeadMovement.Instance != null && splatsOn) Instance.Rebuild(HeadMovement.Instance);
    }

    public static int ReduceMax => reduceMax;
    public static float ReduceSigma => reduceSigma;

    /// <summary>
    /// The hair simulation of <paramref name="hair"/> drives a splat dancer's bound hair Gaussians in this lesson (the follower's
    /// hair on the recap): it must keep running whatever the headset's hair level says (VrQuality, VrPerf's adaptive step) - a
    /// disabled HairStrands stops simulating, the bound hair falls back to the rigid LHM++ skinning ("source not ready (LBS)") and
    /// stops swinging. The hair cards themselves are hidden under the splats, so the hair level changes nothing that is drawn.
    /// False while the splats are off, fell back to the mesh avatars, show the rigid hair (HairMotion off) or carry no binding;
    /// null = any hair (the lesson has bound splat hair at all).
    /// </summary>
    public static bool DrivesSplatHair(HairStrands hair = null)
    {
        SplatDancers s = Instance;
        if (!splatsOn || s == null || s.fellBack || !hairMotion || hairMode == SkinnedSplatAvatar.HairModes.Hidden) return false;
        foreach (Entry e in s.entries)
        {
            if (e.Splat == null || e.Splat.HairBinding == null || e.Splat.HairBinding.Bound <= 0) continue;
            if (hair == null || hair.Avatar == e.Avatar) return true;
        }

        return false;
    }

    /// <summary>the bound splat hair follows the simulation this frame (any dancer): "live" / the renderer's hair status / "-" (no bound hair)</summary>
    public static string SplatHairState()
    {
        SplatDancers s = Instance;
        if (s == null) return "-";
        foreach (Entry e in s.entries)
        {
            if (e.Splat == null || e.Splat.HairBinding == null) continue;
            return e.Splat.HairLive ? "live" : e.Splat.HairStatus;
        }

        return "-";
    }

    /// <summary>the capture being loaded has a splat avatar for at least one dancer</summary>
    public bool HasSplatData
    {
        get
        {
            foreach (Entry e in entries)
            {
                if (e.JsonPath != null) return true;
            }

            return false;
        }
    }

    static int reduceMax;
    static float reduceSigma;
    static bool questDepth = true;
    static string splatSet = "";
    static float alphaCutoff;
    static int sortEvery = 1;

    /// <summary>the file set, alpha cutoff and sort cadence the next build / every frame uses (a player: VrSettings at each capture
    /// load; the Editor: hm_splatdancers --action quest, to measure the Quest knobs on the desktop; Play mode resets them)</summary>
    public static void SetQuestKnobs(string set, float scale, float cutoff, int sortN)
    {
        bool setChanged = !string.Equals(splatSet, set ?? "", StringComparison.OrdinalIgnoreCase);
        splatSet = set ?? "";
        alphaCutoff = Math.Max(0f, cutoff);
        sortEvery = Math.Max(1, sortN);
        float s = scale > 0f ? Mathf.Clamp(scale, 0.2f, 1f) : 1f;
        bool modeChanged = (s < 0.999f) != (SplatUrpFeature.LowResScale < 0.999f);
        SplatUrpFeature.LowResScale = s;
        // Ensure swaps the package feature and the low-res layer in place (no Remove: destroying the feature the renderer still
        // holds in the same frame is avoided); the dancers are rebuilt only when the file set changes
        if (splatsOn && modeChanged) SplatUrpFeature.Ensure();
        if (setChanged && Instance != null && HeadMovement.Instance != null && splatsOn) Instance.Rebuild(HeadMovement.Instance);
    }

    public static string SplatSet => splatSet;
    public static float AlphaCutoff => alphaCutoff;
    public static int SortEvery => sortEvery;

    /// <summary>the player tier at each capture load (VrSettings: quest_settings.json + the command line): thinning, file set, render
    /// path, splat depth; logged as HM-SPLATS (logcat on the Quest, Player.log on Windows). The splat dancers are on for every capture
    /// with splat data the device can draw ("splats": false / -hmsplats off no longer brings the mesh avatars back: ignored, logged);
    /// without a drawing path (GLES, D3D11) the skeleton lines are shown.</summary>
    static void ApplyPlayerTier(HeadMovement viewer)
    {
        HmVr.VrSettings st = HmVr.VrSettings.Current.Loaded ? HmVr.VrSettings.Current : HmVr.VrSettings.Load();
        bool vr = HmVr.VrMode.Active;
        UnityEngine.Rendering.GraphicsDeviceType api = SystemInfo.graphicsDeviceType;
        SplatDancersDefault.Host host = QuestPlayer ? SplatDancersDefault.Host.Quest : SplatDancersDefault.Host.WindowsPlayer;
        string reason = SplatDancersDefault.NoPath(api, SystemInfo.supportsComputeShaders, host);
        noPathNow = reason;
        string noPath = reason.Length > 0 ? $" ({reason})" : "";
        SplatDancersDefault.Decision d = SplatDancersDefault.Decide(new SplatDancersDefault.Inputs
        {
            Host = host,
            SettingsGiven = st.SplatsGiven,
            SettingsOn = st.Splats,
            Vr = vr,
            CaptureFlag = viewer.Manifest != null && viewer.Manifest.SplatDancersDefaultOn,
            HasSplatData = CaptureHasSplats(viewer.Manifest),
            NoPath = reason
        });
        bool on = d.On;
        splatsOn = on;
        DefaultWhy = d.Why;
        CannotDrawNote = d.Refused ? SplatDancersDefault.Note(reason, host) : "";
        if (d.Refused && refusedLogged != viewer.Manifest?.Folder)
        {
            Debug.LogWarning($"HM-SPLATS {CannotDrawNote} (capture {Path.GetFileName(viewer.Manifest?.Folder ?? "?")})");
            refusedLogged = viewer.Manifest?.Folder;
        }

        reduceMax = st.SplatMax > 0 ? st.SplatMax : st.SplatMax < 0 ? 0 : QuestDefaultMax;
        reduceSigma = st.SplatMaxSigma > 0f ? st.SplatMaxSigma : st.SplatMaxSigma < 0f ? 0f : QuestDefaultSigma;
        renderPath = st.SplatPath == "direct" ? Paths.Direct : st.SplatPath == "composite" ? Paths.Composite : QuestDefaultPath;
        questDepth = st.SplatDepth == "off" ? false : st.SplatDepth == "on" ? true : QuestDefaultDepth;
        SplatUrpFeature.LowResDepth = questDepth; // the low-res layer's own splat depth + depth-tested composite (SplatLowResFeature)
        // the splat depth BEFORE the transparents is for the composite path (overlays depth-test against the dancers); on the direct
        // path the splats are transparents themselves and would test against their own depth: the package's order (after) then
        SplatUrpFeature.DepthBeforeTransparents = renderPath == Paths.Composite;
        // the Quest-only compression knobs (a Windows player: only when its settings name them)
        splatSet = st.SplatSet.Length > 0 ? st.SplatSet : QuestPlayer ? QuestDefaultSet : "";
        float scale = st.SplatScale > 0f ? st.SplatScale : QuestPlayer ? QuestDefaultScale : 1f;
        SplatUrpFeature.LowResScale = renderPath == Paths.Composite ? Mathf.Clamp(scale, 0.2f, 1f) : 1f; // the low-res layer is a composite
        alphaCutoff = st.SplatAlphaCutoff > 0f ? st.SplatAlphaCutoff : QuestPlayer ? QuestDefaultAlphaCutoff : 0f;
        sortEvery = Math.Max(1, st.SplatSortEvery > 0 ? st.SplatSortEvery : QuestPlayer ? QuestDefaultSortEvery : 1);
        string who = QuestPlayer ? "quest" : vr ? "pc vr" : "pc desktop";
        string why = $" ({d.Why})";
        QuestTier = $"{who}: splats {(on ? "on" : "off")}{why}{noPath}, max {(reduceMax > 0 ? reduceMax.ToString() : "all")} per dancer, " +
                    $"sigma clamp {(reduceSigma > 0f ? $"{reduceSigma * 100f:0.##} cm" : "none")}, path {renderPath}, depth {(questDepth ? "on" : "off")}, " +
                    $"set '{(splatSet.Length > 0 ? splatSet : "full")}', layer scale {SplatUrpFeature.LowResScale:0.##}{(SplatUrpFeature.LowResScale < 0.999f ? $" (low-res layer, {(questDepth ? "low-res splat depth" : "no splat depth")})" : "")}, " +
                    $"alpha cutoff {(alphaCutoff > 0f ? alphaCutoff.ToString("0.###") : "package")}, sort every {sortEvery}, api {api}, settings {st.Source}";
        Debug.Log($"HM-SPLATS tier: {QuestTier} (capture {Path.GetFileName(viewer.Manifest?.Folder ?? "?")})");
    }

    static SplatDancers Ensure()
    {
        if (Instance != null) return Instance;
        GameObject go = new("Splat Dancers");
        Instance = go.AddComponent<SplatDancers>();
        return Instance;
    }

    /// <summary>apply the rule to the loaded capture again (hm_splatdancers --action on | auto: e.g. after its splats/ appeared): its
    /// splat dancers when it has splat data the device can draw, else the skeleton lines (splat data) or its mesh avatars (none).
    /// Nothing is reloaded when the splat dancers of this capture are already built.</summary>
    public static void Reapply()
    {
        HeadMovement viewer = HeadMovement.Instance;
        if (viewer == null || viewer.Manifest == null) return;
        SplatDancers s = Instance;
        if (splatsOn && s != null && s.hm == viewer && s.capture == viewer.Manifest.Folder && s.entries.Count > 0 && !s.NeedsLoad &&
            CaptureHasSplats(viewer.Manifest)) return;
        OnAvatarsLoaded(viewer);
    }

    /// <summary>hm_splatdancers --action off: refused for a capture with splat data (its dancers are always drawn as splats, never as
    /// the mesh avatars: the owner's rule of 2026-10-23) - throws with the reason; a capture without splat data has nothing to switch
    /// off (its mesh avatars are drawn) - returns that note. There is no override: hide the dancers with hm_layer avatars off.</summary>
    public static string RefuseOff()
    {
        HeadMovement viewer = HeadMovement.Instance;
        CaptureManifest m = viewer != null ? viewer.Manifest : null;
        if (CaptureHasSplats(m))
            throw new InvalidOperationException(
                $"refused: {Path.GetFileName(m.Folder)} has splat dancers, and a dancer with splat data is always drawn as splats - its mesh avatar " +
                "is never drawn (owner rule 2026-10-23: \"I don't ever want to see the basic avatars if gauss splat avatars are available for a dance\"; " +
                "docs/SPLAT_DANCERS.md \"Splats always\"). There is no switch back to the mesh avatars; hide the dancers with hm_layer --layer avatars --visible false.");
        return m == null ? "no capture loaded" : "this capture has no splat dancers: its mesh avatars are drawn (nothing to switch off)";
    }

    /// <summary>the avatars back, the over-the-splats lists empty, the URP feature out (the toggle is off)</summary>
    static void TurnedOff()
    {
        if (Instance == null) return;
        Instance.ApplyVisibility();
        Instance.LinesBackOn();
        SplatUrpFeature.OverSplats.Clear();
        SplatUrpFeature.OverSplatsInstanced.Clear();
        SplatUrpFeature.Remove();
    }

    void Awake()
    {
        // the Editor's AssetDatabase, or (a player: the Quest) the Resources shader set
        SplatShaderSet shaders = SplatShaderSet.Get();
        compute = shaders != null ? shaders.skin : null;
        if (shaders == null || !shaders.Complete) Debug.LogError($"splat dancers: shader set incomplete (missing {(shaders != null ? shaders.Missing() : "the asset")}): {SplatShaderSet.AssetPath}");
        root = gameObject;
        // a device without a splat path (D3D11, GLES, no compute) gets no renderer at all: this instance only keeps the mesh avatars of a
        // capture with splat data hidden (the skeleton lines show) - nothing of the package's GPU code is started there
        if (SplatDancersDefault.NoPath(SystemInfo.graphicsDeviceType, SystemInfo.supportsComputeShaders,
                TierPlayer ? (QuestPlayer ? SplatDancersDefault.Host.Quest : SplatDancersDefault.Host.WindowsPlayer) : SplatDancersDefault.Host.Editor).Length > 0)
        {
            group = null;
            return;
        }

        group = gameObject.AddComponent<GaussiansGroup>();
        if (shaders != null)
        {
            if (group.m_ShaderSplats == null) group.m_ShaderSplats = shaders.splats;
            if (group.m_CSSplatUtilities == null) group.m_CSSplatUtilities = shaders.utilities;
            if (group.m_ShaderComposite == null) group.m_ShaderComposite = shaders.composite;
        }
        group.m_RenderPath = GaussianSplat3DRenderer.RenderPath.CompositeTexture;
        group.m_ProjectionMode = GaussianSplat3DRenderer.ProjectionMode.Optimal;
        group.m_ConvertGammaToLinear = true; // Direct path: LHM++ colours are sRGB display values; the project is Linear
                                             // (the Composite path converts at the resolve: GaussiansURPFeature default)
        group.m_SortNthFrame = 1;            // the posed positions change every frame
        group.m_WriteDepth = true;           // splat depth for the overlays (SplatUrpFeature.DepthBeforeTransparents)
        if (TierPlayer) group.m_WriteDepth = questDepth; // set once here, never toggled at run time (see FilmVideoFade)
        Shader composite = shaders != null ? shaders.fadeComposite : null;
        if (composite != null && composite.isSupported) group.m_ShaderComposite = composite;
        else Debug.LogWarning($"splat dancers: {CompositeShaderPath} missing or unsupported - the package composite is used (no fade)");
        Shader.SetGlobalFloat(OpacityId, 1f);
    }

    /// <summary>drop what was built for the previous capture and look for this capture's splat avatars</summary>
    public void Rebuild(HeadMovement viewer)
    {
        Clear();
        hm = viewer;
        capture = viewer.Manifest?.Folder;
        if (capture == null) return;
        loadWatch.Restart();
        foreach (KeyValuePair<Role, SmplxAvatar> kv in viewer.Avatars)
        {
            if (kv.Value == null) continue;
            // the Quest's distilled set (splats/<set>/<role>.json) when the capture has it, else the full file, else (a data folder that
            // carries only a named set) that set: a dancer with splat data of any kind is drawn as splats (CaptureManifest.HasSplatAvatar)
            string roleName = kv.Key == Role.Lead ? "lead" : "follow";
            string json = SplatAvatarData.JsonPath(capture, roleName, splatSet, out bool fromSet);
            string onlySet = json == null ? SplatAvatarData.AnySetJsonPath(capture, roleName) : null;
            Entry e = new() { Role = kv.Key, Avatar = kv.Value, JsonPath = json ?? onlySet };
            if (onlySet != null)
                Debug.LogWarning($"{(TierPlayer ? "HM-SPLATS " : "")}splat dancers: {kv.Key}: no splats/{roleName}.json" +
                                 $"{(splatSet.Length > 0 ? $" and no set '{splatSet}'" : "")} in {capture}: set '{Path.GetFileName(Path.GetDirectoryName(onlySet))}' is used");
            else if (json != null && splatSet.Length > 0 && !fromSet && !string.Equals(splatSet, "full", StringComparison.OrdinalIgnoreCase))
                Debug.LogWarning($"{(TierPlayer ? "HM-SPLATS " : "")}splat dancers: {kv.Key}: no set '{splatSet}' in {capture}/splats: the full file is used");
            json = e.JsonPath;
            // the data is read only when the device can draw it (splatsOn; a device without a splat path reads no splat file: its
            // dancers show as skeleton lines)
            if (json != null && splatsOn)
            {
                string path = json;
                int max = reduceMax; // a player: VrSettings at the capture load; the Editor: SetReduction (default none)
                float sigma = reduceSigma;
                e.Loading = Task.Run(() =>
                {
                    SplatAvatarData d = SplatAvatarData.Load(path);
                    if (max <= 0 && sigma <= 0f) return d;
                    SplatAvatarData r = d.Reduce(max, sigma, out string report);
                    e.Reduction = report;
                    return r;
                });
            }

            entries.Add(e);
        }

        // the mesh avatars of the dancers with splat data are hidden now, at the capture load (not one frame later): they are never
        // drawn, also while their splats load (the skeleton lines show until the splats are built)
        ApplyVisibility();
    }

    /// <summary>a dancer of this capture has splat data that was not read (the capture was built while the splats were off)</summary>
    bool NeedsLoad
    {
        get
        {
            foreach (Entry e in entries)
            {
                if (e.JsonPath != null && e.Splat == null && e.Loading == null && e.Error == null) return true;
            }

            return false;
        }
    }

    void Clear()
    {
        fellBack = false;
        badSince = -1f;
        LinesBackOn();
        beadDraws.Clear();
        foreach (Entry e in entries)
        {
            // a dancer with splat data keeps its mesh avatar hidden (a rebuild of the same capture hides it again at once; another
            // capture destroys these avatars)
            if (e.JsonPath == null) Unhide(e);
            if (e.Splat != null)
            {
                e.Splat.Dispose();
                Destroy(e.Splat.gameObject);
            }
        }

        entries.Clear();
    }

    void Update()
    {
        // the viewer replaced its avatars (another capture) without telling us: rebuild
        if (HeadMovement.Instance != null && splatsOn && (hm != HeadMovement.Instance || capture != HeadMovement.Instance.Manifest?.Folder))
        {
            if (HeadMovement.Instance.Avatars.Count > 0) Rebuild(HeadMovement.Instance);
        }

        foreach (Entry e in entries)
        {
            if (e.Loading != null && e.Loading.IsCompleted)
            {
                Task<SplatAvatarData> t = e.Loading;
                e.Loading = null;
                if (t.IsFaulted) Failed(e, t.Exception?.GetBaseException().Message ?? "unreadable", "could not be read");
                else Build(e, t.Result);
            }

            AttachHair(e);
        }
    }

    /// <summary>a dancer with bound hair Gaussians gets its avatar's simulated HairStrands as the hair source (looked for
    /// twice a second until found; dropped when the hair object goes away)</summary>
    static void AttachHair(Entry e)
    {
        SkinnedSplatAvatar s = e.Splat;
        if (s == null || s.HairBinding == null || e.Avatar == null) return;
        if (s.HairSource is HairStrandsSplatSource cur && cur.Hair != null && cur.Hair.Avatar == e.Avatar) return;
        if (Time.unscaledTime < e.NextHairScan) return;
        e.NextHairScan = Time.unscaledTime + 0.5f;
        HairStrands hair = e.Avatar.GetComponentInChildren<HairStrands>(true);
        s.SetHairSource(hair != null ? new HairStrandsSplatSource(hair) : null);
        if (hair != null) Debug.Log($"splat dancers: {e.Role} hair: {s.HairBinding.Bound} bound Gaussians driven by {hair.Guides} simulated guides");
    }

    void Build(Entry e, SplatAvatarData data)
    {
        if (e.Avatar == null) return;
        try
        {
            if (compute == null) throw new InvalidOperationException($"skinning compute shader not found at {SkinnedSplatAvatar.ComputePath}");
            Transform[] bones = new Transform[SplatAvatarData.Joints];
            for (int j = 0; j < bones.Length; j++) bones[j] = e.Avatar.Bone(j);
            e.Splat = SkinnedSplatAvatar.Create(data, bones, root.transform, $"{e.Role} splat dancer", compute);
            e.Splat.gameObject.layer = e.Avatar.gameObject.layer;
            SplatUrpFeature.SplatLayer = e.Avatar.gameObject.layer;
            group.AddMember(e.Splat.Renderer);
            e.LoadSeconds = loadWatch.Elapsed.TotalSeconds;
            Debug.Log($"{(TierPlayer ? "HM-SPLATS " : "")}splat dancers: {e.Role} {data.Count} Gaussians ({data.HairCount} hair, {data.BoundHairCount} bound) from {data.SourcePath}" +
                      $" in {e.LoadSeconds:0.0} s{(e.Reduction != null ? "; thinned: " + e.Reduction : "")}");
            if (TierPlayer) SplatUrpFeature.Ensure();
        }
        catch (Exception ex)
        {
            Debug.LogError($"splat dancers: {e.Role}: {ex}");
            Failed(e, ex.Message, "failed to build");
        }
    }

    /// <summary>a splat avatar that cannot be read or built: that dancer shows its skeleton lines (never its mesh avatar) and the
    /// on-screen note says why</summary>
    static void Failed(Entry e, string message, string what)
    {
        e.Error = message;
        CannotDrawNote = $"the {e.Role.ToString().ToLowerInvariant()} splat avatar {what} ({message}) - skeleton lines shown (the mesh avatars are never drawn for a capture with splat dancers)";
        Debug.LogError($"{(TierPlayer ? "HM-SPLATS " : "")}splat dancers: {CannotDrawNote}");
    }

    void LateUpdate()
    {
        if (group != null)
        {
            GaussianSplat3DRenderer.RenderPath path = renderPath == Paths.Direct ? GaussianSplat3DRenderer.RenderPath.DirectTransparent : GaussianSplat3DRenderer.RenderPath.CompositeTexture;
            if (group.m_RenderPath != path) group.m_RenderPath = path;
            // the Quest knobs (defaults = the package's: cutoff 1/255, sort every frame, compaction above 100,000 rejected)
            float cutoff = alphaCutoff > 0f ? alphaCutoff : GaussianSplat3DRenderer.DefaultAlphaCutoff;
            if (!Mathf.Approximately(group.m_AlphaCutoff, cutoff)) group.m_AlphaCutoff = cutoff;
            if (group.m_SortNthFrame != sortEvery) group.m_SortNthFrame = sortEvery;
            // sort amortisation: the frame keeps its position revision (else the renderer re-sorts after every skinned frame) and the
            // visible-splat compaction is off (it re-sorts whenever the view data changes, i.e. every frame)
            SkinnedSplatAvatar.HoldPositionRevision = sortEvery > 1;
            int compaction = sortEvery > 1 ? -1 : 100000;
            if (group.m_CompactionThreshold != compaction) group.m_CompactionThreshold = compaction;
        }

        float fade = CompositeOpacity();
        DisplayOpacity = fade;
        Shader.SetGlobalFloat(OpacityId, fade);
        ApplyVisibility();
        FillOverSplats();
        if (TierPlayer && splatsOn && entries.Count > 0)
        {
            Watchdog();
            if (Time.unscaledTime >= nextStatusLog) LogStatus();
        }
    }

    bool fellBack;
    float badSince = -1f;

    /// <summary>the players' safety net (an untested GPU path must not leave the lesson without dancers): when the renderer reports
    /// that it cannot draw a shown splat dancer (an unsupported shader or kernel, resources that never become ready) for 3 s, the
    /// splats are hidden for the rest of this capture and the dancers' skeleton lines show - never the mesh avatars (owner rule
    /// 2026-10-23) - and logcat / Player.log says why (HM-SPLATS FALLBACK), as does the on-screen note</summary>
    void Watchdog()
    {
        if (fellBack || group == null) return;
        string reason = group.RenderingError;
        if (reason == null)
        {
            foreach (Entry e in entries)
            {
                if (e.Splat == null || !e.Splat.gameObject.activeInHierarchy || e.Splat.Renderer == null) continue;
                string st = group.MemberRenderingStatus(e.Splat.Renderer, out bool warning);
                if (warning)
                {
                    reason = $"{e.Role}: {st}";
                    break;
                }
            }
        }

        if (reason == null)
        {
            badSince = -1f;
            return;
        }

        if (badSince < 0f) badSince = Time.unscaledTime;
        if (Time.unscaledTime - badSince < 3f) return;
        fellBack = true;
        CannotDrawNote = SplatDancersDefault.Note(reason, QuestPlayer ? SplatDancersDefault.Host.Quest : SplatDancersDefault.Host.WindowsPlayer);
        Debug.LogError($"HM-SPLATS FALLBACK: the splat dancers cannot be drawn on this device ({reason}); the dancers' skeleton lines are shown " +
                       "(never the mesh avatars of a capture with splat dancers)");
    }

    float nextStatusLog;

    /// <summary>logcat every 5 s on the Quest (adb logcat -s Unity | grep HM-SPLATS): what is drawn</summary>
    void LogStatus()
    {
        nextStatusLog = Time.unscaledTime + 5f;
        System.Text.StringBuilder sb = new();
        foreach (Entry e in entries)
        {
            sb.Append($" | {e.Role}: ");
            if (e.Error != null) sb.Append("error " + e.Error);
            else if (e.Loading != null) sb.Append("loading");
            else if (e.Splat == null) sb.Append(e.JsonPath == null ? "no splat data (avatar)" : "not built");
            else
                sb.Append($"{e.Splat.Data.Count} Gaussians, shown {e.Splat.gameObject.activeInHierarchy}, avatar visible {e.Avatar != null && e.Avatar.Visible}, " +
                          $"renderers hidden {e.Hidden.Count}, skinned frames {e.Splat.SkinnedFrames}, hair {(e.Splat.HairBinding != null ? (e.Splat.HairLive ? $"live ({e.Splat.HairFrames} frames)" : e.Splat.HairStatus) : "-")}");
        }

        Debug.Log($"HM-SPLATS status: {(fellBack ? "FELL BACK to the skeleton lines, " : "")}feature {(SplatUrpFeature.Injected ? "in" : "NOT in")} the URP renderer, " +
                  $"{(SplatUrpFeature.LowResActive ? $"low-res layer '{SplatLowResFeature.Status}'" : $"depth patch '{SplatUrpFeature.DepthPatch}'")}, path {renderPath}, " +
                  $"set '{(splatSet.Length > 0 ? splatSet : "full")}', cutoff {group?.m_AlphaCutoff:0.###}, sort every {group?.m_SortNthFrame}, " +
                  $"group '{group?.Status}', participating {group?.ParticipatingSplats ?? 0}, {Math.Round((group?.AllocatedBytes ?? 0) / 1048576.0, 1)} MB, over-splats '{overSplatsStatus}'{sb}");
    }

    // ------------------------------------------------------------------ the film's fade into / out of a phone's video

    /// <summary>
    /// The film director's fade of the 3D dancers at a phone shot (layer "avatars_fade_with_video", the class recap's flights into a
    /// phone and its camera tours): 1 - the phone video's opacity while that video shows, the same factor the director applies to the
    /// mesh avatars (FilmDirector.Apply: avatar opacity x (1 - video)); 1 everywhere else and without a director (the viewer). The
    /// opaque splats are faded with it through the composite (_HmSplatOpacity), so they dissolve into the video like the avatars
    /// instead of standing on it at full opacity and vanishing on one frame (the first splat films, 2026-10-20). The splat depth stays
    /// on through the fade: switching the group's Write Depth off below half opacity crashed the D3D12 device at the first fade
    /// ("failed to Close a command list (80070057)", four editor crashes on 2026-10-21) - never toggle m_WriteDepth at run time.
    /// </summary>
    /// <summary>the skinned splat avatar of a role while the splat dancers are on (the film's face sidecar), else null</summary>
    public static SkinnedSplatAvatar SplatOf(Role role)
    {
        if (!splatsOn || Instance == null) return null;
        foreach (Entry e in Instance.entries)
        {
            if (e.Role == role && e.Splat != null) return e.Splat;
        }

        return null;
    }

    /// <summary>the film's face sidecar (v4.2: the face swap applies to the splat dancers only): how much of a role's body is drawn as splats now,
    /// 0..1 - the composite opacity while that dancer's splats are drawn (its splat avatar loaded and its avatar slot visible, as OpaqueBodiesNow),
    /// else 0 (splats off, not loaded, or the dancer hidden)</summary>
    public static float ShownFor(Role role)
    {
        SplatDancers s = Instance;
        if (!splatsOn || s == null) return 0f;
        foreach (Entry e in s.entries)
        {
            if (e.Role == role && e.Splat != null && e.Avatar != null && e.Avatar.Visible && !Parked(e)) return Mathf.Clamp01(s.CompositeOpacity());
        }

        return 0f;
    }

    public static float FilmVideoFade()
    {
        FilmDirector fd = FilmDirector.Instance;
        if (fd == null || fd.SegmentIndex < 0) return 1f;
        // v3 flow: hidden from a phone's arrival (the frustum surface stood in front of them until then and the hand-off cover hides the
        // switch), back in over splats_back_in_s from the departure - unless the phone's video is missing (the shot is never empty)
        if (fd.FlowSplatVisibility.HasValue)
        {
            float v = fd.FlowSplatVisibility.Value;
            if (v < 1f && fd.FilmCam?.PovCamera != null && fd.Sources != null && !fd.Sources.VideoShowing) return 1f;
            return v;
        }

        FilmDirector.SegLayers l = fd.LayersOf(fd.SegmentIndex);
        if (l == null || !l.AvatarsFadeWithVideo || l.AvatarsOverVideo) return 1f;
        FilmCamera cam = fd.FilmCam;
        if (cam == null || cam.PovCamera == null || fd.Sources == null || !fd.Sources.VideoShowing) return 1f;
        return 1f - Mathf.Clamp01(cam.PovVideoOpacity);
    }

    /// <summary>the composite's opacity now: Opaque = the film's video fade only; FollowAvatars = the avatars' opacity (which
    /// already carries that fade); ScaleSplats = 1 (the Gaussians' own alpha is scaled instead)</summary>
    float CompositeOpacity()
    {
        switch (opacityMode)
        {
            case OpacityModes.FollowAvatars:
            {
                float fade = 0f;
                foreach (Entry e in entries)
                {
                    if (e.Avatar != null) fade = Mathf.Max(fade, Mathf.Clamp01(e.Avatar.Opacity));
                }

                return fade;
            }
            case OpacityModes.Opaque:
                return FilmVideoFade();
            default:
                return 1f;
        }
    }

    /// <summary>
    /// FilmUi.OpaqueBodies (the captions' and chapter cards' darker backing): how much of the opaque splat bodies shows in the
    /// frame being built, 0..1 = the composite opacity when a dancer's splats are drawn (its avatar would be), else 0. Evaluated
    /// when the overlays lay out (FilmLate, after the director's Update), so a cut into a phone shot switches the backing on the
    /// same frame as the bodies.
    /// </summary>
    static float OpaqueBodiesNow()
    {
        SplatDancers s = Instance;
        if (!splatsOn || s == null || s.entries.Count == 0) return 0f;
        bool any = false;
        foreach (Entry e in s.entries)
        {
            if (e.Splat != null && e.Avatar != null && e.Avatar.Visible)
            {
                any = true;
                break;
            }
        }

        return any ? s.CompositeOpacity() : 0f;
    }

    // ------------------------------------------------------------------ over the splats (SplatOverlayFeature)

    // Dancer.SkeletonLines() and ContactDetection's two hand orbs are private: read by reflection (null = not found: nothing
    // is drawn over the splats and State() says so)
    static readonly MethodInfo SkeletonLinesMethod = typeof(Dancer).GetMethod("SkeletonLines", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly FieldInfo[] OrbFields =
    {
        typeof(ContactDetection).GetField("leadLefHandOrb", BindingFlags.NonPublic | BindingFlags.Instance),
        typeof(ContactDetection).GetField("leadRightHandOrb", BindingFlags.NonPublic | BindingFlags.Instance)
    };

    string overSplatsStatus = "off";

    // the follower's spine beads (SpineBeads draws them itself, instanced): its private buffers, read by reflection
    static readonly FieldInfo BeadsField = typeof(Dancer).GetField("followSpineBeads", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly FieldInfo BeadMatrices = typeof(SpineBeads).GetField("matrices", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly FieldInfo BeadCount = typeof(SpineBeads).GetField("count", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly FieldInfo BeadMaterial = typeof(SpineBeads).GetField("material", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly FieldInfo BeadProps = typeof(SpineBeads).GetField("props", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly FieldInfo BeadSphere = typeof(SpineBeads).GetField("sphere", BindingFlags.NonPublic | BindingFlags.Static);
    readonly Dictionary<SpineBeads, SplatUrpFeature.InstancedDraw> beadDraws = new();
    readonly List<MeshRenderer> balanceScratch = new();
    readonly List<MeshRenderer> comScratch = new();
    readonly List<GlowMesh> floorScratch = new();

    /// <summary>the camera looks down steeper than this (forward.y below minus it, about 53 deg): an overhead view, the floor
    /// craft is drawn over the splats</summary>
    public const float OverheadBelowY = 0.8f;

    Camera viewCamera;

    /// <summary>the camera the viewer / film renders with: Camera.main, else (the viewer's camera is untagged) the first
    /// enabled game camera that renders to the screen</summary>
    Camera ViewCamera()
    {
        Camera main = Camera.main;
        if (main != null) return main;
        if (viewCamera != null && viewCamera.isActiveAndEnabled && viewCamera.targetTexture == null) return viewCamera;
        viewCamera = null;
        foreach (Camera c in Camera.allCameras)
        {
            if (c.cameraType == CameraType.Game && c.targetTexture == null)
            {
                viewCamera = c;
                break;
            }
        }

        return viewCamera;
    }

    bool AnySplatShown()
    {
        foreach (Entry e in entries)
        {
            if (e.Splat != null && e.Splat.gameObject.activeSelf) return true;
        }

        return false;
    }
    PhysicsOverlay physicsOverlay;
    float nextPhysicsScan;

    /// <summary>the follower's spine beads as an instanced draw over the splats (null when the viewer's fields are not found)</summary>
    SplatUrpFeature.InstancedDraw BeadDraw(Dancer d)
    {
        if (BeadsField == null || BeadMatrices == null || BeadCount == null || BeadMaterial == null || BeadProps == null || BeadSphere == null) return null;
        if (BeadsField.GetValue(d) is not SpineBeads beads || beads == null) return null;
        if (!beadDraws.TryGetValue(beads, out SplatUrpFeature.InstancedDraw draw))
        {
            draw = new SplatUrpFeature.InstancedDraw { Count = () => beads != null && beads.Visible ? (int)BeadCount.GetValue(beads) : 0 };
            beadDraws[beads] = draw;
        }

        draw.Mesh = BeadSphere.GetValue(null) as Mesh;
        draw.Material = BeadMaterial.GetValue(beads) as Material;
        draw.Matrices = BeadMatrices.GetValue(beads) as Matrix4x4[];
        draw.Properties = BeadProps.GetValue(beads) as MaterialPropertyBlock;
        return draw.Mesh != null && draw.Material != null && draw.Matrices != null ? draw : null;
    }

    readonly List<LineRenderer> linesOff = new();
    readonly List<LineRenderer> linesScratch = new();

    /// <summary>this frame's skeleton lines with the splats (SkeletonOver): drawn over the splats (SplatOverlayFeature) for every
    /// dancer whose splats show, plus the hand-contact lights when the lead's do; or (Physics mode outside the physics view)
    /// those dancers' lines switched off (Renderer.forceRenderingOff, given back when the splats hide or the mode changes)</summary>
    void FillOverSplats()
    {
        List<Renderer> list = SplatUrpFeature.OverSplats;
        list.Clear();
        SplatUrpFeature.OverSplatsInstanced.Clear();
        SplatUrpFeature.OverSplatsOnly.Clear();
        linesScratch.Clear();
        bool followShown = false;
        bool physicsView = hm != null && hm.SkeletonMode == SkeletonStyle.Mode.Physics;
        // a headset (XR: the Quest, PC VR): no extra pass over the splats (SplatOverlayFeature is not verified for single-pass
        // stereo). The VR skeleton draws over the bodies by itself (its second material: ZTest Always, queue 3100), so the lines
        // still show in the physics view and are switched off in the other views; the hand lights, COM spheres and the floor
        // craft keep their own depth-correct draws (nothing is switched off for an over-the-splats pass that would not run)
        bool xr = XRSettings.isDeviceActive;
        bool over = splatsOn && hm != null && !xr && (skeletonOver == SkeletonOverModes.Always || (skeletonOver == SkeletonOverModes.Physics && physicsView));
        bool off = splatsOn && hm != null && skeletonOver == SkeletonOverModes.Physics && !physicsView;
        bool leadShown = false;
        if ((over || off) && SkeletonLinesMethod != null)
        {
            foreach (Entry e in entries)
            {
                if (e.Splat == null || !e.Splat.gameObject.activeSelf) continue;
                Dancer d = e.Role == Role.Lead ? hm.LeadDancer : hm.FollowDancer;
                if (d == null) continue;
                if (e.Role == Role.Lead) leadShown = true;
                else followShown = true;
                // read every frame (a 7-entry array): the viewer may rebuild its lines on a capture load
                if (SkeletonLinesMethod.Invoke(d, null) is not LineRenderer[] lines) continue;
                foreach (LineRenderer l in lines)
                {
                    if (l != null) linesScratch.Add(l);
                }
            }
        }

        // lines switched off last frame and not this frame: back on
        for (int i = linesOff.Count - 1; i >= 0; i--)
        {
            LineRenderer l = linesOff[i];
            if (l == null)
            {
                linesOff.RemoveAt(i);
                continue;
            }

            if (off && linesScratch.Contains(l)) continue;
            l.forceRenderingOff = false;
            linesOff.RemoveAt(i);
        }

        if (off)
        {
            foreach (LineRenderer l in linesScratch)
            {
                if (!l.forceRenderingOff) l.forceRenderingOff = true;
                if (!linesOff.Contains(l)) linesOff.Add(l);
            }
        }

        // the hand-contact lights (the glow where the partners' hands meet) sit inside the joined hands: over the splats in every
        // view (they are no skeleton line, and the film's "connection" call-outs point at them), as through the avatars
        if ((over || off) && !xr && leadShown && hm.TryGetComponent(out ContactDetection contacts))
        {
            foreach (FieldInfo f in OrbFields)
            {
                if (f?.GetValue(contacts) is Component orb && orb != null && orb.TryGetComponent(out Renderer r)) list.Add(r);
            }
        }

        if (over)
        {
            list.AddRange(linesScratch);

            // the follower's spine beads (her trunk in the skeleton colours) and, in the physics view, the balance axes (chest to
            // the stance foot: inside the body too)
            if (followShown && hm.FollowDancer != null && BeadDraw(hm.FollowDancer) is SplatUrpFeature.InstancedDraw beads)
                SplatUrpFeature.OverSplatsInstanced.Add(beads);
            // the physics view's centre-of-mass spheres (PhysicsOverlay "Lead COM" / "Follow COM": inside the pelvis; the XCoM
            // disc and the support polygon lie on the floor and stay depth-correct)
            if (physicsView && (leadShown || followShown))
            {
                if (physicsOverlay == null && Time.unscaledTime >= nextPhysicsScan)
                {
                    nextPhysicsScan = Time.unscaledTime + 1f;
                    physicsOverlay = FindFirstObjectByType<PhysicsOverlay>();
                }

                if (physicsOverlay != null)
                {
                    physicsOverlay.GetComponentsInChildren(false, comScratch);
                    foreach (MeshRenderer r in comScratch)
                    {
                        if (r.name.EndsWith(" COM", StringComparison.Ordinal)) list.Add(r);
                    }
                }
            }

            if (physicsView && (leadShown || followShown) && DanceLayers.Instance != null && DanceLayers.Instance.Balance != null)
            {
                // additive glow: drawn by the over-the-splats pass only (its own draw off), else it doubles in front of the bodies
                DanceLayers.Instance.Balance.GetComponentsInChildren(false, balanceScratch);
                foreach (MeshRenderer r in balanceScratch)
                {
                    SplatUrpFeature.OverSplatsOnly.Add(r);
                    if (!r.forceRenderingOff) r.forceRenderingOff = true;
                    if (!glowOff.Contains(r)) glowOff.Add(r);
                }
            }
        }

        // seen from above (the film's overhead floor-craft shots: the leader's T, the floor record), the floor marks the
        // dancers stand on are drawn over them too, as they show through the translucent avatars; in every other view they
        // stay depth-correct (a ring behind a foot is hidden by it). Additive glow: drawn by the over-the-splats pass only.
        Camera eye = ViewCamera();
        bool overhead = eye != null && eye.transform.forward.y < -OverheadBelowY;
        if (splatsOn && hm != null && !xr && skeletonOver != SkeletonOverModes.Never && overhead && AnySplatShown() &&
            DanceLayers.Instance != null && DanceLayers.Instance.FloorCraft != null)
        {
            DanceLayers.Instance.FloorCraft.GetComponentsInChildren(false, floorScratch);
            foreach (GlowMesh g in floorScratch)
            {
                if (!g.TryGetComponent(out MeshRenderer r)) continue;
                SplatUrpFeature.OverSplatsOnly.Add(r);
                if (!r.forceRenderingOff) r.forceRenderingOff = true;
                if (!glowOff.Contains(r)) glowOff.Add(r);
            }
        }

        for (int i = glowOff.Count - 1; i >= 0; i--)
        {
            Renderer r = glowOff[i];
            if (r != null && SplatUrpFeature.OverSplatsOnly.Contains(r)) continue;
            if (r != null) r.forceRenderingOff = false;
            glowOff.RemoveAt(i);
        }

        overSplatsStatus = !splatsOn ? "off"
            : SkeletonLinesMethod == null ? "Dancer.SkeletonLines not found (viewer code changed?): lines as before"
            : xr ? (off ? $"headset: lines off while the splats show ({linesOff.Count} lines)" : physicsView ? "headset physics view: the VR skeleton's own over-the-bodies draw" : "headset")
            : over ? $"over the splats: {list.Count} renderers + {SplatUrpFeature.OverSplatsOnly.Count} glow + {SplatUrpFeature.OverSplatsInstanced.Count} instanced"
            : off ? $"lines off while the splats show: {linesOff.Count} lines; over the splats: {list.Count} hand lights, {SplatUrpFeature.OverSplatsOnly.Count} floor-craft glow"
            : skeletonOver == SkeletonOverModes.Never ? "never: depth-correct lines" : "no dancer shown";
    }

    readonly List<Renderer> glowOff = new();

    void LinesBackOn()
    {
        foreach (LineRenderer l in linesOff)
        {
            if (l != null) l.forceRenderingOff = false;
        }

        linesOff.Clear();
        foreach (Renderer r in glowOff)
        {
            if (r != null) r.forceRenderingOff = false;
        }

        glowOff.Clear();
        SplatUrpFeature.OverSplatsOnly.Clear();
    }

    /// <summary>splats on where the avatar would be drawn (its layer, role-hidden spans, opacity > 0: SmplxAvatar.Visible); the mesh
    /// avatar of a dancer with splat data is never drawn - every renderer under it stays off whether its splats show, load, cannot be
    /// drawn (the skeleton lines show) or are hidden (owner rule 2026-10-23: SplatDancersDefault.DancerLook)</summary>
    void ApplyVisibility()
    {
        foreach (Entry e in entries)
        {
            if (e.Avatar == null)
            {
                if (e.Splat != null) e.Splat.gameObject.SetActive(false);
                continue;
            }

            bool show = splatsOn && !fellBack && e.Splat != null && e.Avatar.Visible && group != null;
            if (e.Splat != null)
            {
                if (e.Splat.gameObject.activeSelf != show) e.Splat.gameObject.SetActive(show);
                e.Splat.HairMode = hairMode;
                e.Splat.HairMotion = hairMotion;
                // a parked dancer draws nothing (the opaque splats showed through the empty floor in overhead views); it stays
                // "shown" for everything else (the avatar hidden under it, its skeleton lines as for a shown dancer)
                e.Splat.OpacityScale = Parked(e) ? 0f : opacityMode == OpacityModes.ScaleSplats ? Mathf.Clamp01(e.Avatar.Opacity) : 1f;
            }

            if (e.JsonPath != null) Hide(e); // splat data for this role: the mesh avatar is never drawn
            else Unhide(e);                  // no splat data: its mesh avatar, as before
        }
    }

    /// <summary>how a dancer is drawn now (SplatDancersDefault.Look; the CLI state)</summary>
    SplatDancersDefault.Look LookOf(Entry e) =>
        SplatDancersDefault.DancerLook(e.JsonPath != null, splatsOn && group != null && e.Error == null, fellBack);

    static readonly List<Renderer> RendererScratch = new();

    /// <summary>the mesh-avatar renderers under an avatar that are drawn now (enabled, not forceRenderingOff): 0 for a dancer with splat
    /// data (the Play-mode check of the owner's rule)</summary>
    public static int MeshRenderersDrawn(SmplxAvatar avatar)
    {
        if (avatar == null) return 0;
        avatar.GetComponentsInChildren(true, RendererScratch);
        int n = 0;
        foreach (Renderer r in RendererScratch)
        {
            if (r != null && r.enabled && !r.forceRenderingOff && r.gameObject.activeInHierarchy) n++;
        }

        RendererScratch.Clear();
        return n;
    }

    /// <summary>an avatar whose pelvis is more than this far under the floor (in the avatar's own space) is PARKED: dancecap
    /// export_unity park_absent puts a dancer that is not yet in any camera's view 30 m under its partner (capture.json "absent")</summary>
    public const float ParkedBelowM = 5f;

    static bool Parked(Entry e)
    {
        if (e.Avatar == null) return false;
        Transform pelvis = e.Avatar.Bone(0);
        return pelvis != null && e.Avatar.transform.InverseTransformPoint(pelvis.position).y < -ParkedBelowM;
    }

    /// <summary>every renderer under the avatar off, scanned every frame (an attachment that adds its renderer later - the shoes, the
    /// hair cards, a head prop - is off on its first frame; about 70 transforms per avatar, no allocation)</summary>
    void Hide(Entry e)
    {
        e.Avatar.GetComponentsInChildren(true, RendererScratch);
        foreach (Renderer r in RendererScratch)
        {
            if (r == null) continue;
            if (!r.forceRenderingOff) r.forceRenderingOff = true;
            if (!e.Hidden.Contains(r)) e.Hidden.Add(r);
        }

        RendererScratch.Clear();
        for (int i = e.Hidden.Count - 1; i >= 0; i--)
        {
            if (e.Hidden[i] == null) e.Hidden.RemoveAt(i);
        }
    }

    static void Unhide(Entry e)
    {
        foreach (Renderer r in e.Hidden)
        {
            if (r != null) r.forceRenderingOff = false;
        }

        e.Hidden.Clear();
    }

    void OnDisable()
    {
        SplatUrpFeature.OverSplats.Clear();
        SplatUrpFeature.OverSplatsInstanced.Clear();
        LinesBackOn();
        foreach (Entry e in entries)
        {
            if (e.JsonPath == null) Unhide(e); // a dancer with splat data keeps its mesh avatar hidden (never drawn)
        }

        SplatUrpFeature.Remove();
        Shader.SetGlobalFloat(OpacityId, 1f);
    }

    /// <summary>the on-screen note when the loaded capture's splat dancers cannot be drawn (CannotDrawNote: D3D11 / GLES / no compute, the
    /// players' watchdog, a splat avatar that failed to build) - the editor's Game view, a Windows player's desktop window</summary>
    void OnGUI()
    {
        if (string.IsNullOrEmpty(CannotDrawNote) || entries.Count == 0 || HeadMovement.LibraryOwnsScreen) return;
        if (noteStyle == null || noteBackground == null)
        {
            // an opaque panel of its own (the skin's box is nearly transparent over the scene)
            noteBackground = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            noteBackground.SetPixel(0, 0, new Color(0.06f, 0.06f, 0.08f, 0.94f));
            noteBackground.Apply();
            noteStyle = new GUIStyle(GUI.skin.label)
            {
                wordWrap = true, fontSize = 15, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(12, 12, 8, 8)
            };
            noteStyle.normal.textColor = new Color(1f, 0.85f, 0.4f);
            noteStyle.normal.background = noteBackground;
        }

        // bottom centre, above the HUD's timing ticker (HeadMovement: Screen.height - 96 .. - 12): clear of the HUD text block at the top
        // left and of the Enter VR button at the top right
        float w = Mathf.Min(Screen.width - 24f, 760f);
        float h = noteStyle.CalcHeight(new GUIContent(CannotDrawNote), w);
        GUI.Box(new Rect((Screen.width - w) * 0.5f, Mathf.Max(12f, Screen.height - 108f - h), w, h), CannotDrawNote, noteStyle);
    }

    GUIStyle noteStyle;
    Texture2D noteBackground;

    void OnDestroy()
    {
        Clear();
        if (noteBackground != null) Destroy(noteBackground);
        if (Instance == this) Instance = null;
    }

    /// <summary>state for the CLI / playtests</summary>
    public Dictionary<string, object> State()
    {
        Dictionary<string, object> s = new()
        {
            ["enabled"] = splatsOn,
            ["why"] = DefaultWhy,
            ["rule"] = "a dancer with splat data is always drawn as splats, never as its mesh avatar; no splat path: skeleton lines (owner 2026-10-23)",
            ["cannotDrawNote"] = CannotDrawNote,
            ["noPath"] = noPathNow,
            ["captureFlag"] = hm != null && hm.Manifest != null ? hm.Manifest.splat_dancers : null,
            ["capture"] = capture != null ? Path.GetFileName(capture) : null,
            ["opacityMode"] = opacityMode.ToString(),
            ["displayOpacity"] = DisplayOpacity,
            ["renderPath"] = renderPath.ToString(),
            ["questTier"] = QuestTier,
            ["reduceMax"] = reduceMax,
            ["reduceSigma"] = reduceSigma,
            ["splatSet"] = splatSet,
            ["layerScale"] = SplatUrpFeature.LowResScale,
            ["lowResLayer"] = SplatUrpFeature.LowResActive ? SplatLowResFeature.Status : "off",
            ["alphaCutoff"] = group != null ? group.m_AlphaCutoff : -1f,
            ["sortEvery"] = group != null ? group.m_SortNthFrame : -1,
            ["fellBackToSkeleton"] = fellBack,
            ["depthPatch"] = SplatUrpFeature.DepthPatch,
            ["hairMode"] = hairMode.ToString(),
            ["hairMotion"] = hairMotion,
            ["skeletonOverSplats"] = skeletonOver.ToString(),
            ["overSplats"] = overSplatsStatus,
            ["overSplatsDrawnLastFrame"] = SplatOverlayFeature.LastDrawn,
            ["urpFeatureInjected"] = SplatUrpFeature.Injected,
            ["urpFeatureRenderers"] = new List<string>(SplatUrpFeature.TargetNames),
            ["graphicsApi"] = SystemInfo.graphicsDeviceType.ToString(),
            ["groupStatus"] = group != null ? group.Status : null,
            ["groupParticipatingSplats"] = group != null ? group.ParticipatingSplats : 0,
            ["groupAllocatedMB"] = group != null ? Math.Round(group.AllocatedBytes / 1048576.0, 1) : 0
        };
        List<object> dancers = new();
        foreach (Entry e in entries)
        {
            dancers.Add(new Dictionary<string, object>
            {
                ["role"] = e.Role.ToString(),
                ["look"] = LookOf(e).ToString(),
                ["meshRenderersDrawn"] = MeshRenderersDrawn(e.Avatar),
                ["json"] = e.JsonPath,
                ["loading"] = e.Loading != null,
                ["error"] = e.Error,
                ["count"] = e.Splat != null ? e.Splat.Data.Count : 0,
                ["hair"] = e.Splat != null ? e.Splat.Data.HairCount : 0,
                ["hairBound"] = e.Splat != null ? e.Splat.Data.BoundHairCount : 0,
                ["hairSource"] = e.Splat?.HairSource is HairStrandsSplatSource hs && hs.Hair != null ? hs.Hair.name : null,
                ["hairLive"] = e.Splat != null && e.Splat.HairLive,
                ["hairStatus"] = e.Splat?.HairStatus,
                ["hairFrames"] = e.Splat != null ? e.Splat.HairFrames : 0,
                ["hairRestDiffMm"] = e.Splat?.HairBinding != null ? e.Splat.HairBinding.RestDiffMaxMm : float.NaN,
                ["hairCanonResidualMm"] = e.Splat?.HairBinding != null ? e.Splat.HairBinding.CanonResidualMaxMm : float.NaN,
                ["shown"] = e.Splat != null && e.Splat.gameObject.activeInHierarchy,
                ["parked"] = Parked(e),
                ["opacityScale"] = e.Splat != null ? e.Splat.OpacityScale : 0f,
                ["avatarVisible"] = e.Avatar != null && e.Avatar.Visible,
                ["avatarOpacity"] = e.Avatar != null ? e.Avatar.Opacity : 0f,
                ["avatarRenderersHidden"] = e.Hidden.Count,
                ["skinnedFrames"] = e.Splat != null ? e.Splat.SkinnedFrames : 0,
                ["loadSeconds"] = Math.Round(e.LoadSeconds, 2),
                ["posedBounds"] = e.Splat != null ? e.Splat.LastPosedBounds.ToString("F2") : null,
                ["rendererDeformed"] = e.Splat != null && e.Splat.Renderer != null && e.Splat.Renderer.IsDeformed
            });
        }

        s["dancers"] = dancers;
        return s;
    }

    public SkinnedSplatAvatar Splat(Role role)
    {
        foreach (Entry e in entries)
        {
            if (e.Role == role) return e.Splat;
        }

        return null;
    }

    public GaussiansGroup Group => group;
}
#endif
