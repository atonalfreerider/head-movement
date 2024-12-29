Shader "Custom/ProjectedTexture"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _DepthFade ("Depth Fade", Float) = 0.1
        _CollisionSoftness ("Collision Softness", Float) = 0.05
        _DeformStrength ("Deform Strength", Float) = 0.3
    }
    
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 100
        
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off // Render both sides

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float4 worldPos : TEXCOORD1;
                float3 viewDir : TEXCOORD2;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _CameraPosition;
            float4x4 _CollisionPrimitives;
            float _DepthFade;
            float _CollisionSoftness;
            float _DeformStrength;
            
            float SphereCollision(float3 pos, float3 center, float radius)
            {
                return length(pos - center) - radius;
            }
            
            float CapsuleCollision(float3 pos, float3 start, float3 end, float radius)
            {
                float3 pa = pos - start;
                float3 ba = end - start;
                float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
                return length(pa - ba * h) - radius;
            }
            
            float BoxCollision(float3 pos, float3 center, float3 size)
            {
                float3 d = abs(pos - center) - size;
                return min(max(d.x, max(d.y, d.z)), 0.0) + length(max(d, 0.0));
            }

            v2f vert (appdata v)
            {
                v2f o;
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 viewDir = normalize(_CameraPosition - worldPos);
                
                // Calculate total deformation
                float totalDeform = 0;
                float3 deformDir = float3(0,0,0);
                float3 closestPoint = worldPos;
                
                for (int i = 0; i < (int)_CollisionPrimitives[3].x; i++)
                {
                    float4 primitive = _CollisionPrimitives[i];
                    float dist = 0;
                    float3 normal = float3(0,0,0);
                    float3 closest = primitive.xyz;
                    
                    if (i == 0) // Head
                    {
                        dist = SphereCollision(worldPos, primitive.xyz, primitive.w);
                        normal = normalize(worldPos - primitive.xyz);
                    }
                    else if (i == 1) // Torso
                    {
                        dist = BoxCollision(worldPos, primitive.xyz, primitive.www);
                        normal = normalize(worldPos - primitive.xyz);
                    }
                    else // Limbs
                    {
                        float3 capsuleEnd = primitive.xyz + float3(0, primitive.w, 0);
                        dist = CapsuleCollision(worldPos, primitive.xyz, capsuleEnd, primitive.w);
                        float h = saturate(dot(worldPos - primitive.xyz, capsuleEnd - primitive.xyz) 
                            / dot(capsuleEnd - primitive.xyz, capsuleEnd - primitive.xyz));
                        closest = lerp(primitive.xyz, capsuleEnd, h);
                        normal = normalize(worldPos - closest);
                    }
                    
                    float deform = saturate(1 - dist / _CollisionSoftness);
                    if (deform > totalDeform)
                    {
                        totalDeform = deform;
                        closestPoint = closest;
                    }
                    deformDir += normal * deform;
                }
                
                // Apply deformation
                if (totalDeform > 0)
                {
                    float3 toClosest = normalize(closestPoint - worldPos);
                    worldPos += (deformDir * 0.5 + toClosest * 0.5) * totalDeform * _DeformStrength;
                }
                
                o.vertex = UnityWorldToClipPos(worldPos);
                o.worldPos = float4(worldPos, 1);
                o.viewDir = viewDir;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }
            
            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);
                
                if (col.a < 0.01) discard;
                
                // Enhanced view-dependent effects
                float fresnel = pow(1 - abs(dot(normalize(i.viewDir), float3(0,0,-1))), 3);
                float depth = distance(i.worldPos.xyz, _CameraPosition);
                float depthFade = saturate(1 - (depth * _DepthFade));
                
                col.a *= depthFade * (1 - fresnel * 0.7);
                
                return col;
            }
            ENDCG
        }
    }
}
