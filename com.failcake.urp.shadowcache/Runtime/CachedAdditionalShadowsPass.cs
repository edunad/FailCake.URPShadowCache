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
    public class CachedAdditionalShadowsPass : ScriptableRenderPass
    {
        public enum SliceCategory
        {
            REALTIME = 0,
            CACHED_STATIC,
            CACHED_DYNAMIC
        }

        #region STATIC

        private const int SHADOWMAP_BITS = 16;

        private const int BLIT_PASS_COPY = 0;
        private const int BLIT_PASS_CLEAR = 1;

        private const float LIGHT_TYPE_SPOT = 0f;
        private const float LIGHT_TYPE_POINT = 1f;

        private static readonly Vector4 _defaultShadowParams = new Vector4(0f, 0f, 0f, -1f);

        private static readonly int _idAdditionalShadowParams = Shader.PropertyToID("_AdditionalShadowParams");
        private static readonly int _idAdditionalLightsWorldToShadow = Shader.PropertyToID("_AdditionalLightsWorldToShadow");
        private static readonly int _idAdditionalShadowFadeParams = Shader.PropertyToID("_AdditionalShadowFadeParams");
        private static readonly int _idAdditionalShadowOffset0 = Shader.PropertyToID("_AdditionalShadowOffset0");
        private static readonly int _idAdditionalShadowOffset1 = Shader.PropertyToID("_AdditionalShadowOffset1");
        private static readonly int _idAdditionalShadowmapSize = Shader.PropertyToID("_AdditionalShadowmapSize");
        private static readonly int _idAdditionalLightsShadowmapTexture = Shader.PropertyToID("_AdditionalLightsShadowmapTexture");

        private static GlobalKeyword _kAdditionalLightShadows;
        private static GlobalKeyword _kCastingPunctualLightShadow;
        private static bool _keywordsReady;

        #endregion

        #region PRIVATE

        private readonly CachedAdditionalShadowsFeature _feature;

        private Material _blitMaterial;

        private RTHandle _mainAtlas;
        private RTHandle _staticAtlas;
        private int _allocatedAtlasSize;

        private readonly Dictionary<int, Slot> _slots = new Dictionary<int, Slot>();
        private readonly Dictionary<int, Stack<Vector2Int>> _freeByResolution = new Dictionary<int, Stack<Vector2Int>>();

        private int _cursorX;
        private int _cursorY;
        private int _rowHeight;

        private readonly List<FrameSlice> _frameSlices = new List<FrameSlice>();

        private Vector4[] _shadowParams;
        private Matrix4x4[] _worldToShadow;

        private int _maxSlices;

        private TextureHandle _mainHandle;
        private bool _hasShadowsThisFrame;
        private Vector2Int _atlasSizeV2;
        private float _maxShadowDistanceSq;
        private float _cascadeBorder;
        private bool _softShadows;
        private SoftShadowQuality _softShadowQuality;

        #endregion

        public CachedAdditionalShadowsPass(CachedAdditionalShadowsFeature feature, RenderPassEvent evt) {
            this._feature = feature;

            this.renderPassEvent = evt;
            this.profilingSampler = new ProfilingSampler("CachedAdditionalShadows");

            int maxAdditionalLights = UniversalRenderPipeline.maxVisibleAdditionalLights;
            this._maxSlices = maxAdditionalLights;
            this._shadowParams = new Vector4[maxAdditionalLights];
            this._worldToShadow = new Matrix4x4[maxAdditionalLights];
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {
            this._hasShadowsThisFrame = false;

            CachedAdditionalShadowsPass.EnsureKeywords();

            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            UniversalShadowData shadowData = frameData.Get<UniversalShadowData>();

            if (cameraData.cameraType != CameraType.Game && cameraData.cameraType != CameraType.SceneView) return;
            if (!shadowData.supportsAdditionalLightShadows) return;

            if (shadowData.resolution == null || shadowData.bias == null) return;

            int validSlices = this.BuildFrameSlices(ref renderingData.cullResults, cameraData, lightData, shadowData);
            if (validSlices == 0) return;

            this.EnsureAtlases();
            this.FindBlitMaterial();
            if (this._mainAtlas == null || this._staticAtlas == null || !this._blitMaterial) return;

            this._mainHandle = renderGraph.ImportTexture(this._mainAtlas);
            TextureHandle staticHandle = renderGraph.ImportTexture(this._staticAtlas);

            this.CreateRendererLists(renderGraph, ref renderingData.cullResults);

            bool anyStaticBake = false;
            bool anyDynamic = false;

            for (int i = 0; i < this._frameSlices.Count; i++)
            {
                FrameSlice fs = this._frameSlices[i];

                if (fs.category == SliceCategory.CACHED_DYNAMIC)
                {
                    anyDynamic = true;
                    if (fs.staticDirty) anyStaticBake = true;
                }
            }

            if (anyStaticBake) this.RecordStaticBakePass(renderGraph, staticHandle);
            this.RecordMainPass(renderGraph, this._mainHandle, staticHandle, anyDynamic);

            this._atlasSizeV2 = new Vector2Int(this._allocatedAtlasSize, this._allocatedAtlasSize);

            this._maxShadowDistanceSq = cameraData.maxShadowDistance * cameraData.maxShadowDistance;
            this._cascadeBorder = shadowData.mainLightShadowCascadeBorder;
            this._hasShadowsThisFrame = true;

            shadowData.supportsAdditionalLightShadows = false;
        }

        public void RecordPostPass(RenderGraph renderGraph, ContextContainer frameData) {
            if (!this._hasShadowsThisFrame || !this._mainHandle.IsValid()) return;

            using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("CachedAdditionalShadows_Bind", out PostPassData passData, this.profilingSampler);

            passData.shadowParams = this._shadowParams;
            passData.worldToShadow = this._worldToShadow;
            passData.atlasSize = this._atlasSizeV2;
            passData.maxShadowDistanceSq = this._maxShadowDistanceSq;
            passData.cascadeBorder = this._cascadeBorder;
            passData.softShadows = this._softShadows;
            passData.softShadowQuality = this._softShadowQuality;

            builder.UseTexture(this._mainHandle, AccessFlags.Read);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetGlobalTextureAfterPass(this._mainHandle, CachedAdditionalShadowsPass._idAdditionalLightsShadowmapTexture);
            builder.SetRenderFunc<PostPassData>(CachedAdditionalShadowsPass.ExecuteBind);
        }

        public void Dispose() {
            this._mainAtlas?.Release();
            this._staticAtlas?.Release();
            this._mainAtlas = null;
            this._staticAtlas = null;
            this._allocatedAtlasSize = 0;

            if (this._blitMaterial) CoreUtils.Destroy(this._blitMaterial);
            this._blitMaterial = null;

            this._slots.Clear();
            this._freeByResolution.Clear();
        }

        #region SETUP

        private int BuildFrameSlices(ref CullingResults cullResults, UniversalCameraData cameraData, UniversalLightData lightData, UniversalShadowData shadowData) {
            this._frameSlices.Clear();

            int maxAdd = UniversalRenderPipeline.maxVisibleAdditionalLights;
            if (this._shadowParams.Length != maxAdd)
            {
                this._shadowParams = new Vector4[maxAdd];
                this._worldToShadow = new Matrix4x4[maxAdd];
                this._maxSlices = maxAdd;
            }

            for (int i = 0; i < this._shadowParams.Length; i++) this._shadowParams[i] = CachedAdditionalShadowsPass._defaultShadowParams;

            int frame = Time.frameCount;
            bool supportsSoftShadows = shadowData.supportsSoftShadows;
            bool anySoft = false;

            NativeArray<VisibleLight> visibleLights = lightData.visibleLights;
            int additionalLightIndex = -1;

            for (int visibleLightIndex = 0; visibleLightIndex < visibleLights.Length; visibleLightIndex++)
            {
                if (visibleLightIndex == lightData.mainLightIndex) continue;

                additionalLightIndex++;
                if (additionalLightIndex >= maxAdd) break;

                VisibleLight visibleLight = visibleLights[visibleLightIndex];
                Light light = visibleLight.light;
                if (!light) continue;

                LightType lightType = visibleLight.lightType;

                if (lightType != LightType.Spot && lightType != LightType.Point) continue;
                if (light.shadows == LightShadows.None || light.shadowStrength <= 0f) continue;

                EntityId lightId = light.GetEntityId();
                CachedShadowLight cache = this._feature.GetCache(lightId);

                SliceCategory category = !cache
                    ? SliceCategory.REALTIME
                    : cache.renderDynamicCasters
                        ? SliceCategory.CACHED_DYNAMIC
                        : SliceCategory.CACHED_STATIC;

                if (category == SliceCategory.REALTIME && !cullResults.GetShadowCasterBounds(visibleLightIndex, out Bounds _)) continue;

                CullingResults bakeCull = default(CullingResults);
                int bakeLightIndex = -1;

                bool hasBakeCull = false;
                if (category != SliceCategory.REALTIME) hasBakeCull = this._feature.TryGetBakeCull(lightId, out bakeCull, out bakeLightIndex);

                float softShadows = supportsSoftShadows && light.shadows == LightShadows.Soft ? 1f : 0f;
                SoftShadowQuality quality = SoftShadowQuality.UsePipelineSettings;

                if (softShadows > 0f && light.TryGetComponent(out UniversalAdditionalLightData ald))
                {
                    quality = ald.softShadowQuality;
                    softShadows *= 1 + (int)ald.softShadowQuality;
                }

                int perLightSlices = lightType == LightType.Point ? 6 : 1;
                int desiredResolution = this.ResolutionFor(category, cache, shadowData, visibleLightIndex);

                bool needsStaticBake = category != SliceCategory.REALTIME && hasBakeCull && this._feature.NeedsStaticBake(lightId);

                int perLightFirstSlice = -1;
                bool addedAny = false;

                for (int face = 0; face < perLightSlices; face++)
                {
                    if (this._frameSlices.Count >= this._maxSlices) break;

                    int sliceKey = lightId.GetHashCode() * 6 + face;
                    if (!this.TryGetOrAllocSlot(sliceKey, lightId, desiredResolution, frame, out Slot slot)) break;

                    bool ok;

                    Matrix4x4 shadowTransform;
                    Matrix4x4 view;
                    Matrix4x4 proj;

                    if (lightType == LightType.Spot)
                        ok = ShadowUtils.ExtractSpotLightMatrix(ref cullResults, shadowData, visibleLightIndex, out shadowTransform, out view, out proj, out ShadowSplitData _);
                    else
                    {
                        float fovBias = CachedAdditionalShadowsPass.PointLightFovBias(slot.resolution, light.shadows == LightShadows.Soft);
                        ok = ShadowUtils.ExtractPointLightMatrix(ref cullResults, shadowData, visibleLightIndex, (CubemapFace)face, fovBias, out shadowTransform, out view, out proj, out ShadowSplitData _);
                    }

                    if (!ok) continue;

                    int globalSliceIndex = this._frameSlices.Count;
                    if (perLightFirstSlice < 0) perLightFirstSlice = globalSliceIndex;

                    Matrix4x4 sliceTransform = Matrix4x4.identity;

                    float invAtlas = 1f / Mathf.NextPowerOfTwo(Mathf.Max(this._feature.atlasSize, 256));

                    sliceTransform.m00 = slot.resolution * invAtlas;
                    sliceTransform.m11 = slot.resolution * invAtlas;
                    sliceTransform.m03 = slot.offsetX * invAtlas;
                    sliceTransform.m13 = slot.offsetY * invAtlas;

                    this._worldToShadow[globalSliceIndex] = sliceTransform * shadowTransform;

                    Vector4 bias = ShadowUtils.GetShadowBias(ref visibleLight, visibleLightIndex, shadowData, proj, slot.resolution);
                    Vector3 lightPosition = visibleLight.localToWorldMatrix.GetColumn(3);

                    this._frameSlices.Add(new FrameSlice {
                        visibleLightIndex = visibleLightIndex,
                        lightId = lightId,
                        category = category,
                        viewMatrix = view,
                        projMatrix = proj,
                        offsetX = slot.offsetX,
                        offsetY = slot.offsetY,
                        resolution = slot.resolution,
                        staticDirty = needsStaticBake,
                        hasBakeCull = hasBakeCull,
                        bakeCull = bakeCull,
                        bakeLightIndex = bakeLightIndex,
                        bias = bias,
                        lightPosition = lightPosition
                    });

                    addedAny = true;
                }

                if (addedAny)
                {
                    float typeId = lightType == LightType.Point ? CachedAdditionalShadowsPass.LIGHT_TYPE_POINT : CachedAdditionalShadowsPass.LIGHT_TYPE_SPOT;
                    this._shadowParams[additionalLightIndex] = new Vector4(light.shadowStrength, softShadows, typeId, perLightFirstSlice);

                    anySoft |= softShadows > 0f;
                    if (softShadows > 0f) this._softShadowQuality = quality;
                }
            }

            this._softShadows = anySoft;
            this.FreeStaleSlots(frame);

            return this._frameSlices.Count;
        }


        private int ResolutionFor(SliceCategory category, CachedShadowLight cache, UniversalShadowData shadowData, int visibleLightIndex) {
            int atlas = Mathf.NextPowerOfTwo(Mathf.Max(this._feature.atlasSize, 256));

            int res;
            if (category == SliceCategory.REALTIME)
                res = visibleLightIndex < shadowData.resolution.Count && shadowData.resolution[visibleLightIndex] > 0 ? shadowData.resolution[visibleLightIndex] : this._feature.defaultResolution;
            else
                res = cache && cache.cachedResolution > 0 ? cache.cachedResolution : this._feature.defaultResolution;

            res = Mathf.NextPowerOfTwo(Mathf.Clamp(res, 16, atlas));
            return res;
        }

        #endregion

        #region PACKER

        private bool TryGetOrAllocSlot(int sliceKey, EntityId lightId, int resolution, int frame, out Slot slot) {
            if (this._slots.TryGetValue(sliceKey, out slot))
            {
                if (slot.resolution != resolution)
                {
                    this.FreeSlot(slot);

                    this._slots.Remove(sliceKey);
                    this._feature.InvalidateStaticBake(lightId);
                }
                else
                {
                    slot.lastSeenFrame = frame;
                    this._slots[sliceKey] = slot;
                    return true;
                }
            }

            if (!this.AllocRect(resolution, out int x, out int y))
            {
                slot = default(Slot);
                return false;
            }

            slot = new Slot { offsetX = x, offsetY = y, resolution = resolution, lightId = lightId, lastSeenFrame = frame };
            this._slots[sliceKey] = slot;
            return true;
        }

        private bool AllocRect(int resolution, out int x, out int y) {
            if (this._freeByResolution.TryGetValue(resolution, out Stack<Vector2Int> free) && free.Count > 0)
            {
                Vector2Int p = free.Pop();
                x = p.x;
                y = p.y;
                return true;
            }

            int atlas = Mathf.NextPowerOfTwo(Mathf.Max(this._feature.atlasSize, 256));
            if (resolution > atlas)
            {
                x = y = 0;
                return false;
            }

            if (this._cursorX + resolution > atlas)
            {
                this._cursorX = 0;
                this._cursorY += this._rowHeight;
                this._rowHeight = 0;
            }

            if (this._cursorY + resolution > atlas)
            {
                x = y = 0;
                return false;
            }

            x = this._cursorX;
            y = this._cursorY;

            this._cursorX += resolution;
            if (resolution > this._rowHeight) this._rowHeight = resolution;
            return true;
        }

        private void FreeSlot(Slot slot) {
            if (!this._freeByResolution.TryGetValue(slot.resolution, out Stack<Vector2Int> free))
            {
                free = new Stack<Vector2Int>();
                this._freeByResolution[slot.resolution] = free;
            }

            free.Push(new Vector2Int(slot.offsetX, slot.offsetY));
        }

        private void FreeStaleSlots(int frame) {
            List<int> toRemove = null;
            foreach (KeyValuePair<int, Slot> kv in this._slots)
            {
                if (frame - kv.Value.lastSeenFrame < 90) continue;
                (toRemove ??= new List<int>()).Add(kv.Key);
            }

            if (toRemove == null) return;
            for (int i = 0; i < toRemove.Count; i++)
            {
                Slot slot = this._slots[toRemove[i]];
                this.FreeSlot(slot);
                this._feature.InvalidateStaticBake(slot.lightId);
                this._slots.Remove(toRemove[i]);
            }
        }

        #endregion

        private void CreateRendererLists(RenderGraph renderGraph, ref CullingResults cullResults) {
            bool useLayers = UniversalRenderPipeline.asset.useRenderingLayers;

            for (int i = 0; i < this._frameSlices.Count; i++)
            {
                FrameSlice fs = this._frameSlices[i];

                switch (fs.category)
                {
                    case SliceCategory.REALTIME:
                    {
                        ShadowDrawingSettings s = this.MakeSettings(cullResults, fs.visibleLightIndex, ShadowObjectsFilter.AllObjects, useLayers);
                        fs.listAll = renderGraph.CreateShadowRendererList(ref s);
                        break;
                    }

                    case SliceCategory.CACHED_STATIC:
                    {
                        if (fs is { staticDirty: true, hasBakeCull: true })
                        {
                            ShadowDrawingSettings s = this.MakeSettings(fs.bakeCull, fs.bakeLightIndex, ShadowObjectsFilter.StaticOnly, useLayers);
                            fs.listStatic = renderGraph.CreateShadowRendererList(ref s);
                        }

                        break;
                    }

                    case SliceCategory.CACHED_DYNAMIC:
                    {
                        if (fs is { staticDirty: true, hasBakeCull: true })
                        {
                            ShadowDrawingSettings ss = this.MakeSettings(fs.bakeCull, fs.bakeLightIndex, ShadowObjectsFilter.StaticOnly, useLayers);
                            fs.listStatic = renderGraph.CreateShadowRendererList(ref ss);
                        }

  
                        ShadowDrawingSettings sd = fs.hasBakeCull
                            ? this.MakeSettings(fs.bakeCull, fs.bakeLightIndex, ShadowObjectsFilter.DynamicOnly, useLayers)
                            : this.MakeSettings(cullResults, fs.visibleLightIndex, ShadowObjectsFilter.DynamicOnly, useLayers);
                        fs.listDynamic = renderGraph.CreateShadowRendererList(ref sd);
                        break;
                    }
                }

                this._frameSlices[i] = fs;
            }
        }

        private ShadowDrawingSettings MakeSettings(CullingResults cullResults, int visibleLightIndex, ShadowObjectsFilter filter, bool useLayers) {
            return new ShadowDrawingSettings(cullResults, visibleLightIndex) {
                useRenderingLayerMaskTest = useLayers,
                objectsFilter = filter
            };
        }

        private void RecordStaticBakePass(RenderGraph renderGraph, TextureHandle staticHandle) {
            using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("CachedAdditionalShadows_StaticBake", out BakePassData passData, this.profilingSampler);

            passData.pass = this;
            passData.slices = this._frameSlices;

            for (int i = 0; i < this._frameSlices.Count; i++)
            {
                FrameSlice fs = this._frameSlices[i];
                if (fs is { category: SliceCategory.CACHED_DYNAMIC, staticDirty: true } && fs.listStatic.IsValid()) builder.UseRendererList(fs.listStatic);
            }

            builder.SetRenderAttachmentDepth(staticHandle, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc<BakePassData>(CachedAdditionalShadowsPass.ExecuteStaticBake);
        }

        private void RecordMainPass(RenderGraph renderGraph, TextureHandle mainHandle, TextureHandle staticHandle, bool anyDynamic) {
            using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("CachedAdditionalShadows_Main", out MainPassData passData, this.profilingSampler);

            passData.pass = this;
            passData.slices = this._frameSlices;
            passData.staticAtlas = this._staticAtlas;

            for (int i = 0; i < this._frameSlices.Count; i++)
            {
                FrameSlice fs = this._frameSlices[i];
                switch (fs.category)
                {
                    case SliceCategory.REALTIME when fs.listAll.IsValid():
                        builder.UseRendererList(fs.listAll);
                        break;

                    case SliceCategory.CACHED_STATIC when fs.staticDirty && fs.listStatic.IsValid():
                        builder.UseRendererList(fs.listStatic);
                        break;

                    case SliceCategory.CACHED_DYNAMIC when fs.listDynamic.IsValid():
                        builder.UseRendererList(fs.listDynamic);
                        break;
                }
            }

            if (anyDynamic) builder.UseTexture(staticHandle, AccessFlags.Read);
            builder.SetRenderAttachmentDepth(mainHandle, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc<MainPassData>(CachedAdditionalShadowsPass.ExecuteMain);
        }

        private static void ExecuteStaticBake(BakePassData data, RasterGraphContext ctx) {
            RasterCommandBuffer cmd = ctx.cmd;
            CachedAdditionalShadowsPass pass = data.pass;

            Vector4 lastBias = new Vector4(-10f, -10f, -10f, -10f);

            for (int i = 0; i < data.slices.Count; i++)
            {
                FrameSlice fs = data.slices[i];
                if (fs.category != SliceCategory.CACHED_DYNAMIC || !fs.staticDirty || !fs.listStatic.IsValid()) continue;

                if (fs.bias != lastBias)
                {
                    ShadowUtilsReflection.SetShadowBias(cmd, fs.bias);
                    lastBias = fs.bias;
                }

                ShadowUtilsReflection.SetLightPosition(cmd, fs.lightPosition);

                pass.ClearSlot(cmd, fs.offsetX, fs.offsetY, fs.resolution);
                ShadowSliceData sliceData = pass.MakeSliceData(fs);
                RendererList list = fs.listStatic;
                ShadowUtilsReflection.RenderShadowSlice(cmd, ref sliceData, ref list, fs.projMatrix, fs.viewMatrix);

                pass._feature.MarkStaticBaked(fs.lightId);
            }
        }

        private static void ExecuteMain(MainPassData data, RasterGraphContext ctx) {
            RasterCommandBuffer cmd = ctx.cmd;
            CachedAdditionalShadowsPass pass = data.pass;

            cmd.SetKeyword(CachedAdditionalShadowsPass._kCastingPunctualLightShadow, true);

            Vector4 lastBias = new Vector4(-10f, -10f, -10f, -10f);

            for (int i = 0; i < data.slices.Count; i++)
            {
                FrameSlice fs = data.slices[i];

                if (fs.bias != lastBias)
                {
                    ShadowUtilsReflection.SetShadowBias(cmd, fs.bias);
                    lastBias = fs.bias;
                }

                ShadowUtilsReflection.SetLightPosition(cmd, fs.lightPosition);

                ShadowSliceData sliceData = pass.MakeSliceData(fs);

                switch (fs.category)
                {
                    case SliceCategory.REALTIME:
                    {
                        if (!fs.listAll.IsValid()) continue;
                        pass.ClearSlot(cmd, fs.offsetX, fs.offsetY, fs.resolution);

                        RendererList list = fs.listAll;
                        ShadowUtilsReflection.RenderShadowSlice(cmd, ref sliceData, ref list, fs.projMatrix, fs.viewMatrix);
                        break;
                    }

                    case SliceCategory.CACHED_STATIC:
                    {
                        if (!fs.staticDirty) continue;
                        if (!fs.listStatic.IsValid()) continue;

                        pass.ClearSlot(cmd, fs.offsetX, fs.offsetY, fs.resolution);

                        RendererList list = fs.listStatic;
                        ShadowUtilsReflection.RenderShadowSlice(cmd, ref sliceData, ref list, fs.projMatrix, fs.viewMatrix);

                        pass._feature.MarkStaticBaked(fs.lightId);
                        break;
                    }

                    case SliceCategory.CACHED_DYNAMIC:
                    {
                        pass.CopyStaticSlot(cmd, data.staticAtlas, fs.offsetX, fs.offsetY, fs.resolution);
                        if (fs.listDynamic.IsValid())
                        {
                            RendererList list = fs.listDynamic;
                            ShadowUtilsReflection.RenderShadowSlice(cmd, ref sliceData, ref list, fs.projMatrix, fs.viewMatrix);
                        }

                        break;
                    }
                }
            }
        }

        private static void ExecuteBind(PostPassData data, RasterGraphContext ctx) {
            RasterCommandBuffer cmd = ctx.cmd;

            cmd.SetKeyword(CachedAdditionalShadowsPass._kAdditionalLightShadows, true);

            cmd.SetGlobalVectorArray(CachedAdditionalShadowsPass._idAdditionalShadowParams, data.shadowParams);
            cmd.SetGlobalMatrixArray(CachedAdditionalShadowsPass._idAdditionalLightsWorldToShadow, data.worldToShadow);

            ShadowUtilsReflection.GetScaleAndBiasForLinearDistanceFade(data.maxShadowDistanceSq, data.cascadeBorder, out float fadeScale, out float fadeBias);
            cmd.SetGlobalVector(CachedAdditionalShadowsPass._idAdditionalShadowFadeParams, new Vector4(fadeScale, fadeBias, 0f, 0f));

            ShadowUtilsReflection.SetSoftShadowQualityShaderKeywords(cmd, data.softShadows, data.softShadowQuality);

            if (!data.softShadows) return;
            Vector2 invSize = new Vector2(1f / data.atlasSize.x, 1f / data.atlasSize.y);
            Vector2 invHalf = invSize * 0.5f;

            cmd.SetGlobalVector(CachedAdditionalShadowsPass._idAdditionalShadowOffset0, new Vector4(-invHalf.x, -invHalf.y, invHalf.x, -invHalf.y));
            cmd.SetGlobalVector(CachedAdditionalShadowsPass._idAdditionalShadowOffset1, new Vector4(-invHalf.x, invHalf.y, invHalf.x, invHalf.y));
            cmd.SetGlobalVector(CachedAdditionalShadowsPass._idAdditionalShadowmapSize, new Vector4(invSize.x, invSize.y, data.atlasSize.x, data.atlasSize.y));
        }

        private ShadowSliceData MakeSliceData(FrameSlice fs) {
            return new ShadowSliceData {
                offsetX = fs.offsetX,
                offsetY = fs.offsetY,
                resolution = fs.resolution,
                viewMatrix = fs.viewMatrix,
                projectionMatrix = fs.projMatrix
            };
        }

        private void ClearSlot(RasterCommandBuffer cmd, int x, int y, int resolution) {
            cmd.SetViewport(new Rect(x, y, resolution, resolution));
            Blitter.BlitTexture(cmd, new Vector4(1f, 1f, 0f, 0f), this._blitMaterial, CachedAdditionalShadowsPass.BLIT_PASS_CLEAR);
        }

        private void CopyStaticSlot(RasterCommandBuffer cmd, RTHandle staticAtlas, int x, int y, int resolution) {
            float invAtlas = 1f / this._allocatedAtlasSize;

            Vector4 scaleBias = new Vector4(resolution * invAtlas, resolution * invAtlas, x * invAtlas, y * invAtlas);
            cmd.SetViewport(new Rect(x, y, resolution, resolution));
            Blitter.BlitTexture(cmd, staticAtlas, scaleBias, this._blitMaterial, CachedAdditionalShadowsPass.BLIT_PASS_COPY);
        }

        private void EnsureAtlases() {
            int desired = Mathf.NextPowerOfTwo(Mathf.Max(this._feature.atlasSize, 256));
            if (this._allocatedAtlasSize == desired && this._mainAtlas != null && this._staticAtlas != null) return;

            this._mainAtlas?.Release();
            this._staticAtlas?.Release();
            this._slots.Clear();
            this._freeByResolution.Clear();

            this._cursorX = 0;
            this._cursorY = 0;
            this._rowHeight = 0;

            this._allocatedAtlasSize = desired;
            this._mainAtlas = ShadowUtils.AllocShadowRT(desired, desired, CachedAdditionalShadowsPass.SHADOWMAP_BITS, 1, 0, "_AdditionalLightsShadowmapTexture");
            this._staticAtlas = ShadowUtils.AllocShadowRT(desired, desired, CachedAdditionalShadowsPass.SHADOWMAP_BITS, 1, 0, "_CachedAdditionalLightsStaticShadowmap");
        }

        private void FindBlitMaterial() {
            if (this._blitMaterial) return;

            Shader sh = this._feature.blitShader ? this._feature.blitShader : Shader.Find("Hidden/FailCake/CachedShadowSliceBlit");
            if (!sh)
            {
                Debug.LogError("Missing blit shader 'Hidden/FailCake/CachedShadowSliceBlit'.");
                return;
            }

            this._blitMaterial = CoreUtils.CreateEngineMaterial(sh);
        }

        public static void EnsureKeywords() {
            if (CachedAdditionalShadowsPass._keywordsReady) return;

            CachedAdditionalShadowsPass._kAdditionalLightShadows = GlobalKeyword.Create(ShaderKeywordStrings.AdditionalLightShadows);
            CachedAdditionalShadowsPass._kCastingPunctualLightShadow = GlobalKeyword.Create(ShaderKeywordStrings.CastingPunctualLightShadow);
            CachedAdditionalShadowsPass._keywordsReady = true;
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
            }

            return fovBias;
        }

        private struct Slot
        {
            public int offsetX;
            public int offsetY;
            public int resolution;
            public EntityId lightId;
            public int lastSeenFrame;
        }

        private struct FrameSlice
        {
            public int visibleLightIndex;

            public EntityId lightId;

            public SliceCategory category;

            public Matrix4x4 viewMatrix;
            public Matrix4x4 projMatrix;

            public int offsetX;
            public int offsetY;
            public int resolution;

            public bool staticDirty;
            public bool hasBakeCull;

            public CullingResults bakeCull;
            public int bakeLightIndex;

            public Vector4 bias;
            public Vector3 lightPosition;

            public RendererListHandle listAll;
            public RendererListHandle listStatic;
            public RendererListHandle listDynamic;
        }

        private class BakePassData
        {
            public CachedAdditionalShadowsPass pass;
            public List<FrameSlice> slices;
        }

        private class MainPassData
        {
            public CachedAdditionalShadowsPass pass;
            public List<FrameSlice> slices;
            public RTHandle staticAtlas;
        }

        private class PostPassData
        {
            public Vector4[] shadowParams;
            public Matrix4x4[] worldToShadow;
            public Vector2Int atlasSize;
            public float maxShadowDistanceSq;
            public float cascadeBorder;
            public bool softShadows;
            public SoftShadowQuality softShadowQuality;
        }
    }
}