// Hair cards of HairStrands (one dynamic mesh: layered cards that follow the simulated guides + a scalp cap), the way
// console / VR games draw realistic hair. Two passes in ONE shader with one shared vertex function (their depths must
// agree bit for bit), on two materials of the same renderer (Material.SetShaderPassEnabled):
//   "SRPDefaultUnlit" (material 1, queue 2991, right after the avatars' translucent depth prepass at 2990):
//       depth only, Cull Off, alpha-to-coverage of the sharpened atlas coverage (MSAA 4x; with MSAA off _AlphaToMask = 0
//       and it falls back to an alpha test at 0.5) -> per MSAA sample, the depth of the front-most hair.
//   "UniversalForward" (material 2, queue 3001): ZTest Equal, no depth write, premultiplied blend: colour lands exactly on
//       the samples the depth pass covered (anti-aliased edges after the resolve) and the opacity is ONE uniform fade over
//       whatever is behind (skeleton, the other dancer, the background) with no hair-on-hair double blending. At opacity
//       1 the hair is simply opaque.
// Shading (Scheuermann 2004 / Karis 2016, one model on desktop and Quest): Kajiya-Kay with two shifted lobes (white
// primary R toward the root, coloured secondary TRT toward the tip with per-strand sparkle), Karis' transmission (TT:
// the red backlit rim), wrapped diffuse x sin(T, L), SH ambient, occlusion from the layer, the atlas depth and the root;
// the lit body is soft-clamped to 0.6 linear so only the glowing tips (HM_HairTips) cross the bloom threshold.
// Atlas (HairAtlas): R coverage, G strand id, B depth, A strand end. No shadow pass (Quest budget).
// Scalp cap (layer 0): reaches down to the avatar texture's painted hairline (no dark band of baked hair between the
// cards and the skin); it is shaded like the cards (same albedo, Kajiya-Kay along the comb direction) with procedural
// strands across the comb (uv0.x = cross-comb coordinate in m, _CapStrands per m, faded to their mean where they would be
// smaller than a pixel) and a soft, fringed hairline: coverage = hairline coordinate (uv1.w: 0.5 at the hairline, +1
// per fade width) + per-strand jitter, softened over _CapSoft (alpha-to-coverage: a few MSAA levels of fine hair).
// Card roots fade in over the first _RootFade m (per strand: the atlas id), so a card never starts with a hard straight
// edge on the cap (the part, the dropped front roots).
Shader "HeadMovement/HairCards"
{
    Properties
    {
        [NoScaleOffset] _Atlas ("Strand atlas (R cov, G id, B depth, A end)", 2D) = "white" {}
        _BaseColor ("Albedo (linear)", Vector) = (0.30, 0.10, 0.06, 1)
        _HighlightColor ("Secondary (TRT) highlight (linear)", Vector) = (0.55, 0.20, 0.15, 1)
        _VarLo ("Strand variation low", Float) = 0.88
        _VarHi ("Strand variation high", Float) = 1.12
        _Shift1 ("Primary shift (+ = toward the root)", Float) = 0.04
        _Exp1 ("Primary exponent", Float) = 220
        _Spec1 ("Primary strength", Float) = 0.18
        _Tint1 ("Primary tint", Vector) = (1, 0.92, 0.88, 1)
        _Shift2 ("Secondary shift", Float) = -0.10
        _Exp2 ("Secondary exponent", Float) = 36
        _Spec2 ("Secondary strength", Float) = 0.38
        _ShiftJitter ("Per-strand shift jitter", Float) = 0.015
        _SparkleLo ("Secondary sparkle low", Float) = 0.45
        _TTColor ("Transmission colour", Vector) = (1, 0.24, 0.17, 1)
        _TTStrength ("Transmission strength", Float) = 0.8
        _TTBeta ("Transmission longitudinal width", Float) = 0.10
        _TTAlpha ("Transmission longitudinal shift", Float) = 0.035
        _CopperLift ("Tip lift rgb, w = metres from the tip", Vector) = (1.12, 1.18, 1, 0.10)
        _ClampKnee ("Soft clamp knee (linear)", Float) = 0.45
        _ClampMax ("Soft clamp max (linear)", Float) = 0.6
        _Ambient ("Ambient", Float) = 1
        _Fill ("Hemisphere fill (x main light)", Float) = 0.3
        _ViewSpec ("Gloss from a light above the viewer (x main light)", Float) = 0.5
        _Exposure ("Exposure", Float) = 1
        _Opacity ("Opacity", Range(0, 1)) = 1
        _AlphaToMask ("Alpha to coverage (MSAA)", Float) = 1
        _CapStrands ("Cap strands per metre", Float) = 1400
        _CapJitter ("Cap hairline jitter per strand", Float) = 0.6
        _CapSoft ("Cap hairline softness (coverage units)", Float) = 0.25
        _RootFade ("Card root fade-in (m)", Float) = 0.015
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_Atlas);
        SAMPLER(sampler_Atlas);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor, _HighlightColor, _Tint1, _TTColor, _CopperLift;
            float _VarLo, _VarHi, _Shift1, _Exp1, _Spec1, _Shift2, _Exp2, _Spec2, _ShiftJitter, _SparkleLo;
            float _TTStrength, _TTBeta, _TTAlpha, _ClampKnee, _ClampMax, _Ambient, _Exposure, _Opacity, _AlphaToMask, _Fill, _ViewSpec;
            float _CapStrands, _CapJitter, _CapSoft, _RootFade;
        CBUFFER_END

        struct Attributes
        {
            float3 positionOS : POSITION;
            float4 normalOS : NORMAL;   // shading normal (hair volume, tilted at the card edges)
            float4 tangentOS : TANGENT; // root -> tip
            float4 uv0 : TEXCOORD0;     // atlas u (cap: cross-comb coordinate m), s (root 0 .. tip 1), card random, layer (0 cap, 1 inner, 2 mid, 3 outer, 4 part, 5 flyaway)
            float4 uv1 : TEXCOORD1;     // card length m, edge fade flag, layer occlusion, 1 (cap: hairline coordinate, 0 at the hairline)
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float4 uv0 : TEXCOORD0;
            float4 uv1 : TEXCOORD1;
            float3 normalWS : TEXCOORD2;
            float3 tangentWS : TEXCOORD3;
            float3 positionWS : TEXCOORD4;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        // the ONE position path of both passes (ZTest Equal needs identical depths)
        Varyings HairVert(Attributes v)
        {
            Varyings o = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            float3 positionWS = TransformObjectToWorld(v.positionOS);
            o.positionCS = TransformWorldToHClip(positionWS);
            o.positionWS = positionWS;
            o.normalWS = TransformObjectToWorldNormal(v.normalOS.xyz);
            o.tangentWS = TransformObjectToWorldDir(v.tangentOS.xyz);
            o.uv0 = v.uv0;
            o.uv1 = v.uv1;
            return o;
        }

        float HairHash(float x) { return frac(sin(x * 12.9898) * 43758.5453); }

        // cap: strand id across the comb (0..1), faded to 0.5 where the strands get smaller than a pixel (no shimmer)
        float CapStrand(float crossM)
        {
            float x = crossM * _CapStrands;
            float fw = fwidth(x);
            return lerp(0.5, HairHash(floor(x) * 0.6180339 + 0.37), saturate(1.5 - fw));
        }

        // coverage of the card at this fragment: the atlas strands fading in at the root (cap: its fringed hairline),
        // fading cards seen edge-on
        float HairCoverage(Varyings i)
        {
            float2 atlas = SAMPLE_TEXTURE2D(_Atlas, sampler_Atlas, i.uv0.xy).rg; // sampled in uniform control flow
            float capCov = saturate(i.uv1.w + (CapStrand(i.uv0.x) - 0.5) * _CapJitter);
            float3 V = normalize(GetCameraPositionWS() - i.positionWS);
            float ndv = abs(dot(normalize(i.normalWS), V));
            float edge = i.uv1.y > 0.5 ? lerp(0.3, 1.0, saturate(ndv * 3.0)) : 1.0;
            float root = smoothstep(0.0, _RootFade * (0.4 + 1.2 * atlas.g), i.uv0.y * i.uv1.x);
            return i.uv0.w < 0.5 ? capCov : atlas.r * edge * root;
        }

        // Bgolus: a crisp alpha test without MSAA, anti-aliased coverage with it
        float HairSharpen(float a)
        {
            return saturate((a - 0.5) / max(fwidth(a), 1e-4) + 0.5);
        }
        ENDHLSL

        // depth prepass (material 1, queue 2991): front-most hair per MSAA sample
        Pass
        {
            Name "SRPDefaultUnlit"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull Off
            ZWrite On
            ZTest LEqual
            ColorMask 0
            AlphaToMask [_AlphaToMask]

            HLSLPROGRAM
            #pragma vertex HairVert
            #pragma fragment HairDepthFrag
            #pragma multi_compile_instancing

            half4 HairDepthFrag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float cov = HairCoverage(i);
                // cards: crisp anti-aliased strands; cap: a soft hairline (a few MSAA coverage levels)
                float a = i.uv0.w < 0.5 ? saturate((cov - 0.5) / max(fwidth(cov), _CapSoft) + 0.5) : HairSharpen(cov);
                if (_AlphaToMask < 0.5) clip(a - 0.5);
                else clip(a - 0.004);
                return half4(0, 0, 0, a);
            }
            ENDHLSL
        }

        // colour (material 2, queue 3001): exactly the samples of the depth pass, one uniform premultiplied fade
        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            ZTest Equal
            Blend One OneMinusSrcAlpha
            AlphaToMask Off

            HLSLPROGRAM
            #pragma vertex HairVert
            #pragma fragment HairFrag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            float KajiyaKay(float3 T, float3 N, float shift, float3 H, float e)
            {
                float3 t = normalize(T + N * shift);
                float th = dot(t, H);
                return smoothstep(-1.0, 0.0, th) * pow(sqrt(saturate(1.0 - th * th)), e);
            }

            half4 HairFrag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float layer = i.uv0.w;
                bool cap = layer < 0.5;
                float4 atlasTexel = SAMPLE_TEXTURE2D(_Atlas, sampler_Atlas, i.uv0.xy);
                float capId = CapStrand(i.uv0.x);
                float4 at = cap ? float4(1, capId, lerp(0.75, 1.0, capId), 1) : atlasTexel;
                float id = at.g;
                float s = i.uv0.y, len = i.uv1.x;

                float3 T = normalize(i.tangentWS);
                float3 N = normalize(i.normalWS);
                N = normalize(N - T * dot(N, T) + 1e-5);
                float3 V = normalize(GetCameraPositionWS() - i.positionWS);
                Light light = GetMainLight();
                float3 L = light.direction;
                float3 lc = light.color;

                // albedo: uniform root to tip (no dark regrowth), +-12 % per strand, optional copper lift near the tips
                float3 albedo = _BaseColor.rgb * lerp(_VarLo, _VarHi, id);
                float fromTip = (1.0 - s) * len;
                albedo *= lerp(float3(1, 1, 1), _CopperLift.rgb, cap ? 0.0 : 1.0 - smoothstep(0.0, max(_CopperLift.w, 1e-3), fromTip));

                // occlusion: layer x atlas depth x root
                float ao = i.uv1.z * lerp(0.75, 1.0, at.b) * (cap ? 1.0 : lerp(0.8, 1.0, smoothstep(0.0, 0.06, s * len)));

                float TL = dot(T, L);
                float sinTL = sqrt(saturate(1.0 - TL * TL));
                float wrapNL = saturate((dot(N, L) + 0.5) / 1.5);
                float diffuse = wrapNL * lerp(0.35, 1.0, sinTL);

                float3 H = normalize(L + V);
                float jitter = (id - 0.5) * 2.0 * _ShiftJitter;
                float sparkle = lerp(_SparkleLo, 1.0, HairHash(id * 91.7 + 3.1));
                float r = KajiyaKay(T, N, _Shift1 + jitter, H, _Exp1) * _Spec1;
                float trt = KajiyaKay(T, N, _Shift2 + jitter, H, _Exp2) * _Spec2 * sparkle;
                float3 specular = (_Tint1.rgb * r + _HighlightColor.rgb * trt) * saturate(dot(N, L) + 0.35) * ao * ao;

                // transmission (Karis 2016): azimuthal lobe peaks when the light is behind the hair
                float3 Lp = L - T * TL, Vp = V - T * dot(T, V);
                float cosPhi = dot(normalize(Lp + 1e-5), normalize(Vp + 1e-5));
                float Np = exp(-3.65 * cosPhi - 3.98);
                float x = TL + dot(T, V) + _TTAlpha;
                float Mp = exp(-x * x / (2.0 * _TTBeta * _TTBeta)) / (2.5066 * _TTBeta);
                float3 tt = _TTColor.rgb * (_TTStrength * Mp * Np * ao);

                // gloss that reads from any side: the same two lobes for a virtual light above the viewer (the
                // highlight band where the sleek hair curves over the skull), and a soft hemisphere fill (the body
                // shader carries baked studio light; the hair facing away from the key light must not go black)
                float3 Lv = normalize(V + float3(0, 0.7, 0));
                float3 Hv = normalize(Lv + V);
                float rv = KajiyaKay(T, N, _Shift1 + jitter, Hv, _Exp1) * _Spec1;
                float trtv = KajiyaKay(T, N, _Shift2 + jitter, Hv, _Exp2) * _Spec2 * sparkle;
                specular += (_Tint1.rgb * rv + _HighlightColor.rgb * trtv) * _ViewSpec * ao * ao;
                float3 fill = _Fill * (0.6 + 0.4 * N.y);

                float3 ambient = SampleSH(N) * _Ambient;
                float3 col = (albedo * ao * (ambient + lc * (diffuse + fill)) + lc * (specular + tt)) * _Exposure;

                // soft clamp of the lit body (max channel): URP's bloom knee starts at 0.5, only the tip glow may bloom
                float m = max(col.r, max(col.g, col.b));
                if (m > _ClampKnee)
                {
                    float range = max(_ClampMax - _ClampKnee, 1e-3);
                    float mm = _ClampKnee + range * (1.0 - exp(-(m - _ClampKnee) / range));
                    col *= mm / m;
                }

                return half4(col * _Opacity, _Opacity);
            }
            ENDHLSL
        }
    }
}
