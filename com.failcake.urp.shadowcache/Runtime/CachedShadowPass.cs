#region

using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

#endregion

namespace FailCake
{
    internal sealed class CachedShadowPass : ScriptableRenderPass
    {
        #region STATIC

        private const int PASS_COPY = 0;
        private const int PASS_CLEAR = 1;

        private const float LIGHT_TYPE_SPOT = 0f;
        private const float LIGHT_TYPE_POINT = 1f;

        private const string BLIT_SHADER_NAME = "Hidden/FailCake/CachedShadowSliceBlit";
        private const string STATIC_ATLAS_NAME = "_FailCakeCachedShadowStatic";
        private const string MAIN_ATLAS_NAME = "_FailCakeCachedShadowMain";

        private static readonly Vector4 DEFAULT_SHADOW_PARAMS = new Vector4(0f, 0f, 0f, -1f);

        private static readonly int ID_ADDITIONAL_SHADOW_PARAMS = Shader.PropertyToID("_AdditionalShadowParams");
        private static readonly int ID_ADDITIONAL_WORLD_TO_SHADOW = Shader.PropertyToID("_AdditionalLightsWorldToShadow");
        private static readonly int ID_ADDITIONAL_SHADOW_FADE_PARAMS = Shader.PropertyToID("_AdditionalShadowFadeParams");
        private static readonly int ID_ADDITIONAL_SHADOW_OFFSET0 = Shader.PropertyToID("_AdditionalShadowOffset0");
        private static readonly int ID_ADDITIONAL_SHADOW_OFFSET1 = Shader.PropertyToID("_AdditionalShadowOffset1");
        private static readonly int ID_ADDITIONAL_SHADOWMAP_SIZE = Shader.PropertyToID("_AdditionalShadowmapSize");
        private static readonly int ID_ADDITIONAL_SHADOWMAP_TEXTURE = Shader.PropertyToID("_AdditionalLightsShadowmapTexture");

        private static readonly int ID_SHADOW_BIAS = Shader.PropertyToID("_ShadowBias");
        private static readonly int ID_LIGHT_DIRECTION = Shader.PropertyToID("_LightDirection");
        private static readonly int ID_LIGHT_POSITION = Shader.PropertyToID("_LightPosition");
        private static readonly int ID_COPY_SOURCE = Shader.PropertyToID("_FailCakeCachedShadowSource");

        private static readonly GlobalKeyword K_ADDITIONAL_LIGHT_SHADOWS = GlobalKeyword.Create(ShaderKeywordStrings.AdditionalLightShadows);
        private static readonly GlobalKeyword K_CASTING_PUNCTUAL_LIGHT_SHADOW = GlobalKeyword.Create(ShaderKeywordStrings.CastingPunctualLightShadow);
        private static readonly GlobalKeyword K_SOFT_SHADOWS = GlobalKeyword.Create(ShaderKeywordStrings.SoftShadows);
        private static readonly GlobalKeyword K_SOFT_SHADOWS_LOW = GlobalKeyword.Create(ShaderKeywordStrings.SoftShadowsLow);
        private static readonly GlobalKeyword K_SOFT_SHADOWS_MEDIUM = GlobalKeyword.Create(ShaderKeywordStrings.SoftShadowsMedium);
        private static readonly GlobalKeyword K_SOFT_SHADOWS_HIGH = GlobalKeyword.Create(ShaderKeywordStrings.SoftShadowsHigh);

        private static PropertyInfo S_PIPELINE_SOFT_QUALITY_PROPERTY;
        private static FieldInfo S_PIPELINE_SOFT_QUALITY_FIELD;
        private static FieldInfo S_ADDITIONAL_KEYWORD_FIELD;
        private static FieldInfo S_SOFT_KEYWORD_FIELD;

        private static int S_IS_XR_MOBILE = -1;

        private static void GetLinearDistanceFadeParams(float fadeDistanceSq, float cascadeBorder, out float fadeScale, out float fadeBias) {
            if (cascadeBorder < 0.0001f)
            {
                const float MULTIPLIER = 1000f;
                fadeScale = MULTIPLIER;
                fadeBias = -fadeDistanceSq * MULTIPLIER;
                return;
            }

            float border = 1f - cascadeBorder;
            border *= border;

            float near = border * fadeDistanceSq;
            float denominator = Mathf.Max(0.0001f, fadeDistanceSq - near);
            fadeScale = 1f / denominator;
            fadeBias = -near / denominator;
        }

        private static void RestoreCameraViewProjection(RasterCommandBuffer cmd, UniversalCameraData cameraData) {
            cmd.SetViewProjectionMatrices(cameraData.GetViewMatrix(), cameraData.GetProjectionMatrix());
        }

        private static void SetSoftShadowKeywords(RasterCommandBuffer cmd, bool softEnabled) {
            cmd.SetKeyword(CachedShadowPass.K_SOFT_SHADOWS, softEnabled);

            bool perLightQuality = true;
            #if ENABLE_VR && ENABLE_XR_MODULE
            #if PLATFORM_WINRT || PLATFORM_ANDROID
            perLightQuality = !CachedShadowPass.IsXRMobile();
            #endif
            #endif

            if (!perLightQuality)
            {
                SoftShadowQuality quality = CachedShadowPass.TryGetPipelineSoftShadowQuality(out SoftShadowQuality resolved) ? resolved : SoftShadowQuality.Medium;
                bool low = softEnabled && quality == SoftShadowQuality.Low;
                bool medium = softEnabled && quality == SoftShadowQuality.Medium;
                bool high = softEnabled && quality == SoftShadowQuality.High;

                cmd.SetKeyword(CachedShadowPass.K_SOFT_SHADOWS_LOW, low);
                cmd.SetKeyword(CachedShadowPass.K_SOFT_SHADOWS_MEDIUM, medium);
                cmd.SetKeyword(CachedShadowPass.K_SOFT_SHADOWS_HIGH, high);

                if (low || medium || high) cmd.SetKeyword(CachedShadowPass.K_SOFT_SHADOWS, false);
                return;
            }

            cmd.SetKeyword(CachedShadowPass.K_SOFT_SHADOWS_LOW, false);
            cmd.SetKeyword(CachedShadowPass.K_SOFT_SHADOWS_MEDIUM, false);
            cmd.SetKeyword(CachedShadowPass.K_SOFT_SHADOWS_HIGH, false);
        }

        private static bool TryGetPipelineSoftShadowQuality(out SoftShadowQuality quality) {
            quality = SoftShadowQuality.Medium;

            UniversalRenderPipelineAsset asset = UniversalRenderPipeline.asset;
            if (!asset) return false;

            if (CachedShadowPass.S_PIPELINE_SOFT_QUALITY_PROPERTY == null)
            {
                const BindingFlags FLAGS = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

                CachedShadowPass.S_PIPELINE_SOFT_QUALITY_PROPERTY = typeof(UniversalRenderPipelineAsset).GetProperty("softShadowQuality", FLAGS);
                if (CachedShadowPass.S_PIPELINE_SOFT_QUALITY_PROPERTY == null) CachedShadowPass.S_PIPELINE_SOFT_QUALITY_FIELD = typeof(UniversalRenderPipelineAsset).GetField("m_SoftShadowQuality", FLAGS);
            }

            if (CachedShadowPass.S_PIPELINE_SOFT_QUALITY_PROPERTY != null)
            {
                quality = (SoftShadowQuality)CachedShadowPass.S_PIPELINE_SOFT_QUALITY_PROPERTY.GetValue(asset);
                return true;
            }

            if (CachedShadowPass.S_PIPELINE_SOFT_QUALITY_FIELD == null) return false;

            quality = (SoftShadowQuality)CachedShadowPass.S_PIPELINE_SOFT_QUALITY_FIELD.GetValue(asset);
            return true;
        }

        private static bool IsXRMobile() {
            if (CachedShadowPass.S_IS_XR_MOBILE >= 0) return CachedShadowPass.S_IS_XR_MOBILE == 1;
            CachedShadowPass.S_IS_XR_MOBILE = 0;

            Type type = typeof(UniversalRenderPipelineAsset).Assembly.GetType("UnityEngine.Rendering.Universal.PlatformAutoDetect");
            const BindingFlags FLAGS = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            PropertyInfo property = type?.GetProperty("isXRMobile", FLAGS);

            object value = property != null ? property.GetValue(null) : type?.GetField("isXRMobile", FLAGS)?.GetValue(null);
            if (value is bool and true) CachedShadowPass.S_IS_XR_MOBILE = 1;

            return CachedShadowPass.S_IS_XR_MOBILE == 1;
        }

        private static void SetShadowKeywordState(UniversalShadowData shadowData, bool softShadows) {
            if (CachedShadowPass.S_ADDITIONAL_KEYWORD_FIELD == null || CachedShadowPass.S_SOFT_KEYWORD_FIELD == null)
            {
                const BindingFlags FLAGS = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                CachedShadowPass.S_ADDITIONAL_KEYWORD_FIELD = typeof(UniversalShadowData).GetField("isKeywordAdditionalLightShadowsEnabled", FLAGS);
                CachedShadowPass.S_SOFT_KEYWORD_FIELD = typeof(UniversalShadowData).GetField("isKeywordSoftShadowsEnabled", FLAGS);
            }

            if (CachedShadowPass.S_ADDITIONAL_KEYWORD_FIELD == null || CachedShadowPass.S_SOFT_KEYWORD_FIELD == null) throw new UnityException("Unable to update URP shadow keyword state");

            CachedShadowPass.S_ADDITIONAL_KEYWORD_FIELD.SetValue(shadowData, true);
            CachedShadowPass.S_SOFT_KEYWORD_FIELD.SetValue(shadowData, softShadows);
        }

        private static float PointLightFovBias(int shadowSliceResolution, bool shadowFiltering) {
            float fovBias = shadowSliceResolution switch {
                <= 16   => 43.0f,
                <= 32   => 18.55f,
                <= 64   => 8.63f,
                <= 128  => 4.13f,
                <= 256  => 2.03f,
                <= 512  => 1.00f,
                <= 1024 => 0.50f,
                <= 2048 => 0.25f,
                var _   => 4.00f
            };

            if (!shadowFiltering) return fovBias;

            switch (shadowSliceResolution)
            {
                case <= 32:
                    fovBias += 9.35f;
                    break;
                case <= 64:
                    fovBias += 4.07f;
                    break;
                case <= 128:
                    fovBias += 1.77f;
                    break;
                case <= 256:
                    fovBias += 0.85f;
                    break;
                case <= 512:
                    fovBias += 0.39f;
                    break;
                case <= 1024:
                    fovBias += 0.17f;
                    break;
                case <= 2048:
                    fovBias += 0.074f;
                    break;
            }

            return fovBias;
        }

        #endregion

        #region PRIVATE

        private readonly CachedShadowFeature _feature;

        private Material _blitMaterial;

        private RTHandle _staticAtlas;
        private RTHandle _mainAtlas;
        private int _allocatedW;
        private int _allocatedH;
        private int _allocatedDepthBits;

        private int _atlasW;
        private int _atlasH;
        private int _maxSlices;
        private int _cellRes;
        private int _gridX;
        private int _gridY;
        private bool[] _cellUsed;

        private readonly Dictionary<EntityId, LightSlot> _slots = new Dictionary<EntityId, LightSlot>();
        private readonly Dictionary<int, Stack<int>> _freeEntryRuns = new Dictionary<int, Stack<int>>();
        private readonly HashSet<Light> _modifiedCullLights = new HashSet<Light>();
        private int _nextFreeEntry;

        private int[] _entryBlockX;
        private int[] _entryBlockY;
        private int[] _entryBlockRes;
        private int[] _entryVisibleIndex;
        private Matrix4x4[] _worldToShadow;
        private Matrix4x4[] _entryView;
        private Matrix4x4[] _entryProj;
        private Vector4[] _entryBias;
        private Vector3[] _entryLightPos;
        private Vector3[] _entryLightDir;
        private Vector4[] _shadowParams;

        private readonly List<VisibleLightInfo> _visible = new List<VisibleLightInfo>();
        private readonly HashSet<EntityId> _visibleIds = new HashSet<EntityId>();
        private int _lastKeepFrameId = -1;

        private readonly List<int> _staticSliceEntries = new List<int>();
        private readonly List<RendererListHandle> _staticLists = new List<RendererListHandle>();
        private readonly List<int> _copySliceEntries = new List<int>();
        private readonly List<int> _dynamicSliceEntries = new List<int>();
        private readonly List<RendererListHandle> _dynamicLists = new List<RendererListHandle>();
        private readonly PreparedSlice[] _preparedSlices = new PreparedSlice[6];

        private long _cacheUpdateGeneration = -1;
        private bool _softThisFrame;
        private bool _softSupported;

        #endregion

        internal CachedShadowPass(CachedShadowFeature feature) {
            this._feature = feature;

            this.renderPassEvent = RenderPassEvent.AfterRenderingShadows;
            this.profilingSampler = new ProfilingSampler("CachedShadows_Main");

            Shader shader = Shader.Find(CachedShadowPass.BLIT_SHADER_NAME);
            if (!shader)
            {
                Debug.LogError("[CachedShadows] Missing Resources shader 'Hidden/FailCake/CachedShadowSliceBlit'.");
                return;
            }

            this._blitMaterial = CoreUtils.CreateEngineMaterial(shader);
        }

        internal bool Prepare(ShadowData shadowData) {
            if (!this._blitMaterial) return false;

            this.SyncAtlas(shadowData);
            return this._staticAtlas != null && this._mainAtlas != null;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            UniversalShadowData shadowData = frameData.Get<UniversalShadowData>();
            UniversalResourceData resources = frameData.Get<UniversalResourceData>();

            this.BuildVisibleList(lightData, shadowData, cameraData);
            this.AllocateSlices(renderingData, lightData, shadowData);

            TextureHandle staticHandle = renderGraph.ImportTexture(this._staticAtlas);
            TextureHandle mainHandle = renderGraph.ImportTexture(this._mainAtlas);

            resources.additionalShadowsTexture = mainHandle;

            this.CreateRendererLists(renderGraph, renderingData);

            float maxShadowDistanceSq = cameraData.maxShadowDistance * cameraData.maxShadowDistance;
            float cascadeBorder = shadowData.mainLightShadowCascadeBorder;

            if (this._staticSliceEntries.Count > 0)
            {
                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("CachedShadows_StaticBake", out StaticPassData passData, this.profilingSampler);

                passData.pass = this;
                passData.cameraData = cameraData;
                passData.sliceEntries = this._staticSliceEntries;
                passData.lists = this._staticLists;

                for (int i = 0; i < this._staticLists.Count; i++) builder.UseRendererList(this._staticLists[i]);

                builder.SetRenderAttachmentDepth(staticHandle);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc<StaticPassData>(CachedShadowPass.ExecuteStaticBake);
            }

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("CachedShadows_Main", out MainPassData passData, this.profilingSampler))
            {
                passData.pass = this;
                passData.cameraData = cameraData;
                passData.shadowData = shadowData;
                passData.staticTexture = staticHandle;
                passData.copyEntries = this._copySliceEntries;
                passData.dynamicEntries = this._dynamicSliceEntries;
                passData.dynamicLists = this._dynamicLists;
                passData.maxShadowDistanceSq = maxShadowDistanceSq;
                passData.cascadeBorder = cascadeBorder;
                passData.softShadows = this._softThisFrame;

                bool hasDraws = this._dynamicLists.Count > 0 || this._copySliceEntries.Count > 0;
                if (this._copySliceEntries.Count > 0) builder.UseTexture(staticHandle);

                if (hasDraws)
                {
                    builder.SetRenderAttachmentDepth(mainHandle);
                    for (int i = 0; i < this._dynamicLists.Count; i++) builder.UseRendererList(this._dynamicLists[i]);
                }
                else
                    builder.UseTexture(mainHandle);

                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetGlobalTextureAfterPass(mainHandle, CachedShadowPass.ID_ADDITIONAL_SHADOWMAP_TEXTURE);
                builder.SetRenderFunc<MainPassData>(CachedShadowPass.ExecuteMain);
            }
        }

        internal void Dispose() {
            foreach (Light light in this._modifiedCullLights)
                if (light)
                    light.useViewFrustumForShadowCasterCull = true;

            this._modifiedCullLights.Clear();
            this._staticAtlas?.Release();
            this._mainAtlas?.Release();
            this._staticAtlas = null;
            this._mainAtlas = null;
            this._allocatedW = 0;
            this._allocatedH = 0;
            this._allocatedDepthBits = 0;

            if (this._blitMaterial) CoreUtils.Destroy(this._blitMaterial);
            this._blitMaterial = null;

            this.ResetCache();
        }

        #region PRIVATE

        private void SyncAtlas(ShadowData shadowData) {
            int requestedW = Mathf.Max(1, shadowData.additionalLightsShadowmapWidth);
            int requestedH = Mathf.Max(1, shadowData.additionalLightsShadowmapHeight);
            int maxAdd = Mathf.Max(1, UniversalRenderPipeline.maxVisibleAdditionalLights);
            int cell = Mathf.NextPowerOfTwo(Mathf.Clamp(this._feature.cellResolution, 128, 2048));
            int depthBits = shadowData.shadowmapDepthBufferBits > 0 ? shadowData.shadowmapDepthBufferBits : 16;

            int minAtlas = Mathf.Min(requestedW, requestedH);
            while (cell > minAtlas) cell = Mathf.Max(1, cell >> 1);

            if (this._entryBlockX == null || this._maxSlices != maxAdd || this._cellRes != cell || this._allocatedW != requestedW || this._allocatedH != requestedH || this._allocatedDepthBits != depthBits)
            {
                ShadowUtils.ShadowRTReAllocateIfNeeded(ref this._staticAtlas, requestedW, requestedH, depthBits, name: CachedShadowPass.STATIC_ATLAS_NAME);
                ShadowUtils.ShadowRTReAllocateIfNeeded(ref this._mainAtlas, requestedW, requestedH, depthBits, name: CachedShadowPass.MAIN_ATLAS_NAME);

                this._allocatedW = this._mainAtlas != null && this._mainAtlas.rt != null ? this._mainAtlas.rt.width : requestedW;
                this._allocatedH = this._mainAtlas != null && this._mainAtlas.rt != null ? this._mainAtlas.rt.height : requestedH;
                this._allocatedDepthBits = depthBits;
                this._atlasW = Mathf.Min(this._allocatedW, requestedW);
                this._atlasH = Mathf.Min(this._allocatedH, requestedH);
                this._maxSlices = maxAdd;
                this._cellRes = cell;
                this._gridX = Mathf.Max(1, this._atlasW / cell);
                this._gridY = Mathf.Max(1, this._atlasH / cell);

                this._entryBlockX = new int[this._maxSlices];
                this._entryBlockY = new int[this._maxSlices];
                this._entryBlockRes = new int[this._maxSlices];
                this._entryVisibleIndex = new int[this._maxSlices];
                this._worldToShadow = new Matrix4x4[this._maxSlices];
                this._entryView = new Matrix4x4[this._maxSlices];
                this._entryProj = new Matrix4x4[this._maxSlices];
                this._entryBias = new Vector4[this._maxSlices];
                this._entryLightPos = new Vector3[this._maxSlices];
                this._entryLightDir = new Vector3[this._maxSlices];
                this._shadowParams = new Vector4[this._maxSlices];
                this._cellUsed = new bool[this._gridX * this._gridY];

                this.ResetCache();
            }
        }

        private void ResetCache() {
            this._slots.Clear();
            this._freeEntryRuns.Clear();
            this._nextFreeEntry = 0;

            if (this._cellUsed != null) Array.Clear(this._cellUsed, 0, this._cellUsed.Length);
        }

        private void BuildVisibleList(UniversalLightData lightData, UniversalShadowData shadowData, UniversalCameraData cameraData) {
            this._visible.Clear();
            this._softSupported = shadowData.supportsSoftShadows;

            for (int i = 0; i < this._shadowParams.Length; i++) this._shadowParams[i] = CachedShadowPass.DEFAULT_SHADOW_PARAMS;

            NativeArray<VisibleLight> lights = lightData.visibleLights;
            int mainIndex = lightData.mainLightIndex;
            bool supportsSoft = shadowData.supportsSoftShadows;
            this._softThisFrame = supportsSoft && mainIndex >= 0 && mainIndex < lights.Length && lights[mainIndex].light && lights[mainIndex].light.shadows == LightShadows.Soft;

            int frameId = CachedShadowFeature.FRAME_ID;
            if (this._lastKeepFrameId != frameId)
            {
                this._lastKeepFrameId = frameId;
                this._visibleIds.Clear();
            }

            Vector3 camPos = cameraData.worldSpaceCameraPos;
            float maxShadowDistance = cameraData.maxShadowDistance;

            int paramIndex = 0;
            for (int i = 0; i < lights.Length; i++)
            {
                if (i == mainIndex) continue;

                paramIndex++;
                if (paramIndex > this._maxSlices) break;

                VisibleLight visibleLight = lights[i];
                Light light = visibleLight.light;
                if (!light) continue;
                if (visibleLight.lightType != LightType.Spot && visibleLight.lightType != LightType.Point) continue;

                bool isPoint = visibleLight.lightType == LightType.Point;
                bool soft = supportsSoft && light.shadows == LightShadows.Soft;
                if (light.shadows != LightShadows.None) this._shadowParams[paramIndex - 1] = new Vector4(light.shadowStrength, this.SoftShadowProperty(light, soft), isPoint ? CachedShadowPass.LIGHT_TYPE_POINT : CachedShadowPass.LIGHT_TYPE_SPOT, -1f);

                if (light.shadows == LightShadows.None || light.shadowStrength <= 0f) continue;
                if (soft) this._softThisFrame = true;

                Vector3 lightPos = visibleLight.localToWorldMatrix.GetColumn(3);
                float fadeDistance = maxShadowDistance + light.range;
                if ((lightPos - camPos).sqrMagnitude > fadeDistance * fadeDistance) continue;

                EntityId lightId = light.GetEntityId();

                this._visible.Add(new VisibleLightInfo {
                    visibleIndex = i,
                    paramIndex = paramIndex - 1,
                    lightId = lightId,
                    isPoint = isPoint,
                    sliceCount = isPoint ? 6 : 1
                });

                this._visibleIds.Add(lightId);
            }
        }

        private void AllocateSlices(UniversalRenderingData renderingData, UniversalLightData lightData, UniversalShadowData shadowData) {
            this._staticSliceEntries.Clear();
            this._copySliceEntries.Clear();
            this._dynamicSliceEntries.Clear();

            long cacheUpdateGeneration = CachedShadowFeature.GetCacheUpdateGeneration();
            if (this._cacheUpdateGeneration != cacheUpdateGeneration)
            {
                this._cacheUpdateGeneration = cacheUpdateGeneration;
                foreach (KeyValuePair<EntityId, LightSlot> pair in this._slots) pair.Value.dirtyStatic = true;
            }

            int frame = Time.frameCount;

            for (int v = 0; v < this._visible.Count; v++)
            {
                VisibleLightInfo info = this._visible[v];
                VisibleLight visibleLight = lightData.visibleLights[info.visibleIndex];
                Light light = visibleLight.light;
                if (!light) continue;

                bool hasShadowCasters = renderingData.cullResults.GetShadowCasterBounds(info.visibleIndex, out Bounds _);
                bool casterCullChanged = light.useViewFrustumForShadowCasterCull;
                if (casterCullChanged)
                {
                    this._modifiedCullLights.Add(light);
                    light.useViewFrustumForShadowCasterCull = false;
                }

                int blockDim = this.BlockDimFor(shadowData, info.visibleIndex);
                bool shadowRequested = CachedShadowFeature.ConsumeShadowRequest(info.lightId);

                if (this._slots.TryGetValue(info.lightId, out LightSlot slot) && shadowRequested && (slot.sliceCount != info.sliceCount || slot.blockDim != blockDim))
                {
                    this.FreeSlot(slot);
                    slot = null;
                }

                if (slot == null)
                {
                    slot = this.AllocSlot(info.lightId, info.sliceCount, blockDim, info.isPoint, frame);
                    if (slot == null) continue;
                }

                slot.lastSeenFrame = frame;

                for (int s = 0; s < slot.sliceCount; s++) this._entryVisibleIndex[slot.firstEntry + s] = info.visibleIndex;

                if (shadowRequested) slot.dirtyStatic = true;

                if (slot.dirtyStatic && hasShadowCasters && !casterCullChanged && this.PrepareSliceRender(slot, ref visibleLight, light, ref renderingData.cullResults, shadowData, info.visibleIndex))
                {
                    slot.dirtyStatic = false;
                    slot.hasRenderedOnce = true;

                    for (int s = 0; s < slot.sliceCount; s++)
                    {
                        int entry = slot.firstEntry + s;
                        this._staticSliceEntries.Add(entry);
                    }
                }

            if (!slot.hasRenderedOnce) continue;

            this._shadowParams[info.paramIndex].z = slot.isPoint ? CachedShadowPass.LIGHT_TYPE_POINT : CachedShadowPass.LIGHT_TYPE_SPOT;
            this._shadowParams[info.paramIndex].w = slot.firstEntry;
            for (int s = 0; s < slot.sliceCount; s++)
            {
                int entry = slot.firstEntry + s;

                this._copySliceEntries.Add(entry);
                if (hasShadowCasters && light.intensity > 0f) this._dynamicSliceEntries.Add(entry);
            }
        }
        }

        private int BlockDimFor(UniversalShadowData shadowData, int visibleIndex) {
            int resolution = shadowData.resolution != null && shadowData.resolution.Count > visibleIndex ? shadowData.resolution[visibleIndex] : this._cellRes;
            if (resolution <= 0) resolution = this._cellRes;

            resolution = Mathf.NextPowerOfTwo(Mathf.Clamp(resolution, 1, Mathf.Min(this._atlasW, this._atlasH)));
            return Mathf.Clamp(resolution / this._cellRes, 1, Mathf.Max(1, Mathf.Min(this._gridX, this._gridY)));
        }

        private bool PrepareSliceRender(LightSlot slot, ref VisibleLight visibleLight, Light light, ref CullingResults cullResults, UniversalShadowData shadowData, int visibleIndex) {
            bool soft = light.shadows == LightShadows.Soft;
            int tileRes = slot.blockDim * this._cellRes;

            float invW = 1f / this._atlasW;
            float invH = 1f / this._atlasH;
            float fovBias = slot.isPoint ? CachedShadowPass.PointLightFovBias(tileRes, soft) : 0f;

            for (int s = 0; s < slot.sliceCount; s++)
            {
                int entry = slot.firstEntry + s;

                bool ok = slot.isPoint
                    ? ShadowUtils.ExtractPointLightMatrix(ref cullResults, shadowData, visibleIndex, (CubemapFace)s, fovBias, out Matrix4x4 shadowMatrix, out Matrix4x4 view, out Matrix4x4 proj, out ShadowSplitData _)
                    : ShadowUtils.ExtractSpotLightMatrix(ref cullResults, shadowData, visibleIndex, out shadowMatrix, out view, out proj, out ShadowSplitData _);

                if (!ok) return false;

                Matrix4x4 sliceTransform = Matrix4x4.identity;
                sliceTransform.m00 = tileRes * invW;
                sliceTransform.m11 = tileRes * invH;
                sliceTransform.m03 = this._entryBlockX[entry] * this._cellRes * invW;
                sliceTransform.m13 = this._entryBlockY[entry] * this._cellRes * invH;

                this._preparedSlices[s] = new PreparedSlice {
                    view = view,
                    proj = proj,
                    worldToShadow = sliceTransform * shadowMatrix,
                    bias = ShadowUtils.GetShadowBias(ref visibleLight, visibleIndex, shadowData, proj, tileRes)
                };
            }

            Vector3 lightPos = visibleLight.localToWorldMatrix.GetColumn(3);
            Vector3 lightDir = -visibleLight.localToWorldMatrix.GetColumn(2);

            for (int s = 0; s < slot.sliceCount; s++)
            {
                int entry = slot.firstEntry + s;
                PreparedSlice preparedSlice = this._preparedSlices[s];

                this._entryView[entry] = preparedSlice.view;
                this._entryProj[entry] = preparedSlice.proj;
                this._entryBlockRes[entry] = tileRes;
                this._worldToShadow[entry] = preparedSlice.worldToShadow;
                this._entryBias[entry] = preparedSlice.bias;
                this._entryLightPos[entry] = lightPos;
                this._entryLightDir[entry] = lightDir;
            }

            return true;
        }

        private LightSlot AllocSlot(EntityId lightId, int sliceCount, int blockDim, bool isPoint, int frame) {
            while (true)
            {
                int firstEntry = this.AllocEntryRun(sliceCount);
                if (firstEntry >= 0)
                {
                    bool blocksOk = true;

                    for (int s = 0; s < sliceCount; s++)
                    {
                        if (!this.AllocBlock(blockDim, out int blockX, out int blockY))
                        {
                            for (int r = 0; r < s; r++) this.ReleaseBlock(this._entryBlockX[firstEntry + r], this._entryBlockY[firstEntry + r], blockDim);
                            this.FreeEntryRun(firstEntry, sliceCount);
                            blocksOk = false;
                            break;
                        }

                        this._entryBlockX[firstEntry + s] = blockX;
                        this._entryBlockY[firstEntry + s] = blockY;
                    }

                    if (blocksOk)
                    {
                        LightSlot slot = new LightSlot {
                            lightId = lightId,
                            firstEntry = firstEntry,
                            sliceCount = sliceCount,
                            blockDim = blockDim,
                            isPoint = isPoint,
                            dirtyStatic = true,
                            hasRenderedOnce = false,
                            lastSeenFrame = frame
                        };

                        for (int s = 0; s < sliceCount; s++)
                        {
                            int entry = firstEntry + s;
                            this._entryBlockRes[entry] = blockDim * this._cellRes;
                            this._worldToShadow[entry] = Matrix4x4.zero;
                        }

                        this._slots.Add(lightId, slot);
                        return slot;
                    }
                }

                if (!this.EvictOne(lightId)) return null;
            }
        }

        private void FreeSlot(LightSlot slot) {
            for (int s = 0; s < slot.sliceCount; s++)
            {
                int entry = slot.firstEntry + s;
                this.ReleaseBlock(this._entryBlockX[entry], this._entryBlockY[entry], slot.blockDim);
                this._entryBlockRes[entry] = 0;
            }

            this.FreeEntryRun(slot.firstEntry, slot.sliceCount);
            this._slots.Remove(slot.lightId);
        }

        private int AllocEntryRun(int sliceCount) {
            if (this._freeEntryRuns.TryGetValue(sliceCount, out Stack<int> pool) && pool.Count > 0) return pool.Pop();
            if (this._nextFreeEntry + sliceCount > this._maxSlices) return -1;

            int firstEntry = this._nextFreeEntry;
            this._nextFreeEntry += sliceCount;

            return firstEntry;
        }

        private void FreeEntryRun(int firstEntry, int sliceCount) {
            if (!this._freeEntryRuns.TryGetValue(sliceCount, out Stack<int> pool))
            {
                pool = new Stack<int>();
                this._freeEntryRuns.Add(sliceCount, pool);
            }

            pool.Push(firstEntry);
        }

        private bool AllocBlock(int blockDim, out int blockX, out int blockY) {
            int maxX = this._gridX - blockDim;
            int maxY = this._gridY - blockDim;

            for (int y = 0; y <= maxY; y++)
            {
                for (int x = 0; x <= maxX; x++)
                {
                    if (!this.IsSquareFree(x, y, blockDim)) continue;

                    this.OccupySquare(x, y, blockDim);
                    blockX = x;
                    blockY = y;
                    return true;
                }
            }

            blockX = 0;
            blockY = 0;
            return false;
        }

        private void ReleaseBlock(int blockX, int blockY, int blockDim) {
            for (int y = 0; y < blockDim; y++)
                for (int x = 0; x < blockDim; x++)
                    this._cellUsed[(blockY + y) * this._gridX + blockX + x] = false;
        }

        private bool IsSquareFree(int blockX, int blockY, int blockDim) {
            for (int y = 0; y < blockDim; y++)
                for (int x = 0; x < blockDim; x++)
                    if (this._cellUsed[(blockY + y) * this._gridX + blockX + x])
                        return false;

            return true;
        }

        private void OccupySquare(int blockX, int blockY, int blockDim) {
            for (int y = 0; y < blockDim; y++)
                for (int x = 0; x < blockDim; x++)
                    this._cellUsed[(blockY + y) * this._gridX + blockX + x] = true;
        }

        private bool EvictOne(EntityId excludeId) {
            LightSlot victim = null;
            LightSlot oldestRendered = null;

            foreach (KeyValuePair<EntityId, LightSlot> pair in this._slots)
            {
                LightSlot candidate = pair.Value;
                if (candidate.lightId == excludeId) continue;
                if (this._visibleIds.Contains(candidate.lightId)) continue;

                if (!candidate.hasRenderedOnce)
                {
                    if (victim == null || candidate.lastSeenFrame < victim.lastSeenFrame) victim = candidate;
                }
                else
                {
                    if (oldestRendered == null || candidate.lastSeenFrame < oldestRendered.lastSeenFrame) oldestRendered = candidate;
                }
            }

            if (victim == null) victim = oldestRendered;
            if (victim == null) return false;

            this.FreeSlot(victim);
            return true;
        }

        private void CreateRendererLists(RenderGraph renderGraph, UniversalRenderingData renderingData) {
            bool useLayers = UniversalRenderPipeline.asset != null && UniversalRenderPipeline.asset.useRenderingLayers;

            this.FillLists(renderGraph, renderingData, this._staticSliceEntries, this._staticLists, ShadowObjectsFilter.StaticOnly, useLayers);
            this.FillLists(renderGraph, renderingData, this._dynamicSliceEntries, this._dynamicLists, ShadowObjectsFilter.DynamicOnly, useLayers);
        }

        private void FillLists(RenderGraph renderGraph, UniversalRenderingData renderingData, List<int> entries, List<RendererListHandle> lists, ShadowObjectsFilter objectsFilter, bool useLayers) {
            lists.Clear();

            for (int i = 0; i < entries.Count; i++)
            {
                int entry = entries[i];
                ShadowDrawingSettings settings = new ShadowDrawingSettings(renderingData.cullResults, this._entryVisibleIndex[entry]) {
                    useRenderingLayerMaskTest = useLayers,
                    objectsFilter = objectsFilter
                };

                lists.Add(renderGraph.CreateShadowRendererList(ref settings));
            }
        }

        private float SoftShadowProperty(Light light, bool soft) {
            if (!soft || !this._softSupported) return 0f;

            SoftShadowQuality quality = SoftShadowQuality.UsePipelineSettings;
            if (light.TryGetComponent(out UniversalAdditionalLightData additionalLightData)) quality = additionalLightData.softShadowQuality;
            if (quality == SoftShadowQuality.UsePipelineSettings) quality = CachedShadowPass.TryGetPipelineSoftShadowQuality(out SoftShadowQuality resolved) ? resolved : SoftShadowQuality.Medium;

            return Mathf.Max((int)quality, (int)SoftShadowQuality.Low);
        }

        private void SetupSliceGlobals(RasterCommandBuffer cmd, int entry) {
            cmd.SetGlobalVector(CachedShadowPass.ID_SHADOW_BIAS, this._entryBias[entry]);

            Vector3 lightDir = this._entryLightDir[entry];
            cmd.SetGlobalVector(CachedShadowPass.ID_LIGHT_DIRECTION, new Vector4(lightDir.x, lightDir.y, lightDir.z, 0f));

            Vector3 lightPos = this._entryLightPos[entry];
            cmd.SetGlobalVector(CachedShadowPass.ID_LIGHT_POSITION, new Vector4(lightPos.x, lightPos.y, lightPos.z, 1f));
        }

        private void RenderSlice(RasterCommandBuffer cmd, int entry, RendererList rendererList) {
            cmd.SetGlobalDepthBias(1.0f, 2.5f);
            cmd.SetViewport(new Rect(this._entryBlockX[entry] * this._cellRes, this._entryBlockY[entry] * this._cellRes, this._entryBlockRes[entry], this._entryBlockRes[entry]));
            cmd.SetViewProjectionMatrices(this._entryView[entry], this._entryProj[entry]);
            if (rendererList.isValid) cmd.DrawRendererList(rendererList);
            cmd.DisableScissorRect();
            cmd.SetGlobalDepthBias(0f, 0f);
        }

        private void ClearTile(RasterCommandBuffer cmd, int entry) {
            cmd.SetViewport(new Rect(this._entryBlockX[entry] * this._cellRes, this._entryBlockY[entry] * this._cellRes, this._entryBlockRes[entry], this._entryBlockRes[entry]));
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.DrawProcedural(Matrix4x4.identity, this._blitMaterial, CachedShadowPass.PASS_CLEAR, MeshTopology.Triangles, 3);
        }

        private void CopyTile(RasterCommandBuffer cmd, TextureHandle source, int entry) {
            cmd.SetViewport(new Rect(this._entryBlockX[entry] * this._cellRes, this._entryBlockY[entry] * this._cellRes, this._entryBlockRes[entry], this._entryBlockRes[entry]));
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.SetGlobalTexture(CachedShadowPass.ID_COPY_SOURCE, source);
            cmd.DrawProcedural(Matrix4x4.identity, this._blitMaterial, CachedShadowPass.PASS_COPY, MeshTopology.Triangles, 3);
        }

        private void SetupReceiverConstants(RasterCommandBuffer cmd, float maxShadowDistanceSq, float cascadeBorder, bool softShadows) {
            cmd.SetGlobalVectorArray(CachedShadowPass.ID_ADDITIONAL_SHADOW_PARAMS, this._shadowParams);
            cmd.SetGlobalMatrixArray(CachedShadowPass.ID_ADDITIONAL_WORLD_TO_SHADOW, this._worldToShadow);

            CachedShadowPass.GetLinearDistanceFadeParams(maxShadowDistanceSq, cascadeBorder, out float fadeScale, out float fadeBias);
            cmd.SetGlobalVector(CachedShadowPass.ID_ADDITIONAL_SHADOW_FADE_PARAMS, new Vector4(fadeScale, fadeBias, 0f, 0f));

            if (softShadows)
            {
                float invW = 1f / Mathf.Max(1, this._atlasW);
                float invH = 1f / Mathf.Max(1, this._atlasH);
                float invHalfW = 0.5f * invW;
                float invHalfH = 0.5f * invH;

                cmd.SetGlobalVector(CachedShadowPass.ID_ADDITIONAL_SHADOW_OFFSET0, new Vector4(-invHalfW, -invHalfH, invHalfW, -invHalfH));
                cmd.SetGlobalVector(CachedShadowPass.ID_ADDITIONAL_SHADOW_OFFSET1, new Vector4(-invHalfW, invHalfH, invHalfW, invHalfH));
                cmd.SetGlobalVector(CachedShadowPass.ID_ADDITIONAL_SHADOWMAP_SIZE, new Vector4(invW, invH, this._atlasW, this._atlasH));
            }
        }

        private static void ExecuteStaticBake(StaticPassData data, RasterGraphContext ctx) {
            RasterCommandBuffer cmd = ctx.cmd;
            CachedShadowPass pass = data.pass;

            cmd.SetKeyword(CachedShadowPass.K_CASTING_PUNCTUAL_LIGHT_SHADOW, true);

            for (int i = 0; i < data.sliceEntries.Count; i++)
            {
                int entry = data.sliceEntries[i];

                pass.SetupSliceGlobals(cmd, entry);
                pass.ClearTile(cmd, entry);

                RendererList rendererList = data.lists[i];
                pass.RenderSlice(cmd, entry, rendererList);
            }

            CachedShadowPass.RestoreCameraViewProjection(cmd, data.cameraData);
        }

        private static void ExecuteMain(MainPassData data, RasterGraphContext ctx) {
            RasterCommandBuffer cmd = ctx.cmd;
            CachedShadowPass pass = data.pass;

            if (data.copyEntries.Count > 0 || data.dynamicEntries.Count > 0)
            {
                cmd.SetKeyword(CachedShadowPass.K_CASTING_PUNCTUAL_LIGHT_SHADOW, true);

                for (int i = 0; i < data.copyEntries.Count; i++) pass.CopyTile(cmd, data.staticTexture, data.copyEntries[i]);

                for (int i = 0; i < data.dynamicEntries.Count; i++)
                {
                    int entry = data.dynamicEntries[i];

                    pass.SetupSliceGlobals(cmd, entry);

                    RendererList rendererList = data.dynamicLists[i];
                    pass.RenderSlice(cmd, entry, rendererList);
                }
            }

            CachedShadowPass.SetShadowKeywordState(data.shadowData, data.softShadows);
            cmd.SetKeyword(CachedShadowPass.K_ADDITIONAL_LIGHT_SHADOWS, true);
            CachedShadowPass.SetSoftShadowKeywords(cmd, data.softShadows);
            pass.SetupReceiverConstants(cmd, data.maxShadowDistanceSq, data.cascadeBorder, data.softShadows);
            CachedShadowPass.RestoreCameraViewProjection(cmd, data.cameraData);
        }

        #endregion

        private struct VisibleLightInfo
        {
            public int visibleIndex;
            public int paramIndex;
            public EntityId lightId;
            public bool isPoint;
            public int sliceCount;
        }

        private struct PreparedSlice
        {
            public Matrix4x4 view;
            public Matrix4x4 proj;
            public Matrix4x4 worldToShadow;
            public Vector4 bias;
        }

        private class LightSlot
        {
            public EntityId lightId;
            public int firstEntry;
            public int sliceCount;
            public int blockDim;
            public bool isPoint;
            public bool dirtyStatic;
            public bool hasRenderedOnce;
            public int lastSeenFrame;
        }

        private class StaticPassData
        {
            public CachedShadowPass pass;
            public UniversalCameraData cameraData;
            public List<int> sliceEntries;
            public List<RendererListHandle> lists;
        }

        private class MainPassData
        {
            public CachedShadowPass pass;
            public UniversalCameraData cameraData;
            public UniversalShadowData shadowData;
            public TextureHandle staticTexture;
            public List<int> copyEntries;
            public List<int> dynamicEntries;
            public List<RendererListHandle> dynamicLists;
            public float maxShadowDistanceSq;
            public float cascadeBorder;
            public bool softShadows;
        }
    }
}