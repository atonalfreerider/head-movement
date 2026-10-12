// Solid skeleton lines for the headset (VR mode; docs/QUEST.md "Skeletons at full opacity"). Unlit, alpha 1, no blending: a skeleton pixel
// REPLACES whatever is under it (the translucent body, the hair, the passthrough camera), so it can never look see-through.
//
// Why it exists: the viewer's skeleton (Assets/bloom-shader.shadergraph) shows the beat pulse as a glow. Between beats the line colour is only
// 0.22 of the base (a dark grey / dark red, ~12 % linear light) and only the pulse and the HDR bloom bring it up; the Quest tier has no HDR and
// no bloom, so on a bright passthrough room the skeleton read as a faint, translucent line (owner: "skeletons should be full opacity").
// This shader keeps the hue and the pulse but maps the brightness into [_Idle, 1] (linear light), so the skeleton is always bright; the beat
// is a rise from _Idle to full plus a short white flash. Fades (RoleAlpha scales the colour towards black) are kept: below the idle level the
// brightness falls to 0 with the colour.
//
// Used as two materials on every skeleton line (Dancer.SolidSkeleton):
//   _OnTop 0  queue Geometry, ZWrite On, ZTest LEqual: the depth-correct solid line (what an opaque occluder in front of it hides);
//   _OnTop 1  queue Transparent+100 (3100: after the translucent bodies 2990-3022 and the hair, before the UI 3900), ZWrite Off, ZTest Always:
//             the same line drawn again over the bodies, so a body's alpha blend can never dim the skeleton inside it. No stencil needed
//             (the stencil route depends on the headset's eye buffer having a stencil plane).
// The blend / z state is read from the material's properties (_SrcBlend, _DstBlend, _ZWrite, _ZTest), so the HM-VR skeleton log line reports
// the real state. Single-pass instanced stereo safe.
Shader "HeadMovement/SkeletonSolid"
{
    Properties
    {
        _Idle ("Brightness between beats (linear, 0..1)", Range(0.3, 1)) = 0.85
        _PulseAt ("Vertex brightness (linear) that counts as a full beat", Range(0.1, 1)) = 0.5
        _Flash ("White added at the top of a beat", Range(0, 0.6)) = 0.3
        _Outline ("Dark edge: share of the half width", Range(0, 0.5)) = 0.24
        _IdleVertex ("Vertex brightness (linear) of a skeleton between beats (SkeletonStyle.IdleLevel 0.22)", Float) = 0.0397
        [HideInInspector] _SrcBlend ("Src blend (One = opaque)", Float) = 1
        [HideInInspector] _DstBlend ("Dst blend (Zero = opaque)", Float) = 0
        [HideInInspector] _ZWrite ("ZWrite", Float) = 1
        [HideInInspector] [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
        [HideInInspector] _OnTop ("0 depth-correct (Geometry), 1 over the bodies", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "SkeletonSolid"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            ZTest [_ZTest]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Idle;
                float _PulseAt;
                float _Flash;
                float _Outline;
                float _IdleVertex;
                float _SrcBlend;
                float _DstBlend;
                float _ZWrite;
                float _ZTest;
                float _OnTop;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3 color : COLOR;
                float across : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // vertex colour (linear: what the line's gradient hands the shader) -> solid colour. Mirrors HmVr.SolidSkeleton.Map (EditMode tests).
            float3 SolidColour(float3 v)
            {
                float m = max(v.r, max(v.g, v.b));
                float3 hue = v / max(m, 1e-5);
                float f = m < _IdleVertex ? _Idle * (m / _IdleVertex) : lerp(_Idle, 1.0, saturate((m - _IdleVertex) / max(_PulseAt - _IdleVertex, 1e-4)));
                float beat = saturate((m - _IdleVertex) / max(_PulseAt - _IdleVertex, 1e-4));
                return saturate(hue * f + _Flash * beat * beat * step(_IdleVertex, m));
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                ZERO_INITIALIZE(Varyings, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = (half3)SolidColour(input.color.rgb);
                output.across = input.uv.y; // a LineRenderer quad: v runs across the width (0 / 1 = the two edges); |2v-1| is taken per fragment (interpolating it would give 1 everywhere)
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                // a thin dark edge: a white or pale line stays readable against a bright room (the passthrough), and costs nothing against a dark one
                half edge = smoothstep(1.0 - _Outline - 0.1, 1.0 - _Outline, abs(input.across * 2.0 - 1.0));
                return half4(lerp(input.color, half3(0.02, 0.02, 0.02), edge * 0.85h), 1);
            }
            ENDHLSL
        }
    }
}
