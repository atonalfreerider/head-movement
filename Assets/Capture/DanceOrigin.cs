using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// VIEWER_SPEC 3.0 - every dance starts at the origin. The couple centre (midpoint of the two pelvis positions,
/// projected to the floor) at the first frame of the take is placed at world (0, 0, 0); ONE horizontal offset,
/// computed once per loaded capture, is added to everything that comes from the capture (dancers, avatars,
/// physics, counterbalance, cameras, splats, room) so they stay mutually aligned, and from there the dance travels.
/// Looping never re-centres. capture.json "origin" (written by the exporter, v4) wins when present.
///
/// Capture coordinates = the exported Unity positions; world = capture + Offset.
/// </summary>
public static class DanceOrigin
{
    /// <summary>added to every capture position (y is always 0)</summary>
    public static Vector3 Offset { get; private set; }

    /// <summary>couple centre at the first frame in capture coordinates (floor-projected)</summary>
    public static Vector3 CaptureCentre { get; private set; }

    /// <summary>"first frame" (computed here) or "capture.json origin"</summary>
    public static string Source { get; private set; } = "none";

    /// <summary>distance (m) between capture.json's origin and the measured first-frame centre, NaN without one</summary>
    public static float ManifestDisagreementM { get; private set; } = float.NaN;

    public static string Capture { get; private set; }

    /// <summary>raised after a capture's offset is known (before its layers are built)</summary>
    public static event Action Changed;

    public static Vector3 ToWorld(Vector3 capturePosition) => capturePosition + Offset;

    /// <summary>
    /// Compute the offset for a freshly loaded capture and apply it to the figure joints in place (everything the
    /// viewer derives from the skeletons - floor patterns, contacts, timing rings, traces - follows).
    /// </summary>
    public static Vector3 Begin(CaptureManifest manifest, List<List<Vector3>> lead, List<List<Vector3>> follow)
    {
        Vector3 centre = FirstFrameCentre(lead, follow);
        Vector3? fromManifest = ReadManifestOrigin(manifest, out bool isOffset);
        ManifestDisagreementM = float.NaN;
        if (fromManifest.HasValue)
        {
            Vector3 c = isOffset ? -fromManifest.Value : fromManifest.Value;
            c.y = 0;
            if (!float.IsNaN(centre.x)) ManifestDisagreementM = new Vector2(c.x - centre.x, c.z - centre.z).magnitude;
            centre = c;
            Source = "capture.json origin";
        }
        else
        {
            Source = "first frame";
        }

        if (float.IsNaN(centre.x) || float.IsNaN(centre.z))
        {
            centre = Vector3.zero;
            Source = "none (no valid first frame)";
        }

        CaptureCentre = new Vector3(centre.x, 0, centre.z);
        Offset = new Vector3(-centre.x, 0, -centre.z);
        Capture = manifest?.DisplayName;
        Shift(lead);
        Shift(follow);
        Changed?.Invoke();
        return Offset;
    }

    /// <summary>midpoint of the two pelvis joints at the first frame where both are valid, floor-projected</summary>
    public static Vector3 FirstFrameCentre(List<List<Vector3>> lead, List<List<Vector3>> follow)
    {
        int n = Mathf.Min(lead?.Count ?? 0, follow?.Count ?? 0);
        for (int f = 0; f < n; f++)
        {
            if (lead[f] == null || follow[f] == null || lead[f].Count == 0 || follow[f].Count == 0) continue;
            Vector3 a = lead[f][(int)SmplJoint.Pelvis], b = follow[f][(int)SmplJoint.Pelvis];
            if (float.IsNaN(a.x) || float.IsNaN(b.x)) continue;
            Vector3 m = (a + b) * 0.5f;
            return new Vector3(m.x, 0, m.z);
        }

        return new Vector3(float.NaN, 0, float.NaN);
    }

    /// <summary>
    /// capture.json "origin": [x, z] / [x, y, z] or {"centre"|"center"|"couple_centre"|"unity": [...]} = the couple
    /// centre to move to (0,0); {"offset": [...]} = the translation to add. "frame": "world" converts (x, y, -z).
    /// </summary>
    static Vector3? ReadManifestOrigin(CaptureManifest manifest, out bool isOffset)
    {
        isOffset = false;
        if (manifest?.Folder == null) return null;
        string path = Path.Combine(manifest.Folder, "capture.json");
        if (!File.Exists(path)) return null;
        try
        {
            JToken origin = JObject.Parse(File.ReadAllText(path))["origin"];
            if (origin == null || origin.Type == JTokenType.Null) return null;
            bool world = false;
            JToken values = origin;
            if (origin is JObject o)
            {
                world = string.Equals(o.Value<string>("frame"), "world", StringComparison.OrdinalIgnoreCase);
                if (o["offset"] is JArray off)
                {
                    values = off;
                    isOffset = true;
                }
                else
                {
                    values = o["centre"] ?? o["center"] ?? o["couple_centre"] ?? o["couple_center"] ?? o["unity"];
                }
            }

            if (values is not JArray a || a.Count < 2) return null;
            Vector3 v = a.Count == 2
                ? new Vector3(a[0].Value<float>(), 0, a[1].Value<float>())
                : new Vector3(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>());
            if (world) v.z = -v.z;
            return v;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"capture.json origin unreadable ({e.Message}) - using the first frame");
            return null;
        }
    }

    static void Shift(List<List<Vector3>> poses)
    {
        if (poses == null || Offset == Vector3.zero) return;
        foreach (List<Vector3> pose in poses)
        {
            if (pose == null) continue;
            for (int j = 0; j < pose.Count; j++) pose[j] += Offset;
        }
    }

    /// <summary>physics.json positions (COM, XCoM, support polygons); forces are directions and stay as they are</summary>
    public static void Shift(PhysicsData physics)
    {
        if (physics == null || Offset == Vector3.zero) return;
        Vector2 xz = new(Offset.x, Offset.z);
        foreach (PhysicsData.DancerTrack d in physics.Dancers.Values)
        {
            if (d.Com != null)
            {
                for (int i = 0; i < d.Com.Length; i++) d.Com[i] += Offset;
            }

            if (d.XcomXZ != null)
            {
                for (int i = 0; i < d.XcomXZ.Length; i++) d.XcomXZ[i] += xz;
            }

            if (d.Support == null) continue;
            foreach (Vector2[] poly in d.Support)
            {
                if (poly == null) continue;
                for (int i = 0; i < poly.Length; i++) poly[i] += xz;
            }
        }
    }

    /// <summary>move a capture-space object (avatar root, splat cloud, room) by the offset; idempotent</summary>
    public static void Place(Transform t)
    {
        if (t != null) t.position = Offset;
    }

    /// <summary>shift the points of world-space LineRenderers built from capture data (virtual camera frustums)</summary>
    public static void ShiftWorldLines(Transform root)
    {
        if (root == null || Offset == Vector3.zero) return;
        foreach (LineRenderer line in root.GetComponentsInChildren<LineRenderer>(true))
        {
            if (!line.useWorldSpace) continue;
            for (int i = 0; i < line.positionCount; i++) line.SetPosition(i, line.GetPosition(i) + Offset);
        }
    }

    public static Dictionary<string, object> State(Dancer lead, Dancer follow, int frame)
    {
        Dictionary<string, object> s = new()
        {
            ["offset"] = new[] { Offset.x, Offset.y, Offset.z },
            ["captureCentre"] = new[] { CaptureCentre.x, CaptureCentre.z },
            ["source"] = Source
        };
        if (!float.IsNaN(ManifestDisagreementM)) s["manifestDisagreementM"] = ManifestDisagreementM;
        if (lead != null && follow != null && lead.FrameCount > 0 && follow.FrameCount > 0)
        {
            Vector3 c0 = (lead.Joint(0, SmplJoint.Pelvis) + follow.Joint(0, SmplJoint.Pelvis)) * 0.5f;
            s["coupleCentreAtStart"] = new[] { c0.x, c0.z };
            s["coupleCentreAtStartErrorM"] = new Vector2(c0.x, c0.z).magnitude;
            if (frame >= 0 && frame < Mathf.Min(lead.FrameCount, follow.FrameCount))
            {
                Vector3 c = (lead.Joint(frame, SmplJoint.Pelvis) + follow.Joint(frame, SmplJoint.Pelvis)) * 0.5f;
                s["coupleCentre"] = new[] { c.x, c.z };
            }
        }

        return s;
    }
}
