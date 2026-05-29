#region

using UnityEngine;

#endregion

namespace FailCake
{
    [RequireComponent(typeof(Light))]
    [ExecuteAlways]
    public class CachedShadowLight : MonoBehaviour
    {
        [Header("Cache")]
        public bool renderDynamicCasters = true;

        public int cachedResolution = 1024;

        [Range(0, 10)]
        public float refreshInterval;

        #region PRIVATE

        private Light _light;
        private float _nextRefreshAtTime;

        private Vector3 _lastPosition;
        private Quaternion _lastRotation;
        private float _lastRange;

        #endregion

        public void Awake() {
            this._light = this.GetComponent<Light>();
            if (!this._light) throw new UnityException("Missing Light component");
        }

        public void OnEnable() {
            if (CachedAdditionalShadowsFeature.Instance) CachedAdditionalShadowsFeature.Instance.Register(this);

            this._nextRefreshAtTime = Time.unscaledTime + this.refreshInterval;
            this.CaptureTransform();
        }

        public void OnDisable() {
            if (CachedAdditionalShadowsFeature.Instance) CachedAdditionalShadowsFeature.Instance.Unregister(this);
        }

        public void Update() {
            if (this.HasTransformChanged())
            {
                this.CaptureTransform();
                this.RequestUpdate();
            }

            if (this.refreshInterval <= 0f) return;
            if (Time.unscaledTime < this._nextRefreshAtTime) return;

            this._nextRefreshAtTime = Time.unscaledTime + this.refreshInterval;
            this.RequestUpdate();
        }

        public EntityId GetLightId() {
            return this._light ? this._light.GetEntityId() : EntityId.None;
        }

        public Light GetLight() {
            return this._light;
        }

        public void RequestUpdate() {
            if (!CachedAdditionalShadowsFeature.Instance || !this._light) return;
            CachedAdditionalShadowsFeature.Instance.RequestUpdate(this._light);
        }

        private bool HasTransformChanged() {
            if (!this._light) return false;

            Transform t = this.transform;
            return (t.position - this._lastPosition).sqrMagnitude > 1e-6f
                   || Quaternion.Angle(t.rotation, this._lastRotation) > 0.01f
                   || !Mathf.Approximately(this._light.range, this._lastRange);
        }

        private void CaptureTransform() {
            if (!this._light) return;

            Transform t = this.transform;

            this._lastPosition = t.position;
            this._lastRotation = t.rotation;
            this._lastRange = this._light.range;
        }
    }
}