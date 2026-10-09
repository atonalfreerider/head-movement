// Source-phone video for the film (Assets/Film/CameraVideoRig.cs): an unlit textured quad at a phone's image plane.
// 3d_over_video: Background queue, ZTest Always, no depth write -> drawn first, every other object paints over it, so the
// phone's frame is the backdrop of the 3D (skeletons, avatars, floor marks, annotations) exactly as the phone saw it.
// video_over_3d: Overlay queue, ZTest Always. Frustum-glyph thumbnails: ZTest LEqual in the transparent queue.
// Alpha = _Alpha (fades the video in and out). No culling, no depth write. Single-pass instanced stereo safe.
Shader "HeadMovement/CameraVideo"
{
    Properties
    {
        _MainTex ("Frame", 2D) = "black" {}
        _Alpha ("Opacity", Range(0, 1)) = 1
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest (Always = backdrop / overlay; LessEqual = glyph thumbnail)", Float) = 8
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Background" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "CameraVideo"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest [_ZTest]
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half _Alpha;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                ZERO_INITIALIZE(Varyings, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                return half4(c.rgb, _Alpha);
            }
            ENDHLSL
        }
    }
}
