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

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
                output.positionCS = float4(uv * 2.0 - 1.0, UNITY_RAW_FAR_CLIP_VALUE, 1.0);
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

            half4 Frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
    Fallback Off
}
