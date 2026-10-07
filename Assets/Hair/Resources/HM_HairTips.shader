// Tip glow of HairStrands: the original head-movement hair bloom kept as a stylised trace of the whip. Sparse,
// view-facing ribbons over the last few cm of the outer cards' and flyaways' strands, each on a real atlas strand and
// ending where that strand visibly ends (expanded per eye in the vertex shader, single-pass instanced safe). White HDR
// emission along the original 6-point curve E(s) (dancecap docs/HAIR_REFERENCE.md 5: above the bloom threshold 1.0 for
// the last 27 % of the window, 3.1 at the tip) as a SOFT line: a Gaussian core at least _CorePx wide plus a wider,
// fainter halo (_HaloPx, _Halo x the core), with a round end cap past the tip (the ribbon's extra cap point is pushed
// out by the halo radius); dimmed by sqrt(strand width / pixel size), clamped to 0.4..1, so it does not grow brighter
// relative to the hair at a distance. The halo is drawn here, so the tips glow softly with or without URP's bloom (Quest may run
// without post-processing); with bloom on, the core (> 1.0) blooms on top. Additive (Blend One One), alpha untouched
// (Quest passthrough), depth-tested against the hair depth prepass (nudged 3 mm toward the camera), queue 3002. _TipGlow
// = user scale (0 = off) x a fade with the AVATAR's opacity (HairStrands: smoothstep(0, 0.1, avatar opacity)); not
// faded with the hair's own opacity.
Shader "HeadMovement/HairTips"
{
    Properties
    {
        _TipGlow ("Tip glow", Float) = 1
        _Emit0123 ("Tip emission s=0,.2,.4,.6", Vector) = (0, 0.012, 0.024, 0.219)
        _Emit45 ("Tip emission s=.8,1", Vector) = (1.398, 3.1, 0, 0)
        _CorePx ("Core minimum width (px)", Float) = 1.5
        _HaloPx ("Halo radius (px, max)", Float) = 5
        _HaloMinPx ("Halo radius (px, min)", Float) = 2.5
        _HaloWorld ("Halo radius (m)", Float) = 0.004
        _Halo ("Halo strength (x core)", Float) = 0.3
        _Nudge ("Toward the camera (m)", Float) = 0.003
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend One One
            ColorMask RGB

            HLSLPROGRAM
            #pragma vertex TipVert
            #pragma fragment TipFrag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _TipGlow, _CorePx, _HaloPx, _HaloMinPx, _HaloWorld, _Halo, _Nudge;
                float4 _Emit0123, _Emit45;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 tangentOS : TANGENT;  // xyz = strand tangent, w = side -1 / +1
                float4 data : TEXCOORD0;     // x = glow parameter s, y = core width (m), z = 1 on the end-cap point
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 g : TEXCOORD0; // x emission, y across (px from the centre line), z along past the tip (px), w core sigma (px)
                float haloSigma : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float TipEmission(float s)
            {
                float x = saturate(s) * 5.0;
                float i = min(floor(x), 4.0);
                float f = x - i;
                float e0 = i < 0.5 ? _Emit0123.x : i < 1.5 ? _Emit0123.y : i < 2.5 ? _Emit0123.z : i < 3.5 ? _Emit0123.w : _Emit45.x;
                float e1 = i < 0.5 ? _Emit0123.y : i < 1.5 ? _Emit0123.z : i < 2.5 ? _Emit0123.w : i < 3.5 ? _Emit45.x : _Emit45.y;
                return lerp(e0, e1, f);
            }

            Varyings TipVert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 centreWS = TransformObjectToWorld(v.positionOS);
                float3 T = normalize(TransformObjectToWorldDir(v.tangentOS.xyz));
                float3 V = normalize(GetCameraPositionWS() - centreWS);
                centreWS += V * _Nudge;
                float3 B = cross(T, V);
                float bl = length(B);
                B = bl > 1e-4 ? B / bl : normalize(cross(T, float3(0.01, 1, 0)));
                float4 cc = TransformWorldToHClip(centreWS);
                float pixelWorld = max(abs(cc.w) * 2.0 / max(_ScreenParams.y * abs(GetViewToHClipMatrix()[1][1]), 1e-4), 1e-6);
                float corePx = max(v.data.y / pixelWorld, _CorePx);              // full core width in px
                float haloPx = clamp(_HaloWorld / pixelWorld, _HaloMinPx, _HaloPx);
                float halfPx = 0.5 * corePx + haloPx;
                float side = v.tangentOS.w > 0 ? 1.0 : -1.0;
                float cap = v.data.z;
                float3 p = centreWS + B * (side * halfPx * pixelWorld) + T * (cap * haloPx * pixelWorld);
                o.positionCS = TransformWorldToHClip(p);
                // a strand thinner than a pixel covers only part of it: less energy at a distance (what the MSAA resolve
                // did to the old 1 px lines, without losing the bloom up close)
                float cover = clamp(sqrt(v.data.y / pixelWorld), 0.4, 1.0);
                o.g = float4(TipEmission(v.data.x) * _TipGlow * cover, side * halfPx, cap * haloPx, max(0.35 * corePx, 0.6));
                o.haloSigma = max(haloPx * 0.45, 1.0);
                return o;
            }

            half4 TipFrag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float r2 = i.g.y * i.g.y + i.g.z * i.g.z; // px^2 from the strand (round past the tip)
                float core = exp(-0.5 * r2 / (i.g.w * i.g.w));
                float halo = exp(-0.5 * r2 / (i.haloSigma * i.haloSigma));
                float e = i.g.x * (core + _Halo * halo);
                return half4(e, e, e, 0);
            }
            ENDHLSL
        }
    }
}
