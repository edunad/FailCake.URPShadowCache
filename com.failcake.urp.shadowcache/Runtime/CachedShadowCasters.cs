#region

using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

#endregion

namespace HyenaQuest
{
    internal sealed class CachedShadowCasters : IDisposable
    {
        #region STATIC

        private delegate void GetChanges(Type type, List<Object> changed, out NativeArray<EntityId> changedIds, out NativeArray<EntityId> destroyedIds, Allocator allocator, bool sorted);

        private static readonly Type DISPATCHER_TYPE = typeof(Object).Assembly.GetType("UnityEngine.ObjectDispatcher");
        private static readonly Type TRACKING_FLAGS = CachedShadowCasters.DISPATCHER_TYPE?.GetNestedType("TypeTrackingFlags");
        private static readonly MethodInfo ENABLE_TRACKING = CachedShadowCasters.TRACKING_FLAGS == null ? null : CachedShadowCasters.DISPATCHER_TYPE.GetMethod("EnableTypeTracking", new[] { CachedShadowCasters.TRACKING_FLAGS, typeof(Type[]) });

        private static readonly MethodInfo GET_CHANGES = CachedShadowCasters.DISPATCHER_TYPE?.GetMethod("GetTypeChangesAndClear",
            new[] { typeof(Type), typeof(List<Object>), typeof(NativeArray<EntityId>).MakeByRefType(), typeof(NativeArray<EntityId>).MakeByRefType(), typeof(Allocator), typeof(bool) });

        private static readonly PropertyInfo HISTORY_FRAMES = CachedShadowCasters.DISPATCHER_TYPE?.GetProperty("maxDispatchHistoryFramesCount");

        internal static int GetFaceMask(Vector3 center, Vector3 extents, float range, float slack) {
            Vector3 nearest = new Vector3(Mathf.Max(0f, Mathf.Abs(center.x) - extents.x), Mathf.Max(0f, Mathf.Abs(center.y) - extents.y), Mathf.Max(0f, Mathf.Abs(center.z) - extents.z));
            if (nearest.sqrMagnitude > range * range) return 0;

            Vector3 positive = center + extents;
            Vector3 negative = extents - center;
            Vector3 limits = new Vector3(Mathf.Max(nearest.y, nearest.z), Mathf.Max(nearest.x, nearest.z), Mathf.Max(nearest.x, nearest.y)) * slack;
            int mask = 0;

            if (positive.x > 0f && positive.x >= limits.x) mask |= 1;
            if (negative.x > 0f && negative.x >= limits.x) mask |= 2;
            if (positive.y > 0f && positive.y >= limits.y) mask |= 4;
            if (negative.y > 0f && negative.y >= limits.y) mask |= 8;
            if (positive.z > 0f && positive.z >= limits.z) mask |= 16;
            if (negative.z > 0f && negative.z >= limits.z) mask |= 32;

            return mask;
        }

        #endregion

        #region PRIVATE FIELDS

        private struct CasterBounds
        {
            public Vector3 center;
            public Vector3 extents;
            public int layerMask;
            public uint renderingLayerMask;
        }

        private IDisposable _dispatcher;
        private GetChanges _getChanges;
        private Type[] _types;
        private readonly Dictionary<EntityId, Component> _casters = new Dictionary<EntityId, Component>();
        private readonly List<Object> _changed = new List<Object>();
        private readonly List<CasterBounds> _bounds = new List<CasterBounds>();

        private bool _unboundedCasters;

        #endregion

        internal CachedShadowCasters() {
            if (CachedShadowCasters.ENABLE_TRACKING == null || CachedShadowCasters.GET_CHANGES == null || CachedShadowCasters.HISTORY_FRAMES?.SetMethod == null) return;
            try
            {
                List<Type> types = new List<Type> {
                    typeof(MeshRenderer), typeof(SkinnedMeshRenderer), typeof(LineRenderer), typeof(TrailRenderer), typeof(SpriteRenderer), typeof(BillboardRenderer), typeof(Terrain)
                };

                foreach (string name in new[] { "UnityEngine.ParticleSystemRenderer, UnityEngine.ParticleSystemModule", "UnityEngine.VFX.VFXRenderer, UnityEngine.VFXModule" })
                {
                    Type rendererType = Type.GetType(name, false);
                    if (rendererType != null) types.Add(rendererType);
                }

                this._types = types.ToArray();
                this._dispatcher = (IDisposable)Activator.CreateInstance(CachedShadowCasters.DISPATCHER_TYPE);
                this._getChanges = (GetChanges)Delegate.CreateDelegate(typeof(GetChanges), this._dispatcher, CachedShadowCasters.GET_CHANGES);

                CachedShadowCasters.HISTORY_FRAMES.SetValue(this._dispatcher, int.MaxValue);
                CachedShadowCasters.ENABLE_TRACKING.Invoke(this._dispatcher, new[] { Enum.ToObject(CachedShadowCasters.TRACKING_FLAGS, 1), this._types });
            }
            catch (Exception exception) when (exception is MemberAccessException or ArgumentException or TargetInvocationException or TypeLoadException)
            {
                this.Dispose();
            }
        }

        public void Update() {
            this._bounds.Clear();
            this._unboundedCasters = this._getChanges == null;
            if (this._unboundedCasters) return;

            for (int i = 0; i < this._types.Length; i++)
            {
                this._getChanges(this._types[i], this._changed, out NativeArray<EntityId> changedIds, out NativeArray<EntityId> destroyedIds, Allocator.Temp, false);

                try
                {
                    for (int j = 0; j < destroyedIds.Length; j++) this._casters.Remove(destroyedIds[j]);
                    for (int j = 0; j < this._changed.Count; j++)
                    {
                        if (this._changed[j] is not Component component || !component) continue;
                        EntityId id = component.GetEntityId();

                        if (component is Renderer { staticShadowCaster: true })
                            this._casters.Remove(id);
                        else
                            this._casters[id] = component;
                    }
                }
                finally
                {
                    changedIds.Dispose();
                    destroyedIds.Dispose();
                }
            }

            foreach (KeyValuePair<EntityId, Component> pair in this._casters)
            {
                Component component = pair.Value;
                if (!component) continue;

                GameObject gameObject = component.gameObject;
                if (!gameObject.activeInHierarchy) continue;

                if (component is Terrain terrain)
                {
                    if (terrain.enabled && ((terrain.drawHeightmap && terrain.shadowCastingMode != ShadowCastingMode.Off) || terrain.drawTreesAndFoliage)) this._unboundedCasters = true;
                    continue;
                }

                Renderer caster = (Renderer)component;
                if (!caster.enabled || caster.forceRenderingOff || caster.staticShadowCaster || caster.shadowCastingMode == ShadowCastingMode.Off) continue;

                Bounds bounds = caster.bounds;
                Vector3 center = bounds.center;
                Vector3 extents = bounds.extents;

                if (!float.IsFinite(center.sqrMagnitude) || !float.IsFinite(extents.sqrMagnitude) || extents.x < 0f || extents.y < 0f || extents.z < 0f)
                {
                    this._unboundedCasters = true;
                    continue;
                }

                this._bounds.Add(new CasterBounds {
                    center = center,
                    extents = extents,
                    layerMask = 1 << gameObject.layer,
                    renderingLayerMask = caster.renderingLayerMask
                });
            }
        }

        public int GetPointFaceMask(Vector3 lightPosition, float range, int cullingMask, uint renderingLayerMask, bool useRenderingLayers, float projectionScale, float padding) {
            if (this._unboundedCasters || this._getChanges == null) return 63;

            int mask = 0;
            float slack = Mathf.Clamp01(Mathf.Abs(projectionScale) * 0.99f);
            padding = Mathf.Max(0.001f, Mathf.Abs(padding));

            for (int i = 0; i < this._bounds.Count; i++)
            {
                CasterBounds caster = this._bounds[i];
                if ((caster.layerMask & cullingMask) == 0 || (useRenderingLayers && (caster.renderingLayerMask & renderingLayerMask) == 0)) continue;

                mask |= CachedShadowCasters.GetFaceMask(caster.center - lightPosition, caster.extents + Vector3.one * padding, range + padding, slack);
                if (mask == 63) break;
            }

            return mask;
        }

        public void Dispose() {
            this._dispatcher?.Dispose();
            this._dispatcher = null;
            this._getChanges = null;

            this._types = null;

            this._casters.Clear();
            this._changed.Clear();
            this._bounds.Clear();
        }
    }
}