// The dance floor plane (VIEWER_SPEC 3.1): an unlit, alpha-blended flat colour (black at alpha ~0.6 by default, ~0.25 in
// passthrough) so the floor reads without hiding what is below it. No depth write; queue Transparent-30 (before the glow
// overlays at -15 and the translucent avatars), so everything on the floor draws over it.
Shader "HeadMovement/FloorPlane"
{
    Properties
    {
        _Color ("Colour (rgb, alpha = opacity)", Color) = (0, 0, 0, 0.6)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-30" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "FloorPlane"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
            CBUFFER_END

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

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return half4(_Color.rgb, _Color.a);
            }
            ENDHLSL
        }
    }
}
