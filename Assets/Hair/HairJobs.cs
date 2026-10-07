using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Burst jobs of the guide-strand hair (HairStrands). Positions live in the avatar's local space (capture space);
/// every strand is independent, so one job instance runs all substeps of one capture-frame step per strand.
/// StrandSim holds the per-strand solver (shared by the live step job and the bake job, so both give the same hair);
/// CardMeshJob / GlowRibbonJob turn the guide points into the hair cards and the tip glow ribbons; the Atlas jobs
/// generate the procedural strand atlas (HairAtlas).
/// </summary>
namespace HeadMovementHair
{
    /// <summary>a capsule at the start (a0, b0) and end (a1, b1) of a capture-frame step; radius r. firstPoint: points
    /// below it ignore the capsule (arms: the scalp-hugging top is never squeezed between an arm and the head);
    /// friction &lt; 0 = the solver's friction; maxPush &gt; 0 caps the push-out per substep (trapped hair slides
    /// instead of jittering)</summary>
    public struct Capsule
    {
        public float3 a0, b0, a1, b1;
        public float r;
        public int firstPoint;
        public float friction;
        public float maxPush;
    }

    /// <summary>
    /// Head-attached exclusion box in front of the face (head-bone space; r = head-sphere radius). A point is inside
    /// when it is more than fwdMin r in front of the head-sphere centre, between yLo r and yHi r of its height and
    /// within halfWidth r sideways. It is pushed out (velocity-neutral) sideways to halfWidth r + particle radius +
    /// margin on its guide's side of the part (a side bias decides near the middle), or down under the chin when that
    /// is shorter (a strand swinging up from the chest). Never up or forward: a box that pushes up is a shelf the
    /// front strands rest on (a visor over the eyes).
    /// </summary>
    public struct FaceBox
    {
        public float3 centre;
        public float r, fwdMin, yLo, yHi, halfWidth, margin, sideBias;
        public int enabled;

        public bool Inside(float3 q)
        {
            float3 d = q - centre;
            return enabled != 0 && -d.z > fwdMin * r && d.y > yLo * r && d.y < yHi * r && math.abs(d.x) < halfWidth * r;
        }

        /// <summary>head-local offset that moves q out of the box (zero when outside)</summary>
        public float3 Push(float3 q, float side, float radius)
        {
            if (!Inside(q)) return float3.zero;
            float3 d = q - centre;
            float edge = halfWidth * r + radius + margin;
            float s = d.x + sideBias * r * side;
            s = s > 0f ? 1f : s < 0f ? -1f : (side >= 0f ? 1f : -1f);
            float dx = s * edge - d.x;
            float dy = yLo * r - radius - margin - d.y; // negative: down below the chin
            return -dy < math.abs(dx) ? new float3(0, dy, 0) : new float3(dx, 0, 0);
        }
    }

    /// <summary>one capture-frame step (or a static settle) of the solver</summary>
    public struct SimParams
    {
        public int points, substeps, iterations;
        public float dt;              // seconds per substep
        public float damping;         // 1/s
        public float bendCompliance;  // XPBD compliance (scaled by 1/dt^2)
        public float friction;
        public float particleRadius;
        public float maxSpeed;        // m/s clamp (safety)
        public float maxStretch;      // e.g. 1.02
        public float3 gravity;
        public float3 head0, head1;
        public quaternion rot0, rot1;
        public FaceBox face;
        public int capStart, capCount;
    }

    /// <summary>
    /// XPBD-style guide strands: Verlet integration with damping and gravity, then per iteration a head-relative
    /// shape constraint (stiff at the root, free at the tip; per guide: the front-right section is held longer), a
    /// bending constraint (distance i-2 .. i, XPBD compliance)
    /// and a stretch constraint (distance i-1 .. i); then capsule collisions (arms first, then the body), the face box,
    /// a deviation cone around the rest shape and a follow-the-leader clamp that limits stretch to maxStretch. Points 0
    /// and 1 are pinned to the head (root + root direction). Head pose and capsules are interpolated linearly across the
    /// substeps of the step. guideInfo = (max deviation x arc length, side of the part -1 / +1).
    /// </summary>
    public struct StrandSim
    {
        public NativeArray<float3> pos, prev;
        public NativeArray<float3> restLocal;
        public NativeArray<float> segLen, bendRest, shapeK;
        public NativeArray<float2> guideInfo;
        public NativeArray<Capsule> capsules;
        public NativeArray<int> resets;

        public void Step(int g, in SimParams p)
        {
            int points = p.points;
            int o = g * points;
            float L = segLen[g];
            float2 info = guideInfo[g];
            float alphaBend = p.bendCompliance / (p.dt * p.dt);
            float3 gdt2 = p.gravity * p.dt * p.dt;
            float keep = math.saturate(1f - p.damping * p.dt);
            float vmax = p.maxSpeed * p.dt;
            int ko = shapeK.Length >= o + points ? o : 0; // shape stiffness per guide (G * S) or one profile (S)

            for (int s = 0; s < p.substeps; s++)
            {
                float a = (s + 1f) / p.substeps;
                float3 hp = math.lerp(p.head0, p.head1, a);
                quaternion hr = math.nlerp(p.rot0, p.rot1, a);

                // pinned root and root direction
                for (int i = 0; i < 2; i++)
                {
                    float3 q = hp + math.mul(hr, restLocal[o + i]);
                    prev[o + i] = pos[o + i];
                    pos[o + i] = q;
                }

                // integrate
                for (int i = 2; i < points; i++)
                {
                    float3 q = pos[o + i];
                    float3 v = (q - prev[o + i]) * keep;
                    float sp = math.length(v);
                    if (sp > vmax) v *= vmax / sp;
                    prev[o + i] = q;
                    pos[o + i] = q + v + gdt2;
                }

                for (int it = 0; it < p.iterations; it++)
                {
                    // shape: toward the head-relative rest shape (the groom near the roots)
                    for (int i = 2; i < points; i++)
                    {
                        float k = shapeK[ko + i];
                        if (k <= 0f) continue;
                        float3 target = hp + math.mul(hr, restLocal[o + i]);
                        pos[o + i] += (target - pos[o + i]) * k;
                    }

                    // bending: distance i-2 .. i (XPBD, compliant; one-sided root to tip: a two-sided distance bend fights
                    // the stretch / collision projections and pumps energy into the strand)
                    for (int i = 2; i < points; i++)
                    {
                        float3 d = pos[o + i] - pos[o + i - 2];
                        float len = math.length(d);
                        if (len < 1e-6f) continue;
                        float c = len - bendRest[o + i];
                        float lambda = -c / (1f + alphaBend);
                        pos[o + i] += lambda * (d / len);
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

                Collide(o, a, p);

                // deviation cone around the rest shape (velocity-neutral: prev moves with the point)
                if (info.x > 0f)
                {
                    for (int i = 2; i < points; i++)
                    {
                        float3 target = hp + math.mul(hr, restLocal[o + i]);
                        float3 d = pos[o + i] - target;
                        float len = math.length(d);
                        float lim = 0.02f + info.x * L * i;
                        if (len <= lim) continue;
                        float3 shift = d * (lim / len - 1f);
                        pos[o + i] += shift;
                        prev[o + i] += shift;
                    }
                }

                // follow-the-leader (never stretch beyond maxStretch: every strand bounded by its length), then the face
                // box (head-attached, velocity-neutral; the rest of the strand moves with a pushed point, a pushed point
                // keeps its segment length when that stays clear of the face) - twice, so neither leaves the other's
                // violation behind
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int i = 2; i < points; i++)
                    {
                        float3 d = pos[o + i] - pos[o + i - 1];
                        float len = math.length(d);
                        float lmax = L * p.maxStretch;
                        if (len > lmax) pos[o + i] = pos[o + i - 1] + d * (lmax / len);
                    }

                    if (p.face.enabled == 0) break;
                    quaternion inv = math.conjugate(hr);
                    float3 carry = float3.zero;
                    bool pushed = false;
                    for (int i = 2; i < points; i++)
                    {
                        pos[o + i] += carry;
                        prev[o + i] += carry;
                        float3 q = math.mul(inv, pos[o + i] - hp);
                        float3 push = p.face.Push(q, info.y, p.particleRadius);
                        if (math.all(push == float3.zero)) continue;
                        pushed = true;
                        float3 before = pos[o + i];
                        float3 np = before + math.mul(hr, push);
                        float3 seg = np - pos[o + i - 1];
                        float sl = math.length(seg), lmax = L * p.maxStretch;
                        if (sl > lmax)
                        {
                            float3 np2 = pos[o + i - 1] + seg * (lmax / sl);
                            if (!p.face.Inside(math.mul(inv, np2 - hp))) np = np2;
                        }

                        float3 shift = np - before;
                        pos[o + i] += shift;
                        prev[o + i] += shift;
                        carry += shift;
                    }

                    if (!pushed) break;
                }
            }

            // blow-up guard: re-hang the strand on the head in its rest shape
            bool bad = false;
            float reach = L * (points - 1) * p.maxStretch + 0.3f; // strand length + root offset from the head joint
            for (int i = 0; i < points; i++)
            {
                float3 q = pos[o + i];
                if (!math.all(math.isfinite(q)) || math.lengthsq(q - p.head1) > reach * reach) bad = true;
            }

            if (bad)
            {
                for (int i = 0; i < points; i++)
                {
                    float3 q = p.head1 + math.mul(p.rot1, restLocal[o + i]);
                    pos[o + i] = q;
                    prev[o + i] = q;
                }

                resets[g] = resets[g] + 1;
            }
        }

        void Collide(int o, float a, in SimParams p)
        {
            for (int c = p.capStart; c < p.capStart + p.capCount; c++)
            {
                Capsule cap = capsules[c];
                if (cap.r <= 0f) continue;
                float3 ca = math.lerp(cap.a0, cap.a1, a);
                float3 cb = math.lerp(cap.b0, cap.b1, a);
                float r = cap.r + p.particleRadius;
                float3 ab = cb - ca;
                float abab = math.max(math.dot(ab, ab), 1e-12f);
                float fr = cap.friction >= 0f ? cap.friction : p.friction;
                for (int i = math.max(2, cap.firstPoint); i < p.points; i++)
                {
                    float3 q0 = pos[o + i];
                    float t = math.saturate(math.dot(q0 - ca, ab) / abab);
                    float3 q = ca + ab * t;
                    float3 d = q0 - q;
                    float dist2 = math.lengthsq(d);
                    if (dist2 >= r * r) continue;
                    float dist = math.sqrt(dist2);
                    float3 n = dist > 1e-6f ? d / dist : new float3(0, 1, 0);
                    float3 np = q + n * r;
                    if (cap.maxPush > 0f)
                    {
                        float3 dp = np - q0;
                        float dl = math.length(dp);
                        if (dl > cap.maxPush) np = q0 + dp * (cap.maxPush / dl);
                    }

                    // velocity-neutral contact: the push-out itself adds no velocity (a deep penetration - the rest
                    // shape re-hung inside a raised arm, a fast torso - would otherwise fling the strand); the inward
                    // normal velocity is removed and friction takes part of the tangential velocity
                    float3 v = q0 - prev[o + i];
                    float vn = math.dot(v, n);
                    float3 vt = v - n * vn;
                    float3 vnew = vt * (1f - fr) + n * math.max(vn, 0f);
                    pos[o + i] = np;
                    prev[o + i] = np - vnew;
                }
            }
        }
    }

    /// <summary>live simulation: one capture-frame step of every strand</summary>
    [BurstCompile(FloatMode = FloatMode.Fast, DisableSafetyChecks = true)]
    public struct StrandSimJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<float3> pos;
        [NativeDisableParallelForRestriction] public NativeArray<float3> prev;
        [ReadOnly] public NativeArray<float3> restLocal;   // head-local rest shape (G * S)
        [ReadOnly] public NativeArray<float> segLen;       // per guide
        [ReadOnly] public NativeArray<float> bendRest;     // G * S: |rest_i - rest_(i-2)|
        [ReadOnly] public NativeArray<float> shapeK;       // G * S (or S): shape stiffness per iteration
        [ReadOnly] public NativeArray<float2> guideInfo;   // G: (max deviation, side)
        [ReadOnly] public NativeArray<Capsule> capsules;
        [NativeDisableParallelForRestriction] public NativeArray<int> resets; // per guide: 1 when the strand was re-hung
        public SimParams p;

        public void Execute(int g)
        {
            StrandSim sim = new()
            {
                pos = pos, prev = prev, restLocal = restLocal, segLen = segLen, bendRest = bendRest, shapeK = shapeK,
                guideInfo = guideInfo, capsules = capsules, resets = resets
            };
            sim.Step(g, p);
        }
    }

    /// <summary>
    /// The bake: every strand simulates a chunk of capture frames on its own (strands are independent, no
    /// synchronisation), frame 0 = re-hang in the rest shape + static settle in frame 0's pose. Kinematics per frame
    /// (head pose, capsules from the previous frame to this one) were read on the main thread. Positions go to the
    /// cache relative to the frame's head position (half precision: under 0.5 mm within 1 m of the head).
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, DisableSafetyChecks = true)]
    public struct BakeChunkJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<float3> pos;
        [NativeDisableParallelForRestriction] public NativeArray<float3> prev;
        [ReadOnly] public NativeArray<float3> restLocal;
        [ReadOnly] public NativeArray<float> segLen;
        [ReadOnly] public NativeArray<float> bendRest;
        [ReadOnly] public NativeArray<float> shapeK;
        [ReadOnly] public NativeArray<float2> guideInfo;
        [ReadOnly] public NativeArray<Capsule> capsules;     // frames * capCount: frame k = from frame k-1 to k
        [ReadOnly] public NativeArray<float3> headPos;       // frames
        [ReadOnly] public NativeArray<quaternion> headRot;   // frames (same hemisphere as the previous frame)
        [ReadOnly] public NativeArray<float> frameDt;        // frames
        [NativeDisableParallelForRestriction] public NativeArray<int> resets;
        [NativeDisableParallelForRestriction] public NativeArray<half3> cache; // (frameEnd - frameStart) * G * S
        public SimParams p;          // template: substeps, iterations, damping, ... (head / dt set per frame)
        public int frameStart, frameEnd, guides, capCount;
        public int settleSubsteps;
        public float settleSeconds, settleDamping;

        public void Execute(int g)
        {
            StrandSim sim = new()
            {
                pos = pos, prev = prev, restLocal = restLocal, segLen = segLen, bendRest = bendRest, shapeK = shapeK,
                guideInfo = guideInfo, capsules = capsules, resets = resets
            };
            int points = p.points;
            int o = g * points;
            for (int k = frameStart; k < frameEnd; k++)
            {
                SimParams q = p;
                q.capStart = k * capCount;
                q.capCount = capCount;
                if (k == 0)
                {
                    for (int i = 0; i < points; i++)
                    {
                        float3 x = headPos[0] + math.mul(headRot[0], restLocal[o + i]);
                        pos[o + i] = x;
                        prev[o + i] = x;
                    }

                    if (settleSubsteps > 0)
                    {
                        q.substeps = settleSubsteps;
                        q.dt = settleSeconds / settleSubsteps;
                        q.damping = settleDamping;
                        q.head0 = q.head1 = headPos[0];
                        q.rot0 = q.rot1 = headRot[0];
                        sim.Step(g, q);
                        for (int i = 0; i < points; i++) prev[o + i] = pos[o + i]; // start the take at rest
                    }
                }
                else
                {
                    q.dt = frameDt[k] / math.max(1, q.substeps);
                    q.head0 = headPos[k - 1];
                    q.head1 = headPos[k];
                    q.rot0 = headRot[k - 1];
                    q.rot1 = headRot[k];
                    sim.Step(g, q);
                }

                int c = ((k - frameStart) * guides + g) * points;
                float3 h = headPos[k];
                for (int i = 0; i < points; i++) cache[c + i] = (half3)(pos[o + i] - h);
            }
        }
    }

    /// <summary>a ribbon vertex of the legacy ribbon stream: strand centre + tangent (w = side -1 / +1); the shader
    /// expands it across the view direction</summary>
    public struct RibbonVertex
    {
        public float3 position;
        public half4 tangent;
    }

    /// <summary>legacy renderer (hm_hair --mode ribbons, A/B only): guide points -> ribbon vertices: the guide itself
    /// and its children (offsets in the guide's frame, outward N from the head centre and binormal B, clumping toward
    /// the tips)</summary>
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

    /// <summary>dynamic vertex of the hair cards: position, shading normal (the hair volume's outward normal, tilted at
    /// the card edges), tangent (root to tip)</summary>
    public struct CardVertex
    {
        public float3 position;
        public half4 normal;
        public half4 tangent;
    }

    /// <summary>one hair card: a weighted blend of 3 guides over the guide parameter range sStart..sEnd (sEnd &gt; 1
    /// extrapolates the last segment: longer layered ends)</summary>
    public struct CardDesc
    {
        public int3 guide;
        public float3 weight;
        public float widthRoot, widthTip, offset, bulge;
        public float sStart, sEnd;
        public float wobble, wobblePhase;
        /// <summary>head-bone-space offset of the card's root (front cards: down to the painted hairline), fading out
        /// by guide parameter sStart + dropS (0 = none)</summary>
        public float3 rootDrop;
        public float dropS;
    }

    /// <summary>
    /// Guide points -> hair card vertices (rows x 3 columns per card) and the rigid scalp cap. Each card's spine is the
    /// weighted blend of its 3 guides (neighbouring cards move as one sheet); its width axis lies on the hair volume
    /// (outward normal N from an axis running from the head centre down along the body), the centre column bulges
    /// outward (a curved card), and the shading normal tilts toward the edges (a rounded strand bundle). Indices
    /// [0, cards) = cards, [cards, cards + capChunks) = 32-vertex chunks of the cap (head pose transform only).
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, DisableSafetyChecks = true)]
    public struct CardMeshJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> pos;
        [ReadOnly] public NativeArray<CardDesc> cards;
        [ReadOnly] public NativeArray<float3> capPos, capNormal, capTangent; // head-bone space
        [NativeDisableParallelForRestriction] public NativeArray<CardVertex> vertices;
        [NativeDisableParallelForRestriction] public NativeArray<float3> chunkMin, chunkMax;
        public int points, rows, cardCount;
        public float3 headCentre, down, headPos;
        public quaternion headRot;
        public float axisLength;

        public const int CapChunk = 32;

        float3 Guide(int g, float s)
        {
            int o = g * points;
            float x = s * (points - 1);
            if (x <= 0f) return pos[o];
            if (x >= points - 1)
            {
                float3 last = pos[o + points - 1];
                return last + (last - pos[o + points - 2]) * (x - (points - 1));
            }

            int i = (int)x;
            return math.lerp(pos[o + i], pos[o + i + 1], x - i);
        }

        float3 Spine(in CardDesc c, float s)
        {
            float3 p = Guide(c.guide.x, s) * c.weight.x;
            if (c.weight.y > 0f) p += Guide(c.guide.y, s) * c.weight.y;
            if (c.weight.z > 0f) p += Guide(c.guide.z, s) * c.weight.z;
            if (c.dropS > 0f) p += math.mul(headRot, c.rootDrop) * (1f - math.smoothstep(0f, c.dropS, s - c.sStart));
            return p;
        }

        public void Execute(int index)
        {
            float3 mn = new(float.MaxValue), mx = new(float.MinValue);
            if (index >= cardCount)
            {
                int v0 = (index - cardCount) * CapChunk;
                int v1 = math.min(v0 + CapChunk, capPos.Length);
                int baseV = cardCount * rows * 3;
                for (int v = v0; v < v1; v++)
                {
                    float3 p = headPos + math.mul(headRot, capPos[v]);
                    float3 n = math.mul(headRot, capNormal[v]);
                    float3 t = math.mul(headRot, capTangent[v]);
                    vertices[baseV + v] = new CardVertex
                    {
                        position = p, normal = new half4((half)n.x, (half)n.y, (half)n.z, (half)0f),
                        tangent = new half4((half)t.x, (half)t.y, (half)t.z, (half)0f)
                    };
                    mn = math.min(mn, p);
                    mx = math.max(mx, p);
                }

                chunkMin[index] = mn;
                chunkMax[index] = mx;
                return;
            }

            CardDesc c = cards[index];
            float ds = (c.sEnd - c.sStart) / (rows - 1);
            float3 nPrev = float3.zero;
            for (int k = 0; k < rows; k++)
            {
                float u = (float)k / (rows - 1);
                float s = c.sStart + ds * k;
                float3 p = Spine(c, s);
                float3 t = k == 0 ? Spine(c, s + ds) - p : k == rows - 1 ? p - Spine(c, s - ds) : Spine(c, s + ds) - Spine(c, s - ds);
                float tl = math.length(t);
                t = tl > 1e-7f ? t / tl : new float3(0, -1, 0);

                // outward normal of the hair volume: away from the axis head centre -> down (radial above the centre)
                float3 rel = p - headCentre;
                float along = math.clamp(math.dot(rel, down), 0f, axisLength);
                float3 n = p - (headCentre + down * along);
                if (k > 0) n = math.normalize(n + 1e-6f) + nPrev * 0.5f; // carried along the spine: never flips
                n -= t * math.dot(n, t);
                float nl = math.length(n);
                n = nl > 1e-6f ? n / nl : (k > 0 ? nPrev : math.normalize(math.cross(t, new float3(1, 0, 0)) + 1e-4f));
                nPrev = n;
                float3 w = math.normalize(math.cross(t, n));

                float width = math.lerp(c.widthRoot, c.widthTip, u);
                float off = c.offset * math.smoothstep(0f, 0.08f, u);
                float3 wob = c.wobble != 0f ? w * (c.wobble * math.sin(c.wobblePhase + 9f * u) * math.smoothstep(0.4f, 1f, u)) : float3.zero;
                half4 th = new((half)t.x, (half)t.y, (half)t.z, (half)0f);
                for (int j = 0; j < 3; j++)
                {
                    float x = j * 0.5f - 0.5f;
                    float3 q = p + w * (x * width) + n * (off + (j == 1 ? c.bulge * width : 0f)) + wob;
                    float3 sn = math.normalize(n + w * (x * 0.8f));
                    vertices[(index * rows + k) * 3 + j] = new CardVertex
                    {
                        position = q, normal = new half4((half)sn.x, (half)sn.y, (half)sn.z, (half)0f), tangent = th
                    };
                    mn = math.min(mn, q);
                    mx = math.max(mx, q);
                }
            }

            chunkMin[index] = mn;
            chunkMax[index] = mx;
        }
    }

    /// <summary>tip glow ribbon vertex: centre + tangent (w = side -1 / +1) + (glow parameter s, width m)</summary>
    public struct GlowVertex
    {
        public float3 position;
        public half4 tangent;
        public half4 data;
    }

    public struct GlowDesc
    {
        public int card;
        public float u;    // across the card, 0..1: an atlas strand (clump centre) of the card's tile
        public float vEnd; // where that strand visibly ends (fraction of the card's rows; 0 = the job's endFraction)
    }

    /// <summary>
    /// The stylised tip glow: view-facing ribbons over the last `span` metres of an outer / flyaway card's strand at
    /// column u, ending where that atlas strand visibly ends (vEnd: the glow never runs past the hair). s = 1 - (distance
    /// to the tip) / window (the original bloom's strand parameter: above 1.0 for the last 27 % of the window). With
    /// endCap, one more point per ribbon repeats the tip with data.z = 1: the shader pushes it past the tip by the halo
    /// radius (a round, soft end). data = (s, core width m, cap flag, 0).
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, DisableSafetyChecks = true)]
    public struct GlowRibbonJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<CardVertex> cardVertices;
        [ReadOnly] public NativeArray<GlowDesc> glows;
        [NativeDisableParallelForRestriction] public NativeArray<GlowVertex> vertices; // glows * (points + endCap) * 2
        public int rows, points, endCap;
        public float window, span, widthRoot, widthTip;
        public float endFraction; // fallback end (fraction of the card's rows) of a glow without vEnd

        float3 At(int baseV, int k, float u)
        {
            int v = baseV + k * 3;
            return u <= 0.5f
                ? math.lerp(cardVertices[v].position, cardVertices[v + 1].position, u * 2f)
                : math.lerp(cardVertices[v + 1].position, cardVertices[v + 2].position, u * 2f - 1f);
        }

        public void Execute(int gi)
        {
            GlowDesc gd = glows[gi];
            int baseV = gd.card * rows * 3;
            FixedList128Bytes<float> arc = new();
            arc.Add(0f);
            float3 prevP = At(baseV, 0, gd.u);
            for (int k = 1; k < rows; k++)
            {
                float3 p = At(baseV, k, gd.u);
                arc.Add(arc[k - 1] + math.distance(p, prevP));
                prevP = p;
            }

            // the end: where the atlas strand visibly ends, as a fractional row of the card
            float ve = gd.vEnd > 0f ? gd.vEnd : (endFraction <= 0f ? 1f : endFraction);
            float rEnd = math.saturate(ve) * (rows - 1);
            int r0 = math.min((int)rEnd, rows - 2);
            float total = math.lerp(arc[r0], arc[r0 + 1], math.saturate(rEnd - r0));
            int seg = 0;
            int stride = points + (endCap != 0 ? 1 : 0);
            float3 tipP = float3.zero;
            half4 tipT = default, tipData = default;
            for (int m = 0; m < points; m++)
            {
                float f = (float)m / (points - 1);
                float a = math.max(0f, total - span + span * f);
                while (seg < rows - 2 && arc[seg + 1] < a) seg++;
                float sl = math.max(arc[seg + 1] - arc[seg], 1e-6f);
                float x = math.saturate((a - arc[seg]) / sl);
                float3 p0 = At(baseV, seg, gd.u), p1 = At(baseV, seg + 1, gd.u);
                float3 p = math.lerp(p0, p1, x);
                float3 t = math.normalize(p1 - p0 + 1e-7f);
                float sg = 1f - (total - a) / window;
                float w = math.lerp(widthRoot, widthTip, f);
                int v = (gi * stride + m) * 2;
                half4 data = new((half)sg, (half)w, (half)0f, (half)0f);
                half4 th = new((half)t.x, (half)t.y, (half)t.z, (half)(-1f));
                vertices[v] = new GlowVertex { position = p, tangent = th, data = data };
                vertices[v + 1] = new GlowVertex { position = p, tangent = new half4(th.x, th.y, th.z, (half)1f), data = data };
                tipP = p;
                tipT = th;
                tipData = data;
            }

            if (endCap != 0)
            {
                int v = (gi * stride + points) * 2;
                half4 data = new(tipData.x, tipData.y, (half)1f, (half)0f);
                vertices[v] = new GlowVertex { position = tipP, tangent = tipT, data = data };
                vertices[v + 1] = new GlowVertex { position = tipP, tangent = new half4(tipT.x, tipT.y, tipT.z, (half)1f), data = data };
            }
        }
    }

    /// <summary>a strand of the procedural atlas (pixels of its tile): x at the root, clump centre it converges to,
    /// root width, end v, depth (0 back .. 1 front), id</summary>
    public struct AtlasStrand
    {
        public float x0, xc, clump, w0, vEnd, depth, id, wave;
    }

    /// <summary>one row of one tile: union coverage of the tile's strands (Gaussian profiles, thinner and fainter
    /// toward their ends), the top-most strand's id / depth / end, an optional near-solid core (inner tiles)</summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    public struct AtlasRowJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<AtlasStrand> strands;
        [ReadOnly] public NativeArray<int2> tileRange;   // (first strand, count) per tile
        [ReadOnly] public NativeArray<float> tileFill;   // near-solid core coverage per tile
        [NativeDisableParallelForRestriction] public NativeArray<float4> texels; // width * height (R cov, G id, B depth, A end)
        public int width, height, tileWidth, margin, tiles;

        public void Execute(int index)
        {
            int tile = index % tiles;
            int y = index / tiles;
            float v = (y + 0.5f) / height;
            int x0 = tile * tileWidth;
            NativeArray<float> keep = new(tileWidth, Allocator.Temp);
            NativeArray<float> top = new(tileWidth, Allocator.Temp);
            NativeArray<float3> topInfo = new(tileWidth, Allocator.Temp);
            for (int x = 0; x < tileWidth; x++)
            {
                keep[x] = 1f;
                top[x] = -1f;
                topInfo[x] = new float3(0.5f, 0f, 1f);
            }

            float fill = tileFill[tile];
            if (fill > 0f)
            {
                float vf = 1f - math.smoothstep(0.85f, 1f, v);
                for (int x = 0; x < tileWidth; x++)
                {
                    float u = (x + 0.5f) / tileWidth;
                    float edge = math.smoothstep(0.08f, 0.3f, u) * math.smoothstep(0.92f, 0.7f, u);
                    keep[x] *= 1f - fill * edge * vf;
                }
            }

            int2 range = tileRange[tile];
            for (int k = range.x; k < range.x + range.y; k++)
            {
                AtlasStrand s = strands[k];
                if (v > s.vEnd) continue;
                float conv = s.clump * math.smoothstep(0.45f, 1f, v);
                float xc = math.lerp(s.x0, s.xc, conv) + s.wave * math.sin(v * 37f + s.id * 40f);
                float taper = math.smoothstep(s.vEnd * 0.55f, s.vEnd, v);
                float w = math.lerp(s.w0, 0.4f, taper);
                float peak = math.min(1f, w) * (1f - math.smoothstep(s.vEnd - 0.015f, s.vEnd, v));
                float sigma = math.max(w, 0.5f) * 0.5f;
                int lo = math.max(margin, (int)math.floor(xc - 3f * sigma));
                int hi = math.min(tileWidth - margin - 1, (int)math.ceil(xc + 3f * sigma));
                for (int x = lo; x <= hi; x++)
                {
                    float dx = (x + 0.5f - xc) / sigma;
                    float c = peak * math.exp(-0.5f * dx * dx);
                    if (c < 0.002f) continue;
                    keep[x] *= 1f - c;
                    if (c > 0.3f && s.depth > top[x])
                    {
                        top[x] = s.depth;
                        topInfo[x] = new float3(s.id, s.depth, s.vEnd);
                    }
                }
            }

            int row = y * width + x0;
            for (int x = 0; x < tileWidth; x++)
            {
                float3 info = topInfo[x];
                texels[row + x] = new float4(1f - keep[x], info.x, info.y, info.z);
            }

            keep.Dispose();
            top.Dispose();
            topInfo.Dispose();
        }
    }

    /// <summary>
    /// Mip chain of the atlas with coverage-preserving alpha (Castano): `pyramid` holds every level (level 0 filled by
    /// AtlasRowJob, offsets in texels in `levelOffset`); each level is box-filtered from the previous unscaled level
    /// (id / depth / end from the most covered child), then per tile the coverage is scaled so the fraction of texels
    /// at or above 0.5 matches level 0's. Writes the RGBA32 bytes of every level into `bytes` (same offsets x 4).
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    public struct AtlasMipJob : IJob
    {
        public NativeArray<float4> pyramid;
        public NativeArray<byte> bytes;
        [ReadOnly] public NativeArray<int> levelOffset;
        public NativeArray<float> target; // per tile
        public int width, height, levels, tiles;

        static byte B(float x) => (byte)math.clamp((int)math.round(x * 255f), 0, 255);

        public void Execute()
        {
            int w = width, h = height;
            for (int t = 0; t < tiles; t++) target[t] = Fraction(0, w, h, t, 1f);
            for (int lvl = 0; lvl < levels; lvl++)
            {
                int cur = levelOffset[lvl];
                if (lvl > 0)
                {
                    int src = levelOffset[lvl - 1];
                    int pw = w, nw = math.max(1, w / 2), nh = math.max(1, h / 2);
                    for (int y = 0; y < nh; y++)
                    for (int x = 0; x < nw; x++)
                    {
                        float4 a = pyramid[src + (2 * y) * pw + 2 * x], b = pyramid[src + (2 * y) * pw + 2 * x + 1];
                        float4 c = pyramid[src + (2 * y + 1) * pw + 2 * x], d = pyramid[src + (2 * y + 1) * pw + 2 * x + 1];
                        float4 best = a;
                        if (b.x > best.x) best = b;
                        if (c.x > best.x) best = c;
                        if (d.x > best.x) best = d;
                        pyramid[cur + y * nw + x] = new float4((a.x + b.x + c.x + d.x) * 0.25f, best.y, best.z, best.w);
                    }

                    w = nw;
                    h = nh;
                }

                for (int t = 0; t < tiles; t++)
                {
                    float k = 1f;
                    if (lvl > 0)
                    {
                        float lo = 0f, hi = 8f;
                        for (int it = 0; it < 12; it++)
                        {
                            float mid = 0.5f * (lo + hi);
                            if (Fraction(cur, w, h, t, mid) < target[t]) lo = mid;
                            else hi = mid;
                        }

                        k = 0.5f * (lo + hi);
                    }

                    int tw = math.max(1, w / tiles);
                    for (int y = 0; y < h; y++)
                    for (int x = t * tw; x < (t + 1) * tw; x++)
                    {
                        float4 c = pyramid[cur + y * w + x];
                        int i = (cur + y * w + x) * 4;
                        bytes[i] = B(math.saturate(c.x * k));
                        bytes[i + 1] = B(c.y);
                        bytes[i + 2] = B(c.z);
                        bytes[i + 3] = B(c.w);
                    }
                }
            }
        }

        float Fraction(int offset, int w, int h, int tile, float k)
        {
            int tw = math.max(1, w / tiles);
            int n = 0, hit = 0;
            for (int y = 0; y < h; y++)
            for (int x = tile * tw; x < (tile + 1) * tw; x++)
            {
                n++;
                if (pyramid[offset + y * w + x].x * k >= 0.5f) hit++;
            }

            return n > 0 ? (float)hit / n : 0f;
        }
    }
}
