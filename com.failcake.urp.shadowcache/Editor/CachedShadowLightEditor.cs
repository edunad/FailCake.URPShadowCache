#if UNITY_EDITOR

#region

using UnityEditor;
using UnityEngine;

#endregion

namespace FailCake
{
    [CustomEditor(typeof(CachedShadowLight))]
    public class CachedShadowLightEditor : Editor
    {
        public override void OnInspectorGUI() {
            CachedShadowLight cache = (CachedShadowLight)this.target;

            this.DrawValidationWarnings(cache);
            this.DrawSettings();

            EditorGUILayout.Space();

            if (!CachedAdditionalShadowsFeature.Instance) EditorGUILayout.HelpBox("CachedAdditionalShadowsFeature is not added on the current renderer", MessageType.Info);
            if (!GUILayout.Button("Force Refresh")) return;

            cache.RequestUpdate();
            if (CachedAdditionalShadowsFeature.Instance) CachedAdditionalShadowsFeature.Instance.Invalidate();
        }

        private void DrawValidationWarnings(CachedShadowLight cache) {
            Light light = cache.GetLight();
            if (!light) return;

            if (light.shadows == LightShadows.None) EditorGUILayout.HelpBox("Enable shadows on the Light or remove CachedShadowLight", MessageType.Warning);
            if (light.type != LightType.Spot && light.type != LightType.Point) EditorGUILayout.HelpBox("Shadow cache only applies to Spot & Point lights", MessageType.Warning);
        }

        private void DrawSettings() {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Cache Settings", EditorStyles.boldLabel);

            this.serializedObject.Update();

            SerializedProperty dyn = this.serializedObject.FindProperty("renderDynamicCasters");
            if (dyn != null) EditorGUILayout.PropertyField(dyn);

            SerializedProperty res = this.serializedObject.FindProperty("cachedResolution");
            if (res != null) EditorGUILayout.PropertyField(res);

            SerializedProperty interval = this.serializedObject.FindProperty("refreshInterval");
            if (interval != null) EditorGUILayout.PropertyField(interval);

            this.serializedObject.ApplyModifiedProperties();
            EditorGUILayout.EndVertical();
        }
    }
}

#endif