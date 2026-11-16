using System.Collections.Generic;
using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// Where the dance happens, once per loaded capture: the floor footprint of both dancers over the whole take (pelvis and ankles every
    /// third frame, world x/z after the capture's origin shift; parked and missing samples skipped), its centre (the minimum enclosing circle
    /// of the kept points, FootprintMath) and radius. The start placement (passthrough), the start vantage (immersive) and the chapter ring
    /// are all centred on it.
    /// </summary>
    public static class VrStage
    {
        public const float ParkedBelowM = 5f;

        static DanceFootprint footprint;
        static Dancer forLead, forFollow;
        static int forFrames = -1;
        static readonly List<Vector2> Points = new();

        public static bool Valid => forFrames > 0 && footprint.Samples > 0;

        /// <summary>the footprint of the capture the viewer has loaded (computed on the first call after a load)</summary>
        public static DanceFootprint Footprint(HeadMovement hm)
        {
            Dancer lead = hm != null ? hm.LeadDancer : null, follow = hm != null ? hm.FollowDancer : null;
            if (lead == null || follow == null) return default;
            int n = Mathf.Min(lead.FrameCount, follow.FrameCount);
            if (ReferenceEquals(lead, forLead) && ReferenceEquals(follow, forFollow) && n == forFrames) return footprint;
            Points.Clear();
            for (int f = 0; f < n; f += 3)
            {
                Add(lead, f);
                Add(follow, f);
            }

            footprint = FootprintMath.Compute(Points);
            forLead = lead;
            forFollow = follow;
            forFrames = n;
            Debug.Log($"VrStage: footprint of the dance: centre ({footprint.Centre.x:0.00}, {footprint.Centre.y:0.00}), radius {footprint.Radius:0.00} m "
                      + $"(p98 {footprint.RadiusP98:0.00} m) from {footprint.Samples} floor points");
            return footprint;
        }

        /// <summary>the footprint's centre on the floor (world, y = 0)</summary>
        public static Vector3 Centre(HeadMovement hm)
        {
            DanceFootprint fp = Footprint(hm);
            return new Vector3(fp.Centre.x, 0f, fp.Centre.y);
        }

        static void Add(Dancer d, int f)
        {
            Add(d.Joint(f, SmplJoint.Pelvis));
            Add(d.Joint(f, SmplJoint.L_Ankle));
            Add(d.Joint(f, SmplJoint.R_Ankle));
        }

        static void Add(Vector3 p)
        {
            if (float.IsNaN(p.x) || float.IsNaN(p.z) || float.IsInfinity(p.x) || float.IsInfinity(p.z) || p.y < -ParkedBelowM) return;
            Points.Add(new Vector2(p.x, p.z));
        }

        public static Dictionary<string, object> State(HeadMovement hm)
        {
            DanceFootprint fp = Footprint(hm);
            return new Dictionary<string, object>
            {
                ["centre"] = new[] { fp.Centre.x, fp.Centre.y }, ["radius"] = fp.Radius, ["radiusP98"] = fp.RadiusP98, ["samples"] = fp.Samples,
                ["viewDistance"] = FootprintMath.ViewDistance(fp)
            };
        }
    }
}
