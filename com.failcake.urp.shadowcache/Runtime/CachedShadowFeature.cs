#region

using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

#endregion

namespace FailCake
{
    public sealed class CachedShadowFeature : ScriptableRendererFeature
    {
        #region STATIC

        private static readonly HashSet<EntityId> REQUESTED_SHADOWS = new HashSet<EntityId>();

        internal static int FRAME_ID;

        private static long CACHE_UPDATE_GENERATION;

        private static int S_USES_STRUCTURED_BUFFER = -1;
        private static PropertyInfo S_RENDERING_MODE_PROPERTY;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RESET_STATICS() {
            CachedShadowFeature.REQUESTED_SHADOWS.Clear();
            CachedShadowFeature.FRAME_ID = 0;
            CachedShadowFeature.CACHE_UPDATE_GENERATION = 0;
            CachedShadowFeature.S_USES_STRUCTURED_BUFFER = -1;
            CachedShadowFeature.S_RENDERING_MODE_PROPERTY = null;

            // EVENTS ----
            RenderPipelineManager.beginContextRendering -= CachedShadowFeature.OnBeginContextRendering;
            RenderPipelineManager.beginContextRendering += CachedShadowFeature.OnBeginContextRendering;
            // ---------
        }

        public static void RefreshShadow(Light light) {
            if (!light || light.shadows == LightShadows.None) return;
            CachedShadowFeature.REQUESTED_SHADOWS.Add(light.GetEntityId());
        }

        public static void ForceRefresh() {
            CachedShadowFeature.CACHE_UPDATE_GENERATION++;
        }

        internal static bool ConsumeShadowRequest(EntityId lightId) {
            return CachedShadowFeature.REQUESTED_SHADOWS.Remove(lightId);
        }

        internal static long GetCacheUpdateGeneration() {
            return CachedShadowFeature.CACHE_UPDATE_GENERATION;
        }

        private static bool IsDeferred(ScriptableRenderer renderer) {
            if (renderer is not UniversalRenderer universalRenderer) return false;

            if (CachedShadowFeature.S_RENDERING_MODE_PROPERTY == null) CachedShadowFeature.S_RENDERING_MODE_PROPERTY = typeof(UniversalRenderer).GetProperty("renderingModeActual", BindingFlags.Instance | BindingFlags.NonPublic);
            if (CachedShadowFeature.S_RENDERING_MODE_PROPERTY == null) return false;

            return (RenderingMode)CachedShadowFeature.S_RENDERING_MODE_PROPERTY.GetValue(universalRenderer) == RenderingMode.Deferred;
        }

        private static bool UsesStructuredBuffer() {
            if (CachedShadowFeature.S_USES_STRUCTURED_BUFFER < 0)
            {
                CachedShadowFeature.S_USES_STRUCTURED_BUFFER = 0;

                Type type = typeof(UniversalRenderPipelineAsset).Assembly.GetType("UnityEngine.Rendering.Universal.RenderingUtils");
                const BindingFlags FLAGS = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                PropertyInfo property = type?.GetProperty("useStructuredBuffer", FLAGS);

                try
                {
                    if (property != null)
                    {
                        if ((bool)property.GetValue(null)) CachedShadowFeature.S_USES_STRUCTURED_BUFFER = 1;
                    }
                    else
                    {
                        FieldInfo field = type?.GetField("useStructuredBuffer", FLAGS);
                        if (field != null && (bool)field.GetValue(null)) CachedShadowFeature.S_USES_STRUCTURED_BUFFER = 1;
                    }
                }
                catch
                {
                    CachedShadowFeature.S_USES_STRUCTURED_BUFFER = 0;
                }
            }

            return CachedShadowFeature.S_USES_STRUCTURED_BUFFER == 1;
        }

        private static void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras) {
            CachedShadowFeature.FRAME_ID++;
        }

        #endregion

        [Header("Cache")]
        public int cellResolution = 512;

        #region PRIVATE FIELDS

        private CachedShadowPass _pass;

        #endregion

        public override void Create() {
            this._pass?.Dispose();

            this._pass = new CachedShadowPass(this);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
            if (!Application.isPlaying) return;

            ref CameraData cameraData = ref renderingData.cameraData;

            if (cameraData.renderType != CameraRenderType.Base) return;
            if (cameraData.cameraType != CameraType.Game) return;

            if (this._pass == null) return;

            if (CachedShadowFeature.IsDeferred(renderer))
            {
                Debug.LogWarning("Deferred not supported. Switch the renderer to Forward+ or Deferred+.");
                return;
            }

            if (CachedShadowFeature.UsesStructuredBuffer())
            {
                Debug.LogWarning("Additional-light shadows needs to be enabled");
                return;
            }

            if (!renderingData.shadowData.supportsAdditionalLightShadows) return;

            NativeArray<VisibleLight> lights = renderingData.lightData.visibleLights;
            int mainIndex = renderingData.lightData.mainLightIndex;

            for (int i = 0; i < lights.Length; i++)
            {
                if (i == mainIndex) continue;

                VisibleLight visibleLight = lights[i];
                Light light = visibleLight.light;
                if (!light) continue;
                if (visibleLight.lightType != LightType.Spot && visibleLight.lightType != LightType.Point) continue;
                if (light.shadows == LightShadows.None || light.shadowStrength <= 0f) continue;
                if (!this._pass.Prepare(renderingData.shadowData)) return;

                renderingData.shadowData.supportsAdditionalLightShadows = false;
                renderer.EnqueuePass(this._pass);
                return;
            }
        }

        protected override void Dispose(bool disposing) {
            this._pass?.Dispose();
            this._pass = null;
        }
    }
}