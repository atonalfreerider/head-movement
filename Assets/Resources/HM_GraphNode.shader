// Dance-graph node icons (VIEWER_SPEC 3.10/3.11), drawn with Graphics.RenderMeshInstanced: one call per icon
// shape, per-instance colour and glow from MaterialPropertyBlock arrays (_NodeColor rgb = tone, a = diffuse
// weight; _NodeGlow x = emission, y = rim). Simple fixed-direction shading + rim + emission so the icons read in
// any room light (passthrough included). _Fade dims the whole graph during view-state blends.
// Single-pass instanced stereo safe.
Shader "HeadMovement/GraphNode"
{
    Properties
    {
        _Ambient ("Ambient", Float) = 0.35
        _Fade ("Fade", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+10" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "GraphNode"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Ambient;
                float _Fade;
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(NodeProps)
                UNITY_DEFINE_INSTANCED_PROP(float4, _NodeColor)
                UNITY_DEFINE_INSTANCED_PROP(float4, _NodeGlow)
            UNITY_INSTANCING_BUFFER_END(NodeProps)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                ZERO_INITIALIZE(Varyings, output);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewWS = GetCameraPositionWS() - positionWS;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float4 color = UNITY_ACCESS_INSTANCED_PROP(NodeProps, _NodeColor);
                float4 glow = UNITY_ACCESS_INSTANCED_PROP(NodeProps, _NodeGlow);
                float3 n = normalize(input.normalWS);
                float3 v = normalize(input.viewWS);
                float3 l = normalize(float3(0.35, 0.85, -0.4));
                float diffuse = saturate(dot(n, l)) * 0.65 + _Ambient;
                float rim = pow(1.0 - saturate(dot(n, v)), 2.0);
                float3 c = color.rgb * (diffuse * color.a + glow.x) + color.rgb * rim * glow.y;
                return half4(c * _Fade, 1);
            }
            ENDHLSL
        }
    }
}
