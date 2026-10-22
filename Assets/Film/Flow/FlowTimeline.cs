using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HmFilm
{
    /// <summary>a shot's camera.transition_in ("hm film flow v1", dancecap docs/RECAP_FILM.md "The schema")</summary>
    public sealed class FlowTransition
    {
        public string Type = "flight", Path = "arc", Easing = "smootherstep";
        public float StartS, DurationS = 1f;
        public bool Frozen, Crane;
        public float AxisRunM = 1f, AxisShare = 0.3f;
        public float HeightLo = 0.5f, HeightHi = 2.4f, ClearRadiusM = 1.4f;
        public string FromKind, ToKind, FromPhone, ToPhone;
        public float PlanDeltaRad = float.NaN;
        public float FullFrameAtS = float.NaN, SplatsHiddenAtS = float.NaN, OverlaysOnTopInS = float.NaN;
        public float SurfaceFromS = float.NaN, SplatsBackInS = float.NaN, OverlaysOnTopOffBeforeS = float.NaN;
        public float LayersSwitchS;
        public float TransitFilm0 = float.NaN, TransitFilm1 = float.NaN;
        public JObject TransitLayers;
        public float FlashS = float.NaN;
        public JObject Json;

        public bool PhoneIn => Path == "arc_then_axis";
        public bool PhoneOut => Path == "axis_then_arc";
        public bool PhoneToPhone => Path == "axis_arc_axis";
        public bool CranePath => Path == "crane";

        static float F(JToken t, float fallback = float.NaN) =>
            t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? t.Value<float>() : fallback;

        public static FlowTransition Parse(JObject j)
        {
            if (j == null) return null;
            FlowTransition f = new()
            {
                Json = j, Type = j.Value<string>("type") ?? "flight", Path = j.Value<string>("path") ?? "arc", Easing = j.Value<string>("easing") ?? "smootherstep",
                StartS = F(j["start_s"], 0f), DurationS = Mathf.Max(1e-3f, F(j["duration_s"], 1f)), Frozen = j.Value<string>("from_pose") == "frozen",
                Crane = j.Value<bool?>("crane") ?? false, AxisRunM = F(j["axis_run_m"], 1f), AxisShare = F(j["axis_share"], 0.3f),
                LayersSwitchS = F(j["layers_switch_s"], 0f), FlashS = F(j["flash_s"])
            };
            if (j["constraints"] is JObject c)
            {
                f.ClearRadiusM = F(c["clear_radius_m"], 1.4f);
                if (c["height_m"] is JArray h && h.Count == 2)
                {
                    f.HeightLo = F(h[0], 0.5f);
                    f.HeightHi = h[1].Type == JTokenType.Null ? float.PositiveInfinity : F(h[1], 2.4f);
                }
            }

            if (f.CranePath) f.HeightHi = float.PositiveInfinity;
            if (j["from"] is JObject fr)
            {
                f.FromKind = fr.Value<string>("kind");
                f.FromPhone = fr["phone"]?.Type == JTokenType.String ? fr.Value<string>("phone") : null;
            }

            if (j["to"] is JObject to)
            {
                f.ToKind = to.Value<string>("kind");
                f.ToPhone = to["phone"]?.Type == JTokenType.String ? to.Value<string>("phone") : null;
            }

            if (j["arc"] is JObject arc) f.PlanDeltaRad = F(arc["delta_deg"]) * Mathf.Deg2Rad;
            if (j["video"] is JObject v)
            {
                f.FullFrameAtS = F(v["full_frame_at_s"]);
                f.SplatsHiddenAtS = F(v["splats_hidden_at_s"]);
                f.OverlaysOnTopInS = F(v["overlays_on_top_in_s"]);
            }

            if (j["video_out"] is JObject vo)
            {
                f.SurfaceFromS = F(vo["surface_from_s"]);
                f.SplatsBackInS = F(vo["splats_back_in_s"]);
                f.OverlaysOnTopOffBeforeS = F(vo["overlays_on_top_off_before_s"]);
            }

            if (j["transit_layers"] is JObject tl && tl["film"] is JArray tf && tf.Count == 2)
            {
                f.TransitFilm0 = F(tf[0]);
                f.TransitFilm1 = F(tf[1]);
                f.TransitLayers = tl["layers"] as JObject;
            }

            return f;
        }
    }

    /// <summary>one flow.frustums entry: a phone's frustum with its live video standing in the 3D world over [a, b, c, d]</summary>
    public sealed class FlowFrustum
    {
        public string Camera, Role, Shot;
        public float A, B, C, D, DepthM = 0.9f;
        public int ShotIndex = -1;

        /// <summary>the depth actually used (FilmCamera.SurfaceDepth: closer to the phone when the 3D end of its way stands inside the frustum
        /// before the picture - the picture is then behind that view instead of covering it); NaN = DepthM</summary>
        public float DepthUsed = float.NaN;
        public bool Enter => Role == "enter";
    }

    /// <summary>a frustum surface drawn now (FlowTimeline.Surfaces): alpha = its envelope; Top = the share of it also drawn OVER every 3D overlay
    /// (the hand-off cover: 0 -> 1 over the last OverlaysIn seconds of a way in, 1 -> 0 over the first ones of a way out)</summary>
    public struct FlowSurface
    {
        public string Camera, Role;
        public float Alpha, Top, DepthM;
        public FlowFrustum Frustum;
    }

    /// <summary>a shot of the flow (the same grouping as FilmCamera.BuildShots: consecutive segments with an identical camera block and no
    /// dance-time jump)</summary>
    public sealed class FlowShot
    {
        public int Index, FirstSegment, LastSegment;
        public string Mode, FirstSegmentId, Phone;
        public float F0, F1;
        public FlowTransition T;
        public float TransStart => T != null ? F0 + T.StartS : F0;
        public float TransEnd => T != null ? F0 + T.StartS + T.DurationS : F0;
        public bool IsPhone => Mode == "camera_tour";
    }

    /// <summary>a phone's full-frame window: from the arrival of its way in to the start of the next shot's way out</summary>
    public struct FlowPhoneWindow
    {
        public int Shot;
        public string Phone;
        public float Arrival, Departure, SplatsHidden, OverlaysIn, OverlaysOff, SplatsBackIn;
    }

    /// <summary>
    /// The timing of a v3 direction (top-level "flow"): shots and their transitions, the phones' full-frame windows, the frustum surfaces with
    /// their envelopes tied to the camera's own arrival / departure times (so the surface and the full-frame video hand over in the same frame),
    /// the hand-off covers, the splat dancers' visibility and when each segment's layers take over. Built once from the direction JSON;
    /// every query is a pure function of film time. Null (Build) for a direction without "flow": older films play as before.
    /// </summary>
    public sealed class FlowTimeline
    {
        public const float CutJump = 0.02f;
        public readonly List<FlowShot> Shots = new();
        public readonly List<FlowFrustum> Frustums = new();
        public readonly List<FlowPhoneWindow> Windows = new();
        public readonly List<float> SegmentF0 = new(), SegmentLayerStart = new();
        public readonly List<string> SegmentIds = new();
        public readonly List<string> Warnings = new();
        public float FrustumDepthM = 0.9f, OverlaysInS = 0.35f, OverlaysOffS = 0.25f, SplatsBackInS = 0.35f;

        /// <summary>the hand-off covers are complete this long before an arrival and stay complete this long after a departure (a little over a
        /// frame at 30 fps): the frames on both sides of a switch show the phone's video alone, exactly</summary>
        public const float CoverHoldS = 0.04f;
        public float GroundLo = 0.5f, GroundHi = 2.4f;

        static float F(JToken t, float fallback = float.NaN) =>
            t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? t.Value<float>() : fallback;

        public static FlowTimeline Build(JObject root)
        {
            if (root?["flow"] is not JObject flow || root["segments"] is not JArray segs) return null;
            FlowTimeline tl = new();
            if (flow["defaults"] is JObject def)
            {
                tl.FrustumDepthM = F(def["frustum_depth_m"], 0.9f);
                if (def["ground_m"] is JArray g && g.Count == 2)
                {
                    tl.GroundLo = F(g[0], 0.5f);
                    tl.GroundHi = F(g[1], 2.4f);
                }
            }

            FlowShot cur = null;
            JObject curCam = null;
            float prevD1 = float.NaN;
            for (int i = 0; i < segs.Count; i++)
            {
                if (segs[i] is not JObject s) continue;
                JObject cam = s["camera"] as JObject ?? new JObject();
                float f0 = 0f, f1 = 0f, d0 = 0f, d1 = 0f;
                if (s["film"] is JArray fa && fa.Count == 2)
                {
                    f0 = F(fa[0], 0f);
                    f1 = F(fa[1], f0);
                }

                if (s["dance"] is JArray da && da.Count == 2)
                {
                    d0 = F(da[0], 0f);
                    d1 = F(da[1], d0);
                }

                bool cut = i > 0 && !float.IsNaN(prevD1) && Mathf.Abs(prevD1 - d0) > CutJump;
                prevD1 = d1;
                tl.SegmentF0.Add(f0);
                tl.SegmentLayerStart.Add(f0);
                tl.SegmentIds.Add(s.Value<string>("id") ?? $"seg{i}");
                if (cur != null && !cut && JToken.DeepEquals(curCam, cam))
                {
                    cur.LastSegment = i;
                    cur.F1 = f1;
                    continue;
                }

                cur = new FlowShot
                {
                    Index = tl.Shots.Count, FirstSegment = i, LastSegment = i, Mode = cam.Value<string>("mode") ?? "orbit", F0 = f0, F1 = f1,
                    FirstSegmentId = s.Value<string>("id"), T = FlowTransition.Parse(cam["transition_in"] as JObject)
                };
                if (cam["cameras"] is JArray list && list.Count > 0) cur.Phone = list[0].Value<string>("camera");
                curCam = cam;
                tl.Shots.Add(cur);
                if (cur.T != null) tl.SegmentLayerStart[i] = f0 + cur.T.LayersSwitchS;
            }

            // the phones' full-frame windows
            for (int k = 0; k < tl.Shots.Count; k++)
            {
                FlowShot s = tl.Shots[k];
                if (!s.IsPhone || s.T == null) continue;
                FlowShot next = k + 1 < tl.Shots.Count ? tl.Shots[k + 1] : null;
                float dep = next != null ? next.TransStart : s.F1;
                float hidden = !float.IsNaN(s.T.SplatsHiddenAtS) ? s.F0 + s.T.SplatsHiddenAtS : s.TransEnd;
                tl.Windows.Add(new FlowPhoneWindow
                {
                    Shot = k, Phone = s.Phone, Arrival = s.TransEnd, Departure = Mathf.Max(dep, s.TransEnd), SplatsHidden = Mathf.Min(hidden, s.TransEnd),
                    OverlaysIn = !float.IsNaN(s.T.OverlaysOnTopInS) ? s.T.OverlaysOnTopInS : tl.OverlaysInS,
                    OverlaysOff = next?.T != null && !float.IsNaN(next.T.OverlaysOnTopOffBeforeS) ? next.T.OverlaysOnTopOffBeforeS : tl.OverlaysOffS,
                    SplatsBackIn = next?.T != null && !float.IsNaN(next.T.SplatsBackInS) ? next.T.SplatsBackInS : tl.SplatsBackInS
                });
            }

            if (flow["frustums"] is JArray frs)
            {
                foreach (JToken t in frs)
                {
                    if (t is not JObject o || o["film"] is not JArray w || w.Count != 4) continue;
                    FlowFrustum fr = new()
                    {
                        Camera = o.Value<string>("camera"), Role = o.Value<string>("role") ?? "enter", Shot = o.Value<string>("shot"), A = F(w[0], 0f), B = F(w[1], 0f),
                        C = F(w[2], 0f), D = F(w[3], 0f), DepthM = F(o["depth_m"], tl.FrustumDepthM)
                    };
                    fr.ShotIndex = tl.Shots.FindIndex(x => x.FirstSegmentId == fr.Shot);
                    // the hand-off is the CAMERA's arrival / departure (the same floats FilmCamera switches on): the enter envelope ends, the leave
                    // envelope starts exactly there, so in every frame either the surface or the full-frame video carries the picture
                    int wi = tl.Windows.FindIndex(x => x.Shot == fr.ShotIndex);
                    if (wi >= 0)
                    {
                        FlowPhoneWindow pw = tl.Windows[wi];
                        if (fr.Enter)
                        {
                            if (Mathf.Abs(fr.D - pw.Arrival) > 0.02f) tl.Warnings.Add($"frustum {fr.Camera} enter of {fr.Shot}: video_full_at {fr.D:0.000} != camera arrival {pw.Arrival:0.000}");
                            fr.C = fr.D = pw.Arrival;
                            fr.B = Mathf.Min(fr.B, fr.C);
                            fr.A = Mathf.Min(fr.A, fr.B);
                        }
                        else
                        {
                            if (Mathf.Abs(fr.A - pw.Departure) > 0.02f) tl.Warnings.Add($"frustum {fr.Camera} leave of {fr.Shot}: video_full_until {fr.A:0.000} != camera departure {pw.Departure:0.000}");
                            fr.A = fr.B = pw.Departure;
                            fr.C = Mathf.Max(fr.C, fr.B);
                            fr.D = Mathf.Max(fr.D, fr.C);
                        }
                    }
                    else
                    {
                        tl.Warnings.Add($"frustum {fr.Camera} {fr.Role}: shot {fr.Shot} is not a phone shot with a transition");
                    }

                    tl.Frustums.Add(fr);
                }
            }

            return tl;
        }

        // ------------------------------------------------------------------ queries (pure functions of film time)

        /// <summary>index into Shots of the shot that starts at or before film (the segment clock's shot)</summary>
        public int ShotIndexAt(float film)
        {
            for (int i = Shots.Count - 1; i >= 0; i--)
            {
                if (Shots[i].F0 <= film + 1e-5f) return i;
            }

            return Shots.Count > 0 ? 0 : -1;
        }

        /// <summary>the camera's state at film: the shot whose OWN pose is on screen (own &gt;= 0, trans = -1) or the shot whose transition_in is
        /// running (trans &gt;= 0; its previous shot is the "from" pose). A transition may start before its shot (start_s &lt; 0) or after it (&gt; 0:
        /// the previous view holds into the shot's first segment).</summary>
        public void CameraAt(float film, out int own, out int trans, out float u)
        {
            own = trans = -1;
            u = 0f;
            int i = ShotIndexAt(film);
            if (i < 0) return;
            FlowShot s = Shots[i];
            if (s.T != null && film < s.TransStart && i > 0)
            {
                own = i - 1;
                return;
            }

            if (i + 1 < Shots.Count && Shots[i + 1].T != null && film >= Shots[i + 1].TransStart)
            {
                trans = i + 1;
            }
            else if (s.T != null && film < s.TransEnd)
            {
                trans = i;
            }

            if (trans >= 0)
            {
                FlowShot t = Shots[trans];
                u = Mathf.Clamp01((film - t.TransStart) / t.T.DurationS);
                return;
            }

            own = i;
        }

        /// <summary>the phone window containing film (arrival &lt;= film &lt; departure), or -1</summary>
        public int WindowAt(float film)
        {
            for (int i = 0; i < Windows.Count; i++)
            {
                if (film >= Windows[i].Arrival && film < Windows[i].Departure) return i;
            }

            return -1;
        }

        /// <summary>the full-frame video's cover over every 3D overlay (1 = only the video shows): 1 at the arrival, gone over OverlaysIn
        /// seconds; back to 1 over the last OverlaysOff seconds before the departure. 0 outside the phone windows.</summary>
        public float VideoCover(float film)
        {
            int i = WindowAt(film);
            if (i < 0) return 0f;
            FlowPhoneWindow w = Windows[i];
            float inK = 1f - FlowMath.Ramp(film, w.Arrival + CoverHoldS, w.Arrival + CoverHoldS + w.OverlaysIn);
            float offK = FlowMath.Ramp(film, w.Departure - CoverHoldS - w.OverlaysOff, w.Departure - CoverHoldS);
            return Mathf.Max(inK, offK);
        }

        /// <summary>the splat dancers' visibility (their composite opacity factor): 0 inside a phone's full-frame window (from splats_hidden_at),
        /// back in over splats_back_in_s from the departure, 1 elsewhere</summary>
        public float SplatVisibility(float film)
        {
            float v = 1f;
            foreach (FlowPhoneWindow w in Windows)
            {
                if (film >= w.SplatsHidden && film < w.Departure) return 0f;
                if (film >= w.Departure && film < w.Departure + w.SplatsBackIn) v = Mathf.Min(v, FlowMath.Ramp(film, w.Departure, w.Departure + w.SplatsBackIn));
            }

            return v;
        }

        /// <summary>every frustum surface with alpha &gt; 0 at film</summary>
        public void Surfaces(float film, List<FlowSurface> outList)
        {
            outList.Clear();
            foreach (FlowFrustum f in Frustums)
            {
                if (film < f.A || film >= f.D) continue;
                float a = FlowMath.Envelope(f.A, f.B, f.C, f.D, film);
                if (a <= 1e-4f) continue;
                float top = 0f;
                int wi = Windows.FindIndex(x => x.Shot == f.ShotIndex);
                if (wi >= 0)
                {
                    FlowPhoneWindow w = Windows[wi];
                    top = f.Enter ? FlowMath.Ramp(film, w.Arrival - CoverHoldS - w.OverlaysIn, w.Arrival - CoverHoldS)
                        : 1f - FlowMath.Ramp(film, w.Departure + CoverHoldS, w.Departure + CoverHoldS + w.OverlaysIn);
                }

                outList.Add(new FlowSurface { Camera = f.Camera, Role = f.Role, Alpha = a, Top = top, DepthM = float.IsNaN(f.DepthUsed) ? f.DepthM : f.DepthUsed, Frustum = f });
            }
        }

        /// <summary>film time from which segment i's layers apply (its F0, or for the first segment of a shot with a transition F0 + layers_switch_s)</summary>
        public float LayerStart(int segment) => segment >= 0 && segment < SegmentLayerStart.Count ? SegmentLayerStart[segment] : 0f;

        /// <summary>the transition's transit layers window covering film (phone to phone: the 3D world between the two phones), or null</summary>
        public FlowShot TransitAt(float film)
        {
            foreach (FlowShot s in Shots)
            {
                if (s.T?.TransitLayers != null && film >= s.T.TransitFilm0 && film < s.T.TransitFilm1) return s;
            }

            return null;
        }
    }
}
