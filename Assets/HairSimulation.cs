using System.Collections.Generic;
using UnityEngine;

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
    public float cardioidScale = 0.12f;

    [Header("Top Hair Generation")]
    public int topHairCount = 20;
    public float domeMaxAngle = 60f;

    [Header("Side/Back Hair Generation")]
    public int sideLayerCount = 3;
    public int sideStrandsPerLayer = 10;
    [Tooltip("Degrees of scalp coverage for side/back hair beyond the dome.")]
    public float ringAngleSpan = 60f;
    
    [Header("Strand Parameters")]
    public int segmentsPerStrand = 6;
    public float strandThickness = 0.035f;

    [Header("Physics Parameters")]
    [Tooltip("Gravity applied to hair segments (m/s^2).")]
    public Vector3 gravity = new(0, -9.81f, 0);

    [Tooltip("Substeps per frame for stability.")]
    public int subSteps = 10;

    [Tooltip("Constraint relaxation passes per sub-step.")]
    public int constraintIterations = 2;

    [Tooltip("Damping factor for velocity (higher => hair settles more quickly).")]
    public float damping = 3.0f;

    [Tooltip("Friction at skull collision (0 => no sliding, 1 => full tangential speed).")]
    public float collisionFriction = 0.5f;

    // ---------------- NEW ----------------
    [Header("Mass / Inertia")]
    [Tooltip("Larger mass => hair reacts less to constraints, collisions, head motion.")]
    public float hairMass = 50f;
    // -------------------------------------

    class HairStrand
    {
        public Vector3[] positions;
        public Vector3[] velocities;
        public float[] segmentRestLengths;
        public LineRenderer lineRenderer;
        public Vector3 rootPosition;
        public float alpha;
        public HairType hairType;
    }

    readonly List<HairStrand> hairStrands = new();

    SphereCollider skullCollider;

    Vector3 previousHeadPosition;
    Quaternion previousHeadRotation;

    Material bloomMat;

    void Awake()
    {
        skullCollider = GetComponent<SphereCollider>() ?? gameObject.AddComponent<SphereCollider>();
        skullCollider.isTrigger = true;
        skullCollider.radius = skullRadius;
        
    }

    public void Init(Material bloom)
    {
        bloomMat = bloom;
        GenerateHair();

        previousHeadPosition = transform.position;
        previousHeadRotation = transform.rotation;
    }

    void GenerateHair()
    {
        ClearHair();
        GenerateTopDomeHair();
        GenerateSideBackHair();
        InitializeStrands();
    }

    void ClearHair()
    {
        foreach (HairStrand strand in hairStrands)
        {
            if (strand.lineRenderer)
                Destroy(strand.lineRenderer.gameObject);
        }
        hairStrands.Clear();
    }

    void GenerateTopDomeHair()
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
            Vector3 rootPos = new(x, y, z);

            float alpha = theta + Mathf.PI/2;

            HairStrand strand = new()
            {
                rootPosition = rootPos,
                alpha = alpha,
                hairType = HairType.Dome
            };
            hairStrands.Add(strand);
        }
    }

    void GenerateSideBackHair()
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
                Vector3 rootPos = new(x, y, z);

                float alpha = 2f * theta;

                HairStrand strand = new()
                {
                    rootPosition = rootPos,
                    alpha = alpha,
                    hairType = HairType.Ring
                };
                hairStrands.Add(strand);
            }
        }
    }
    
    readonly Color darkGrey = new(0.1f, 0.1f, 0.1f);

    void InitializeStrands()
    {
        float alpha = 1;
        foreach (HairStrand strand in hairStrands)
        {
            strand.positions = new Vector3[segmentsPerStrand];
            strand.velocities = new Vector3[segmentsPerStrand];
            strand.segmentRestLengths = new float[segmentsPerStrand - 1];

            GameObject lrObj = new("HairStrand");
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

            strand.lineRenderer = lr;

            float length = cardioidScale * (1f - Mathf.Cos(strand.alpha));
            float segLen = length / (segmentsPerStrand - 1);

            for (int i = 0; i < strand.segmentRestLengths.Length; i++)
                strand.segmentRestLengths[i] = segLen;

            Vector3 rootWorld = transform.TransformPoint(strand.rootPosition);
            for (int i = 0; i < segmentsPerStrand; i++)
            {
                strand.positions[i]  = rootWorld;
                strand.velocities[i] = Vector3.zero;
            }
        }
    }

    void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        Vector3 headPosNow = transform.position;
        Quaternion headRotNow = transform.rotation;

        Vector3 linearVel = (headPosNow - previousHeadPosition) / dt;

        Quaternion deltaRot = headRotNow * Quaternion.Inverse(previousHeadRotation);
        deltaRot.ToAngleAxis(out float deltaAngleDeg, out Vector3 axis);
        if (axis == Vector3.zero) axis = Vector3.up;
        float deltaAngleRad = deltaAngleDeg * Mathf.Deg2Rad;
        Vector3 angularVel  = axis.normalized * (deltaAngleRad / dt);

        float subDt = dt / subSteps;
        for (int s = 0; s < subSteps; s++)
        {
            ApplyHeadMotion(linearVel, angularVel, subDt);
            UpdateHair(subDt);
        }

        previousHeadPosition = headPosNow;
        previousHeadRotation = headRotNow;
    }

    /// <summary>
    /// We scale head motion impulses by 1/hairMass
    /// so heavier hair isn't moved as violently when the head moves.
    /// </summary>
    void ApplyHeadMotion(Vector3 linearVel, Vector3 angularVel, float dt)
    {
        foreach (HairStrand strand in hairStrands)
        {
            for (int i = 1; i < segmentsPerStrand; i++)
            {
                // linear
                strand.positions[i] += (linearVel * dt) / hairMass;

                // angular
                Vector3 relPos = strand.positions[i] - transform.position;
                Vector3 tangentialVel = Vector3.Cross(angularVel, relPos);
                strand.positions[i] += (tangentialVel * dt) / hairMass;
            }
        }
    }

    /// <summary>
    /// Integrate with gravity normally (same acceleration for all),
    /// but scale constraint/collision impulses by 1/hairMass
    /// so heavier hair doesn't fling around from constraints.
    /// 
    /// For collisions, ring hair is ignored, dome hair only for first 3 segments.
    /// </summary>
    void UpdateHair(float dt)
    {
        // 1) Pin root, apply gravity & damping
        foreach (HairStrand strand in hairStrands)
        {
            // Root pinned
            Vector3 rootWorld = transform.TransformPoint(strand.rootPosition);
            strand.positions[0] = rootWorld;
            strand.velocities[0] = Vector3.zero;

            for (int i = 1; i < segmentsPerStrand; i++)
            {
                // Gravity => same for all mass if we want same fall speed
                strand.velocities[i] += gravity * dt;

                // Damping
                strand.velocities[i] -= strand.velocities[i] * (damping * dt);

                // Integrate
                strand.positions[i] += strand.velocities[i] * dt;
            }
        }

        // 2) Rope constraints
        for (int iter = 0; iter < constraintIterations; iter++)
        {
            foreach (HairStrand strand in hairStrands)
            {
                for (int i = 0; i < segmentsPerStrand - 1; i++)
                {
                    Vector3 p1 = strand.positions[i];
                    Vector3 p2 = strand.positions[i + 1];

                    float restLen = strand.segmentRestLengths[i];
                    Vector3 dir = p2 - p1;
                    float dist = dir.magnitude;
                    if (dist < 1e-8f) continue;

                    float diff = dist - restLen;
                    Vector3 correction = (diff / dist) * 0.5f * dir;

                    if (i == 0)
                    {
                        // Move p2 only
                        strand.positions[i + 1] = p2 - (correction * 2f) / hairMass;

                        // Adjust velocity, scaled by 1/hairMass
                        strand.velocities[i + 1] -= (correction * 2f) / (dt * hairMass);
                    }
                    else
                    {
                        strand.positions[i]   = p1 + (correction / hairMass);
                        strand.positions[i+1] = p2 - (correction / hairMass);

                        strand.velocities[i]   += correction / (dt * hairMass);
                        strand.velocities[i+1] -= correction / (dt * hairMass);
                    }
                }
            }
        }

        // 3) SELECTIVE Collision
        Vector3 skullCenter = transform.position;
        foreach (HairStrand strand in hairStrands)
        {
            if (strand.hairType == HairType.Ring)
                continue; // ring hair ignores collision

            // only first 3 segments for dome
            for (int i = 0; i < segmentsPerStrand; i++)
            {
                if (i >= 3) break;

                Vector3 toSegment = strand.positions[i] - skullCenter;
                float dist = toSegment.magnitude;
                if (dist < skullRadius)
                {
                    Vector3 normal = toSegment.normalized;
                    strand.positions[i] = skullCenter + normal * skullRadius;

                    float normalVel = Vector3.Dot(strand.velocities[i], normal);
                    if (Mathf.Abs(normalVel) > 0f)
                    {
                        // remove normal velocity, scaled by mass
                        strand.velocities[i] -= normalVel * normal;
                    }

                    // friction on tangential
                    Vector3 tangentVel = Vector3.ProjectOnPlane(strand.velocities[i], normal);
                    // no mass scaling for friction factor, but you can tweak if desired:
                    strand.velocities[i] = tangentVel * collisionFriction;
                }
            }
        }

        // 4) Update line renderers
        foreach (HairStrand strand in hairStrands)
        {
            for (int i = 0; i < segmentsPerStrand; i++)
            {
                strand.lineRenderer.SetPosition(i, strand.positions[i]);
            }
        }
    }
}
