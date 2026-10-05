// Preview for Gaussian splats: each splat is 4 vertices at its centre, expanded here into a view-aligned quad
// (TEXCOORD0 = corner in [-1,1], TEXCOORD1.x = radius at ~3 sigma) with a Gaussian falloff. Unsorted
// premultiplied blending - good enough to place dancers in the room, not a faithful 3DGS render.
// Single-pass instanced stereo safe (per-eye view matrix via the instancing macros).
Shader "HeadMovement/SplatPreview"
{
    Properties
    {
        _SizeScale ("Size Scale", Float) = 1
        _Opacity ("Opacity", Range(0, 1)) = 1
        _Brightness ("Brightness", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "SplatPreview"
            Tags { "LightMode" = "UniversalForward" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _SizeScale;
                float _Opacity;
                float _Brightness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
                float2 corner : TEXCOORD0;
                float2 radius : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR;
                float2 corner : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                ZERO_INITIALIZE(Varyings, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 centerVS = TransformWorldToView(TransformObjectToWorld(input.positionOS.xyz));
                centerVS.xy += input.corner * input.radius.x * _SizeScale;
                output.positionCS = TransformWViewToHClip(centerVS);
                output.color = input.color;
                output.corner = input.corner;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float d2 = dot(input.corner, input.corner);
                if (d2 > 1.0) discard;
                // corner radius = 3 sigma -> exp(-0.5 * (3 r)^2)
                float alpha = exp(-4.5 * d2) * input.color.a * _Opacity;
                return half4(input.color.rgb * _Brightness * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
