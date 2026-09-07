#region

using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

#endregion

namespace HyenaQuest
{
    public sealed class CachedShadowFeature : ScriptableRendererFeature
    {
        #region STATIC

        private static readonly Dictionary<EntityId, long> REQUESTED_SHADOWS = new Dictionary<EntityId, long>();

        private static long UPDATE_GENERATION;
        private static long CACHE_UPDATE_GENERATION;

        private static Func<UniversalRenderer, RenderingMode> RENDERING_MODE;
        private static int USES_STRUCTURED_BUFFER = -1;

        internal static int FRAME_ID;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() {
            CachedShadowFeature.REQUESTED_SHADOWS.Clear();

            CachedShadowFeature.UPDATE_GENERATION = 0;
            CachedShadowFeature.CACHE_UPDATE_GENERATION = 0;
            CachedShadowFeature.FRAME_ID = 0;

            // EVENTS ---
            RenderPipelineManager.beginContextRendering -= CachedShadowFeature.OnBeginContextRendering;
            RenderPipelineManager.beginContextRendering += CachedShadowFeature.OnBeginContextRendering;
            // ----------
        }

        public static void RefreshShadow(Light light) {
            if (!light) return;
            CachedShadowFeature.REQUESTED_SHADOWS[light.GetEntityId()] = ++CachedShadowFeature.UPDATE_GENERATION;
        }

        public static void ForceRefresh() {
            CachedShadowFeature.CACHE_UPDATE_GENERATION = ++CachedShadowFeature.UPDATE_GENERATION;
            CachedShadowFeature.REQUESTED_SHADOWS.Clear();
        }

        internal static long GetShadowGeneration(EntityId lightId) {
            return CachedShadowFeature.REQUESTED_SHADOWS.TryGetValue(lightId, out long generation) ? generation : CachedShadowFeature.CACHE_UPDATE_GENERATION;
        }

        private static bool IsDeferred(ScriptableRenderer renderer) {
            if (renderer is not UniversalRenderer universalRenderer) return false;
            if (CachedShadowFeature.RENDERING_MODE == null)
            {
                MethodInfo getter = typeof(UniversalRenderer).GetProperty("renderingModeActual", BindingFlags.Instance | BindingFlags.NonPublic)?.GetGetMethod(true);
                if (getter == null) throw new UnityException("renderingModeActual not found");
                CachedShadowFeature.RENDERING_MODE = (Func<UniversalRenderer, RenderingMode>)Delegate.CreateDelegate(typeof(Func<UniversalRenderer, RenderingMode>), getter);
            }

            return CachedShadowFeature.RENDERING_MODE(universalRenderer) == RenderingMode.Deferred;
        }

        private static bool UsesStructuredBuffer() {
            if (CachedShadowFeature.USES_STRUCTURED_BUFFER < 0)
            {
                const BindingFlags FLAGS = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

                Type type = typeof(UniversalRenderPipelineAsset).Assembly.GetType("UnityEngine.Rendering.Universal.RenderingUtils");
                PropertyInfo property = type?.GetProperty("useStructuredBuffer", FLAGS);

                object value = property != null ? property.GetValue(null) : type?.GetField("useStructuredBuffer", FLAGS)?.GetValue(null);
                if (value is not bool) throw new UnityException("useStructuredBuffer not found");

                CachedShadowFeature.USES_STRUCTURED_BUFFER = (bool)value ? 1 : 0;
            }

            return CachedShadowFeature.USES_STRUCTURED_BUFFER == 1;
        }

        private static void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras) {
            CachedShadowFeature.FRAME_ID++;
        }

        #endregion

        [Header("Settings"), Min(128)]
        public int cellResolution = 512;

        [Min(0)]
        public int maxStaticSlicesPerFrame;

        public bool cullDynamicPointFaces = true;

        #region PRIVATE

        private CachedShadowPass _pass;

        #endregion

        public override void Create() {
            this._pass?.Dispose();
            this._pass = null;

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            this._pass = new CachedShadowPass(this);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
            if (!Application.isPlaying || this._pass == null) return;

            ref CameraData cameraData = ref renderingData.cameraData;
            if (cameraData.renderType != CameraRenderType.Base || cameraData.cameraType != CameraType.Game) return;

            if (CachedShadowFeature.IsDeferred(renderer) || CachedShadowFeature.UsesStructuredBuffer()) return;
            if (!renderingData.shadowData.supportsAdditionalLightShadows) return;

            NativeArray<VisibleLight> lights = renderingData.lightData.visibleLights;
            for (int i = 0; i < lights.Length; i++)
            {
                if (i == renderingData.lightData.mainLightIndex) continue; // Skip sun

                VisibleLight visibleLight = lights[i];
                if (visibleLight.lightType != LightType.Spot && visibleLight.lightType != LightType.Point) continue;

                Light light = visibleLight.light;
                if (!light || light.shadows == LightShadows.None || light.shadowStrength <= 0f) continue;
                if (!this._pass.Prepare(renderingData.shadowData)) return;

                renderingData.shadowData.supportsAdditionalLightShadows = false;
                renderer.EnqueuePass(this._pass);
                return;
            }
        }

        #region PRIVATE

        protected override void Dispose(bool disposing) {
            this._pass?.Dispose();
            this._pass = null;
        }

        #endregion
    }
}