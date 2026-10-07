// Hair ribbons of HairStrands (one dynamic mesh). Each vertex is a strand centre point + tangent (w = side -1/+1);
// the vertex shader expands it across the view direction (per eye in single-pass instanced stereo). Kajiya-Kay
// shading (main light + SH ambient, two shifted specular lobes), measured root / mid / tip colour with per-strand
// variation, procedural sub-strands across the ribbon with alpha-to-coverage (sharpened alpha: anti-aliased with MSAA,
// an alpha test without it), and the original head-movement tip bloom: white emission along the strand following
// the 6-point curve of the old LineRenderer hair (above 1.0 from s = 0.73, 3.1 at the tip; dancecap
// docs/HAIR_REFERENCE.md section 5). Opaque queue (AlphaTest), depth write, no shadow pass (Quest budget).
Shader "HeadMovement/HairStrands"
{
    Properties
    {
        _RootColor ("Root (linear)", Vector) = (0.19, 0.03, 0.02, 1)
        _MidColor ("Mid (linear)", Vector) = (0.175, 0.025, 0.017, 1)
        _TipColor ("Tip (linear)", Vector) = (0.16, 0.026, 0.018, 1)
        _HighlightColor ("Highlight (linear)", Vector) = (0.58, 0.096, 0.077, 1)
        _VarLo ("Strand variation low", Float) = 0.6
        _VarHi ("Strand variation high", Float) = 1.6
        _WidthRoot ("Ribbon width root (m)", Float) = 0.022
        _WidthTip ("Ribbon width tip (m)", Float) = 0.006
        _Strands ("Sub-strands per ribbon", Float) = 5
        _Shift1 ("Primary shift", Float) = -0.06
        _Shift2 ("Secondary shift", Float) = 0.08
        _Exp1 ("Primary exponent", Float) = 90
        _Exp2 ("Secondary exponent", Float) = 18
        _Specular ("Specular", Float) = 0.25
        _Exposure ("Exposure", Float) = 1.0
        _Ambient ("Ambient", Float) = 1.0
        _TipGlow ("Tip glow", Float) = 1
        _Opacity ("Opacity", Range(0, 1)) = 1
        _Emit0123 ("Tip emission s=0,.2,.4,.6", Vector) = (0, 0.012, 0.024, 0.219)
        _Emit45 ("Tip emission s=.8,1", Vector) = (1.398, 3.1, 0, 0)
        _HeadCenterWS ("Head centre (world)", Vector) = (0, 1.6, 0, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _RootColor, _MidColor, _TipColor, _HighlightColor;
            float _VarLo, _VarHi, _WidthRoot, _WidthTip, _Strands;
            float _Shift1, _Shift2, _Exp1, _Exp2, _Specular, _Exposure, _Ambient;
            float _TipGlow, _Opacity;
            float4 _Emit0123, _Emit45, _HeadCenterWS;
        CBUFFER_END

        struct Attributes
        {
            float3 positionOS : POSITION;
            float4 tangentOS : TANGENT;
            float4 uv : TEXCOORD0; // side 0|1, s, ribbon random, width scale
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float4 uv : TEXCOORD0;
            float3 tangentWS : TEXCOORD1;
            float3 normalWS : TEXCOORD2;
            float3 positionWS : TEXCOORD3;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings HairVert(Attributes v)
        {
            Varyings o = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            float3 centreWS = TransformObjectToWorld(v.positionOS);
            float3 T = TransformObjectToWorldDir(v.tangentOS.xyz);
            float3 V = normalize(GetCameraPositionWS() - centreWS);
            float3 B = cross(T, V);
            float bl = length(B);
            B = bl > 1e-4 ? B / bl : normalize(cross(T, float3(0.01, 1, 0)));
            float w = lerp(_WidthRoot, _WidthTip, v.uv.y) * v.uv.w;
            float3 posWS = centreWS + B * (sign(v.tangentOS.w) * 0.5 * w);
            float3 N = centreWS - _HeadCenterWS.xyz;
            N -= T * dot(N, T);
            N = normalize(N + float3(0, 1e-5, 0));
            o.positionWS = posWS;
            o.positionCS = TransformWorldToHClip(posWS);
            o.tangentWS = T;
            o.normalWS = N;
            o.uv = v.uv;
            return o;
        }

        float HairHash(float x) { return frac(sin(x * 12.9898) * 43758.5453); }

        // sub-strands across the ribbon: thinner and fewer toward the tips (piecey ends), soft ribbon edges.
        // glow = the thin cores of about half of the GUIDE ribbons' sub-strands (width scale 1): the tip bloom stays a
        // sparse set of thin bright ends like the original 180 line strands, not a glowing curtain
        float HairStrandAlpha(float4 uv, out float glow)
        {
            float u = uv.x, s = uv.y, rnd = uv.z;
            float k = u * _Strands + rnd * 13.1;
            float id = floor(k);
            float h = HairHash(id + rnd * 71.3);
            float fr = frac(k);
            float thick = lerp(0.6, 1.0, h) * (1.0 - 0.4 * s);
            float a = 1.0 - smoothstep(thick * 0.5, thick * 0.5 + 0.18, abs(fr - 0.5));
            float endS = lerp(0.82, 1.0, HairHash(id * 3.7 + rnd * 19.1));
            float ends = 1.0 - smoothstep(endS - 0.05, endS, s);
            a *= ends * smoothstep(0.0, 0.1, u) * smoothstep(1.0, 0.9, u);
            glow = step(0.95, uv.w) * step(0.5, HairHash(id * 5.3 + rnd * 3.1)) * (1.0 - smoothstep(0.08, 0.22, abs(fr - 0.5))) * ends;
            return a * _Opacity;
        }

        float HairStrandAlpha(float4 uv)
        {
            float glow;
            return HairStrandAlpha(uv, glow);
        }

        // alpha-to-coverage with a sharpened alpha (Bgolus): a crisp alpha test without MSAA, anti-aliased with it
        float HairSharpenAlpha(float a, float cutoff)
        {
            return saturate((a - cutoff) / max(fwidth(a), 1e-4) + 0.5);
        }

        float HairTipEmission(float s)
        {
            float x = saturate(s) * 5.0;
            float i = min(floor(x), 4.0);
            float f = x - i;
            float e0 = i < 0.5 ? _Emit0123.x : i < 1.5 ? _Emit0123.y : i < 2.5 ? _Emit0123.z : i < 3.5 ? _Emit0123.w : _Emit45.x;
            float e1 = i < 0.5 ? _Emit0123.y : i < 1.5 ? _Emit0123.z : i < 2.5 ? _Emit0123.w : i < 3.5 ? _Emit45.x : _Emit45.y;
            return lerp(e0, e1, f);
        }
        ENDHLSL

        Pass
        {
            Name "HairForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite On
            AlphaToMask On

            HLSLPROGRAM
            #pragma vertex HairVert
            #pragma fragment HairFrag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            half4 HairFrag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float glowMask;
                float a = HairSharpenAlpha(HairStrandAlpha(i.uv, glowMask), 0.5);
                clip(a - 0.001);
                float s = i.uv.y, rnd = i.uv.z;
                float3 T = normalize(i.tangentWS);
                float3 N = normalize(i.normalWS);
                float3 V = normalize(GetCameraPositionWS() - i.positionWS);
                Light light = GetMainLight();
                float3 L = light.direction;

                float3 base = s < 0.5 ? lerp(_RootColor.rgb, _MidColor.rgb, smoothstep(0.0, 0.5, s))
                                      : lerp(_MidColor.rgb, _TipColor.rgb, smoothstep(0.5, 1.0, s));
                base *= lerp(_VarLo, _VarHi, rnd);

                float TL = dot(T, L);
                float sinTL = sqrt(saturate(1.0 - TL * TL));
                float wrapNL = saturate(dot(N, L) * 0.5 + 0.5);
                float diffuse = lerp(0.3, 1.0, sinTL) * wrapNL;

                float3 H = normalize(L + V);
                float3 T1 = normalize(T + N * (_Shift1 + (rnd - 0.5) * 0.1));
                float3 T2 = normalize(T + N * (_Shift2 + (rnd - 0.5) * 0.2));
                float d1 = dot(T1, H), d2 = dot(T2, H);
                float spec1 = pow(sqrt(saturate(1.0 - d1 * d1)), _Exp1);
                float spec2 = pow(sqrt(saturate(1.0 - d2 * d2)), _Exp2);
                float3 hl = _HighlightColor.rgb;
                float3 specular = (spec1 * lerp(hl, float3(1, 1, 1), 0.15) + spec2 * hl) * _Specular * wrapNL;

                // under-layer occlusion: roots and inner strands sit under the outer hair
                float ao = lerp(0.5, 1.0, saturate(s * 2.5)) * lerp(0.7, 1.0, rnd);
                float3 ambient = SampleSH(N) * _Ambient;
                float3 col = (base * (ambient * ao + light.color * diffuse) + light.color * specular) * _Exposure;
                col += HairTipEmission(s) * _TipGlow * glowMask;
                return half4(col, a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull Off
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex HairVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            half DepthFrag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                clip(HairStrandAlpha(i.uv) - 0.5);
                return i.positionCS.z;
            }
            ENDHLSL
        }
    }
}
