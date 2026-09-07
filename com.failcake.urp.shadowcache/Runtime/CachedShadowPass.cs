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

namespace HyenaQuest
{
    internal sealed class CachedShadowSlot
    {
        public EntityId lightId;

        public int firstEntry;
        public int sliceCount;
        public int blockDim;

        public bool isPoint;
        public bool dirtyStatic;
        public bool hasRenderedOnce;

        public int lastSeenFrame;

        public ulong contentHash;
        public long refreshGeneration = -1;

        public int lastUpdateFrame = -1;
        public int dynamicFaceMask;
        public bool compositeDirty = true;
    }

    internal sealed class CachedShadowPassData
    {
        public CachedShadowPass pass;
        public UniversalCameraData cameraData;

        public TextureHandle staticTexture;

        public List<int> copyEntries;
        public List<int> sliceEntries;

        public List<RendererListHandle> lists;

        public float maxShadowDistanceSq;
        public float cascadeBorder;

        public bool softShadows;
    }

    internal sealed class CachedShadowPass : ScriptableRenderPass
    {
        #region STATIC

        private const int PASS_COPY = 0;
        private const int PASS_CLEAR = 1;

        private const int MAX_COPY_TILES = 256;

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
        private static readonly int ID_COPY_TILES = Shader.PropertyToID("_FailCakeCachedShadowTiles");
        private static readonly int ID_COPY_ATLAS_SIZE = Shader.PropertyToID("_FailCakeCachedShadowAtlasSize");

        private static readonly FieldInfo ATLAS_LAYOUT = typeof(UniversalShadowData).GetField("shadowAtlasLayout", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ATLAS_LIGHT_INDICES = CachedShadowPass.ATLAS_LAYOUT?.FieldType.GetField("m_VisibleLightIndexToSortedShadowResolutionRequestsFirstSliceIndex", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly GlobalKeyword K_ADDITIONAL_LIGHT_SHADOWS = GlobalKeyword.Create(ShaderKeywordStrings.AdditionalLightShadows);
        private static readonly GlobalKeyword K_CASTING_PUNCTUAL_LIGHT_SHADOW = GlobalKeyword.Create(ShaderKeywordStrings.CastingPunctualLightShadow);
        private static readonly GlobalKeyword K_SOFT_SHADOWS = GlobalKeyword.Create(ShaderKeywordStrings.SoftShadows);
        private static readonly GlobalKeyword K_SOFT_SHADOWS_LOW = GlobalKeyword.Create(ShaderKeywordStrings.SoftShadowsLow);
        private static readonly GlobalKeyword K_SOFT_SHADOWS_MEDIUM = GlobalKeyword.Create(ShaderKeywordStrings.SoftShadowsMedium);
        private static readonly GlobalKeyword K_SOFT_SHADOWS_HIGH = GlobalKeyword.Create(ShaderKeywordStrings.SoftShadowsHigh);

        private static Func<UniversalRenderPipelineAsset, SoftShadowQuality> PIPELINE_SOFT_QUALITY;
        private static FieldInfo S_ADDITIONAL_KEYWORD_FIELD;
        private static FieldInfo S_SOFT_KEYWORD_FIELD;

        private static int S_IS_XR_MOBILE = -1;

        private static ulong HashValue(ulong hash, int value) {
            return unchecked((hash ^ (uint)value) * 1099511628211UL);
        }

        private static ulong GetLightHash(ref VisibleLight visibleLight, Light light, UniversalShadowData shadowData, int visibleIndex, float softQuality) {
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < 16; i++) hash = CachedShadowPass.HashValue(hash, visibleLight.localToWorldMatrix[i].GetHashCode());

            hash = CachedShadowPass.HashValue(hash, visibleLight.range.GetHashCode());
            hash = CachedShadowPass.HashValue(hash, visibleLight.spotAngle.GetHashCode());
            hash = CachedShadowPass.HashValue(hash, light.shadowNearPlane.GetHashCode());
            hash = CachedShadowPass.HashValue(hash, (int)light.shadows);
            hash = CachedShadowPass.HashValue(hash, light.cullingMask);
            hash = CachedShadowPass.HashValue(hash, light.renderingLayerMask);
            hash = CachedShadowPass.HashValue(hash, softQuality.GetHashCode());
            hash = CachedShadowPass.HashValue(hash, shadowData.supportsSoftShadows ? 1 : 0);
            hash = CachedShadowPass.HashValue(hash, UniversalRenderPipeline.asset.useRenderingLayers ? 1 : 0);

            if (shadowData.bias == null || visibleIndex >= shadowData.bias.Count) return hash;

            hash = CachedShadowPass.HashValue(hash, shadowData.bias[visibleIndex].x.GetHashCode());
            hash = CachedShadowPass.HashValue(hash, shadowData.bias[visibleIndex].y.GetHashCode());

            return hash;
        }

        private static void GetLinearDistanceFadeParams(float fadeDistanceSq, float cascadeBorder, out float fadeScale, out float fadeBias) {
            if (cascadeBorder < 0.0001f)
            {
                fadeScale = 1000f;
                fadeBias = -fadeDistanceSq * 1000f;
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

            if (CachedShadowPass.PIPELINE_SOFT_QUALITY == null)
            {
                const BindingFlags FLAGS = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                MethodInfo getter = typeof(UniversalRenderPipelineAsset).GetProperty("softShadowQuality", FLAGS)?.GetGetMethod(true);

                if (getter == null) throw new UnityException("Unable to resolve URP soft shadow quality");
                CachedShadowPass.PIPELINE_SOFT_QUALITY = (Func<UniversalRenderPipelineAsset, SoftShadowQuality>)Delegate.CreateDelegate(typeof(Func<UniversalRenderPipelineAsset, SoftShadowQuality>), getter);
            }

            quality = CachedShadowPass.PIPELINE_SOFT_QUALITY(asset);
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

        private static void ExecuteStaticBake(CachedShadowPassData data, RasterGraphContext ctx) {
            RasterCommandBuffer cmd = ctx.cmd;
            CachedShadowPass pass = data.pass;

            cmd.SetKeyword(CachedShadowPass.K_CASTING_PUNCTUAL_LIGHT_SHADOW, true);
            cmd.DisableScissorRect();
            cmd.SetGlobalDepthBias(0f, 0f);

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

        private static void ExecuteMain(CachedShadowPassData data, RasterGraphContext ctx) {
            RasterCommandBuffer cmd = ctx.cmd;
            CachedShadowPass pass = data.pass;

            if (data.copyEntries.Count > 0 || data.sliceEntries.Count > 0)
            {
                cmd.SetKeyword(CachedShadowPass.K_CASTING_PUNCTUAL_LIGHT_SHADOW, true);
                pass.CopyTiles(cmd, data.staticTexture, data.copyEntries);

                for (int i = 0; i < data.sliceEntries.Count; i++)
                {
                    int entry = data.sliceEntries[i];
                    pass.SetupSliceGlobals(cmd, entry);

                    RendererList rendererList = data.lists[i];
                    pass.RenderSlice(cmd, entry, rendererList);
                }
            }

            cmd.SetKeyword(CachedShadowPass.K_ADDITIONAL_LIGHT_SHADOWS, true);
            CachedShadowPass.SetSoftShadowKeywords(cmd, data.softShadows);

            pass.SetupReceiverConstants(cmd, data.maxShadowDistanceSq, data.cascadeBorder, data.softShadows);
            CachedShadowPass.RestoreCameraViewProjection(cmd, data.cameraData);
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
        private CachedShadowCasters _dynamicCasters;

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

        private readonly Dictionary<EntityId, CachedShadowSlot> _slots = new Dictionary<EntityId, CachedShadowSlot>();
        private readonly HashSet<Light> _modifiedCullLights = new HashSet<Light>();
        private bool[] _entryUsed;

        private int[] _entryBlockX;
        private int[] _entryBlockY;
        private int[] _entryBlockRes;
        private int[] _entryVisibleIndex;
        private int[] _entryFace;

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
        private readonly Vector4[] _copyTiles = new Vector4[CachedShadowPass.MAX_COPY_TILES];

        private int _lastBudgetFrameId = -1;
        private int _remainingStaticSlices;

        private bool _softThisFrame;
        private bool _softSupported;

        #endregion

        internal CachedShadowPass(CachedShadowFeature feature) {
            this._feature = feature;

            this.renderPassEvent = RenderPassEvent.AfterRenderingShadows;
            this.profilingSampler = new ProfilingSampler("CachedShadows_Main");

            Shader shader = Shader.Find(CachedShadowPass.BLIT_SHADER_NAME);
            if (!shader) throw new UnityException("Missing shader Hidden/FailCake/CachedShadowSliceBlit");
            if (CachedShadowPass.ATLAS_LIGHT_INDICES == null) throw new UnityException("Unable to resolve URP shadow atlas layout");

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
            this.UpdateDynamicCasters();
            this.AllocateSlices(renderingData, lightData, shadowData);

            TextureHandle staticHandle = renderGraph.ImportTexture(this._staticAtlas);
            TextureHandle mainHandle = renderGraph.ImportTexture(this._mainAtlas);

            resources.additionalShadowsTexture = mainHandle;

            this.CreateRendererLists(renderGraph, renderingData);
            CachedShadowPass.SetShadowKeywordState(shadowData, this._softThisFrame);

            float maxShadowDistanceSq = cameraData.maxShadowDistance * cameraData.maxShadowDistance;
            float cascadeBorder = shadowData.mainLightShadowCascadeBorder;

            if (this._staticSliceEntries.Count > 0)
            {
                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("CachedShadows_StaticBake", out CachedShadowPassData passData, this.profilingSampler);

                passData.pass = this;
                passData.cameraData = cameraData;
                passData.sliceEntries = this._staticSliceEntries;
                passData.lists = this._staticLists;

                for (int i = 0; i < this._staticLists.Count; i++) builder.UseRendererList(this._staticLists[i]);

                builder.SetRenderAttachmentDepth(staticHandle);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc<CachedShadowPassData>(CachedShadowPass.ExecuteStaticBake);
            }

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("CachedShadows_Main", out CachedShadowPassData passData, this.profilingSampler))
            {
                passData.pass = this;
                passData.cameraData = cameraData;
                passData.staticTexture = staticHandle;
                passData.copyEntries = this._copySliceEntries;
                passData.sliceEntries = this._dynamicSliceEntries;
                passData.lists = this._dynamicLists;
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
                builder.SetRenderFunc<CachedShadowPassData>(CachedShadowPass.ExecuteMain);
            }
        }

        internal void Dispose() {
            this._dynamicCasters?.Dispose();
            this._dynamicCasters = null;
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
                this._entryFace = new int[this._maxSlices];
                this._entryUsed = new bool[this._maxSlices];
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
            this._visibleIds.Clear();

            this._lastKeepFrameId = -1;
            this._lastBudgetFrameId = -1;

            if (this._cellUsed != null) Array.Clear(this._cellUsed, 0, this._cellUsed.Length);
            if (this._entryUsed != null) Array.Clear(this._entryUsed, 0, this._entryUsed.Length);
        }

        private void BuildVisibleList(UniversalLightData lightData, UniversalShadowData shadowData, UniversalCameraData cameraData) {
            this._visible.Clear();
            this._softSupported = shadowData.supportsSoftShadows;

            for (int i = 0; i < this._shadowParams.Length; i++) this._shadowParams[i] = CachedShadowPass.DEFAULT_SHADOW_PARAMS; // RESET

            NativeArray<VisibleLight> lights = lightData.visibleLights;
            NativeArray<int> shadowIndices = (NativeArray<int>)CachedShadowPass.ATLAS_LIGHT_INDICES.GetValue(CachedShadowPass.ATLAS_LAYOUT.GetValue(shadowData));

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
                if (!shadowIndices.IsCreated || i >= shadowIndices.Length || shadowIndices[i] < 0) continue;

                Vector3 lightPos = visibleLight.localToWorldMatrix.GetColumn(3);
                float fadeDistance = maxShadowDistance + light.range;
                if ((lightPos - camPos).sqrMagnitude > fadeDistance * fadeDistance) continue;

                EntityId lightId = light.GetEntityId();

                this._visible.Add(new VisibleLightInfo {
                    visibleIndex = i,
                    paramIndex = paramIndex - 1,
                    lightId = lightId,
                    isPoint = isPoint,
                    sliceCount = isPoint ? 6 : 1,
                    priority = shadowIndices[i],
                    lastUpdateFrame = this._feature.maxStaticSlicesPerFrame > 0 && this._slots.TryGetValue(lightId, out CachedShadowSlot slot) ? slot.lastUpdateFrame : -1
                });

                this._visibleIds.Add(lightId);
            }

            for (int i = 1; i < this._visible.Count; i++)
            {
                VisibleLightInfo info = this._visible[i];
                int j = i - 1;
                while (j >= 0 && (this._visible[j].lastUpdateFrame > info.lastUpdateFrame || (this._visible[j].lastUpdateFrame == info.lastUpdateFrame && this._visible[j].priority > info.priority)))
                {
                    this._visible[j + 1] = this._visible[j];
                    j--;
                }

                this._visible[j + 1] = info;
            }
        }

        private void AllocateSlices(UniversalRenderingData renderingData, UniversalLightData lightData, UniversalShadowData shadowData) {
            this._staticSliceEntries.Clear();
            this._copySliceEntries.Clear();
            this._dynamicSliceEntries.Clear();

            if (this._lastBudgetFrameId != CachedShadowFeature.FRAME_ID)
            {
                this._lastBudgetFrameId = CachedShadowFeature.FRAME_ID;
                this._remainingStaticSlices = this._feature.maxStaticSlicesPerFrame > 0 ? Mathf.Max(6, this._feature.maxStaticSlicesPerFrame) : int.MaxValue;
            }

            int frame = Time.frameCount;
            bool useRenderingLayers = UniversalRenderPipeline.asset && UniversalRenderPipeline.asset.useRenderingLayers;

            for (int v = 0; v < this._visible.Count; v++)
            {
                VisibleLightInfo info = this._visible[v];
                VisibleLight visibleLight = lightData.visibleLights[info.visibleIndex];

                Light light = visibleLight.light;
                if (!light) continue;

                int blockDim = this.BlockDimFor(shadowData, info.visibleIndex);

                if (this._slots.TryGetValue(info.lightId, out CachedShadowSlot slot) && (slot.sliceCount != info.sliceCount || slot.blockDim != blockDim))
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

                bool casterCullChanged = light.useViewFrustumForShadowCasterCull;
                if (casterCullChanged)
                {
                    this._modifiedCullLights.Add(light);
                    light.useViewFrustumForShadowCasterCull = false;
                    slot.dirtyStatic = true;
                }

                for (int s = 0; s < slot.sliceCount; s++)
                {
                    this._entryVisibleIndex[slot.firstEntry + s] = info.visibleIndex;
                    this._entryFace[slot.firstEntry + s] = s;
                }

                long generation = CachedShadowFeature.GetShadowGeneration(info.lightId);
                ulong contentHash = CachedShadowPass.GetLightHash(ref visibleLight, light, shadowData, info.visibleIndex, this._shadowParams[info.paramIndex].y);
                if (slot.contentHash != contentHash || slot.refreshGeneration != generation) slot.dirtyStatic = true;
                bool hasShadowCasters = renderingData.cullResults.GetShadowCasterBounds(info.visibleIndex, out Bounds _);
                if (!hasShadowCasters)
                {
                    slot.dirtyStatic = true;
                    continue;
                }

                if (slot.dirtyStatic && !casterCullChanged && this._remainingStaticSlices >= slot.sliceCount && this.PrepareSliceRender(slot, ref visibleLight, light, ref renderingData.cullResults, shadowData, info.visibleIndex))
                {
                    slot.dirtyStatic = false;
                    slot.hasRenderedOnce = true;
                    slot.contentHash = contentHash;
                    slot.refreshGeneration = generation;
                    slot.lastUpdateFrame = frame;
                    slot.compositeDirty = true;
                    this._remainingStaticSlices -= slot.sliceCount;

                    for (int s = 0; s < slot.sliceCount; s++)
                    {
                        int entry = slot.firstEntry + s;
                        this._staticSliceEntries.Add(entry);
                    }
                }

                if (!slot.hasRenderedOnce || slot.dirtyStatic) continue;
                this._shadowParams[info.paramIndex].w = slot.firstEntry;
                int dynamicMask = 0;
                if (light.intensity > 0f)
                    dynamicMask = slot.isPoint && this._feature.cullDynamicPointFaces
                        ? this._dynamicCasters.GetPointFaceMask(this._entryLightPos[slot.firstEntry], visibleLight.range, light.cullingMask, unchecked((uint)light.renderingLayerMask), useRenderingLayers, this._entryProj[slot.firstEntry].m00,
                            this._entryBias[slot.firstEntry].x)
                        : (1 << slot.sliceCount) - 1;
                this.PrepareComposite(slot, dynamicMask);
            }
        }

        private void UpdateDynamicCasters() {
            if (!this._feature.cullDynamicPointFaces)
            {
                this._dynamicCasters?.Dispose();
                this._dynamicCasters = null;
                return;
            }

            for (int i = 0; i < this._visible.Count; i++)
            {
                if (!this._visible[i].isPoint) continue;
                this._dynamicCasters ??= new CachedShadowCasters();
                this._dynamicCasters.Update();
                return;
            }
        }

        private void PrepareComposite(CachedShadowSlot slot, int dynamicMask) {
            int restoreMask = slot.compositeDirty ? (1 << slot.sliceCount) - 1 : slot.dynamicFaceMask | dynamicMask;
            for (int face = 0; face < slot.sliceCount; face++)
            {
                int entry = slot.firstEntry + face;
                if ((restoreMask & (1 << face)) != 0) this._copySliceEntries.Add(entry);
                if ((dynamicMask & (1 << face)) != 0) this._dynamicSliceEntries.Add(entry);
            }

            slot.dynamicFaceMask = dynamicMask;
            slot.compositeDirty = false;
        }

        private int BlockDimFor(UniversalShadowData shadowData, int visibleIndex) {
            int resolution = shadowData.resolution != null && shadowData.resolution.Count > visibleIndex ? shadowData.resolution[visibleIndex] : this._cellRes;
            if (resolution <= 0) resolution = this._cellRes;

            resolution = Mathf.NextPowerOfTwo(Mathf.Clamp(resolution, 1, Mathf.Min(this._atlasW, this._atlasH)));
            return Mathf.Clamp(resolution / this._cellRes, 1, Mathf.Max(1, Mathf.Min(this._gridX, this._gridY)));
        }

        private bool PrepareSliceRender(CachedShadowSlot slot, ref VisibleLight visibleLight, Light light, ref CullingResults cullResults, UniversalShadowData shadowData, int visibleIndex) {
            bool soft = light.shadows == LightShadows.Soft;
            int tileRes = slot.blockDim * this._cellRes;

            float invW = 1f / this._atlasW;
            float invH = 1f / this._atlasH;
            float fovBias = slot.isPoint ? CachedShadowPass.PointLightFovBias(tileRes, soft) : 0f;
            Vector4 bias = Vector4.zero;

            for (int s = 0; s < slot.sliceCount; s++)
            {
                int entry = slot.firstEntry + s;

                bool ok = slot.isPoint
                    ? ShadowUtils.ExtractPointLightMatrix(ref cullResults, shadowData, visibleIndex, (CubemapFace)s, fovBias, out Matrix4x4 shadowMatrix, out Matrix4x4 view, out Matrix4x4 proj, out ShadowSplitData _)
                    : ShadowUtils.ExtractSpotLightMatrix(ref cullResults, shadowData, visibleIndex, out shadowMatrix, out view, out proj, out ShadowSplitData _);

                if (!ok) return false;
                if (s == 0) bias = ShadowUtils.GetShadowBias(ref visibleLight, visibleIndex, shadowData, proj, tileRes);

                Matrix4x4 sliceTransform = Matrix4x4.identity;
                sliceTransform.m00 = tileRes * invW;
                sliceTransform.m11 = tileRes * invH;
                sliceTransform.m03 = this._entryBlockX[entry] * this._cellRes * invW;
                sliceTransform.m13 = this._entryBlockY[entry] * this._cellRes * invH;

                this._preparedSlices[s] = new PreparedSlice {
                    view = view,
                    proj = proj,
                    worldToShadow = sliceTransform * shadowMatrix,
                    bias = bias
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

        private CachedShadowSlot AllocSlot(EntityId lightId, int sliceCount, int blockDim, bool isPoint, int frame) {
            if (sliceCount > this._maxSlices || (long)sliceCount * blockDim * blockDim > this._cellUsed.Length) return null;
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
                        CachedShadowSlot slot = new CachedShadowSlot {
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

        private void FreeSlot(CachedShadowSlot slot) {
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
            int runLength = 0;
            for (int i = 0; i < this._entryUsed.Length; i++)
            {
                runLength = this._entryUsed[i] ? 0 : runLength + 1;
                if (runLength < sliceCount) continue;
                int firstEntry = i - sliceCount + 1;
                for (int s = 0; s < sliceCount; s++) this._entryUsed[firstEntry + s] = true;
                return firstEntry;
            }

            return -1;
        }

        private void FreeEntryRun(int firstEntry, int sliceCount) {
            for (int s = 0; s < sliceCount; s++) this._entryUsed[firstEntry + s] = false;
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
            CachedShadowSlot victim = null;
            CachedShadowSlot oldestRendered = null;

            foreach (KeyValuePair<EntityId, CachedShadowSlot> pair in this._slots)
            {
                CachedShadowSlot candidate = pair.Value;
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
            bool useLayers = UniversalRenderPipeline.asset && UniversalRenderPipeline.asset.useRenderingLayers;

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
                    objectsFilter = objectsFilter,
                    splitIndex = this._entryFace[entry]
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

        private void CopyTiles(RasterCommandBuffer cmd, TextureHandle source, List<int> entries) {
            if (entries.Count == 0) return;
            cmd.DisableScissorRect();
            cmd.SetGlobalDepthBias(0f, 0f);
            cmd.SetViewport(new Rect(0f, 0f, this._atlasW, this._atlasH));
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.SetGlobalTexture(CachedShadowPass.ID_COPY_SOURCE, source);
            cmd.SetGlobalVector(CachedShadowPass.ID_COPY_ATLAS_SIZE, new Vector4(1f / this._atlasW, 1f / this._atlasH, 0f, 0f));
            for (int start = 0; start < entries.Count; start += CachedShadowPass.MAX_COPY_TILES)
            {
                int count = Mathf.Min(CachedShadowPass.MAX_COPY_TILES, entries.Count - start);
                for (int i = 0; i < count; i++)
                {
                    int entry = entries[start + i];
                    this._copyTiles[i] = new Vector4(this._entryBlockX[entry] * this._cellRes, this._entryBlockY[entry] * this._cellRes, this._entryBlockRes[entry], this._entryBlockRes[entry]);
                }

                cmd.SetGlobalVectorArray(CachedShadowPass.ID_COPY_TILES, this._copyTiles);
                cmd.DrawProcedural(Matrix4x4.identity, this._blitMaterial, CachedShadowPass.PASS_COPY, MeshTopology.Triangles, count * 6);
            }
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

        #endregion

        private struct VisibleLightInfo
        {
            public int visibleIndex;
            public int paramIndex;

            public EntityId lightId;
            public bool isPoint;

            public int sliceCount;
            public int priority;
            public int lastUpdateFrame;
        }

        private struct PreparedSlice
        {
            public Matrix4x4 view;
            public Matrix4x4 proj;
            public Matrix4x4 worldToShadow;

            public Vector4 bias;
        }
    }
}