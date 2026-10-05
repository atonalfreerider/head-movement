using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Shows the virtual cameras atlas_bridge asked Atlas to render (and MAMMA triangulated from) as glowing
/// frustums, to sanity check that the ring actually surrounds the dancers.
/// </summary>
public class VirtualCameraRig : MonoBehaviour
{
    public float FrustumDepth = 0.35f;

    readonly List<GameObject> frustums = new();

    [Serializable]
    class CameraList
    {
        public List<CameraEntry> cameras;
    }

    [Serializable]
    class CameraEntry
    {
        public string name;
        public Float3 position;
        public Float3 forward;
        public Float3 up;
        public float vfov_deg;
        public float aspect;
    }

    [Serializable]
    class Float3
    {
        public float x, y, z;
        public Vector3 V => new(x, y, z);
    }

    public void Load(string camerasJson, Material glowMat)
    {
        Clear();
        if (string.IsNullOrEmpty(camerasJson) || !File.Exists(camerasJson)) return;

        CameraList list = JsonConvert.DeserializeObject<CameraList>(File.ReadAllText(camerasJson));
        foreach (CameraEntry cam in list.cameras)
        {
            Quaternion rotation = Quaternion.LookRotation(cam.forward.V, cam.up.V);
            float h = Mathf.Tan(cam.vfov_deg * 0.5f * Mathf.Deg2Rad) * FrustumDepth;
            float w = h * cam.aspect;
            Vector3 apex = cam.position.V;
            Vector3 C(float x, float y) => apex + rotation * new Vector3(x, y, FrustumDepth);
            Vector3 tl = C(-w, h), tr = C(w, h), br = C(w, -h), bl = C(-w, -h);

            GameObject go = new($"Virtual Camera {cam.name}");
            go.transform.SetParent(transform, false);
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.material = glowMat;
            line.useWorldSpace = true;
            line.widthMultiplier = 0.004f;
            line.startColor = line.endColor = new Color(0.05f, 0.12f, 0.2f);
            // one continuous path over all 8 frustum edges
            Vector3[] path = { apex, tl, tr, apex, br, bl, apex, bl, tl, tr, br };
            line.positionCount = path.Length;
            line.SetPositions(path);
            frustums.Add(go);
        }
    }

    public void SetVisible(bool visible)
    {
        foreach (GameObject go in frustums) go.SetActive(visible);
    }

    public void Clear()
    {
        foreach (GameObject go in frustums) Destroy(go);
        frustums.Clear();
    }
}
