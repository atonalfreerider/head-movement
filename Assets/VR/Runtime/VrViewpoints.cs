using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// The places a lesson can be watched from (the viewpoint presets of the hand panel), computed for the loaded capture:
    ///   start     the comfort vantage a lesson begins at: about 3 m from the dancers at the edge of the floor, facing them. From the lesson's
    ///             own data when it has some (library.json entry "vr": { "start": { "x", "z", "yawDeg" } }, else capture.json "vr_start"), else
    ///             the spot where the class stood (the typical position of a filming phone 2-6 m from the dancers), else 3 m on the floor's front.
    ///   front     a front-row spot, close
    ///   side      from the side
    ///   overhead  an elevated platform behind the start vantage, looking down at the floor
    ///   phone:N   each filming phone's real position in the room (Unity world position and heading at the moment it is chosen, at the phone's own height)
    /// The dancers' position is where the couple is NOW (the viewpoint is relative to the moving couple), except for the data-driven start.
    /// </summary>
    public static class VrViewpoints
    {
        public const float EdgeDistance = 3f, FrontDistance = 1.9f, SideDistance = 3f, OverheadDistance = 3.2f, OverheadFloorY = 2.0f;

        public static Vector3 CoupleCentre(HeadMovement hm)
        {
            if (hm == null || hm.LeadDancer == null || hm.FollowDancer == null || hm.CurrentFrame < 0) return Vector3.zero;
            try
            {
                int f = hm.CurrentFrame;
                Vector3 p = (hm.LeadDancer.Joint(f, SmplJoint.Pelvis) + hm.FollowDancer.Joint(f, SmplJoint.Pelvis)) * 0.5f;
                return new Vector3(p.x, 0f, p.z);
            }
            catch (Exception)
            {
                return Vector3.zero;
            }
        }

        /// <summary>the lesson's own start vantage from data, or null</summary>
        public static Viewpoint? DataStart(LibraryEntry entry, HeadMovement hm)
        {
            JObject node = null;
            try
            {
                if (entry != null && LibraryData.Exists)
                {
                    JObject root = JObject.Parse(File.ReadAllText(LibraryData.FilePath));
                    JArray films = root["films"] as JArray ?? root["entries"] as JArray;
                    JObject film = films?.OfType<JObject>().FirstOrDefault(o => string.Equals(o.Value<string>("id"), entry.Id, StringComparison.OrdinalIgnoreCase));
                    node = film?["vr"]?["start"] as JObject;
                }

                if (node == null && hm?.Manifest != null)
                {
                    string cj = Path.Combine(hm.Manifest.Folder, "capture.json");
                    if (File.Exists(cj)) node = JObject.Parse(File.ReadAllText(cj))["vr_start"] as JObject;
                }
            }
            catch (Exception)
            {
                return null;
            }

            if (node == null) return null;
            float? x = node.Value<float?>("x"), z = node.Value<float?>("z");
            if (!x.HasValue || !z.HasValue) return null;
            float yaw = node.Value<float?>("yawDeg") ?? ViewpointMath.Bearing(new Vector3(x.Value, 0f, z.Value), CoupleCentre(hm));
            return new Viewpoint { Id = "start", Label = "Where we start", Kind = "start", Head = new Vector3(x.Value, ViewpointMath.EyeHeight, z.Value), YawDeg = yaw, FloorY = 0f };
        }

        /// <summary>the typical position of a phone over the whole take (median of samples), world space</summary>
        static bool TypicalPosition(string cam, HeadMovement hm, out Vector3 pos)
        {
            pos = default;
            if (hm?.Timeline == null) return false;
            float t0 = hm.Timeline.First - hm.Timeline.TimeToAudio, t1 = hm.Timeline.Last - hm.Timeline.TimeToAudio;
            List<float> xs = new(), zs = new(), ys = new();
            const int n = 48;
            for (int i = 0; i < n; i++)
            {
                float t = Mathf.Lerp(t0, t1, (i + 0.5f) / n);
                if (!FilmCameraVideo.TryGetPose(cam, t, out Vector3 p, out _, out _, out _)) continue;
                xs.Add(p.x);
                ys.Add(p.y);
                zs.Add(p.z);
            }

            if (xs.Count == 0) return false;
            xs.Sort();
            ys.Sort();
            zs.Sort();
            pos = new Vector3(xs[xs.Count / 2], ys[ys.Count / 2], zs[zs.Count / 2]);
            return true;
        }

        public static List<Viewpoint> Build(HeadMovement hm, LibraryEntry entry, float danceTime)
        {
            List<Viewpoint> list = new();
            Vector3 centre = CoupleCentre(hm);
            string[] cams = FilmCameraVideo.Cameras;

            // ---- the start vantage
            Viewpoint start;
            Viewpoint? data = DataStart(entry, hm);
            if (data.HasValue)
            {
                start = data.Value;
            }
            else
            {
                // the class stood somewhere: a phone that stayed 2-6 m from the dancers is a natural place for a spectator
                bool found = false;
                Vector3 best = default;
                float bestD = float.MaxValue;
                foreach (string cam in cams)
                {
                    if (!TypicalPosition(cam, hm, out Vector3 p)) continue;
                    float d = new Vector2(p.x - centre.x, p.z - centre.z).magnitude;
                    if (d < 2f || d > 6f) continue;
                    float score = Mathf.Abs(d - EdgeDistance);
                    if (score < bestD)
                    {
                        bestD = score;
                        best = p;
                        found = true;
                    }
                }

                float yawOffset = hm != null ? hm.FloorYawDeg : 0f;
                if (found)
                {
                    // 3 m out on that phone's side of the floor, facing the couple
                    float bearing = ViewpointMath.Bearing(centre, best);
                    start = ViewpointMath.Facing("start", "Floor edge", "start", centre, bearing, EdgeDistance);
                }
                else
                {
                    start = ViewpointMath.Facing("start", "Floor edge", "start", centre, 180f - yawOffset, EdgeDistance);
                }
            }

            start.Id = "start";
            if (string.IsNullOrEmpty(start.Label)) start.Label = "Floor edge";
            list.Add(start);
            float startBearing = ViewpointMath.Bearing(centre, start.Head);
            list.Add(ViewpointMath.Facing("front", "Front row", "front", centre, startBearing, FrontDistance));
            list.Add(ViewpointMath.Facing("side", "From the side", "side", centre, startBearing + 90f, SideDistance));
            list.Add(ViewpointMath.Facing("overhead", "Overhead", "overhead", centre, startBearing, OverheadDistance, OverheadFloorY));

            // ---- each phone, where it is now (or typically, when it has no pose at this moment)
            foreach (string cam in cams)
            {
                Vector3 pos;
                Quaternion rot;
                if (!FilmCameraVideo.TryGetPose(cam, danceTime, out pos, out rot, out _, out _))
                {
                    if (!TypicalPosition(cam, hm, out pos)) continue;
                    rot = Quaternion.LookRotation(centre - pos, Vector3.up);
                }

                Vector3 fwd = rot * Vector3.forward;
                float yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                if (new Vector2(fwd.x, fwd.z).magnitude < 0.3f) yaw = ViewpointMath.Bearing(pos, centre); // the phone points at the floor or the ceiling
                float eye = Mathf.Clamp(pos.y, 1.2f, 2.0f);
                list.Add(new Viewpoint { Id = "phone:" + cam, Label = "Phone " + cam, Kind = "phone", Head = new Vector3(pos.x, eye, pos.z), YawDeg = yaw, FloorY = 0f });
            }

            return list;
        }
    }
}
