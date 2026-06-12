#region

using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

#endregion

namespace FailCake
{
    public class CachedAdditionalShadowsFeature : ScriptableRendererFeature
    {
        public static CachedAdditionalShadowsFeature Instance;

        [Header("Settings")]
        [Tooltip("Shadow atlas. Must be power of two.")]
        public int atlasSize = 4096;

        [Tooltip("Fallback shadow resolution")]
        public int defaultResolution = 1024;

        [Tooltip("Defaults to Hidden/FailCake/CachedShadowSliceBlit.")]
        public Shader blitShader;

        #region PRIVATE

        private CachedAdditionalShadowsPass _shadowPass;
        private CachedAdditionalShadowsPostPass _postPass;

        private readonly Dictionary<EntityId, CachedShadowLight> _byLightId = new Dictionary<EntityId, CachedShadowLight>();
        private readonly HashSet<EntityId> _pending = new HashSet<EntityId>();
        private readonly HashSet<EntityId> _staticBaked = new HashSet<EntityId>();

        private readonly Dictionary<EntityId, BakeCull> _bakeCulls = new Dictionary<EntityId, BakeCull>();

        private Camera _bakeCamera;
        private readonly Plane[] _frustumPlanes = new Plane[6];

        #endregion

        public override void Create() {
            this._shadowPass = new CachedAdditionalShadowsPass(this, RenderPassEvent.BeforeRenderingShadows);
            this._postPass = new CachedAdditionalShadowsPostPass(this._shadowPass, RenderPassEvent.AfterRenderingShadows);

            CachedAdditionalShadowsFeature.Instance = this;

            RenderPipelineManager.beginCameraRendering -= this.OnBeginCameraRendering;
            RenderPipelineManager.beginCameraRendering += this.OnBeginCameraRendering;

            this.RegisterEntities();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
            ref CameraData cameraData = ref renderingData.cameraData;
            if (cameraData.renderType != CameraRenderType.Base) return;
            if (cameraData.cameraType != CameraType.Game && cameraData.cameraType != CameraType.SceneView) return;

            renderer.EnqueuePass(this._shadowPass);
            renderer.EnqueuePass(this._postPass);
        }

        protected override void Dispose(bool disposing) {
            RenderPipelineManager.beginCameraRendering -= this.OnBeginCameraRendering;

            this._shadowPass?.Dispose();
            this._shadowPass = null;
            this._postPass = null;

            if (CachedAdditionalShadowsFeature.Instance == this) CachedAdditionalShadowsFeature.Instance = null;

            this._byLightId.Clear();
            this._pending.Clear();
            this._staticBaked.Clear();
            this._bakeCulls.Clear();

            if (this._bakeCamera)
            {
                Object.DestroyImmediate(this._bakeCamera.gameObject);
                this._bakeCamera = null;
            }
        }

        #region REGISTRATION

        public void Register(CachedShadowLight cache) {
            if (!cache) return;
            EntityId id = cache.GetLightId();
            if (id == EntityId.None) return;

            this._byLightId[id] = cache;
            this._pending.Add(id);
        }

        public void Unregister(CachedShadowLight cache) {
            if (!cache) return;

            EntityId id = cache.GetLightId();
            if (id == EntityId.None) return;

            this._byLightId.Remove(id);
            this._pending.Remove(id);
            this._staticBaked.Remove(id);
            this._bakeCulls.Remove(id);
        }

        public void RequestUpdate(Light light) {
            if (!light) return;
            this._pending.Add(light.GetEntityId());
        }

        public void RequestUpdate(EntityId lightId) {
            if (lightId == EntityId.None) return;
            this._pending.Add(lightId);
        }

        public CachedShadowLight GetCache(EntityId lightId) {
            return this._byLightId.TryGetValue(lightId, out CachedShadowLight c) && c ? c : null;
        }

        public bool NeedsStaticBake(EntityId lightId) {
            return this._pending.Contains(lightId) || !this._staticBaked.Contains(lightId);
        }

        public bool TryGetBakeCull(EntityId lightId, out CullingResults results, out int lightIndex) {
            if (this._bakeCulls.TryGetValue(lightId, out BakeCull bc))
            {
                results = bc.results;
                lightIndex = bc.lightIndex;
                return true;
            }

            results = default(CullingResults);
            lightIndex = -1;
            return false;
        }

        public void MarkStaticBaked(EntityId lightId) {
            this._staticBaked.Add(lightId);
            this._pending.Remove(lightId);
        }

        public void InvalidateStaticBake(EntityId lightId) {
            this._staticBaked.Remove(lightId);
        }

        public void Invalidate() {
            this._staticBaked.Clear();
            foreach (KeyValuePair<EntityId, CachedShadowLight> kv in this._byLightId) this._pending.Add(kv.Key);
        }

        private void RegisterEntities() {
            CachedShadowLight[] all = Object.FindObjectsByType<CachedShadowLight>(FindObjectsInactive.Exclude);
            for (int i = 0; i < all.Length; i++) this.Register(all[i]);
        }

        #endregion

        #region BAKE CULLING

        private void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam) {
            if (!cam || (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView)) return;

            this._bakeCulls.Clear();
            if (this._byLightId.Count == 0) return;

            GeometryUtility.CalculateFrustumPlanes(cam, this._frustumPlanes);

            Vector3 camPos = cam.transform.position;
            float shadowDistance = UniversalRenderPipeline.asset ? UniversalRenderPipeline.asset.shadowDistance : float.MaxValue;

            foreach (KeyValuePair<EntityId, CachedShadowLight> kv in this._byLightId)
            {
                EntityId id = kv.Key;
                CachedShadowLight cache = kv.Value;
                if (!cache || !this.NeedsStaticBake(id)) continue;

                Light light = cache.GetLight();
                if (!light || light.shadows == LightShadows.None || light.shadowStrength <= 0f) continue;
                if (light.type != LightType.Spot && light.type != LightType.Point) continue;

                float range = light.range;
                if (range <= 0f) continue;

                Vector3 lightPos = light.transform.position;

                float fadeDistance = shadowDistance + range;
                if ((lightPos - camPos).sqrMagnitude > fadeDistance * fadeDistance) continue;

                Bounds influence = new Bounds(lightPos, Vector3.one * (range * 2f));

                if (!GeometryUtility.TestPlanesAABB(this._frustumPlanes, influence)) continue;
                if (!this._bakeCamera) this.CreateBakeCamera();

                this._bakeCamera.transform.position = lightPos - Vector3.forward * range;
                this._bakeCamera.transform.rotation = Quaternion.identity;
                this._bakeCamera.orthographic = true;
                this._bakeCamera.orthographicSize = range;
                this._bakeCamera.aspect = 1f;
                this._bakeCamera.nearClipPlane = 0.01f;
                this._bakeCamera.farClipPlane = range * 2f;

                if (!this._bakeCamera.TryGetCullingParameters(out ScriptableCullingParameters cp)) continue;
                cp.shadowDistance = range * 2f;

                LODParameters lodp = cp.lodParameters;
                lodp.isOrthographic = true;
                lodp.orthoSize = 0.0001f;
                cp.lodParameters = lodp;

                CullingResults results = ctx.Cull(ref cp);

                int idx = -1;
                NativeArray<VisibleLight> vl = results.visibleLights;
                for (int i = 0; i < vl.Length; i++)
                    if (vl[i].light == light)
                    {
                        idx = i;
                        break;
                    }

                if (idx < 0) continue;
                this._bakeCulls[id] = new BakeCull { results = results, lightIndex = idx };
            }
        }

        private void CreateBakeCamera() {
            if (this._bakeCamera) return;

            GameObject go = new GameObject("CachedShadowBakeCamera") {
                hideFlags = HideFlags.HideAndDontSave
            };

            this._bakeCamera = go.AddComponent<Camera>();
            this._bakeCamera.enabled = false;
            this._bakeCamera.cullingMask = ~0;
            this._bakeCamera.clearFlags = CameraClearFlags.Nothing;
        }

        #endregion

        private struct BakeCull
        {
            public CullingResults results;
            public int lightIndex;
        }
    }

    public class CachedAdditionalShadowsPostPass : ScriptableRenderPass
    {
        private readonly CachedAdditionalShadowsPass _shadowPass;

        public CachedAdditionalShadowsPostPass(CachedAdditionalShadowsPass shadowPass, RenderPassEvent evt) {
            this._shadowPass = shadowPass;
            this.renderPassEvent = evt;

            this.profilingSampler = new ProfilingSampler("CachedAdditionalShadows_Bind");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {
            this._shadowPass?.RecordPostPass(renderGraph, frameData);
        }
    }
}