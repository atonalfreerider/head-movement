using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = UnityEngine.Random;

[RequireComponent(typeof(SphereCollider))]
public class HairSimulation : MonoBehaviour
{
    enum HairType
    {
        Dome,
        Ring
    }

    [Header("Basic Skull / Scalp Parameters")]
    public float skullRadius = 0.07f;
    public float cardioidScale = 0.16f;

    [Header("Top Hair Generation")]
    public int topHairCount = 20;
    public float domeMaxAngle = 60f;

    [Header("Side/Back Hair Generation")]
    public int sideLayerCount = 3;
    public int sideStrandsPerLayer = 10;
    public float ringAngleSpan = 60f;
    
    [Header("Strand Parameters")]
    public int segmentsPerStrand = 6;
    public float strandThickness = 0.035f;

    [Header("Physics Parameters")]
    public Vector3 gravity = new(0, -9.81f, 0);
    public int subSteps = 10;
    public int constraintIterations = 10;
    public float damping = 6.0f;
    public float collisionFriction = 0.1f;

    [Header("Mass / Inertia")]
    public float hairMass = 250f;

    // ---------------------- INTERNAL DATA STRUCTURES ----------------------

    // We label each strand with offset + hairType
    struct StrandInfo
    {
        public int offset;    // index in positions[] and velocities[]
        public int hairType;  // 0 => Dome, 1 => Ring
    }

    // --------------- NativeArray Data ---------------
    // We keep the hair data in contiguous arrays to be used in the job.

    NativeArray<float3> positions;         // All segments' positions
    NativeArray<float3> velocities;        // All segments' velocities
    NativeArray<float> restLengths;        // Rope rest lengths
    NativeArray<StrandInfo> strands;       // Info about each strand
    NativeArray<float3> localRootPositions;// The local (relative) root pos for each strand

    // We also keep a separate array for pinnedRootPositions in world space, 
    // but we can fill it each frame from localRootPositions.
    NativeArray<float3> pinnedRootPositions; 

    // Managed references to line renderers
    LineRenderer[] lineRenderers;

    // Tracking total strands, segments
    int totalStrands;
    int totalSegments;
    int totalRestLinks;

    // For inertia
    Vector3 previousHeadPosition;
    Quaternion previousHeadRotation;

    Material bloomMat;
    SphereCollider skullCollider;
    bool isInitialized;

    // ---------------------------------------------------------------------
    void Awake()
    {
        skullCollider = GetComponent<SphereCollider>() ?? gameObject.AddComponent<SphereCollider>();
        skullCollider.isTrigger = true;
        skullCollider.radius = skullRadius;
    }

    public void Init(Material bloom)
    {
        if (isInitialized) return;
        isInitialized = true;
        
        bloomMat = bloom;
        GenerateHair();

        previousHeadPosition = transform.position;
        previousHeadRotation = transform.rotation;
    }

    // ---------------------------------------------------------------------
    // Generation
    // ---------------------------------------------------------------------

    void GenerateHair()
    {
        ClearHair();

        // We'll accumulate data for each strand in lists
        List<Vector3> strandRoots = new List<Vector3>();
        List<float> strandAlphas= new List<float>();
        List<HairType> strandTypes = new List<HairType>();

        // Generate top / dome hair
        GenerateTopDomeHair(strandRoots, strandAlphas, strandTypes);

        // Generate side / back hair
        GenerateSideBackHair(strandRoots, strandAlphas, strandTypes);

        // Build the arrays
        totalStrands = strandRoots.Count;
        if (totalStrands == 0) return;

        totalSegments  = totalStrands * segmentsPerStrand;
        totalRestLinks = totalStrands * (segmentsPerStrand - 1);

        positions            = new NativeArray<float3>(totalSegments,    Allocator.Persistent);
        velocities           = new NativeArray<float3>(totalSegments,    Allocator.Persistent);
        restLengths          = new NativeArray<float>(totalRestLinks,    Allocator.Persistent);
        strands              = new NativeArray<StrandInfo>(totalStrands, Allocator.Persistent);
        localRootPositions   = new NativeArray<float3>(totalStrands,     Allocator.Persistent);
        pinnedRootPositions  = new NativeArray<float3>(totalStrands,     Allocator.Persistent);

        lineRenderers = new LineRenderer[totalStrands];

        int segOffset  = 0;
        int restOffset = 0;
        const float alpha = 1;
        Color darkGrey = new(0.1f, 0.1f, 0.1f);

        for (int i = 0; i < totalStrands; i++)
        {
            // Mark info
            StrandInfo stInfo = new()
            {
                offset   = segOffset,
                hairType = (strandTypes[i] == HairType.Dome) ? 0 : 1
            };
            strands[i] = stInfo;

            // Store the local root (the rootPos is local to 'transform')
            // Actually, the code used transform.TransformPoint in the original generation. 
            // Let's keep it truly local:
            // We'll treat "rootPos" from generation as local. 
            // (If it's actually the sphere radius in local space, that's fine.)
            localRootPositions[i] = strandRoots[i];

            // Create a line renderer
            GameObject lrObj = new($"HairStrand_{i}");
            lrObj.transform.parent = transform;
            LineRenderer lr = lrObj.AddComponent<LineRenderer>();
            lr.positionCount = segmentsPerStrand;
            lr.startWidth    = strandThickness;
            lr.endWidth      = strandThickness * 0.3f;
            lr.material      = bloomMat;
            lr.useWorldSpace = true;
            
            Gradient followGradient = new();
            Color endColor = Color.Lerp(darkGrey, Color.magenta, 0.2f); // intensify on HDR 
                        
            followGradient.SetKeys(
                new[]
                {
                    new GradientColorKey(endColor, 0.0f),
                    new GradientColorKey(endColor, 1.0f)
                },
                new[]
                {
                    new GradientAlphaKey(alpha, 0.0f),
                    new GradientAlphaKey(alpha, 1.0f)
                }
            );
            lr.colorGradient = followGradient;

            lineRenderers[i] = lr;

            // Cardioid rest length
            float alphaVal = strandAlphas[i];
            float length   = cardioidScale * (1f - Mathf.Cos(alphaVal));
            float segLen   = length / (segmentsPerStrand - 1);

            for (int r = 0; r < (segmentsPerStrand - 1); r++)
            {
                restLengths[restOffset + r] = segLen;
            }

            // Initialize positions & velocities
            for (int seg = 0; seg < segmentsPerStrand; seg++)
            {
                positions[segOffset + seg]  = float3.zero;
                velocities[segOffset + seg] = float3.zero;
            }

            segOffset  += segmentsPerStrand;
            restOffset += (segmentsPerStrand - 1);
        }
    }

    void ClearHair()
    {
        if (positions.IsCreated)           positions.Dispose();
        if (velocities.IsCreated)          velocities.Dispose();
        if (restLengths.IsCreated)         restLengths.Dispose();
        if (strands.IsCreated)             strands.Dispose();
        if (localRootPositions.IsCreated)  localRootPositions.Dispose();
        if (pinnedRootPositions.IsCreated) pinnedRootPositions.Dispose();

        if (lineRenderers != null)
        {
            foreach (LineRenderer lr in lineRenderers)
            {
                if (lr) Destroy(lr.gameObject);
            }
            lineRenderers = null;
        }
    }

    void GenerateTopDomeHair(
      List<Vector3> roots,
      List<float> alphas,
      List<HairType> types)
    {
        if (topHairCount <= 0) return;

        float maxPhi = domeMaxAngle * Mathf.Deg2Rad;

        for (int i = 0; i < topHairCount; i++)
        {
            float phi = Random.Range(0f, maxPhi);
            float theta = Random.Range(0f, 2f * Mathf.PI);

            float x = skullRadius * Mathf.Sin(phi) * Mathf.Cos(theta);
            float y = skullRadius * Mathf.Cos(phi);
            float z = skullRadius * Mathf.Sin(phi) * Mathf.Sin(theta);

            Vector3 localRoot = new(x, y, z);
            float alphaVal = theta + (Mathf.PI / 2f);

            roots.Add(localRoot);
            alphas.Add(alphaVal);
            types.Add(HairType.Dome);
        }
    }

    void GenerateSideBackHair(
      List<Vector3> roots,
      List<float> alphas,
      List<HairType> types)
    {
        float startPhi = domeMaxAngle * Mathf.Deg2Rad;
        float endPhi   = (domeMaxAngle + ringAngleSpan) * Mathf.Deg2Rad;

        for (int layer = 0; layer < sideLayerCount; layer++)
        {
            float t = (sideLayerCount <= 1) ? 0f : (float)layer / (sideLayerCount - 1);
            float phi = Mathf.Lerp(startPhi, endPhi, t);

            float minTheta = 0f;
            float maxTheta = Mathf.PI;

            for (int i = 0; i < sideStrandsPerLayer; i++)
            {
                float tf = (sideStrandsPerLayer <= 1) ? 0f : (float)i / (sideStrandsPerLayer - 1);
                float theta = Mathf.Lerp(minTheta, maxTheta, tf);

                float x = skullRadius * Mathf.Sin(phi) * Mathf.Cos(theta);
                float y = skullRadius * Mathf.Cos(phi);
                float z = skullRadius * Mathf.Sin(phi) * Mathf.Sin(theta);

                Vector3 localRoot = new(x, y, z);
                float alphaVal    = 2f * theta;

                roots.Add(localRoot);
                alphas.Add(alphaVal);
                types.Add(HairType.Ring);
            }
        }
    }

    // ---------------------------------------------------------------------
    // Main update
    // ---------------------------------------------------------------------
    void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f || totalStrands == 0) return;

        // 1) Update pinned root positions to match the object's current transform
        //    So if the transform moved, the hair root moves too.
        for (int i = 0; i < totalStrands; i++)
        {
            float3 localRoot = localRootPositions[i];
            float3 worldRoot = transform.TransformPoint(localRoot);
            pinnedRootPositions[i] = worldRoot;
        }

        // 2) Compute head motion for inertia
        Vector3 headPosNow  = transform.position;
        Quaternion headRotNow = transform.rotation;

        Vector3 linearVel = (headPosNow - previousHeadPosition) / dt;

        Quaternion deltaRot = headRotNow * Quaternion.Inverse(previousHeadRotation);
        deltaRot.ToAngleAxis(out float deltaAngleDeg, out Vector3 axis);
        if (axis == Vector3.zero) axis = Vector3.up;
        float deltaAngleRad = deltaAngleDeg * Mathf.Deg2Rad;
        Vector3 angularVel  = axis.normalized * (deltaAngleRad / dt);

        // 3) Run sub-steps
        float stepDt = dt / subSteps;

        for (int s = 0; s < subSteps; s++)
        {
            ApplyHeadMotion(linearVel, angularVel, stepDt);

            // Job 1 sub-step
            HairSimulationJob job = new()
            {
                positions          = positions,
                velocities         = velocities,
                restLengths        = restLengths,
                strands            = strands,
                pinnedRootPositions= pinnedRootPositions,

                segmentsPerStrand  = segmentsPerStrand,
                constraintIterations= constraintIterations,
                hairMass           = hairMass,
                dt                 = stepDt,
                damping            = damping,
                collisionFriction  = collisionFriction,
                gravity            = gravity,
                skullCenter        = transform.position,
                skullRadius        = skullRadius
            };

            JobHandle handle = job.Schedule(totalStrands, 1);
            handle.Complete();
        }

        // 4) Update line renderers
        UpdateLineRenderers();

        // 5) Save previous transform
        previousHeadPosition = headPosNow;
        previousHeadRotation = headRotNow;
    }

    void OnDestroy()
    {
        ClearHair();
    }

    // ---------------------------------------------------------------------
    // Move hair segments based on head motion (like inertia).
    // This is on the main thread for clarity, but can also be a job.
    // ---------------------------------------------------------------------
    void ApplyHeadMotion(Vector3 linearVel, Vector3 angularVel, float dt)
    {
        float3 objPos = transform.position;

        for (int s = 0; s < totalStrands; s++)
        {
            StrandInfo stInfo = strands[s];
            int offset = stInfo.offset;

            // skip pinned root
            for (int i = 1; i < segmentsPerStrand; i++)
            {
                int segIdx = offset + i;

                float3 pos   = positions[segIdx];
                float3 relPos= pos - objPos;

                // linear
                pos += (float3)(linearVel * dt) / hairMass;

                // angular
                float3 tangential = math.cross(angularVel, relPos);
                pos += (tangential * dt) / hairMass;

                positions[segIdx] = pos;
            }
        }
    }

    // ---------------------------------------------------------------------
    // Copy final positions[] to line renderers
    // ---------------------------------------------------------------------
    void UpdateLineRenderers()
    {
        for (int i = 0; i < totalStrands; i++)
        {
            StrandInfo stInfo = strands[i];
            int offset = stInfo.offset;
            LineRenderer lr = lineRenderers[i];
            for (int seg = 0; seg < segmentsPerStrand; seg++)
            {
                lr.SetPosition(seg, positions[offset + seg]);
            }
        }
    }

    // ---------------------------------------------------------------------
    // The job that does one sub-step of hair physics per strand, in parallel.
    // ---------------------------------------------------------------------
    [BurstCompile]
    struct HairSimulationJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction]
        public NativeArray<float3> positions;

        [NativeDisableParallelForRestriction]
        public NativeArray<float3> velocities;

        [NativeDisableParallelForRestriction]
        public NativeArray<float> restLengths;

        [NativeDisableParallelForRestriction]
        public NativeArray<StrandInfo> strands;

        // The pinned root in world space, updated each frame
        [NativeDisableParallelForRestriction]
        public NativeArray<float3> pinnedRootPositions;

        public int segmentsPerStrand;
        public int constraintIterations;
        public float hairMass;
        public float dt;
        public float damping;
        public float collisionFriction;
        public float3 gravity;
        public float3 skullCenter;
        public float skullRadius;

        public void Execute(int strandIndex)
        {
            StrandInfo st = strands[strandIndex];
            int offset    = st.offset;
            bool isRing   = (st.hairType == 1);

            // 1) Pin root to pinnedRootPositions
            float3 pinnedRoot = pinnedRootPositions[strandIndex];
            positions[offset]  = pinnedRoot;
            velocities[offset] = float3.zero;

            // 2) Gravity + damping for segments 1..end
            for (int i = 1; i < segmentsPerStrand; i++)
            {
                int idx = offset + i;
                float3 vel = velocities[idx];
                vel += gravity * dt;            // gravity
                vel -= vel * (damping * dt);    // damping
                float3 pos = positions[idx] + vel * dt;
                positions[idx]  = pos;
                velocities[idx] = vel;
            }

            // 3) Rope constraints
            // rest lengths for this strand => (strandIndex*(segmentsPerStrand-1)).. 
            int restBase = strandIndex * (segmentsPerStrand - 1);

            for (int iter = 0; iter < constraintIterations; iter++)
            {
                for (int i = 0; i < (segmentsPerStrand - 1); i++)
                {
                    int idxA = offset + i;
                    int idxB = offset + (i + 1);
                    float3 pA = positions[idxA];
                    float3 pB = positions[idxB];

                    float restLen = restLengths[restBase + i];
                    float3 dir = pB - pA;
                    float dist = math.length(dir);
                    if (dist < 1e-8f) continue;

                    float diff = dist - restLen;
                    float3 correction = (diff / dist) * 0.5f * dir;

                    if (i == 0)
                    {
                        // pinned => only move pB
                        pB -= (correction * 2f) / hairMass;
                        float3 dv = (correction * 2f) / (dt * hairMass);
                        velocities[idxB] -= dv;
                    }
                    else
                    {
                        pA += correction / hairMass;
                        pB -= correction / hairMass;

                        float3 dv = correction / (dt * hairMass);
                        velocities[idxA] += dv;
                        velocities[idxB] -= dv;
                    }

                    positions[idxA] = pA;
                    positions[idxB] = pB;
                }
            }

            // 4) SELECTIVE collision
            if (!isRing)
            {
                // Only first 3 segments for dome
                for (int i = 0; i < math.min(3, segmentsPerStrand); i++)
                {
                    int idx = offset + i;
                    float3 pos = positions[idx];
                    float3 toSegment = pos - skullCenter;
                    float dist = math.length(toSegment);

                    if (dist < skullRadius)
                    {
                        float3 normal = math.normalize(toSegment);
                        pos = skullCenter + normal * skullRadius;

                        float3 vel = velocities[idx];
                        float normalVel = math.dot(vel, normal);
                        if (math.abs(normalVel) > 0f)
                        {
                            vel -= normalVel * normal;
                        }

                        // friction
                        float3 tangent = vel - math.projectsafe(vel, normal);
                        vel = tangent * collisionFriction;

                        positions[idx]  = pos;
                        velocities[idx] = vel;
                    }
                }
            }
        }
    }
}
