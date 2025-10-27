using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

[RequireComponent(typeof(CapsuleCollider))]
public class AdvancedHairSimulation : MonoBehaviour
{
    public enum HairstyleType
    {
        Long,
        Medium,
        Ponytail,
        Pigtails
    }

    public enum HairTexture
    {
        Straight,
        Wavy,
        Curly
    }

    public enum BangStyle
    {
        None,
        Short,
        LongSweptBack
    }

    [Header("Hairstyle Configuration")]
    public HairstyleType hairstyle = HairstyleType.Long;
    public HairTexture texture = HairTexture.Wavy;
    public BangStyle bangs = BangStyle.Short;
    
    [Header("Hair Distribution")]
    [Range(50, 500)]
    public int strandCount = 200;
    [Range(0.3f, 1.0f)]
    public float hairDensity = 0.8f;
    [Tooltip("Randomize strand distribution")]
    public bool randomizeDistribution = true;

    [Header("Strand Properties")]
    [Range(4, 20)]
    public int segmentsPerStrand = 12;
    [Range(0.001f, 0.02f)]
    public float strandThickness = 0.008f;
    [Range(0.0001f, 0.01f)]
    public float strandEndThickness = 0.002f;
    
    [Header("Hair Length")]
    [Range(0.1f, 0.6f)]
    public float baseHairLength = 0.3f;
    [Range(0.0f, 0.3f)]
    public float lengthVariation = 0.1f;

    [Header("Physics Parameters")]
    public Vector3 gravity = new(0, -9.81f, 0);
    [Range(1, 10)]
    public int subSteps = 4;
    [Range(2, 20)]
    public int constraintIterations = 8;
    
    [Range(0.5f, 20f)]
    public float stiffness = 5.0f;
    [Range(0.1f, 10f)]
    public float damping = 2.0f;
    [Range(0.01f, 2f)]
    public float mass = 0.5f;
    
    [Header("Cohesion & Smoothing")]
    [Range(0f, 1f)]
    [Tooltip("How much strands influence neighboring strands")]
    public float strandCohesion = 0.4f;
    [Range(0.01f, 0.15f)]
    [Tooltip("Maximum distance for strand interaction")]
    public float cohesionRadius = 0.05f;
    [Range(0f, 1f)]
    [Tooltip("Velocity damping between neighboring strands")]
    public float velocitySmoothing = 0.3f;
    [Range(0f, 1f)]
    [Tooltip("Global velocity damping to reduce jitter")]
    public float globalVelocityDamping = 0.85f;
    
    [Header("Collision")]
    public float headRadius = 0.08f;
    public float neckRadius = 0.04f;
    public float neckLength = 0.12f;
    [Range(0f, 1f)]
    public float collisionFriction = 0.3f;
    
    [Header("Wind")]
    public bool enableWind = true;
    [Range(0f, 5f)]
    public float windStrength = 1.0f;
    public float windFrequency = 0.5f;
    public Vector3 windDirection = new Vector3(1, 0, 0.5f);

    [Header("Curl/Wave Settings")]
    [Range(0f, 1f)]
    public float curlAmount = 0.3f;
    [Range(0.05f, 0.3f)]
    public float curlFrequency = 0.15f;
    [Range(0f, 360f)]
    public float curlPhaseOffset = 45f;

    [Header("Visual")]
    public Material hairMaterial;
    public Gradient hairColorGradient;
    
    // Internal data
    struct HairStrand
    {
        public int segmentOffset;
        public int segmentCount;
        public float length;
        public float curlPhase;
        public int group; // 0=main, 1=left pigtail, 2=right pigtail, 3=bangs
    }

    NativeArray<float3> positions;
    NativeArray<float3> prevPositions;
    NativeArray<float3> velocities;
    NativeArray<float> restLengths;
    NativeArray<HairStrand> strands;
    NativeArray<float3> rootPositions;
    NativeArray<float3> rootNormals;

    LineRenderer[] lineRenderers;
    int totalStrands;
    int totalSegments;

    Vector3 previousHeadPosition;
    Quaternion previousHeadRotation;
    float windTime;

    CapsuleCollider headCollider;
    bool isInitialized;

    void Awake()
    {
        headCollider = GetComponent<CapsuleCollider>() ?? gameObject.AddComponent<CapsuleCollider>();
        headCollider.isTrigger = true;
        headCollider.radius = headRadius;
        headCollider.height = headRadius * 2 + neckLength;
        headCollider.direction = 1; // Y-axis
        headCollider.center = new Vector3(0, -neckLength * 0.5f, 0);
    }

    public void Init(Material material = null)
    {
        if (isInitialized) return;
        isInitialized = true;

        if (material != null) hairMaterial = material;
        if (hairColorGradient == null) CreateDefaultGradient();

        GenerateHair();
        
        previousHeadPosition = transform.position;
        previousHeadRotation = transform.rotation;
    }

    void CreateDefaultGradient()
    {
        hairColorGradient = new Gradient();
        hairColorGradient.SetKeys(
            new GradientColorKey[]
            {
                new GradientColorKey(new Color(0.1f, 0.05f, 0.02f), 0f),
                new GradientColorKey(new Color(0.2f, 0.1f, 0.05f), 0.5f),
                new GradientColorKey(new Color(0.4f, 0.25f, 0.15f), 1f)
            },
            new GradientAlphaKey[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(0.8f, 1f)
            }
        );
    }

    void GenerateHair()
    {
        ClearHair();

        List<Vector3> roots = new List<Vector3>();
        List<Vector3> normals = new List<Vector3>();
        List<float> lengths = new List<float>();
        List<int> groups = new List<int>();

        switch (hairstyle)
        {
            case HairstyleType.Long:
            case HairstyleType.Medium:
                GenerateFullHair(roots, normals, lengths, groups);
                break;
            case HairstyleType.Ponytail:
                GeneratePonytail(roots, normals, lengths, groups);
                break;
            case HairstyleType.Pigtails:
                GeneratePigtails(roots, normals, lengths, groups);
                break;
        }

        if (bangs != BangStyle.None)
        {
            GenerateBangs(roots, normals, lengths, groups);
        }

        totalStrands = roots.Count;
        if (totalStrands == 0) return;

        totalSegments = totalStrands * segmentsPerStrand;

        positions = new NativeArray<float3>(totalSegments, Allocator.Persistent);
        prevPositions = new NativeArray<float3>(totalSegments, Allocator.Persistent);
        velocities = new NativeArray<float3>(totalSegments, Allocator.Persistent);
        restLengths = new NativeArray<float>(totalSegments - totalStrands, Allocator.Persistent);
        strands = new NativeArray<HairStrand>(totalStrands, Allocator.Persistent);
        rootPositions = new NativeArray<float3>(totalStrands, Allocator.Persistent);
        rootNormals = new NativeArray<float3>(totalStrands, Allocator.Persistent);

        lineRenderers = new LineRenderer[totalStrands];

        int segOffset = 0;
        int restOffset = 0;

        for (int i = 0; i < totalStrands; i++)
        {
            float strandLength = lengths[i];
            float segLength = strandLength / (segmentsPerStrand - 1);

            HairStrand strand = new HairStrand
            {
                segmentOffset = segOffset,
                segmentCount = segmentsPerStrand,
                length = strandLength,
                curlPhase = UnityEngine.Random.Range(0f, Mathf.PI * 2f),
                group = groups[i]
            };
            strands[i] = strand;

            rootPositions[i] = roots[i];
            rootNormals[i] = normals[i];

            // Initialize positions along the normal with curl
            Vector3 rootWorld = transform.TransformPoint(roots[i]);
            Vector3 normalWorld = transform.TransformDirection(normals[i]);

            for (int s = 0; s < segmentsPerStrand; s++)
            {
                int idx = segOffset + s;
                float t = (float)s / (segmentsPerStrand - 1);
                
                // Apply curl/wave pattern
                Vector3 offset = ApplyCurlPattern(normalWorld, t, strand.curlPhase, strandLength);
                
                Vector3 pos = rootWorld + offset * strandLength * t;
                positions[idx] = pos;
                prevPositions[idx] = pos;
                velocities[idx] = float3.zero;
            }

            // Rest lengths - slightly progressive stiffness
            for (int s = 0; s < segmentsPerStrand - 1; s++)
            {
                restLengths[restOffset + s] = segLength;
            }

            // Create line renderer
            GameObject lrObj = new GameObject($"HairStrand_{i}");
            lrObj.transform.SetParent(transform);
            LineRenderer lr = lrObj.AddComponent<LineRenderer>();
            lr.positionCount = segmentsPerStrand;
            lr.startWidth = strandThickness;
            lr.endWidth = strandEndThickness;
            lr.material = hairMaterial;
            lr.useWorldSpace = true;
            lr.colorGradient = hairColorGradient;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;

            lineRenderers[i] = lr;

            segOffset += segmentsPerStrand;
            restOffset += segmentsPerStrand - 1;
        }
    }

    Vector3 ApplyCurlPattern(Vector3 baseDirection, float t, float phase, float length)
    {
        if (texture == HairTexture.Straight) return baseDirection;

        float curlIntensity = texture == HairTexture.Curly ? curlAmount * 2f : curlAmount;
        
        // Create perpendicular vectors
        Vector3 perpendicular = Vector3.Cross(baseDirection, Vector3.up);
        if (perpendicular.magnitude < 0.01f)
            perpendicular = Vector3.Cross(baseDirection, Vector3.right);
        perpendicular.Normalize();
        
        Vector3 binormal = Vector3.Cross(baseDirection, perpendicular).normalized;

        // Apply sinusoidal curl
        float angle = t * curlFrequency * Mathf.PI * 2f + phase;
        float curlX = Mathf.Sin(angle) * curlIntensity * t;
        float curlZ = Mathf.Cos(angle + curlPhaseOffset * Mathf.Deg2Rad) * curlIntensity * t;

        return baseDirection + perpendicular * curlX + binormal * curlZ;
    }

    void GenerateFullHair(List<Vector3> roots, List<Vector3> normals, List<float> lengths, List<int> groups)
    {
        float lengthMultiplier = hairstyle == HairstyleType.Medium ? 0.6f : 1.0f;
        int count = Mathf.RoundToInt(strandCount * hairDensity);

        for (int i = 0; i < count; i++)
        {
            // Distribute over scalp (upper hemisphere)
            float phi = UnityEngine.Random.Range(0f, 80f) * Mathf.Deg2Rad;
            float theta = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;

            float x = headRadius * Mathf.Sin(phi) * Mathf.Cos(theta);
            float y = headRadius * Mathf.Cos(phi);
            float z = headRadius * Mathf.Sin(phi) * Mathf.Sin(theta);

            Vector3 rootPos = new Vector3(x, y, z);
            Vector3 normal = rootPos.normalized;

            float length = (baseHairLength + UnityEngine.Random.Range(-lengthVariation, lengthVariation)) * lengthMultiplier;

            roots.Add(rootPos);
            normals.Add(normal);
            lengths.Add(length);
            groups.Add(0);
        }
    }

    void GeneratePonytail(List<Vector3> roots, List<Vector3> normals, List<float> lengths, List<int> groups)
    {
        Vector3 ponytailCenter = new Vector3(0, headRadius * 0.3f, -headRadius * 0.8f);
        float ponytailRadius = headRadius * 0.3f;

        int count = Mathf.RoundToInt(strandCount * hairDensity);

        for (int i = 0; i < count; i++)
        {
            float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
            float radius = UnityEngine.Random.Range(0f, ponytailRadius);

            Vector3 offset = new Vector3(
                Mathf.Cos(angle) * radius,
                0,
                Mathf.Sin(angle) * radius
            );

            Vector3 rootPos = ponytailCenter + offset;
            Vector3 normal = new Vector3(0, -0.3f, -1f).normalized;

            float length = baseHairLength + UnityEngine.Random.Range(-lengthVariation, lengthVariation);

            roots.Add(rootPos);
            normals.Add(normal);
            lengths.Add(length);
            groups.Add(0);
        }
    }

    void GeneratePigtails(List<Vector3> roots, List<Vector3> normals, List<float> lengths, List<int> groups)
    {
        Vector3[] pigtailCenters = new Vector3[]
        {
            new Vector3(-headRadius * 0.7f, headRadius * 0.2f, 0),
            new Vector3(headRadius * 0.7f, headRadius * 0.2f, 0)
        };

        float pigtailRadius = headRadius * 0.25f;
        int strandsPerPigtail = Mathf.RoundToInt(strandCount * hairDensity * 0.5f);

        for (int p = 0; p < 2; p++)
        {
            Vector3 center = pigtailCenters[p];
            
            for (int i = 0; i < strandsPerPigtail; i++)
            {
                float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float radius = UnityEngine.Random.Range(0f, pigtailRadius);

                Vector3 offset = new Vector3(
                    0,
                    Mathf.Cos(angle) * radius,
                    Mathf.Sin(angle) * radius
                );

                Vector3 rootPos = center + offset;
                Vector3 normal = new Vector3(p == 0 ? -0.5f : 0.5f, -0.7f, 0).normalized;

                float length = baseHairLength * 0.8f + UnityEngine.Random.Range(-lengthVariation, lengthVariation);

                roots.Add(rootPos);
                normals.Add(normal);
                lengths.Add(length);
                groups.Add(p + 1);
            }
        }
    }

    void GenerateBangs(List<Vector3> roots, List<Vector3> normals, List<float> lengths, List<int> groups)
    {
        int bangCount = Mathf.RoundToInt(strandCount * 0.2f);
        float bangLength = bangs == BangStyle.Short ? 0.08f : 0.15f;

        for (int i = 0; i < bangCount; i++)
        {
            float x = UnityEngine.Random.Range(-headRadius * 0.6f, headRadius * 0.6f);
            float y = headRadius * UnityEngine.Random.Range(0.85f, 0.95f);
            float z = headRadius * UnityEngine.Random.Range(0.3f, 0.5f);

            Vector3 rootPos = new Vector3(x, y, z);
            Vector3 normal = bangs == BangStyle.LongSweptBack 
                ? new Vector3(x * 0.5f, 0.3f, 1f).normalized 
                : new Vector3(0, -0.2f, 1f).normalized;

            roots.Add(rootPos);
            normals.Add(normal);
            lengths.Add(bangLength);
            groups.Add(3);
        }
    }

    void ClearHair()
    {
        if (positions.IsCreated) positions.Dispose();
        if (prevPositions.IsCreated) prevPositions.Dispose();
        if (velocities.IsCreated) velocities.Dispose();
        if (restLengths.IsCreated) restLengths.Dispose();
        if (strands.IsCreated) strands.Dispose();
        if (rootPositions.IsCreated) rootPositions.Dispose();
        if (rootNormals.IsCreated) rootNormals.Dispose();

        if (lineRenderers != null)
        {
            foreach (var lr in lineRenderers)
            {
                if (lr) Destroy(lr.gameObject);
            }
            lineRenderers = null;
        }
    }

    void LateUpdate()
    {
        if (!isInitialized || totalStrands == 0) return;

        float dt = Time.deltaTime;
        if (dt <= 0f || dt > 0.05f) return; // Cap dt to prevent instability

        windTime += dt;

        Vector3 headPosNow = transform.position;
        Quaternion headRotNow = transform.rotation;

        Vector3 linearVel = (headPosNow - previousHeadPosition) / dt;
        Quaternion deltaRot = headRotNow * Quaternion.Inverse(previousHeadRotation);
        deltaRot.ToAngleAxis(out float deltaAngle, out Vector3 axis);
        if (axis == Vector3.zero) axis = Vector3.up;
        float angularSpeed = (deltaAngle * Mathf.Deg2Rad) / dt;
        Vector3 angularVel = axis.normalized * angularSpeed;

        // Clamp velocities to prevent explosions
        linearVel = Vector3.ClampMagnitude(linearVel, 10f);
        angularVel = Vector3.ClampMagnitude(angularVel, 20f);

        float stepDt = dt / subSteps;

        for (int s = 0; s < subSteps; s++)
        {
            Vector3 wind = enableWind ? CalculateWind() : Vector3.zero;

            HairPhysicsJob job = new HairPhysicsJob
            {
                positions = positions,
                prevPositions = prevPositions,
                velocities = velocities,
                restLengths = restLengths,
                strands = strands,
                rootPositions = rootPositions,
                rootNormals = rootNormals,
                
                headPosition = transform.position,
                headRotation = transform.rotation,
                headRadius = headRadius,
                neckRadius = neckRadius,
                neckLength = neckLength,
                
                segmentsPerStrand = segmentsPerStrand,
                constraintIterations = constraintIterations,
                dt = stepDt,
                stiffness = stiffness,
                damping = damping,
                mass = mass,
                gravity = gravity,
                wind = wind,
                collisionFriction = collisionFriction,
                globalVelocityDamping = globalVelocityDamping,
                
                headLinearVelocity = linearVel,
                headAngularVelocity = angularVel
            };

            JobHandle handle = job.Schedule(totalStrands, 4);
            handle.Complete();

            // Apply cohesion forces after main physics
            if (strandCohesion > 0.001f)
            {
                HairCohesionJob cohesionJob = new HairCohesionJob
                {
                    positions = positions,
                    velocities = velocities,
                    strands = strands,
                    segmentsPerStrand = segmentsPerStrand,
                    cohesionStrength = strandCohesion,
                    cohesionRadius = cohesionRadius,
                    velocitySmoothing = velocitySmoothing,
                    dt = stepDt
                };

                JobHandle cohesionHandle = cohesionJob.Schedule(totalStrands, 4);
                cohesionHandle.Complete();
            }
        }

        UpdateLineRenderers();

        previousHeadPosition = headPosNow;
        previousHeadRotation = headRotNow;
    }

    Vector3 CalculateWind()
    {
        float noise = Mathf.PerlinNoise(windTime * windFrequency, 0) * 2f - 1f;
        return windDirection.normalized * windStrength * (1f + noise * 0.5f);
    }

    void UpdateLineRenderers()
    {
        for (int i = 0; i < totalStrands; i++)
        {
            HairStrand strand = strands[i];
            LineRenderer lr = lineRenderers[i];
            
            for (int s = 0; s < segmentsPerStrand; s++)
            {
                lr.SetPosition(s, positions[strand.segmentOffset + s]);
            }
        }
    }

    void OnDestroy()
    {
        ClearHair();
    }

    [BurstCompile]
    struct HairPhysicsJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction]
        public NativeArray<float3> positions;
        
        [NativeDisableParallelForRestriction]
        public NativeArray<float3> prevPositions;
        
        [NativeDisableParallelForRestriction]
        public NativeArray<float3> velocities;
        
        [ReadOnly]
        public NativeArray<float> restLengths;
        
        [ReadOnly]
        public NativeArray<HairStrand> strands;
        
        [ReadOnly]
        public NativeArray<float3> rootPositions;
        
        [ReadOnly]
        public NativeArray<float3> rootNormals;

        public float3 headPosition;
        public quaternion headRotation;
        public float headRadius;
        public float neckRadius;
        public float neckLength;
        
        public int segmentsPerStrand;
        public int constraintIterations;
        public float dt;
        public float stiffness;
        public float damping;
        public float mass;
        public float3 gravity;
        public float3 wind;
        public float collisionFriction;
        public float globalVelocityDamping;
        
        public float3 headLinearVelocity;
        public float3 headAngularVelocity;

        public void Execute(int strandIndex)
        {
            HairStrand strand = strands[strandIndex];
            int offset = strand.segmentOffset;
            int count = strand.segmentCount;

            // Update root position
            float3 localRoot = rootPositions[strandIndex];
            float3 worldRoot = headPosition + math.rotate(headRotation, localRoot);
            positions[offset] = worldRoot;
            prevPositions[offset] = worldRoot;
            velocities[offset] = float3.zero;

            // Verlet integration with inertia (reduced effect)
            for (int i = 1; i < count; i++)
            {
                int idx = offset + i;
                float3 pos = positions[idx];
                float3 oldPos = prevPositions[idx];
                
                // Calculate segment factor (segments closer to root are stiffer)
                float segmentT = (float)i / (count - 1);
                float rootInfluence = 1f - segmentT * 0.7f; // Root has more influence
                
                // Inertia from head motion (reduced significantly)
                float3 relPos = pos - headPosition;
                float inertiaScale = 0.3f / (mass * (1f + segmentT)); // Reduce inertia effect
                float3 inertialForce = -headLinearVelocity * inertiaScale;
                float3 angularForce = -math.cross(headAngularVelocity, relPos) * inertiaScale * 0.5f;
                
                // Verlet with clamped velocity
                float3 vel = (pos - oldPos) / dt;
                
                // Clamp velocity magnitude
                float velMag = math.length(vel);
                if (velMag > 5f)
                {
                    vel = math.normalize(vel) * 5f;
                }
                
                vel += (gravity + wind * (1f - rootInfluence) + inertialForce + angularForce) * dt;
                
                // Progressive damping (more damping towards the tips)
                float dampingFactor = damping * (1f + segmentT * 0.5f);
                vel *= math.max(0f, 1f - dampingFactor * dt);
                
                // Global velocity damping to reduce jitter
                vel *= globalVelocityDamping;
                
                float3 newPos = pos + vel * dt;
                
                prevPositions[idx] = pos;
                positions[idx] = newPos;
                velocities[idx] = vel;
            }

            // Distance constraints with progressive stiffness
            int restOffset = strandIndex * (segmentsPerStrand - 1);
            
            for (int iter = 0; iter < constraintIterations; iter++)
            {
                for (int i = 0; i < count - 1; i++)
                {
                    int idxA = offset + i;
                    int idxB = offset + i + 1;
                    
                    float3 pA = positions[idxA];
                    float3 pB = positions[idxB];
                    
                    float3 delta = pB - pA;
                    float dist = math.length(delta);
                    if (dist < 1e-6f) continue;
                    
                    float restLen = restLengths[restOffset + i];
                    float diff = (dist - restLen) / dist;
                    float3 correction = delta * diff * 0.5f;
                    
                    // Progressive stiffness: stiffer near root
                    float segmentT = (float)i / (count - 1);
                    float progressiveStiffness = stiffness * (1f + (1f - segmentT) * 0.5f);
                    float stiffnessFactor = math.min(1f, progressiveStiffness * 0.1f);
                    
                    if (i == 0)
                    {
                        // Root is pinned, only move child
                        positions[idxB] -= correction * 2f * stiffnessFactor;
                    }
                    else
                    {
                        // Both segments move, but parent less
                        float parentWeight = 0.3f; // Parent moves less
                        float childWeight = 0.7f;  // Child moves more
                        
                        positions[idxA] += correction * stiffnessFactor * parentWeight;
                        positions[idxB] -= correction * stiffnessFactor * childWeight;
                    }
                }
            }

            // Collision detection
            for (int i = 0; i < count; i++)
            {
                int idx = offset + i;
                float3 pos = positions[idx];
                
                // Head sphere collision
                float3 toSegment = pos - headPosition;
                float dist = math.length(toSegment);
                
                if (dist < headRadius)
                {
                    float3 normal = math.normalize(toSegment);
                    positions[idx] = headPosition + normal * headRadius;
                    
                    float3 vel = velocities[idx];
                    float normalVel = math.dot(vel, normal);
                    vel -= normal * normalVel;
                    velocities[idx] = vel * collisionFriction;
                }
                
                // Neck capsule collision
                float3 neckBottom = headPosition + math.rotate(headRotation, new float3(0, -neckLength, 0));
                float3 neckTop = headPosition;
                float3 toPoint = pos - neckBottom;
                float3 neckAxis = neckTop - neckBottom;
                float neckAxisLen = math.length(neckAxis);
                
                if (neckAxisLen > 0.001f)
                {
                    float3 neckDir = neckAxis / neckAxisLen;
                    float projection = math.clamp(math.dot(toPoint, neckDir), 0f, neckAxisLen);
                    float3 closestPoint = neckBottom + neckDir * projection;
                    float3 toSegmentNeck = pos - closestPoint;
                    float distNeck = math.length(toSegmentNeck);
                    
                    if (distNeck < neckRadius)
                    {
                        float3 normalNeck = math.normalize(toSegmentNeck);
                        positions[idx] = closestPoint + normalNeck * neckRadius;
                        
                        float3 vel = velocities[idx];
                        float normalVelNeck = math.dot(vel, normalNeck);
                        vel -= normalNeck * normalVelNeck;
                        velocities[idx] = vel * collisionFriction;
                    }
                }
            }
        }
    }

    [BurstCompile]
    struct HairCohesionJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction]
        public NativeArray<float3> positions;
        
        [NativeDisableParallelForRestriction]
        public NativeArray<float3> velocities;
        
        [ReadOnly]
        public NativeArray<HairStrand> strands;
        
        public int segmentsPerStrand;
        public float cohesionStrength;
        public float cohesionRadius;
        public float velocitySmoothing;
        public float dt;

        public void Execute(int strandIndex)
        {
            HairStrand strand = strands[strandIndex];
            int offset = strand.segmentOffset;
            int count = strand.segmentCount;
            
            // Skip root
            for (int i = 1; i < count; i++)
            {
                int idx = offset + i;
                float3 pos = positions[idx];
                float3 vel = velocities[idx];
                
                float3 avgPos = float3.zero;
                float3 avgVel = float3.zero;
                int neighborCount = 0;
                
                // Check nearby strands (simple O(n^2), could use spatial hashing for optimization)
                for (int otherStrand = 0; otherStrand < strands.Length; otherStrand++)
                {
                    if (otherStrand == strandIndex) continue;
                    
                    // Only check strands in same group for cohesion
                    if (strands[otherStrand].group != strand.group) continue;
                    
                    HairStrand other = strands[otherStrand];
                    int otherIdx = other.segmentOffset + i;
                    
                    // Make sure the other strand has this segment
                    if (i >= other.segmentCount) continue;
                    
                    float3 otherPos = positions[otherIdx];
                    float dist = math.distance(pos, otherPos);
                    
                    if (dist < cohesionRadius && dist > 0.001f)
                    {
                        float weight = 1f - (dist / cohesionRadius);
                        avgPos += otherPos * weight;
                        avgVel += velocities[otherIdx] * weight;
                        neighborCount++;
                    }
                }
                
                if (neighborCount > 0)
                {
                    avgPos /= neighborCount;
                    avgVel /= neighborCount;
                    
                    // Segment influence: less cohesion near root
                    float segmentT = (float)i / (count - 1);
                    float cohesionFactor = cohesionStrength * segmentT;
                    
                    // Move towards average position
                    float3 cohesionForce = (avgPos - pos) * cohesionFactor;
                    positions[idx] = pos + cohesionForce * dt * 10f;
                    
                    // Smooth velocities
                    velocities[idx] = math.lerp(vel, avgVel, velocitySmoothing * segmentT);
                }
            }
        }
    }
}
