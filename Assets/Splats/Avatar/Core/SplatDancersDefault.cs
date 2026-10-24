using UnityEngine.Rendering;

namespace HmSplats
{
    /// <summary>
    /// How the dancers of a capture are drawn (VIEWER_SPEC 3.2a, docs/SPLAT_DANCERS.md "Splats always"). Pure: no Unity state is read
    /// here; SplatDancers gathers the inputs at HeadMovement.AvatarsLoaded and applies the decision.
    ///
    /// The owner's rule (2026-10-23): "I don't ever want to see the basic avatars if gauss splat avatars are available for a dance.
    /// fully archive them when they are replaced with gauss splats."
    ///
    ///   a dancer whose capture has splat data for its role (splats/&lt;role&gt;.json)   ALWAYS its splats - in the Editor (every view, Play
    ///                                                                                 mode, the film recorder and director), a Windows
    ///                                                                                 player (desktop and VR) and the Quest; its mesh
    ///                                                                                 avatar is never drawn (it stays as an invisible rig:
    ///                                                                                 the splats, the hair simulation and physics ride on
    ///                                                                                 its bones)
    ///   ... on a device that cannot draw splats (D3D11, GLES, no compute; the players' 3-s rendering watchdog)   its SKELETON LINES,
    ///                                                                                 never the mesh avatar, and one log line / on-screen
    ///                                                                                 note (Note: "splat dancers need D3D12 ...")
    ///   a dancer without splat data                                                   its mesh avatar, as before
    ///
    /// Nothing switches a splat capture back to its mesh avatars: not hm_splatdancers --action off (refused), not a session's earlier
    /// hand toggle, not a player's "splats": false / -hmsplats off (ignored, logged), not the capture.json splat_dancers flag (read for the
    /// log only: every capture with splat data is drawn with it).
    /// </summary>
    public static class SplatDancersDefault
    {
        public enum Host { Editor, WindowsPlayer, Quest }

        /// <summary>how ONE dancer is drawn (DancerLook)</summary>
        public enum Look
        {
            /// <summary>its skinned Gaussian splats (the mesh avatar hidden under them)</summary>
            Splats,

            /// <summary>its skeleton lines only: it has splat data the device cannot draw (the mesh avatar is never the fallback)</summary>
            Skeleton,

            /// <summary>its mesh avatar: no splat data for this role</summary>
            MeshAvatar
        }

        /// <summary>what the decision depends on (one capture load)</summary>
        public struct Inputs
        {
            public Host Host;

            /// <summary>the capture has a splat avatar (splats/&lt;role&gt;.json) for at least one dancer</summary>
            public bool HasSplatData;

            /// <summary>the reason the device cannot draw splats ("" = it can: NoPath)</summary>
            public string NoPath;

            // ---- the switches of before 2026-10-23: they decide nothing any more (owner rule); read only to say they are ignored
            /// <summary>Editor: the toggle was set by hand this editor session (an old SessionState value), and its value</summary>
            public bool HandSet, HandOn;

            /// <summary>a player: the tier's "splats" key / -hmsplats switch was given, and its value</summary>
            public bool SettingsGiven, SettingsOn;

            /// <summary>a player: the viewer is in VR</summary>
            public bool Vr;

            /// <summary>the capture's capture.json splat_dancers "on" (the per-capture default of the morning of 2026-10-23)</summary>
            public bool CaptureFlag;
        }

        public readonly struct Decision
        {
            /// <summary>the splat dancers are drawn for this capture</summary>
            public readonly bool On;

            /// <summary>the capture has splat dancers but the device cannot draw them: the dancers show as skeleton lines (log it once,
            /// put Note on screen)</summary>
            public readonly bool Refused;

            /// <summary>why, in words (the CLI state, the log)</summary>
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
            if (api is GraphicsDeviceType.OpenGLES3 or GraphicsDeviceType.OpenGLCore) return "GLES: the renderer has no compute path";
            if (api == GraphicsDeviceType.Direct3D11)
                return $"D3D11: the renderer needs D3D12 - start the {(host == Host.Editor ? "editor" : "exe")} with -force-d3d12";
            if (!computeShaders) return "no compute shaders on this device";
            if (api is not (GraphicsDeviceType.Direct3D12 or GraphicsDeviceType.Vulkan or GraphicsDeviceType.Metal))
                return $"{api}: no splat path in the renderer";
            return "";
        }

        /// <summary>the on-screen note / log line when a capture has splat dancers the device cannot draw (noPath = NoPath, or the
        /// players' watchdog reason): "splat dancers need D3D12: start the editor with -force-d3d12 - skeleton lines shown"</summary>
        public static string Note(string noPath, Host host)
        {
            if (string.IsNullOrEmpty(noPath)) return "";
            if (noPath.StartsWith("D3D11", System.StringComparison.Ordinal))
                return $"splat dancers need D3D12: start the {(host == Host.Editor ? "editor" : "exe")} with -force-d3d12 - skeleton lines shown (the mesh avatars are never drawn for a capture with splat dancers)";
            return $"splat dancers cannot be drawn on this device ({noPath}) - skeleton lines shown (the mesh avatars are never drawn for a capture with splat dancers)";
        }

        /// <summary>the capture-level decision at a capture load: the splat dancers are on exactly when the capture has splat data and the
        /// device can draw it; Refused when it has splat data the device cannot draw (skeleton lines + Note)</summary>
        public static Decision Decide(in Inputs i)
        {
            bool canDraw = string.IsNullOrEmpty(i.NoPath);
            if (!i.HasSplatData)
                return new Decision(false, false, i.CaptureFlag
                    ? "capture.json asks for splat dancers but the capture has no splats/: mesh avatars"
                    : "no splat data in this capture: mesh avatars");
            if (!canDraw) return new Decision(false, true, $"the capture has splat dancers but {i.NoPath}: skeleton lines, never the mesh avatars");
            string why = "the capture has splat dancers: always drawn as splats (owner rule 2026-10-23)";
            if (i.Host == Host.Editor && i.HandSet && !i.HandOn) why += "; the earlier hm_splatdancers off of this editor session is ignored";
            if (i.Host != Host.Editor && i.SettingsGiven && !i.SettingsOn) why += "; \"splats\": false / -hmsplats off is ignored";
            return new Decision(true, false, why);
        }

        /// <summary>how one dancer is drawn: its role has splat data and the device draws it (and the watchdog has not given up) -> Splats;
        /// splat data the device cannot draw -> Skeleton (never the mesh avatar); no splat data for the role -> MeshAvatar</summary>
        public static Look DancerLook(bool roleHasSplatData, bool canDraw, bool fellBack = false)
        {
            if (!roleHasSplatData) return Look.MeshAvatar;
            return canDraw && !fellBack ? Look.Splats : Look.Skeleton;
        }

        /// <summary>the mesh avatar of a dancer may be drawn (only a dancer without splat data: DancerLook == MeshAvatar)</summary>
        public static bool MeshAvatarDrawn(bool roleHasSplatData) => DancerLook(roleHasSplatData, true) == Look.MeshAvatar;
    }
}
