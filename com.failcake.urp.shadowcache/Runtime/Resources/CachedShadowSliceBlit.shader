Shader "Hidden/FailCake/CachedShadowSliceBlit"
{
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "CachedShadowSliceCopyDepth"

            ZWrite On
            ZTest Always
            Cull Off
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_FLOAT(_FailCakeCachedShadowSource);
            float4 _FailCakeCachedShadowTiles[256];
            float4 _FailCakeCachedShadowAtlasSize;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                float4 tile = _FailCakeCachedShadowTiles[vertexID / 6];
                uint corner = vertexID % 6;

                float2 uv = float2(corner == 1 || corner == 3 || corner == 4,
                   corner == 2 || corner == 4 || corner == 5);

                float2 positionCS = (tile.xy + uv * tile.zw) * _FailCakeCachedShadowAtlasSize.xy * 2.0 - 1.0;

                #if UNITY_UV_STARTS_AT_TOP
                positionCS.y = -positionCS.y;
                #endif
                output.positionCS = float4(positionCS, UNITY_RAW_FAR_CLIP_VALUE, 1.0);
                return output;
            }

            void Frag(Varyings input, out float outDepth : SV_Depth)
            {
                outDepth = LOAD_TEXTURE2D(_FailCakeCachedShadowSource, int2(input.positionCS.xy)).r;
            }
            ENDHLSL
        }

        Pass
        {
            Name "CachedShadowSliceClearDepth"

            ZWrite On
            ZTest Always
            Cull Off
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            float4 Vert(uint vertexID : SV_VertexID) : SV_POSITION
            {
                float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
                return float4(uv * 2.0 - 1.0, UNITY_RAW_FAR_CLIP_VALUE, 1.0);
            }

            float Frag() : SV_Depth { return UNITY_RAW_FAR_CLIP_VALUE; }
            ENDHLSL
        }
    }
    Fallback Off
}