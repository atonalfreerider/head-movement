// Dancer body (SmplxAvatar) and its attachments (shoes): photoreal albedo or a flat tint on the skinned SMPL-X body.
// Quest-friendly: one texture fetch, main light + spherical-harmonics ambient only (no additional lights, no PBR).
// The albedo already carries the studio lighting of the source videos, so scene lighting only modulates it
// (_LightInfluence) to give the body shape without double shading. Single-pass instanced stereo safe.
//
// Translucency (VIEWER_SPEC 3.2: avatars ~0.3 opacity, depth-correct): the renderer carries TWO materials of this
// shader. Material 1 keeps only the "SRPDefaultUnlit" depth prepass (queue 2990: ZWrite, no colour); material 2
// keeps only the "UniversalForward" colour pass (queue 3000: ZTest LEqual, alpha blend, no ZWrite). Every
// translucent prepass runs before any translucent colour pass, so each pixel gets the colour of its front-most
// translucent surface once, blended over the opaque skeleton inside - no back faces, no sorting pops between the
// dancers. Material.SetShaderPassEnabled switches the passes per material (pass names = their LightMode tags).
// Skeletons (VIEWER_SPEC 3.3 option, 2026-10-07, off by default: SmplxAvatar.SkeletonsOverBodies): while translucent,
// the colour pass can skip the pixels where this dancer's own glowing skeleton is visible (stencil bit _SkelRef written
// by HM_SkeletonStencil), so the skeleton inside is not tinted by its body; the partner's body in front still blends
// over it. _SkelComp = Always (8) leaves the pass as before; NotEqual (6) turns the skip on.
//
// Shoes (_HideFeet): TEXCOORD1.x is each vertex's rest-pose height above its own ankle joint (m; +1 off the lower
// legs). It is interpolated across the skinned triangles, so clip(h - _FootCut) removes the bare foot along a cut
// that moves with every pose. Applied in every pass (prepass, colour, shadow, depth).
Shader "HeadMovement/AvatarLit"
{
    Properties
    {
        [MainTexture] _BaseMap ("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor ("Tint", Color) = (1, 1, 1, 1)
        _LightInfluence ("Light Influence", Range(0, 1)) = 0.35
        _Wrap ("Wrap Lighting", Range(0, 1)) = 0.5
        _Exposure ("Exposure", Float) = 1.0
        _Opacity ("Opacity", Range(0, 1)) = 1.0
        [Toggle] _UseVertexColor ("Multiply Vertex Colour", Float) = 0
        _Specular ("Specular (Blinn sheen, 0 = off)", Range(0, 1)) = 0
        _SpecPower ("Specular Power", Range(2, 256)) = 32
        [Toggle] _HideFeet ("Hide Feet (shoes)", Float) = 0
        _FootCut ("Foot cut (m above the ankle joint, rest pose)", Range(-0.1, 0.1)) = -0.01
        // skeleton stencil (colour pass only): skip the pixels carrying this dancer's skeleton bit (HM_SkeletonStencil),
        // so the body does not dim its own skeleton. Comp 8 = Always (off), 6 = NotEqual (on; SmplxAvatar sets it).
        [HideInInspector] _SkelRef ("Skeleton stencil bit", Float) = 0
        [HideInInspector] _SkelComp ("Skeleton stencil compare", Float) = 8
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    TEXTURE2D(_BaseMap);
    SAMPLER(sampler_BaseMap);

    CBUFFER_START(UnityPerMaterial)
        float4 _BaseMap_ST;
        half4 _BaseColor;
        half _LightInfluence;
        half _Wrap;
        half _Exposure;
        half _Opacity;
        half _UseVertexColor;
        half _Specular;
        half _SpecPower;
        half _HideFeet;
        float _FootCut;
    CBUFFER_END

    // the foot mask: h = rest height above the ankle (TEXCOORD1.x), interpolated over the skinned triangle
    void ClipFeet(float footHeight)
    {
        if (_HideFeet > 0.5h) clip(footHeight - _FootCut);
    }

    // one position path for the prepass and the colour pass: their depths must agree bit for bit (ZTest LEqual)
    float4 AvatarClipPos(float3 positionOS, out float3 positionWS)
    {
        positionWS = TransformObjectToWorld(positionOS);
        return TransformWorldToHClip(positionWS);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        // translucent depth prepass (material 1, queue 2990): front-most body depth, no colour
        Pass
        {
            Name "SRPDefaultUnlit"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull Back
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vertDepth
            #pragma fragment fragDepth
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 foot : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float foot : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vertDepth(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                ZERO_INITIALIZE(Varyings, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 positionWS;
                output.positionCS = AvatarClipPos(input.positionOS.xyz, positionWS);
                output.foot = input.foot.x;
                return output;
            }

            half4 fragDepth(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                ClipFeet(input.foot);
                return 0;
            }
            ENDHLSL
        }

        // colour (material 2, queue 3000): front-most surface only (LEqual against the prepass), alpha = _Opacity
        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

            // VIEWER_SPEC 3.3: never blend over this dancer's own skeleton (its bit, written by HM_SkeletonStencil)
            Stencil
            {
                Ref [_SkelRef]
                ReadMask [_SkelRef]
                WriteMask 0
                Comp [_SkelComp]
                Pass Keep
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 foot : TEXCOORD1;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                half4 color : TEXCOORD3;
                float foot : TEXCOORD4;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                ZERO_INITIALIZE(Varyings, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = AvatarClipPos(input.positionOS.xyz, output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.color = input.color;
                output.foot = input.foot.x;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                ClipFeet(input.foot);
                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor.rgb;
                albedo *= lerp(half3(1, 1, 1), input.color.rgb, _UseVertexColor);
                half3 n = normalize(input.normalWS);
                Light light = GetMainLight();
                half ndl = saturate((dot(n, light.direction) + _Wrap) / (1.0h + _Wrap));
                half3 lit = light.color * ndl + SampleSH(n);
                half3 shade = lerp(half3(1, 1, 1), lit, _LightInfluence);
                half3 colour = albedo * shade * _Exposure;
                if (_Specular > 0.0h)
                {
                    half3 v = normalize(GetWorldSpaceViewDir(input.positionWS));
                    half3 h = normalize(light.direction + v);
                    colour += light.color * (_Specular * pow(saturate(dot(n, h)), _SpecPower));
                }

                // _BaseColor.a: legacy per-material fade (the dance-graph miniature couple); 1 for the full-size dancers
                return half4(colour, _Opacity * _BaseColor.a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex vertShadow
            #pragma fragment fragShadow
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 foot : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float foot : TEXCOORD0;
            };

            Varyings vertShadow(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                output.positionCS = positionCS;
                output.foot = input.foot.x;
                return output;
            }

            half4 fragShadow(Varyings input) : SV_Target
            {
                ClipFeet(input.foot);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma vertex vertDepthOnly
            #pragma fragment fragDepthOnly
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 foot : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float foot : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vertDepthOnly(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                ZERO_INITIALIZE(Varyings, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.foot = input.foot.x;
                return output;
            }

            half4 fragDepthOnly(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                ClipFeet(input.foot);
                return input.positionCS.z;
            }
            ENDHLSL
        }
    }
}
