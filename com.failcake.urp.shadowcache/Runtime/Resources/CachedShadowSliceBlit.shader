Shader "Hidden/FailCake/CachedShadowSliceBlit"
{
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }

        // Copies one tile of the static atlas into the same tile of the sampled atlas. Both atlases share a layout and the
        // caller sets the viewport to the tile, so a same-pixel LOAD is a 1:1 copy with no UV origin or Y-flip conventions.
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

            // Plain TEXTURE2D, not _X: the atlas is shared, not one slice per eye, so stereo indexing would be wrong in VR.
            TEXTURE2D_FLOAT(_FailCakeCachedShadowSource);

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            // The caller's viewport clips this to the tile. Depth comes from the fragment, so the vertex z doesn't matter.
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

        // Clears one atlas tile to far depth. ClearRenderTarget ignores the viewport and would wipe every other light's
        // tile, so draw a triangle at the far plane instead and let the viewport clip it.
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

            // UNITY_RAW_FAR_CLIP_VALUE handles reversed-Z for us.
            float4 Vert(uint vertexID : SV_VertexID) : SV_POSITION
            {
                float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
                return float4(uv * 2.0 - 1.0, UNITY_RAW_FAR_CLIP_VALUE, 1.0);
            }

            // ColorMask 0 throws this away, but some backends reject a fragment stage with no output.
            half4 Frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
    Fallback Off
}
