using UnityEngine;

/// <summary>
/// Holds the desktop camera rig at a fixed review pose (hm_shoes --action frame) until released: runs after every
/// other camera script (CameraControl, the tour director) so a still is framed exactly as requested.
/// </summary>
[DefaultExecutionOrder(10000)]
public class SneakerReviewCamera : MonoBehaviour
{
    public Vector3 Position;
    public Vector3 Target;

    public static SneakerReviewCamera Hold(Transform rig, Vector3 position, Vector3 target)
    {
        SneakerReviewCamera c = rig.GetComponent<SneakerReviewCamera>();
        if (c == null) c = rig.gameObject.AddComponent<SneakerReviewCamera>();
        c.Position = position;
        c.Target = target;
        c.Apply();
        return c;
    }

    public static void Release(Transform rig)
    {
        SneakerReviewCamera c = rig != null ? rig.GetComponent<SneakerReviewCamera>() : null;
        if (c != null) Destroy(c);
    }

    void LateUpdate() => Apply();

    void Apply()
    {
        transform.position = Position;
        transform.LookAt(Target);
    }
}
