// SPDX-License-Identifier: MIT
// The splat dancers' composite: net.kleinbeck.gaussians Shaders/3DGS/GaussianComposite.shader (MIT, Constantin Kleinbeck;
// derived from aras-p/UnityGaussianSplatting, MIT) with one addition: the accumulated, premultiplied splat image is
// scaled by the global _HmSplatOpacity (SplatDancers.DisplayOpacity: 1 = opaque, the default) before it is blended over
// the camera target, so the dancers can be shown uniformly translucent over the scene and the skeletons. The whole
// image is faded as ONE layer: the inside and the back of the bodies never show (scaling every Gaussian's alpha would).
// Not done: VIEWER_SPEC 3.2 "transparent as displayed" (a per-pixel alpha from the shaded luminance, DisplayTransparency).
//
// Passes 0 and 1 are the package's (0: the BiRP composite, 1: URP, Blitter-driven). Their uv is a memory-order copy (the
// UNITY_UV_STARTS_AT_TOP flip in vert undoes the raster flip of a fullscreen triangle): right when the source and the target have
// the same UV origin (the package forces an intermediate camera texture). SplatLowResFeature composites its own render texture
// (BottomLeft) into whatever URP's active target is - on the Quest the XR eye texture itself (TopLeft on Vulkan) - and flips
// through _BlitScaleBias (-y) when the two origins differ, the way URP's final blit does (RenderingUtils.GetFinalBlitScaleBias).
// Pass 2 (SplatLowResFeature with splat depth): pass 1 plus the low-res splat depth (_HmSplatLowResDepth, written by the
// renderer's SplatDepth pass at the layer's resolution) as SV_Depth, depth-tested against the scene: the splats are hidden behind
// nearer scene geometry and the overlays drawn after (floor marks, the contact disc) depth-test against the dancers.
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

        UNITY_DECLARE_SCREENSPACE_TEXTURE(_HmSplatLowResDepth);
        float4 _HmSplatLowResDepth_TexelSize;

        float NearerDepth(float a, float b)
        {
        #if defined(UNITY_REVERSED_Z)
            return max(a, b);
        #else
            return min(a, b);
        #endif
        }

        // the low-res splat depth, dilated by one texel (the nearest of the 2x2 point samples around uv) so the bilinear colour's
        // reach at the silhouette still has a depth; far where no splat wrote one
        half4 fragBlitDepth(v2f i, out float depth : SV_Depth) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            float2 uv = SplatLinearToRasterUV(i.uv) * _BlitScaleBias.xy + _BlitScaleBias.zw;
            float2 h = 0.5 * _HmSplatLowResDepth_TexelSize.xy;
            float d = UNITY_SAMPLE_SCREENSPACE_TEXTURE(_HmSplatLowResDepth, uv + float2(-h.x, -h.y)).r;
            d = NearerDepth(d, UNITY_SAMPLE_SCREENSPACE_TEXTURE(_HmSplatLowResDepth, uv + float2(h.x, -h.y)).r);
            d = NearerDepth(d, UNITY_SAMPLE_SCREENSPACE_TEXTURE(_HmSplatLowResDepth, uv + float2(-h.x, h.y)).r);
            d = NearerDepth(d, UNITY_SAMPLE_SCREENSPACE_TEXTURE(_HmSplatLowResDepth, uv + float2(h.x, h.y)).r);
        #if defined(UNITY_REVERSED_Z)
            if (d <= 0.0) discard;
        #else
            if (d >= 1.0) discard;
        #endif
            half4 col = ResolveSplatColor(UNITY_SAMPLE_SCREENSPACE_TEXTURE(_BlitTexture, uv));
            if (col.a <= 0.0) discard;
            depth = d;
            return col;
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
        Pass
        {
            Name "CompositeURPLowResDepth"
            ZWrite On
            ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragBlitDepth
            #pragma target 3.5
            #pragma multi_compile_instancing
            ENDCG
        }
    }
}
