// SPDX-License-Identifier: MIT
// The splat dancers' composite: net.kleinbeck.gaussians Shaders/3DGS/GaussianComposite.shader (MIT, Constantin Kleinbeck;
// derived from aras-p/UnityGaussianSplatting, MIT) with one addition: the accumulated, premultiplied splat image is
// scaled by the global _HmSplatOpacity (SplatDancers.DisplayOpacity: 1 = opaque, the default) before it is blended over
// the camera target, so the dancers can be shown uniformly translucent over the scene and the skeletons. The whole
// image is faded as ONE layer: the inside and the back of the bodies never show (scaling every Gaussian's alpha would).
// Not done: VIEWER_SPEC 3.2 "transparent as displayed" (a per-pixel alpha from the shaded luminance, DisplayTransparency).
Shader "Hidden/HmSplats/Composite"
{
    Properties
    {
        [HideInInspector] _ConvertCompositeGammaToLinear ("Convert composite gamma to linear", Integer) = 1
    }
    SubShader
    {
        ZWrite Off
        ZTest Always
        Cull Off
        Blend One OneMinusSrcAlpha, SrcAlpha OneMinusSrcAlpha

        CGINCLUDE
        #include "UnityCG.cginc"
        #include_with_pragmas "Packages/net.kleinbeck.gaussians/Shaders/Core/GaussianFoveation.hlsl"

        struct appdata
        {
            uint vertexID : SV_VertexID;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct v2f
        {
            float4 vertex : SV_POSITION;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        UNITY_DECLARE_SCREENSPACE_TEXTURE(_GaussianSplatRT);
        float4 _GaussianSplatRT_TexelSize;
        UNITY_DECLARE_SCREENSPACE_TEXTURE(_BlitTexture);
        float4 _BlitScaleBias;
        uint _ConvertCompositeGammaToLinear;
        float _HmSplatOpacity;

        half4 ResolveSplatColor(half4 col)
        {
            if (col.a <= 0) return 0;
            if (_ConvertCompositeGammaToLinear != 0u)
                col.rgb = GammaToLinearSpace(col.rgb / col.a) * col.a;
            // premultiplied: scaling rgb and alpha together fades the whole splat image uniformly
            return col * saturate(_HmSplatOpacity);
        }

        v2f vert(appdata input)
        {
            v2f o = (v2f)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            float2 uv = float2(input.vertexID & 1, (input.vertexID >> 1) & 1) * 2.0;
            o.vertex = float4(uv * 2.0 - 1.0, 1, 1);
        #if UNITY_UV_STARTS_AT_TOP
            uv.y = 1.0 - uv.y;
        #endif
            o.uv = uv;
            return o;
        }

        half4 frag(v2f i) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            float2 uv = i.vertex.xy * _GaussianSplatRT_TexelSize.xy;
            return ResolveSplatColor(UNITY_SAMPLE_SCREENSPACE_TEXTURE(_GaussianSplatRT, uv));
        }

        half4 fragBlit(v2f i) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            float2 uv = SplatLinearToRasterUV(i.uv) * _BlitScaleBias.xy + _BlitScaleBias.zw;
            return ResolveSplatColor(UNITY_SAMPLE_SCREENSPACE_TEXTURE(_BlitTexture, uv));
        }
        ENDCG

        Pass
        {
            Name "Composite"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing
            ENDCG
        }
        Pass
        {
            Name "CompositeURP"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragBlit
            #pragma target 3.5
            #pragma multi_compile_instancing
            ENDCG
        }
    }
}
