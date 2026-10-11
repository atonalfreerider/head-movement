// Passthrough mixed reality (Assets/VR/Runtime/VrDisplay.cs): the soft translucent contact shadow under the couple. A unit quad on the floor
// (object x, z in -0.5..0.5); black with an alpha that is strongest at the middle and fades to nothing at the edge. Alpha blended with
// premultiplied-correct alpha (the headset composites the picture over the real room), no depth write. Single-pass instanced stereo safe.
Shader "HeadMovement/ContactDisc"
{
    Properties
    {
        _Alpha ("Alpha at the middle", Range(0, 1)) = 0.34
        _Inner ("Fully dark up to (fraction of the radius)", Range(0, 1)) = 0.18
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-30" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ContactDisc"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _Alpha;
                half _Inner;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 disc : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                ZERO_INITIALIZE(Varyings, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.disc = input.positionOS.xz * 2.0;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half r = length(input.disc);
                half a = _Alpha * (1.0h - smoothstep(_Inner, 1.0h, r));
                return half4(0, 0, 0, a);
            }
            ENDHLSL
        }
    }
}
