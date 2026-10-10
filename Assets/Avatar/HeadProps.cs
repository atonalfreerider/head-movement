using System;
using System.Collections.Generic;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

/// <summary>
/// Head-worn props of an avatar (VIEWER_SPEC 3.2b): the teacher's performance headset. Data driven: capture.json <c>props</c> lists
/// { role, kind, ...parameters } per ROLE (HeadPropSpec; the take TOML's [dancers.&lt;role&gt;.props.&lt;kind&gt;] through the exporter),
/// built when the capture's avatars exist (HeadMovement.AvatarsLoaded), no external art: one procedural mesh (ear hook, boom, capsule)
/// as a rigid child of the HEAD bone, laid on the avatar's actual head mesh (HeadPropGeometry: after the take's face-shape edit,
/// whatever the head looks like) so it follows every head pose and never floats or sinks.
///
/// It shares the avatar's materials (SmplxAvatar.Attach: the translucent depth + colour pair, so the avatar's opacity, view-state
/// fade, role_hidden span fades, layer switch, depth sorting between the two dancers and the skeleton stencil all apply), with a small
/// opacity boost like the hair's so a thin prop reads at the translucent default. Skeleton / glow overlays never see it (it is a
/// renderer on the avatar, not on the Dancer). It draws in the free viewer, the directed film and the recorder, which all show the
/// same avatars. Stateless at runtime: nothing is simulated.
/// </summary>
public class HeadProps : MonoBehaviour
{
    static readonly List<HeadProps> all = new();
    public static IReadOnlyList<HeadProps> All => all;

    /// <summary>hair-like ramp: displayed opacity + boost x smoothstep(0, 0.2, opacity)</summary>
    public const float OpacityRamp = 0.2f;

    public SmplxAvatar Avatar { get; private set; }
    public HeadPropSpec Spec { get; private set; }
    public HeadPropGeometry.HeadsetBuild Build { get; private set; }
    public string Warning { get; private set; }
    /// <summary>main-thread cost of the build: skin read + head surface + path + mesh (ms)</summary>
    public double BuildMs { get; private set; }
    public int Triangles { get; private set; }

    /// <summary>CLI / review switch; the avatar's own visibility still applies</summary>
    public bool Visible
    {
        get => visible;
        set
        {
            visible = value;
            if (Avatar != null) OnAppearance(Avatar);
        }
    }

    bool visible = true;
    MeshRenderer meshRenderer;
    Mesh mesh;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Hook()
    {
        HeadMovement.AvatarsLoaded -= OnAvatarsLoaded;
        HeadMovement.AvatarsLoaded += OnAvatarsLoaded;
    }

    /// <summary>the props of a freshly loaded capture (capture.json props; none declared = nothing built)</summary>
    static void OnAvatarsLoaded(HeadMovement hm)
    {
        Newtonsoft.Json.Linq.JArray props = hm.Manifest?.props;
        if (props == null) return;
        foreach (Newtonsoft.Json.Linq.JToken entry in props)
        {
            HeadPropSpec spec = HeadPropSpec.FromJson(entry, out string error);
            if (spec == null)
            {
                Debug.LogWarning($"{hm.Manifest?.DisplayName}: {error}");
                continue;
            }

            if (!Enum.TryParse(spec.Role, true, out Role role) || !hm.Avatars.TryGetValue(role, out SmplxAvatar avatar) || avatar == null) continue;
            try
            {
                Create(avatar, spec);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{hm.Manifest?.DisplayName}: {spec.Role} {spec.Kind} failed: {e.Message}");
            }
        }
    }

    /// <summary>build one prop on an avatar (an earlier prop of the same kind on it is replaced); null when the head has no landmarks</summary>
    public static HeadProps Create(SmplxAvatar avatar, HeadPropSpec spec)
    {
        if (avatar == null) throw new ArgumentNullException(nameof(avatar));
        if (spec == null) throw new ArgumentNullException(nameof(spec));
        Stopwatch sw = Stopwatch.StartNew();
        foreach (HeadProps old in avatar.GetComponentsInChildren<HeadProps>(true))
        {
            if (old.Spec == null || old.Spec.Kind != spec.Kind) continue;
            all.Remove(old);
            avatar.AppearanceChanged -= old.OnAppearance;
            old.gameObject.SetActive(false); // Destroy runs at the end of the frame
            Destroy(old.gameObject);
        }

        // the rest head of the skin the avatar was built from (after the face-shape edit): head-joint-local metres
        SmplxData.Skin skin = SmplxData.ReadSkin(avatar.SkinPath);
        HeadPropGeometry.HeadSurface surface = HeadPropGeometry.HeadSurface.Build(skin, (int)SmplJoint.Head);
        HeadPropGeometry.HeadsetBuild build = HeadPropGeometry.BuildHeadset(surface, spec);
        if (build == null)
        {
            Debug.LogWarning($"{avatar.DancerRole} {spec.Kind}: not the SMPL-X head topology - no prop");
            return null;
        }

        if (build.Mesh == null)
        {
            Debug.LogWarning($"{avatar.DancerRole} {spec.Kind}: {build.Warning}");
            return null;
        }

        GameObject go = new($"Prop {spec.Kind}") { layer = avatar.gameObject.layer };
        // the bones carry identity rotations at rest, so head-joint-local rest coordinates are the bone's own frame
        go.transform.SetParent(avatar.Bone((int)SmplJoint.Head), false);
        HeadProps p = go.AddComponent<HeadProps>();
        p.Avatar = avatar;
        p.Spec = spec;
        p.Build = build;
        p.Warning = build.Warning;
        p.mesh = build.Mesh.ToMesh($"{avatar.DancerRole} {spec.Kind}");
        p.Triangles = p.mesh.triangles.Length / 3;
        go.AddComponent<MeshFilter>().sharedMesh = p.mesh;
        p.meshRenderer = go.AddComponent<MeshRenderer>();
        p.meshRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        p.meshRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        Material colour = avatar.Attach(p.meshRenderer, Color.white, null, true, 0.85f);
        colour.SetFloat("_Specular", spec.Kind == HeadPropSpec.KindHeadset ? 0.12f : 0f);
        colour.SetFloat("_SpecPower", 40f);
        all.Add(p);
        avatar.AppearanceChanged += p.OnAppearance;
        p.OnAppearance(avatar);
        p.BuildMs = sw.Elapsed.TotalMilliseconds;
        return p;
    }

    /// <summary>the opacity the prop draws with for an avatar opacity: a + boost x smoothstep(0, 0.2, a), clamped (0 -> 0: it fades out with the avatar)</summary>
    public static float OpacityFor(float avatarOpacity, float boost) =>
        Mathf.Clamp01(avatarOpacity + boost * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(avatarOpacity / OpacityRamp)));

    /// <summary>runs after SmplxAvatar.ApplyAppearance has set the attachment's materials, visibility and shadows: the prop's own
    /// opacity (boosted) and its review switch go on top</summary>
    void OnAppearance(SmplxAvatar a)
    {
        if (meshRenderer == null || a == null) return;
        float o = OpacityFor(a.Opacity, Spec.OpacityBoost);
        float blend = SmplxAvatar.AlphaMode == SmplxAvatar.AlphaModes.Raw ? -1f : a.Display != null ? a.Display.BlendAlpha(o) : o;
        foreach (Material m in meshRenderer.sharedMaterials)
        {
            if (m == null) continue;
            m.SetFloat("_Opacity", o);
            m.SetFloat("_BlendAlpha", blend);
        }

        meshRenderer.enabled = visible && a.Visible && o > 0.005f;
    }

    void OnDestroy()
    {
        all.Remove(this);
        if (Avatar != null) Avatar.AppearanceChanged -= OnAppearance;
        if (Avatar != null && meshRenderer != null) Avatar.Detach(meshRenderer);
        if (mesh != null) Destroy(mesh);
    }

    /// <summary>numbers for the CLI / playtests (hm_prop)</summary>
    public Dictionary<string, object> State()
    {
        HeadPropGeometry.HeadsetBuild b = Build;
        Dictionary<string, object> d = new()
        {
            ["role"] = Spec.Role, ["kind"] = Spec.Kind, ["side"] = Spec.Right ? "right" : "left", ["visible"] = visible,
            ["drawn"] = meshRenderer != null && meshRenderer.enabled, ["triangles"] = Triangles, ["buildMs"] = BuildMs,
            ["spec"] = Spec.ToString(), ["warning"] = Warning, ["opacityBoost"] = Spec.OpacityBoost,
            ["avatarOpacity"] = Avatar != null ? Avatar.Opacity : 0f
        };
        if (b != null)
        {
            d["boomLengthMm"] = Mathf.Round(b.BoomLengthM * 10000f) / 10f;
            d["boomClearanceMm"] = new[] { Mathf.Round(b.MinBoomClearanceM * 10000f) / 10f, Mathf.Round(b.MaxBoomClearanceM * 10000f) / 10f };
            d["hookClearanceMinMm"] = float.IsNaN(b.MinHookClearanceM) ? (object)null : Mathf.Round(b.MinHookClearanceM * 10000f) / 10f;
            d["capsuleClearanceMinMm"] = Mathf.Round(b.MinCapsuleClearanceM * 10000f) / 10f;
            d["capsuleCentreHeadLocal"] = new[] { b.CapsuleCentre.x, b.CapsuleCentre.y, b.CapsuleCentre.z };
            d["mouthCornerHeadLocal"] = new[] { b.MouthCorner.x, b.MouthCorner.y, b.MouthCorner.z };
            d["earFrontHeadLocal"] = new[] { b.EarFront.x, b.EarFront.y, b.EarFront.z };
            d["boomPoints"] = b.Boom.Length;
        }

        return d;
    }
}
