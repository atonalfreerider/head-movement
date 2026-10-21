#if UNITY_EDITOR || UNITY_STANDALONE_WIN
using HmSplats;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Read-only access to the follow's simulated hair guides for the splat dancers (Assets/Splats/Avatar): the rest shape
/// (head-bone space) and the current points. A new partial-class file (HairStrands.cs is unchanged); Editor / Windows
/// only like the rest of the splat code.
/// </summary>
public partial class HairStrands
{
    /// <summary>the simulation has run at least once and its arrays exist (and the component runs: a disabled hair, e.g.
    /// a headset quality tier, does not simulate)</summary>
    public bool SplatSourceReady => isActiveAndEnabled && avatar != null && pos.IsCreated && restLocal.IsCreated && simFrame >= 0;

    /// <summary>the groom's head sphere centre in head-bone space (radius: HeadRadius)</summary>
    public Vector3 SplatHeadCentreLocal => headCentreLocal;

    /// <summary>the simulation's face box (HairJobs.FaceBox, head-bone space): centre + r, (fwdMin, yLo, yHi, halfWidth),
    /// (margin, sideBias, enabled = FaceGuard, 0)</summary>
    public void SplatFaceBox(out float4 centreR, out float4 box, out float4 extra)
    {
        centreR = new float4(faceBox.centre, faceBox.r);
        box = new float4(faceBox.fwdMin, faceBox.yLo, faceBox.yHi, faceBox.halfWidth);
        extra = new float4(faceBox.margin, faceBox.sideBias, FaceGuard && faceBox.enabled != 0 ? 1f : 0f, 0f);
    }

    /// <summary>rest points (guide-major, guides x points) in head-bone space: relative to the avatar skin's head joint,
    /// T-pose axes (the space of hm_hair --action dump "rest")</summary>
    public void SplatRest(float3[] dst)
    {
        for (int i = 0; i < dst.Length && i < restLocal.Length; i++) dst[i] = restLocal[i];
    }

    /// <summary>current simulated points in WORLD space and the world head pose the simulation used for them (the
    /// points are kept in the avatar root's space: hm_hair dump "pos", "headPos", "headRot")</summary>
    public void SplatFrame(float3[] world, out float3 headPos, out quaternion headRot)
    {
        Transform root = avatar.transform;
        Matrix4x4 m = root.localToWorldMatrix;
        for (int i = 0; i < world.Length && i < pos.Length; i++) world[i] = m.MultiplyPoint3x4(pos[i]);
        headPos = m.MultiplyPoint3x4(curHeadPos);
        headRot = math.normalize(math.mul((quaternion)root.rotation, curHeadRot));
    }
}

/// <summary>The viewer's HairStrands as the hair source of a splat dancer (HmSplats.ISplatHairSource).</summary>
public sealed class HairStrandsSplatSource : ISplatHairSource
{
    public readonly HairStrands Hair;

    public HairStrandsSplatSource(HairStrands hair) => Hair = hair;

    public bool Ready => Hair != null && Hair.SplatSourceReady;
    public int Guides => Hair != null ? Hair.Guides : 0;
    public int Points => Hair != null ? Hair.Points : 0;
    public void GetRest(float3[] restHeadBone) => Hair.SplatRest(restHeadBone);
    public void GetFrame(float3[] worldPoints, out float3 simHeadPos, out quaternion simHeadRot) => Hair.SplatFrame(worldPoints, out simHeadPos, out simHeadRot);

    public bool TryGetFaceBox(out float4 centreR, out float4 box, out float4 extra)
    {
        Hair.SplatFaceBox(out centreR, out box, out extra);
        return true;
    }
}
#endif
