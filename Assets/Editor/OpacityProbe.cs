using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

/// <summary>
/// hm_opacity --probe: the avatar transparency AS DISPLAYED (VIEWER_SPEC 3.2: "65 % transparent"). One body alone
/// (partner, hair, skeletons, contact lines and every overlay layer hidden), the view camera parked 3 m in front of it
/// (25 deg off its chest's facing), rendered off-screen twice per opacity over two known backgrounds - black and grey
/// (camera clear colour) - and twice without the body:
///   display  the Game-view pipeline: LDR sRGB 8-bit target with the camera's post-processing ON (what the screen shows)
///   linear   HDR half-float target, post-processing OFF (linear light, the blend itself)
/// Body pixels = where the OPAQUE body differs from both backgrounds, eroded 2 px (no MSAA edges). Per pixel:
///   transparency (difference matte) = (C_grey - C_black) / (B_grey - B_black): the share of a background change that
///     comes through one body layer - the measure of "x % transparent";
///   dimming over black = 1 - (C_black - B_black) / (O_black - B_black): how much darker than the opaque body it shows
///     over the black stage.
/// Reported as median / p10 / p90 / mean per opacity. With includeHair (the follow) the same matte is taken over her
/// HAIR pixels at the hair opacity that follows each avatar opacity (avatar + 0.15, HairStrands.HairOpacityFor): hair
/// pixels = where the opaque hair changes the render of the opaque body (the hair's depth prepass hides the body
/// behind it, so each hair pixel is one hair layer over the background). Everything it touched is restored.
/// </summary>
public static class OpacityProbe
{
    static readonly string[] Layers = { "floor", "grid", "axis", "floorcraft", "tension", "timing", "physics", "counterbalance", "traces", "neck", "graph", "splats", "room", "cameras", "hud" };

    public static Dictionary<string, object> Measure(HeadMovement hm, Role role, float[] opacities, int w = 640, int h = 640,
        bool includeHair = false)
    {
        if (!hm.Avatars.TryGetValue(role, out SmplxAvatar body) || body == null) throw new InvalidOperationException($"no {role} avatar");
        Role other = role == Role.Lead ? Role.Follow : Role.Lead;
        hm.Avatars.TryGetValue(other, out SmplxAvatar partner);
        Camera cam = DanceText.ViewCamera;
        if (cam == null) throw new InvalidOperationException("no view camera");

        Dictionary<string, bool> keepLayers = Layers.ToDictionary(l => l, hm.LayerVisible);
        float keepOpacity = hm.AvatarOpacity;
        HairStrands hair = HairStrands.Active;
        bool keepHair = hair != null && hair.Visible;
        float keepHairOverride = hair != null ? hair.OpacityOverride : -1f;
        List<Renderer> hidden = new();
        foreach (Dancer d in new[] { hm.LeadDancer, hm.FollowDancer })
        {
            if (d == null) continue;
            foreach (Renderer r in d.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled) continue;
                r.enabled = false;
                hidden.Add(r);
            }
        }

        foreach (LineRenderer r in hm.GetComponentsInChildren<LineRenderer>(true))
        {
            if (!r.enabled) continue;
            r.enabled = false;
            hidden.Add(r);
        }

        Transform t = cam.transform;
        Vector3 keepPos = t.position;
        Quaternion keepRot = t.rotation;
        CameraClearFlags keepClear = cam.clearFlags;
        Color keepBg = cam.backgroundColor;
        bool keepHdr = cam.allowHDR;
        float keepFov = cam.fieldOfView;
        RenderTexture keepTarget = cam.targetTexture;
        UniversalAdditionalCameraData camData = cam.GetUniversalAdditionalCameraData();
        bool keepPost = camData != null && camData.renderPostProcessing;
        int msaa = GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp ? Mathf.Max(1, urp.msaaSampleCount) : 1;
        RenderTexture rtDisplay = new(new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGB32, 24) { sRGB = true, msaaSamples = msaa });
        RenderTexture rtLinear = new(new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGBHalf, 24) { sRGB = false, msaaSamples = msaa });
        Texture2D texDisplay = new(w, h, TextureFormat.RGBA32, false, false);
        Texture2D texLinear = new(w, h, TextureFormat.RGBAHalf, false, true);
        Color grey = new(0.75f, 0.75f, 0.75f, 1f);

        Vector3 pelvis = body.BonePosition(0), head = body.BonePosition(15);
        Vector3 fwd = body.Bone(9).rotation * Vector3.back; // SMPL-X bodies face -z in bone space
        fwd.y = 0f;
        fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
        Vector3 look = new(pelvis.x, 0.5f * (pelvis.y + head.y) - 0.15f, pelvis.z);
        Vector3 eye = look + Quaternion.AngleAxis(25f, Vector3.up) * fwd * 3f + Vector3.up * 0.25f;

        float[] Grab(bool display, Color background)
        {
            t.position = eye;
            t.LookAt(look);
            cam.fieldOfView = 40f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = background;
            if (camData != null) camData.renderPostProcessing = display && keepPost;
            cam.allowHDR = display ? keepHdr : true;
            RenderTexture rt = display ? rtDisplay : rtLinear;
            Texture2D tex = display ? texDisplay : texLinear;
            if (hair != null) hair.Refresh();
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = keepTarget;
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            tex.Apply(false);
            RenderTexture.active = prev;
            float[] v = new float[w * h];
            if (display)
            {
                Color32[] px = tex.GetPixels32(); // the stored sRGB bytes = the displayed values
                for (int i = 0; i < px.Length; i++) v[i] = (px[i].r + px[i].g + px[i].b) / (3f * 255f);
            }
            else
            {
                Color[] px = tex.GetPixels();
                for (int i = 0; i < px.Length; i++) v[i] = (px[i].r + px[i].g + px[i].b) / 3f;
            }

            return v;
        }

        static float Q(List<float> l, float q) => l.Count == 0 ? float.NaN : l[Mathf.Clamp((int)(q * (l.Count - 1)), 0, l.Count - 1)];

        Dictionary<string, object> result = new()
        {
            ["role"] = role.ToString().ToLowerInvariant(), ["msaa"] = msaa, ["post"] = keepPost, ["hdr"] = keepHdr,
            ["colorSpace"] = QualitySettings.activeColorSpace.ToString(), ["backgrounds"] = "black / grey 0.75 (camera clear)"
        };
        try
        {
            foreach (string l in Layers)
            {
                try
                {
                    hm.SetLayerVisible(l, false);
                }
                catch (ArgumentException)
                {
                }
            }

            if (hair != null) hair.Visible = false;
            if (partner != null) partner.SetVisible(false);
            foreach (bool display in new[] { true, false })
            {
                body.SetVisible(false);
                float[] bB = Grab(display, Color.black), bG = Grab(display, grey);
                body.SetVisible(true);
                hm.SetAvatarOpacity(1f);
                float[] oB = Grab(display, Color.black), oG = Grab(display, grey);
                bool[] m = new bool[w * h];
                for (int i = 0; i < m.Length; i++) m[i] = Mathf.Abs(oB[i] - bB[i]) > 0.01f && Mathf.Abs(oG[i] - bG[i]) > 0.01f && bG[i] - bB[i] > 0.3f;
                for (int pass = 0; pass < 2; pass++)
                {
                    bool[] e = new bool[m.Length];
                    for (int y = 1; y < h - 1; y++)
                    for (int x = 1; x < w - 1; x++)
                    {
                        int i = y * w + x;
                        e[i] = m[i] && m[i - 1] && m[i + 1] && m[i - w] && m[i + w];
                    }

                    m = e;
                }

                List<int> idx = Enumerable.Range(0, m.Length).Where(i => m[i]).ToList();
                Dictionary<string, object> per = new();
                foreach (float o in opacities)
                {
                    hm.SetAvatarOpacity(o);
                    float[] cB = Grab(display, Color.black), cG = Grab(display, grey);
                    List<float> matte = new(), dim = new();
                    foreach (int i in idx)
                    {
                        matte.Add((cG[i] - cB[i]) / Mathf.Max(1e-4f, bG[i] - bB[i]));
                        if (oB[i] - bB[i] > 0.05f) dim.Add(1f - (cB[i] - bB[i]) / (oB[i] - bB[i]));
                    }

                    matte.Sort();
                    dim.Sort();
                    per[o.ToString("0.###", CultureInfo.InvariantCulture)] = new Dictionary<string, object>
                    {
                        ["blendAlpha"] = body.BlendAlpha, ["predicted"] = body.PredictedTransparency, ["transparency"] = Q(matte, 0.5f), ["transparencyP10"] = Q(matte, 0.1f), ["transparencyP90"] = Q(matte, 0.9f),
                        ["transparencyMean"] = matte.Count > 0 ? matte.Average() : float.NaN,
                        ["dimOverBlack"] = Q(dim, 0.5f), ["dimOverBlackP10"] = Q(dim, 0.1f), ["dimOverBlackP90"] = Q(dim, 0.9f)
                    };
                }

                Dictionary<string, object> res = new()
                {
                    ["bodyPixels"] = idx.Count, ["backgroundBlack"] = idx.Count > 0 ? idx.Average(i => bB[i]) : 0f,
                    ["backgroundGrey"] = idx.Count > 0 ? idx.Average(i => bG[i]) : 0f,
                    ["opaqueBodyOverBlack"] = idx.Count > 0 ? idx.Average(i => oB[i]) : 0f, ["byOpacity"] = per
                };
                result[display ? "display" : "linear"] = res;
                if (!includeHair || hair == null || role != Role.Follow) continue;

                // her hair: mask = where the opaque hair changes the opaque body's render (over both backgrounds)
                hm.SetAvatarOpacity(1f);
                hair.Visible = true;
                hair.OpacityOverride = 1f;
                float[] hB = Grab(display, Color.black), hG = Grab(display, grey);
                hair.OpacityOverride = -1f;
                bool[] hm2 = new bool[w * h];
                for (int i = 0; i < hm2.Length; i++) hm2[i] = Mathf.Abs(hB[i] - oB[i]) > 0.01f && Mathf.Abs(hG[i] - oG[i]) > 0.01f && bG[i] - bB[i] > 0.3f;
                List<int> hidx = Enumerable.Range(0, hm2.Length).Where(i => hm2[i]).ToList();
                Dictionary<string, object> hper = new();
                foreach (float o in opacities)
                {
                    hm.SetAvatarOpacity(o);
                    float[] cB = Grab(display, Color.black), cG = Grab(display, grey);
                    List<float> matte = hidx.Select(i => (cG[i] - cB[i]) / Mathf.Max(1e-4f, bG[i] - bB[i])).OrderBy(x => x).ToList();
                    hper[o.ToString("0.###", CultureInfo.InvariantCulture)] = new Dictionary<string, object>
                    {
                        ["hairOpacity"] = HairStrands.HairOpacityFor(o), ["transparency"] = Q(matte, 0.5f),
                        ["transparencyP10"] = Q(matte, 0.1f), ["transparencyP90"] = Q(matte, 0.9f)
                    };
                }

                hair.Visible = false;
                res["hairPixels"] = hidx.Count;
                res["hairByAvatarOpacity"] = hper;
            }
        }
        finally
        {
            hm.SetAvatarOpacity(keepOpacity);
            body.SetVisible(hm.LayerVisible("avatars"));
            if (partner != null) partner.SetVisible(hm.LayerVisible("avatars"));
            if (hair != null)
            {
                hair.Visible = keepHair;
                hair.OpacityOverride = keepHairOverride;
            }

            foreach (KeyValuePair<string, bool> kv in keepLayers)
            {
                try
                {
                    hm.SetLayerVisible(kv.Key, kv.Value);
                }
                catch (ArgumentException)
                {
                }
            }

            foreach (Renderer r in hidden)
            {
                if (r != null) r.enabled = true;
            }

            t.SetPositionAndRotation(keepPos, keepRot);
            cam.clearFlags = keepClear;
            cam.backgroundColor = keepBg;
            cam.allowHDR = keepHdr;
            cam.fieldOfView = keepFov;
            cam.targetTexture = keepTarget;
            if (camData != null) camData.renderPostProcessing = keepPost;
            rtDisplay.Release();
            rtLinear.Release();
            Object.DestroyImmediate(rtDisplay);
            Object.DestroyImmediate(rtLinear);
            Object.DestroyImmediate(texDisplay);
            Object.DestroyImmediate(texLinear);
        }

        return result;
    }
}
