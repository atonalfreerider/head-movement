// Skeleton stencil marker (VIEWER_SPEC 3.3: skeletons fully bright inside the translucent avatars).
// A second material on each glowing-skeleton LineRenderer (Dancer.cs), drawn right after the opaque skeleton lines
// (queue Geometry+1) on the same mesh with the same culling: it writes no colour and no depth, only the dancer's
// stencil bit (_SkelRef: lead 1, follow 2) where that dancer's skeleton is visible against the opaque scene.
// HM_AvatarLit's colour pass skips the pixels carrying ITS OWN dancer's bit, so a translucent body no longer dims
// the skeleton inside it, while the partner's body in front still blends over it (partner bit != own bit).
Shader "HeadMovement/SkeletonStencil"
{
    Properties
    {
        _SkelRef ("Skeleton stencil bit (lead 1, follow 2)", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+1" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "SkeletonStencil"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull Back       // the skeleton's bloom shader graph renders front faces only: identical coverage
            ZWrite Off
            ZTest LEqual    // equal depth on the skeleton's own pixels; anything opaque in front keeps them hidden
            Offset -1, -1   // robust against tiny depth differences between this and the shader graph's vertex path
            ColorMask 0

            Stencil
            {
                Ref [_SkelRef]
                WriteMask [_SkelRef]
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return 0;
            }
            ENDHLSL
        }
    }
}
