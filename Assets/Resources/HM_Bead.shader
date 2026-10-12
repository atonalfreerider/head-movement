// Glowing skeleton beads (the follower's spine, SpineBeads.cs): small opaque spheres drawn GPU-instanced in ONE draw
// call (Graphics.RenderMeshInstanced), one colour per bead. Same look as the skeleton lines' bloom shader graph
// (Assets/bloom-shader.shadergraph: unlit, opaque, colour = linear vertex colour x 3.1 HDR, feeding the bloom pass):
// colour = _BeadColor (per instance, the line colour as the LineRenderer hands it to the graph: linear) x _Intensity,
// with a gentle view-facing falloff (rim _Rim of the centre) so a bead reads as a sphere up close.
// Queue Geometry like the lines: depth-tested and depth-writing BEFORE the translucent avatars (prepass 2990, colour
// 3000), so each translucent body blends over the beads inside it exactly as over the lines (VIEWER_SPEC 3.2 / 3.3).
// The VIEWER_SPEC 3.3 "skeletons over bodies" option: the pass itself writes the follower's stencil bit (_SkelRef)
// where a bead is visible (_StencilPass = Replace, 2) or leaves the stencil alone (Keep, 0) - the job the extra
// HM_SkeletonStencil marker material does on each line, without a second draw call.
// Single-pass instanced stereo safe (Quest).
Shader "HeadMovement/Bead"
{
    Properties
    {
        _Intensity ("Intensity (the skeleton shader graph's HDR gain)", Float) = 3.1
        _Rim ("Edge brightness relative to the centre", Range(0, 1)) = 0.72
        [HideInInspector] _SkelRef ("Skeleton stencil bit (lead 1, follow 2)", Float) = 2
        [HideInInspector] _StencilPass ("Stencil op: 0 Keep, 2 Replace", Float) = 0
        // VR solid skeleton (Dancer.SolidSkeleton, HM_SkeletonSolid.shader): 1 = the bead colour goes through the same brightness model (always
        // bright, hue and beat kept, alpha 1) and the bead is drawn over the translucent bodies (SpineBeads sets _ZTest Always, _ZWrite 0, queue 3101)
        [HideInInspector] _Solid ("Solid skeleton colour model", Float) = 0
        [HideInInspector] _ZTest ("ZTest (4 LEqual, 8 Always)", Float) = 4
        [HideInInspector] _ZWrite ("ZWrite", Float) = 1
        [HideInInspector] _Idle ("Solid: brightness between beats (linear)", Float) = 0.85
        [HideInInspector] _PulseAt ("Solid: vertex brightness of a full beat", Float) = 0.5
        [HideInInspector] _Flash ("Solid: white at the top of a beat", Float) = 0.3
        [HideInInspector] _IdleVertex ("Solid: vertex brightness between beats", Float) = 0.0397
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Bead"
            Tags { "LightMode" = "UniversalForwardOnly" }
            Cull Back
            ZWrite [_ZWrite]
            ZTest [_ZTest]

            Stencil
            {
                Ref [_SkelRef]
                WriteMask [_SkelRef]
                Comp Always
                Pass [_StencilPass]
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nolightprobe nolightmap
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Intensity;
                float _Rim;
                float _SkelRef;
                float _StencilPass;
                float _Solid;
                float _ZTest;
                float _ZWrite;
                float _Idle;
                float _PulseAt;
                float _Flash;
                float _IdleVertex;
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(BeadProps)
                UNITY_DEFINE_INSTANCED_PROP(float4, _BeadColor)
            UNITY_INSTANCING_BUFFER_END(BeadProps)

            // HmVr.SolidSkeleton.Map (and HM_SkeletonSolid.shader): hue kept, brightness mapped into [_Idle, 1], white flash at the top of a beat
            float3 SolidColour(float3 v)
            {
                float m = max(v.r, max(v.g, v.b));
                float3 hue = v / max(m, 1e-5);
                float beat = saturate((m - _IdleVertex) / max(_PulseAt - _IdleVertex, 1e-4));
                float f = m < _IdleVertex ? _Idle * (m / _IdleVertex) : lerp(_Idle, 1.0, beat);
                return saturate(hue * f + _Flash * beat * beat * step(_IdleVertex, m));
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3 color : COLOR;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                ZERO_INITIALIZE(Varyings, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                float3 bead = UNITY_ACCESS_INSTANCED_PROP(BeadProps, _BeadColor).rgb;
                output.color = (half3)(_Solid > 0.5 ? SolidColour(bead) : bead * _Intensity);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 v = normalize(GetWorldSpaceViewDir(input.positionWS));
                float facing = saturate(dot(normalize(input.normalWS), v));
                half rim = _Solid > 0.5 ? lerp(0.15h, 1.0h, smoothstep(0.05h, 0.4h, (half)facing)) : lerp(_Rim, 1.0, facing); // solid: a dark edge like the lines
                return half4(input.color * rim, 1);
            }
            ENDHLSL
        }

        // depth for the camera depth texture / any depth prepass (URP renders opaques without this pass invisible there)
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nolightprobe nolightmap
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Intensity;
                float _Rim;
                float _SkelRef;
                float _StencilPass;
                float _Solid;
                float _ZTest;
                float _ZWrite;
                float _Idle;
                float _PulseAt;
                float _Flash;
                float _IdleVertex;
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(BeadProps)
                UNITY_DEFINE_INSTANCED_PROP(float4, _BeadColor)
            UNITY_INSTANCING_BUFFER_END(BeadProps)

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                ZERO_INITIALIZE(Varyings, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return input.positionCS.z;
            }
            ENDHLSL
        }
    }
}
