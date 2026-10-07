using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Burst jobs of the guide-strand hair (HairStrands). Positions live in the avatar's local space (capture space);
/// every strand is independent, so one job instance runs all substeps of one capture-frame step per strand.
/// </summary>
namespace HeadMovementHair
{
    /// <summary>a capsule at the start (a0, b0) and end (a1, b1) of a capture-frame step; radius r</summary>
    public struct Capsule
    {
        public float3 a0, b0, a1, b1;
        public float r;
    }

    /// <summary>
    /// XPBD-style guide strands: Verlet integration with damping and gravity, then per iteration a head-relative
    /// shape constraint (stiff at the root, free at the tip), a bending constraint (distance i-2 .. i, XPBD compliance)
    /// and a stretch constraint (distance i-1 .. i), then capsule collisions with friction and a follow-the-leader
    /// clamp that limits stretch to MaxStretch. Points 0 and 1 are pinned to the head (root + root direction). Head
    /// pose and capsules are interpolated linearly across the substeps of the step.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, DisableSafetyChecks = true)]
    public struct StrandSimJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<float3> pos;
        [NativeDisableParallelForRestriction] public NativeArray<float3> prev;
        [ReadOnly] public NativeArray<float3> restLocal;   // head-local rest shape (G * S)
        [ReadOnly] public NativeArray<float> segLen;       // per guide
        [ReadOnly] public NativeArray<float> bendRest;     // G * S: |rest_i - rest_(i-2)|
        [ReadOnly] public NativeArray<float> shapeK;       // S: shape stiffness per iteration
        [ReadOnly] public NativeArray<Capsule> capsules;
        [NativeDisableParallelForRestriction] public NativeArray<int> resets; // per guide: 1 when the strand was re-hung

        public int points;
        public int substeps;
        public int iterations;
        public float dt;              // seconds per substep
        public float damping;         // 1/s
        public float bendCompliance;  // XPBD compliance (m/N-ish units, scaled by 1/dt^2)
        public float friction;
        public float particleRadius;
        public float maxSpeed;        // m/s clamp (safety)
        public float maxStretch;      // e.g. 1.02
        public float maxDeviation;    // a point may stray at most maxDeviation x its arc length (+2 cm) from its
                                      // head-relative rest target (no strand flips over the head); <= 0 = off
        public float3 gravity;
        public float3 head0, head1;
        public quaternion rot0, rot1;

        public void Execute(int g)
        {
            int o = g * points;
            float L = segLen[g];
            float alphaBend = bendCompliance / (dt * dt);
            float3 gdt2 = gravity * dt * dt;
            float keep = math.saturate(1f - damping * dt);
            float vmax = maxSpeed * dt;

            for (int s = 0; s < substeps; s++)
            {
                float a = (s + 1f) / substeps;
                float3 hp = math.lerp(head0, head1, a);
                quaternion hr = math.nlerp(rot0, rot1, a);

                // pinned root and root direction
                for (int i = 0; i < 2; i++)
                {
                    float3 p = hp + math.mul(hr, restLocal[o + i]);
                    prev[o + i] = pos[o + i];
                    pos[o + i] = p;
                }

                // integrate
                for (int i = 2; i < points; i++)
                {
                    float3 p = pos[o + i];
                    float3 v = (p - prev[o + i]) * keep;
                    float sp = math.length(v);
                    if (sp > vmax) v *= vmax / sp;
                    prev[o + i] = p;
                    pos[o + i] = p + v + gdt2;
                }

                for (int it = 0; it < iterations; it++)
                {
                    // shape: toward the head-relative rest shape (style volume near the roots)
                    for (int i = 2; i < points; i++)
                    {
                        float k = shapeK[i];
                        if (k <= 0f) continue;
                        float3 target = hp + math.mul(hr, restLocal[o + i]);
                        pos[o + i] += (target - pos[o + i]) * k;
                    }

                    // bending: distance i-2 .. i (XPBD, compliant)
                    for (int i = 2; i < points; i++)
                    {
                        const float w0 = 0f; // one-sided (root to tip): a two-sided distance bend fights the stretch / collision projections and pumps energy into the strand
                        const float w1 = 1f;
                        float3 d = pos[o + i] - pos[o + i - 2];
                        float len = math.length(d);
                        if (len < 1e-6f) continue;
                        float c = len - bendRest[o + i];
                        float lambda = -c / (w0 + w1 + alphaBend);
                        float3 n = d / len;
                        pos[o + i - 2] -= w0 * lambda * n;
                        pos[o + i] += w1 * lambda * n;
                    }

                    // stretch: distance i-1 .. i (stiff)
                    for (int i = 2; i < points; i++)
                    {
                        float w0 = i - 1 < 2 ? 0f : 1f;
                        float3 d = pos[o + i] - pos[o + i - 1];
                        float len = math.length(d);
                        if (len < 1e-6f) continue;
                        float c = len - L;
                        float3 n = d / len;
                        float wsum = w0 + 1f;
                        pos[o + i - 1] += w0 / wsum * c * n;
                        pos[o + i] -= 1f / wsum * c * n;
                    }
                }

                Collide(o, a);

                // deviation cone around the rest shape (velocity-neutral: prev moves with the point)
                if (maxDeviation > 0f)
                {
                    for (int i = 2; i < points; i++)
                    {
                        float3 target = hp + math.mul(hr, restLocal[o + i]);
                        float3 d = pos[o + i] - target;
                        float len = math.length(d);
                        float lim = 0.02f + maxDeviation * L * i;
                        if (len <= lim) continue;
                        float3 shift = d * (lim / len - 1f);
                        pos[o + i] += shift;
                        prev[o + i] += shift;
                    }
                }

                // follow-the-leader: never stretch beyond maxStretch (keeps every strand bounded by its length)
                for (int i = 2; i < points; i++)
                {
                    float3 d = pos[o + i] - pos[o + i - 1];
                    float len = math.length(d);
                    float lmax = L * maxStretch;
                    if (len > lmax) pos[o + i] = pos[o + i - 1] + d * (lmax / len);
                }
            }

            // blow-up guard: re-hang the strand on the head in its rest shape
            bool bad = false;
            float reach = L * (points - 1) * maxStretch + 0.3f; // strand length + root offset from the head joint
            for (int i = 0; i < points; i++)
            {
                float3 p = pos[o + i];
                if (!math.all(math.isfinite(p)) || math.lengthsq(p - head1) > reach * reach) bad = true;
            }

            if (bad)
            {
                for (int i = 0; i < points; i++)
                {
                    float3 p = head1 + math.mul(rot1, restLocal[o + i]);
                    pos[o + i] = p;
                    prev[o + i] = p;
                }

                resets[g] = resets[g] + 1;
            }
        }

        void Collide(int o, float a)
        {
            for (int c = 0; c < capsules.Length; c++)
            {
                Capsule cap = capsules[c];
                float3 ca = math.lerp(cap.a0, cap.a1, a);
                float3 cb = math.lerp(cap.b0, cap.b1, a);
                float r = cap.r + particleRadius;
                float3 ab = cb - ca;
                float abab = math.max(math.dot(ab, ab), 1e-12f);
                for (int i = 2; i < points; i++)
                {
                    float3 p = pos[o + i];
                    float t = math.saturate(math.dot(p - ca, ab) / abab);
                    float3 q = ca + ab * t;
                    float3 d = p - q;
                    float dist2 = math.lengthsq(d);
                    if (dist2 >= r * r) continue;
                    float dist = math.sqrt(dist2);
                    float3 n = dist > 1e-6f ? d / dist : new float3(0, 1, 0);
                    float3 np = q + n * r;
                    // velocity-neutral contact: the push-out itself adds no velocity (a deep penetration - the rest
                    // shape re-hung inside a raised arm, a fast torso - would otherwise fling the strand); the inward
                    // normal velocity is removed and friction takes part of the tangential velocity
                    float3 v = p - prev[o + i];
                    float vn = math.dot(v, n);
                    float3 vt = v - n * vn;
                    float3 vnew = vt * (1f - friction) + n * math.max(vn, 0f);
                    pos[o + i] = np;
                    prev[o + i] = np - vnew;
                }
            }
        }
    }

    /// <summary>a ribbon vertex of the dynamic stream: strand centre + tangent (w = side -1 / +1); the shader expands
    /// it across the view direction</summary>
    public struct RibbonVertex
    {
        public float3 position;
        public half4 tangent;
    }

    /// <summary>guide points -> ribbon vertices: the guide itself and its children (offsets in the guide's frame, outward
    /// N from the head centre and binormal B, clumping toward the tips)</summary>
    [BurstCompile(FloatMode = FloatMode.Fast, DisableSafetyChecks = true)]
    public struct RibbonMeshJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> pos;           // G * S guide points
        [ReadOnly] public NativeArray<float2> childOffset;   // R: offset of ribbon r in (N, B) units of spread
        [ReadOnly] public NativeArray<float2> wavePhase;     // R: (phase along the strand, direction angle in the N-B plane)
        [ReadOnly] public NativeArray<float> segLen;         // G
        [NativeDisableParallelForRestriction] public NativeArray<RibbonVertex> vertices; // R * S * 2
        [NativeDisableParallelForRestriction] public NativeArray<float3> ribbonMin, ribbonMax; // R
        public int points;
        public int ribbonsPerGuide;
        public float spread;
        public float clump;
        public float volume;      // child spread grows by this factor toward the mid-lengths
        public float waveAmp;     // m, soft waves through the mid-lengths (render only; the guides stay smooth)
        public float waveLength;  // m
        public float3 headCentre;

        public void Execute(int r)
        {
            int g = r / ribbonsPerGuide;
            int o = g * points;
            float2 off = childOffset[r];
            float2 ph = wavePhase[r];
            float len = segLen[g] * (points - 1);
            float3 mn = new(float.MaxValue), mx = new(float.MinValue);
            for (int i = 0; i < points; i++)
            {
                float3 p = pos[o + i];
                float3 t = pos[o + math.min(i + 1, points - 1)] - pos[o + math.max(i - 1, 0)];
                float tl = math.length(t);
                t = tl > 1e-7f ? t / tl : new float3(0, -1, 0);
                float3 n = p - headCentre;
                n -= t * math.dot(n, t);
                float nl = math.length(n);
                n = nl > 1e-7f ? n / nl : math.normalize(math.cross(t, new float3(1, 0, 0)) + 1e-4f);
                float3 b = math.cross(t, n);
                float s = (float)i / (points - 1);
                float k = spread * (1f + volume * math.smoothstep(0f, 0.35f, s)) * (1f - clump * math.smoothstep(0.55f, 1f, s));
                float amp = waveAmp * math.smoothstep(0.12f, 0.45f, s) * (1f - 0.3f * s);
                float wave = amp * math.sin(2f * math.PI * s * len / waveLength + ph.x);
                float3 c = p + (n * off.x + b * off.y) * k + (n * math.cos(ph.y) * 0.5f + b * math.sin(ph.y)) * wave;
                int v = (r * points + i) * 2;
                half4 tp = new((half)t.x, (half)t.y, (half)t.z, (half)(-1f));
                half4 tn = new((half)t.x, (half)t.y, (half)t.z, (half)1f);
                vertices[v] = new RibbonVertex { position = c, tangent = tp };
                vertices[v + 1] = new RibbonVertex { position = c, tangent = tn };
                mn = math.min(mn, c);
                mx = math.max(mx, c);
            }

            ribbonMin[r] = mn;
            ribbonMax[r] = mx;
        }
    }
}
