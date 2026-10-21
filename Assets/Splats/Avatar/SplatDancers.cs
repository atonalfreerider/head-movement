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
/// Viewer toggle "splat dancers" (default OFF: the viewer shows the current mesh avatars until the splat recordings are
/// approved). While on, and when the loaded capture has <c>splats/&lt;role&gt;.json</c> + <c>.splatav</c> (dancecap
/// SPLAT_AVATAR_FORMAT.md; LHM++ skinned Gaussians), each dancer's mesh avatar (body, shoes, head props, hair cards and
/// tip glows: every Renderer under the avatar) is hidden with Renderer.forceRenderingOff and a SkinnedSplatAvatar draws
/// that dancer instead, posed every frame from the avatar's own bones (so playback, seeks, slow motion and the film
/// director's sub-frame poses all drive it). Both dancers sit in one GaussiansGroup: one global sort.
///
/// Display (VIEWER_SPEC 3.2): OPAQUE. The splats show at full opacity whatever the avatars' opacity says (only 0 =
/// hidden is honoured), they are hidden with the avatars layer, inside role-hidden spans and when the avatar is not
/// drawn. The 65 % / 75 % transparent look of the mesh avatars needs the renderer's Composite Texture path with a
/// composite that scales the accumulated alpha (and, for "transparent as displayed", a per-pixel alpha from the
/// composited luminance like DisplayTransparency's AlphaLut); OpacityMode.ScaleSplats is a crude review stand-in
/// (scales every Gaussian's alpha, so the inside and the back of the body show through).
///
/// The renderer's URP feature is added to the URP renderer at run time only while splat dancers are on
/// (SplatUrpFeature); nothing in the project's render or XR settings changes. Editor, Windows and Android (the Quest).
/// On the Quest (a player build for Android) the toggle is ON by default (the owner's choice, 2026-10-20): a lesson whose
/// capture has splats/ (the class recap) shows the splat dancers, every other lesson its mesh avatars; the tier is data
/// (quest_settings.json "splats", "splat_max", "splat_max_sigma", "splat_path", "splat_depth"; -hmsplats on|off and the
/// other switches, VrSettings), and the Gaussians are thinned at load to the Quest default (QuestDefaultMax /
/// QuestDefaultSigma: SplatAvatarData.Reduce) so the lesson holds 72 Hz. GLES (no compute path in the renderer) keeps the
/// avatars. The PC default stays OFF. Hair: when the splat file carries a hair binding
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

    /// <summary>the "splat dancers" toggle (default off)</summary>
    public static bool Enabled => splatsOn;

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
        public int ScannedHierarchy = -1;
        public float NextScan;
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

    /// <summary>Editor: the toggle survives Play-mode restarts (the film recorder re-enters Play mode) for the rest of the
    /// editor session (SessionState, never saved to the project); a new editor session starts with it off.</summary>
    const string SessionKey = "HM.SplatDancers.On";

    /// <summary>a player build for the Quest (Android): splats on by default, thinned to the Quest budget</summary>
    public static readonly bool QuestPlayer =
#if UNITY_ANDROID && !UNITY_EDITOR
        true;
#else
        false;
#endif

    /// <summary>the Quest's Gaussians per dancer (load-time thinning, SplatAvatarData.Reduce; 0 = all), measured on a Quest 3
    /// (docs/SPLAT_DANCERS.md "Quest"); quest_settings.json "splat_max" / -hmsplatmax override it</summary>
    public const int QuestDefaultMax = 0;

    /// <summary>the Quest's clamp of every Gaussian's standard deviation in metres (0 = none); "splat_max_sigma" / -hmsplatsigma</summary>
    public const float QuestDefaultSigma = 0f;

    /// <summary>the Quest's render path and splat depth (see Paths; "splat_path" / "splat_depth")</summary>
    public const Paths QuestDefaultPath = Paths.Composite;
    public const bool QuestDefaultDepth = true;

    /// <summary>what the Quest tier applied at the last capture load (State, logcat)</summary>
    public static string QuestTier { get; private set; } = "";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
#if UNITY_EDITOR
        splatsOn = UnityEditor.SessionState.GetBool(SessionKey, false);
#else
        splatsOn = QuestPlayer; // the Quest: on by default (VrSettings "splats" / -hmsplats off turn it off at the capture load)
#endif
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
        if (QuestPlayer) ApplyQuestTier(viewer);
        if (Instance != null) Instance.Rebuild(viewer);
        else if (splatsOn) Ensure().Rebuild(viewer);
        if (!QuestPlayer)
        {
            if (splatsOn) SplatUrpFeature.Ensure();
        }
        else if (!splatsOn || Instance == null || !Instance.HasSplatData) SplatUrpFeature.Remove(); // a Quest lesson without splat data renders as before
        // (on the Quest the Gaussians feature is added when the first splat dancer is built: Build)
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

    /// <summary>the Quest tier at each capture load (VrSettings: quest_settings.json + the command line): on / off, thinning,
    /// render path, splat depth; logged as HM-SPLATS</summary>
    static void ApplyQuestTier(HeadMovement viewer)
    {
        HmVr.VrSettings st = HmVr.VrSettings.Current;
        bool on = st.Splats;
        bool gles = SystemInfo.graphicsDeviceType is UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 or UnityEngine.Rendering.GraphicsDeviceType.OpenGLCore;
        if (gles || !SystemInfo.supportsComputeShaders) on = false; // the renderer has no OpenGL path: the avatars stay
        splatsOn = on;
        reduceMax = st.SplatMax > 0 ? st.SplatMax : st.SplatMax < 0 ? 0 : QuestDefaultMax;
        reduceSigma = st.SplatMaxSigma > 0f ? st.SplatMaxSigma : st.SplatMaxSigma < 0f ? 0f : QuestDefaultSigma;
        renderPath = st.SplatPath == "direct" ? Paths.Direct : st.SplatPath == "composite" ? Paths.Composite : QuestDefaultPath;
        questDepth = st.SplatDepth == "off" ? false : st.SplatDepth == "on" ? true : QuestDefaultDepth;
        // the splat depth BEFORE the transparents is for the composite path (overlays depth-test against the dancers); on the direct
        // path the splats are transparents themselves and would test against their own depth: the package's order (after) then
        SplatUrpFeature.DepthBeforeTransparents = renderPath == Paths.Composite;
        QuestTier = $"splats {(on ? "on" : "off")}{(gles ? " (GLES: no compute path, avatars)" : "")}, max {(reduceMax > 0 ? reduceMax.ToString() : "all")} per dancer, " +
                    $"sigma clamp {(reduceSigma > 0f ? $"{reduceSigma * 100f:0.##} cm" : "none")}, path {renderPath}, depth {(questDepth ? "on" : "off")}, api {SystemInfo.graphicsDeviceType}, settings {st.Source}";
        Debug.Log($"HM-SPLATS tier: {QuestTier} (capture {Path.GetFileName(viewer.Manifest?.Folder ?? "?")})");
    }

    static SplatDancers Ensure()
    {
        if (Instance != null) return Instance;
        GameObject go = new("Splat Dancers");
        Instance = go.AddComponent<SplatDancers>();
        return Instance;
    }

    /// <summary>turn the splat dancers on or off (on: loads the capture's splat avatars if it has any)</summary>
    public static void SetEnabled(bool on)
    {
        splatsOn = on;
#if UNITY_EDITOR
        UnityEditor.SessionState.SetBool(SessionKey, on);
#endif
        if (on)
        {
            SplatDancers s = Ensure();
            if (HeadMovement.Instance != null && (s.entries.Count == 0 || s.hm != HeadMovement.Instance)) s.Rebuild(HeadMovement.Instance);
            SplatUrpFeature.Ensure();
        }
        else if (Instance != null)
        {
            Instance.ApplyVisibility();
            Instance.LinesBackOn();
            SplatUrpFeature.OverSplats.Clear();
            SplatUrpFeature.OverSplatsInstanced.Clear();
            SplatUrpFeature.Remove();
        }
    }

    void Awake()
    {
        // the Editor's AssetDatabase, or (a player: the Quest) the Resources shader set
        SplatShaderSet shaders = SplatShaderSet.Get();
        compute = shaders != null ? shaders.skin : null;
        if (shaders == null || !shaders.Complete) Debug.LogError($"splat dancers: shader set incomplete (missing {(shaders != null ? shaders.Missing() : "the asset")}): {SplatShaderSet.AssetPath}");
        root = gameObject;
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
        if (QuestPlayer) group.m_WriteDepth = questDepth; // set once here, never toggled at run time (see FilmVideoFade)
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
            string json = SplatAvatarData.JsonPath(capture, kv.Key == Role.Lead ? "lead" : "follow");
            Entry e = new() { Role = kv.Key, Avatar = kv.Value, JsonPath = json };
            if (json != null && (splatsOn || !QuestPlayer))
            {
                string path = json;
                int max = QuestPlayer ? reduceMax : 0;
                float sigma = QuestPlayer ? reduceSigma : 0f;
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
    }

    void Clear()
    {
        fellBack = false;
        badSince = -1f;
        LinesBackOn();
        beadDraws.Clear();
        foreach (Entry e in entries)
        {
            Unhide(e);
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
                if (t.IsFaulted) e.Error = t.Exception?.GetBaseException().Message;
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
            Debug.Log($"{(QuestPlayer ? "HM-SPLATS " : "")}splat dancers: {e.Role} {data.Count} Gaussians ({data.HairCount} hair, {data.BoundHairCount} bound) from {data.SourcePath}" +
                      $" in {e.LoadSeconds:0.0} s{(e.Reduction != null ? "; thinned: " + e.Reduction : "")}");
            if (QuestPlayer) SplatUrpFeature.Ensure();
        }
        catch (Exception ex)
        {
            e.Error = ex.Message;
            Debug.LogError($"splat dancers: {e.Role}: {ex}");
        }
    }

    void LateUpdate()
    {
        GaussianSplat3DRenderer.RenderPath path = renderPath == Paths.Direct ? GaussianSplat3DRenderer.RenderPath.DirectTransparent : GaussianSplat3DRenderer.RenderPath.CompositeTexture;
        if (group.m_RenderPath != path) group.m_RenderPath = path;
        float fade = CompositeOpacity();
        DisplayOpacity = fade;
        Shader.SetGlobalFloat(OpacityId, fade);
        ApplyVisibility();
        FillOverSplats();
        if (QuestPlayer && splatsOn && entries.Count > 0)
        {
            Watchdog();
            if (Time.unscaledTime >= nextStatusLog) LogStatus();
        }
    }

    bool fellBack;
    float badSince = -1f;

    /// <summary>the Quest's safety net (an untested GPU path must not leave the lesson without dancers): when the renderer reports
    /// that it cannot draw a shown splat dancer (an unsupported shader or kernel, resources that never become ready) for 3 s, the
    /// mesh avatars come back for the rest of this capture and logcat says why (HM-SPLATS FALLBACK)</summary>
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
        Debug.LogError($"HM-SPLATS FALLBACK: the splat dancers cannot be drawn on this device ({reason}); the mesh avatars are shown instead");
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

        Debug.Log($"HM-SPLATS status: {(fellBack ? "FELL BACK to the avatars, " : "")}feature {(SplatUrpFeature.Injected ? "in" : "NOT in")} the URP renderer, depth patch '{SplatUrpFeature.DepthPatch}', path {renderPath}, " +
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
    public static float FilmVideoFade()
    {
        FilmDirector fd = FilmDirector.Instance;
        if (fd == null || fd.SegmentIndex < 0) return 1f;
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

    /// <summary>splats on where the avatar would be drawn; the avatar's renderers off while its splats show</summary>
    void ApplyVisibility()
    {
        foreach (Entry e in entries)
        {
            if (e.Avatar == null)
            {
                if (e.Splat != null) e.Splat.gameObject.SetActive(false);
                continue;
            }

            bool show = splatsOn && !fellBack && e.Splat != null && e.Avatar.Visible;
            if (e.Splat != null)
            {
                if (e.Splat.gameObject.activeSelf != show) e.Splat.gameObject.SetActive(show);
                e.Splat.HairMode = hairMode;
                e.Splat.HairMotion = hairMotion;
                e.Splat.OpacityScale = opacityMode == OpacityModes.ScaleSplats ? Mathf.Clamp01(e.Avatar.Opacity) : 1f;
            }

            if (show) Hide(e);
            else Unhide(e);
        }
    }

    void Hide(Entry e)
    {
        Transform t = e.Avatar.transform;
        if (t.hierarchyCount != e.ScannedHierarchy || Time.unscaledTime >= e.NextScan)
        {
            e.ScannedHierarchy = t.hierarchyCount;
            e.NextScan = Time.unscaledTime + 0.5f;
            foreach (Renderer r in e.Avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (!e.Hidden.Contains(r)) e.Hidden.Add(r);
            }
        }

        for (int i = e.Hidden.Count - 1; i >= 0; i--)
        {
            if (e.Hidden[i] == null) e.Hidden.RemoveAt(i);
            else e.Hidden[i].forceRenderingOff = true;
        }
    }

    static void Unhide(Entry e)
    {
        foreach (Renderer r in e.Hidden)
        {
            if (r != null) r.forceRenderingOff = false;
        }

        e.Hidden.Clear();
        e.ScannedHierarchy = -1;
    }

    void OnDisable()
    {
        SplatUrpFeature.OverSplats.Clear();
        SplatUrpFeature.OverSplatsInstanced.Clear();
        LinesBackOn();
        foreach (Entry e in entries) Unhide(e);
        SplatUrpFeature.Remove();
        Shader.SetGlobalFloat(OpacityId, 1f);
    }

    void OnDestroy()
    {
        Clear();
        if (Instance == this) Instance = null;
    }

    /// <summary>state for the CLI / playtests</summary>
    public Dictionary<string, object> State()
    {
        Dictionary<string, object> s = new()
        {
            ["enabled"] = splatsOn,
            ["capture"] = capture != null ? Path.GetFileName(capture) : null,
            ["opacityMode"] = opacityMode.ToString(),
            ["displayOpacity"] = DisplayOpacity,
            ["renderPath"] = renderPath.ToString(),
            ["questTier"] = QuestTier,
            ["fellBackToAvatars"] = fellBack,
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
