// Source-phone video for the film (Assets/Film/CameraVideoRig.cs): an unlit textured quad at a phone's image plane.
// 3d_over_video: Background queue, ZTest Always, no depth write -> drawn first, every other object paints over it, so the
// phone's frame is the backdrop of the 3D (skeletons, avatars, floor marks, annotations) exactly as the phone saw it.
// video_over_3d: Overlay queue, ZTest Always. Frustum-glyph thumbnails: ZTest LEqual in the transparent queue.
// Alpha = _Alpha (fades the video in and out). No culling, no depth write (_ZWrite 0) except a v3 frustum surface at full opacity
// (CameraVideoRig.SetSurfaces: queue 3100, ZTest LEqual, _ZWrite 1 - what stands behind it is hidden by it). Single-pass instanced stereo safe.
Shader "HeadMovement/CameraVideo"
{
    Properties
    {
        _MainTex ("Frame", 2D) = "black" {}
        _Alpha ("Opacity", Range(0, 1)) = 1
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest (Always = backdrop / overlay; LessEqual = glyph thumbnail)", Float) = 8
        [Enum(Off, 0, On, 1)] _ZWrite ("ZWrite (On only for an opaque frustum surface)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Background" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "CameraVideo"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite [_ZWrite]
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
                float4 _BlurUV;
                half _Dim;
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
                // side fill (CameraVideoRig.SetSideFill): _BlurUV.x > 0 = a 48-tap golden-angle disc blur and a dim; otherwise the frame as it is
                if (_BlurUV.x > 0.00001)
                {
                    half3 acc = c.rgb;
                    [unroll] for (int k = 1; k <= 48; k++)
                    {
                        float r = sqrt(k / 48.0);
                        float a = k * 2.399963;
                        acc += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv + float2(cos(a), sin(a)) * _BlurUV.xy * r).rgb;
                    }

                    c.rgb = acc / 49.0 * _Dim;
                }

                return half4(c.rgb, _Alpha);
            }
            ENDHLSL
        }
    }
}
