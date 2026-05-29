#region

using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

#endregion

namespace FailCake
{
    public static class ShadowUtilsReflection // Override & patch unity URP shadows
    {
        private const BindingFlags FLAGS = BindingFlags.Static | BindingFlags.NonPublic;

        private delegate void DRenderShadowSlice(RasterCommandBuffer cmd, ref ShadowSliceData shadowSliceData, ref RendererList shadowRendererList, Matrix4x4 proj, Matrix4x4 view);

        private delegate void DSetShadowBias(RasterCommandBuffer cmd, Vector4 shadowBias);

        private delegate void DSetLightPosition(RasterCommandBuffer cmd, Vector3 lightPosition);

        private delegate void DGetScaleAndBiasForLinearDistanceFade(float fadeDistance, float border, out float scale, out float bias);

        private static DRenderShadowSlice _renderShadowSlice;
        private static DSetShadowBias _setShadowBias;
        private static DSetLightPosition _setLightPosition;
        private static DGetScaleAndBiasForLinearDistanceFade _getScaleAndBiasForLinearDistanceFade;

        private static GlobalKeyword _kSoftShadows;
        private static GlobalKeyword _kSoftShadowsLow;
        private static GlobalKeyword _kSoftShadowsMedium;
        private static GlobalKeyword _kSoftShadowsHigh;
        private static bool _keywordsReady;

        public static void RenderShadowSlice(RasterCommandBuffer cmd, ref ShadowSliceData shadowSliceData, ref RendererList shadowRendererList, Matrix4x4 proj, Matrix4x4 view) {
            if (ShadowUtilsReflection._renderShadowSlice == null)
            {
                MethodInfo mi = typeof(ShadowUtils).GetMethod("RenderShadowSlice", ShadowUtilsReflection.FLAGS, null,
                    new[] { typeof(RasterCommandBuffer), typeof(ShadowSliceData).MakeByRefType(), typeof(RendererList).MakeByRefType(), typeof(Matrix4x4), typeof(Matrix4x4) }, null);
                ShadowUtilsReflection._renderShadowSlice = (DRenderShadowSlice)Delegate.CreateDelegate(typeof(DRenderShadowSlice), mi);
            }

            ShadowUtilsReflection._renderShadowSlice(cmd, ref shadowSliceData, ref shadowRendererList, proj, view);
        }

        public static void SetShadowBias(RasterCommandBuffer cmd, Vector4 shadowBias) {
            if (ShadowUtilsReflection._setShadowBias == null)
            {
                MethodInfo mi = typeof(ShadowUtils).GetMethod("SetShadowBias", ShadowUtilsReflection.FLAGS, null, new[] { typeof(RasterCommandBuffer), typeof(Vector4) }, null);
                ShadowUtilsReflection._setShadowBias = (DSetShadowBias)Delegate.CreateDelegate(typeof(DSetShadowBias), mi);
            }

            ShadowUtilsReflection._setShadowBias(cmd, shadowBias);
        }

        public static void SetLightPosition(RasterCommandBuffer cmd, Vector3 lightPosition) {
            if (ShadowUtilsReflection._setLightPosition == null)
            {
                MethodInfo mi = typeof(ShadowUtils).GetMethod("SetLightPosition", ShadowUtilsReflection.FLAGS, null, new[] { typeof(RasterCommandBuffer), typeof(Vector3) }, null);
                ShadowUtilsReflection._setLightPosition = (DSetLightPosition)Delegate.CreateDelegate(typeof(DSetLightPosition), mi);
            }

            ShadowUtilsReflection._setLightPosition(cmd, lightPosition);
        }

        public static void GetScaleAndBiasForLinearDistanceFade(float fadeDistance, float border, out float scale, out float bias) {
            if (ShadowUtilsReflection._getScaleAndBiasForLinearDistanceFade == null)
            {
                MethodInfo mi = typeof(ShadowUtils).GetMethod("GetScaleAndBiasForLinearDistanceFade", ShadowUtilsReflection.FLAGS, null,
                    new[] { typeof(float), typeof(float), typeof(float).MakeByRefType(), typeof(float).MakeByRefType() }, null);
                ShadowUtilsReflection._getScaleAndBiasForLinearDistanceFade = (DGetScaleAndBiasForLinearDistanceFade)Delegate.CreateDelegate(typeof(DGetScaleAndBiasForLinearDistanceFade), mi);
            }

            ShadowUtilsReflection._getScaleAndBiasForLinearDistanceFade(fadeDistance, border, out scale, out bias);
        }

        public static void SetSoftShadowQualityShaderKeywords(RasterCommandBuffer cmd, bool softShadows, SoftShadowQuality softShadowQuality) {
            ShadowUtilsReflection.EnsureKeywords();

            cmd.SetKeyword(ShadowUtilsReflection._kSoftShadowsLow, softShadows && softShadowQuality == SoftShadowQuality.Low);
            cmd.SetKeyword(ShadowUtilsReflection._kSoftShadowsMedium, softShadows && softShadowQuality == SoftShadowQuality.Medium);
            cmd.SetKeyword(ShadowUtilsReflection._kSoftShadowsHigh, softShadows && softShadowQuality == SoftShadowQuality.High);
            cmd.SetKeyword(ShadowUtilsReflection._kSoftShadows, softShadows && softShadowQuality == SoftShadowQuality.UsePipelineSettings);
        }

        private static void EnsureKeywords() {
            if (ShadowUtilsReflection._keywordsReady) return;

            ShadowUtilsReflection._kSoftShadows = GlobalKeyword.Create(ShaderKeywordStrings.SoftShadows);
            ShadowUtilsReflection._kSoftShadowsLow = GlobalKeyword.Create(ShaderKeywordStrings.SoftShadowsLow);
            ShadowUtilsReflection._kSoftShadowsMedium = GlobalKeyword.Create(ShaderKeywordStrings.SoftShadowsMedium);
            ShadowUtilsReflection._kSoftShadowsHigh = GlobalKeyword.Create(ShaderKeywordStrings.SoftShadowsHigh);
            ShadowUtilsReflection._keywordsReady = true;
        }
    }
}