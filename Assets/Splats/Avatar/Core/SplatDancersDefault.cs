using UnityEngine.Rendering;

namespace HmSplats
{
    /// <summary>
    /// Whether the splat dancers are on when a capture loads (VIEWER_SPEC 3.2a, docs/SPLAT_DANCERS.md "Default per capture"). Pure: no
    /// Unity state is read here; SplatDancers gathers the inputs at HeadMovement.AvatarsLoaded and applies the decision.
    ///
    ///   Editor          the toggle set by hand this editor session (hm_splatdancers --action on | off) wins, as before; otherwise a
    ///                   capture whose capture.json asks for it (splat_dancers "on") and has splat data shows its splat dancers when the
    ///                   device can draw them (D3D12 / Vulkan / Metal with compute shaders); a refusal is reported once (Refused); every
    ///                   other capture: off (the desktop default).
    ///   Windows player  the tier's "splats" key / -hmsplats switch when given; otherwise on in VR, and on the desktop for a flagged
    ///                   capture with splat data; never without a drawing path (D3D11, GLES, no compute).
    ///   Quest           unchanged: the tier's "splats" (on unless given off), never without a drawing path.
    /// </summary>
    public static class SplatDancersDefault
    {
        public enum Host { Editor, WindowsPlayer, Quest }

        /// <summary>what the decision depends on (one capture load)</summary>
        public struct Inputs
        {
            public Host Host;

            /// <summary>Editor: the toggle was set by hand this editor session (hm_splatdancers --action on | off), and its value</summary>
            public bool HandSet, HandOn;

            /// <summary>a player: the tier's "splats" key / -hmsplats switch was given, and the tier's value (the Quest: on unless given off)</summary>
            public bool SettingsGiven, SettingsOn;

            /// <summary>a player: the viewer is in VR (a headset through OpenXR, or -hmvr sim)</summary>
            public bool Vr;

            /// <summary>the capture's capture.json asks for its splat dancers by default (splat_dancers "on")</summary>
            public bool CaptureFlag;

            /// <summary>the capture has a splat avatar (splats/&lt;role&gt;.json) for at least one dancer</summary>
            public bool HasSplatData;

            /// <summary>the reason the device cannot draw splats ("" = it can: NoPath)</summary>
            public string NoPath;
        }

        public readonly struct Decision
        {
            /// <summary>the splat dancers are on for this capture</summary>
            public readonly bool On;

            /// <summary>the capture's flag (or the player tier) asked for the splats but the device cannot draw them (log it once)</summary>
            public readonly bool Refused;

            /// <summary>why, in words (the CLI state, the log; the Editor's refusal names the missing path, a player's tier line appends it)</summary>
            public readonly string Why;

            public Decision(bool on, bool refused, string why)
            {
                On = on;
                Refused = refused;
                Why = why;
            }

            public override string ToString() => $"{(On ? "on" : "off")}{(Refused ? " (refused)" : "")}: {Why}";
        }

        /// <summary>"" when the renderer can draw splats with this device (D3D12 / Vulkan / Metal with compute shaders), else why not</summary>
        public static string NoPath(GraphicsDeviceType api, bool computeShaders, Host host)
        {
            if (api is GraphicsDeviceType.OpenGLES3 or GraphicsDeviceType.OpenGLCore) return "GLES: no compute path, avatars";
            if (api == GraphicsDeviceType.Direct3D11)
                return $"D3D11: the renderer needs D3D12 - start the {(host == Host.Editor ? "editor" : "exe")} with -force-d3d12; avatars";
            if (!computeShaders) return "no compute shaders: avatars";
            if (api is not (GraphicsDeviceType.Direct3D12 or GraphicsDeviceType.Vulkan or GraphicsDeviceType.Metal))
                return $"{api}: no splat path in the renderer, avatars";
            return "";
        }

        public static Decision Decide(in Inputs i)
        {
            bool canDraw = string.IsNullOrEmpty(i.NoPath);
            bool flagged = i.CaptureFlag && i.HasSplatData;
            switch (i.Host)
            {
                case Host.Editor:
                    if (i.HandSet) return new Decision(i.HandOn, false, $"set by hand this editor session ({(i.HandOn ? "on" : "off")})");
                    if (i.CaptureFlag && !i.HasSplatData) return new Decision(false, false, "capture.json asks for splat dancers but the capture has no splats/: avatars");
                    if (!flagged) return new Decision(false, false, "viewer default (off on the desktop)");
                    return canDraw
                        ? new Decision(true, false, "capture default (capture.json splat_dancers on)")
                        : new Decision(false, true, $"capture default refused: capture.json asks for splat dancers but {i.NoPath}");
                case Host.WindowsPlayer:
                {
                    bool want = i.SettingsGiven ? i.SettingsOn : i.Vr || flagged;
                    string why = i.SettingsGiven ? "given"
                        : i.Vr ? "pc default: on in VR, off on the desktop"
                        : flagged ? "capture default on the desktop (capture.json splat_dancers on)"
                        : "pc default: on in VR, off on the desktop";
                    // a player's tier line appends NoPath itself (HM-SPLATS tier: ...)
                    if (!want) return new Decision(false, false, why);
                    return new Decision(canDraw, !canDraw, why);
                }
                default:
                    if (!i.SettingsOn) return new Decision(false, false, "quest");
                    return new Decision(canDraw, !canDraw, "quest");
            }
        }
    }
}
