Shader "Hidden/MeshLodPartId"
{
    // Editor-only, used by the mesh LOD atlas baker: renders the snapshot geometry with the
    // part id (vertex color) in R and 16-bit normalized view depth in G/B, producing a
    // per-pixel identity + depth reference that matches the beauty captures.
    // Must render into a LINEAR target — sRGB encoding would remap the id/depth bytes.
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual
            Blend Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color      : COLOR;
                float depth01     : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                float3 positionVS = TransformWorldToView(positionWS);
                // Normalized [near, far] view depth — linear for the baker's ortho cameras.
                output.depth01 = saturate((-positionVS.z - _ProjectionParams.y) / (_ProjectionParams.z - _ProjectionParams.y));
                output.color = input.color;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                float2 depthEncoded = frac(float2(1.0, 255.0) * input.depth01);
                depthEncoded.x -= depthEncoded.y * (1.0 / 255.0);
                return float4(input.color.r, depthEncoded.x, depthEncoded.y, 1.0);
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
            #pragma vertex depthVert
            #pragma fragment depthFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            DepthVaryings depthVert(DepthAttributes input)
            {
                DepthVaryings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half depthFrag(DepthVaryings input) : SV_Target
            {
                return input.positionCS.z;
            }
            ENDHLSL
        }
    }

    // Built-in Render Pipeline Fallback
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }

        Pass
        {
            Cull Back
            ZWrite On
            ZTest LEqual
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
            };

            struct v2f
            {
                float4 vertex  : SV_POSITION;
                float4 color   : COLOR;
                float  depth01 : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                float3 viewPos = UnityObjectToViewPos(v.vertex);
                o.depth01 = saturate((-viewPos.z - _ProjectionParams.y) / (_ProjectionParams.z - _ProjectionParams.y));
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 depthEncoded = frac(float2(1.0, 255.0) * i.depth01);
                depthEncoded.x -= depthEncoded.y * (1.0 / 255.0);
                return fixed4(i.color.r, depthEncoded.x, depthEncoded.y, 1.0);
            }
            ENDCG
        }
    }
}
