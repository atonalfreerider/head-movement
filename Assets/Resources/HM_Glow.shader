// Additive glow for the dance overlays (counterbalance axis, pivot rings, traces, graph links, miniature
// skeletons). Colour = vertex colour (rgb * a) * _Tint (rgb * a) * _Intensity, added to the frame: black is
// invisible, so fades are colour fades and nothing needs sorting. HDR output feeds the bloom pass.
// No depth write, no culling (strips are visible from both sides). Single-pass instanced stereo safe.
// Queue 2985: before the translucent avatar/hair depth prepasses (2990+), so strips inside a body show through it.
Shader "HeadMovement/Glow"
{
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _Intensity ("Intensity", Float) = 2
        _NearFade ("Near fade: invisible closer than x m, full beyond y m (0,0 = off)", Vector) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-15" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Glow"
            Tags { "LightMode" = "UniversalForward" }
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float _Intensity;
                float4 _NearFade;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR;
                float3 positionWS : TEXCOORD0;
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
                output.color = input.color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 c = input.color.rgb * input.color.a * _Tint.rgb * _Tint.a * _Intensity;
                // lines right in front of the camera (the chase camera flies through the graph) fade out
                if (_NearFade.y > _NearFade.x)
                {
                    float d = distance(GetCameraPositionWS(), input.positionWS);
                    c *= saturate((d - _NearFade.x) / (_NearFade.y - _NearFade.x));
                }
                return half4(c, 0);
            }
            ENDHLSL
        }
    }
}
