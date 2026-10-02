using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace FogSix.Source
{
    // Gate for the LAYERED froxel path (the FROXEL_* named passes): one Blit writes up to 32 slices of a 3D render
    // target via geometry-shader instancing + SV_RenderTargetArrayIndex, instead of one Blit (and a render-
    // target switch) per slice. That needs Unity to bind ALL slices for depthSlice -1 on a Tex3D, which can't
    // be confirmed from the docs for this build — so it's PROVEN once at startup: render the slice-id test
    // pass into a tiny 40-slice volume (2 batches, so the batch offset is exercised too) and read it back.
    // Only a readback where every slice holds its own id enables the path; anything else (old bundle without
    // the passes, no GS/readback support, binding ignored) leaves FogFroxels on the per-slice Blits.
    internal static class FogLayered
    {
        public const int Batch = 32;          // = [instance(32)] in the shader's FOG_LAYER_GEOMETRY
        // Found by pass NAME (Material.FindPass), so reordering/removing shader passes can't desync them.
        // Bundles from before the names return -1 -> the layered path stays off (per-slice fallback).
        public static int PassScatter = -1, PassInteg = -1, PassApplyInteg = -1;
        private static int _passTest = -1;

        private const int TestSlices = 40;
        private static readonly int SliceBaseId = Shader.PropertyToID("_FogSliceBase");
        private static readonly int VolParamsId = Shader.PropertyToID("_FogVolParams");

        private enum State { Untested, Pending, Done }
        private static State _state = State.Untested;
        private static RenderTexture _testRT;

        public static bool Ok { get; private set; }

        // Live A/B: false forces the per-slice volume + per-pixel slice loop even when verified.
        public static bool enabled = true;

        // Kicks off the one-time test (first froxel frame). Result arrives a few frames later; until then
        // Ok stays false and the per-slice path renders.
        public static void EnsureTested(Material mat)
        {
            if (_state != State.Untested) return;
            _state = State.Done;

            PassScatter = mat.FindPass("FROXEL_LAYERED");
            PassInteg = mat.FindPass("FROXEL_INTEGRATE");
            _passTest = mat.FindPass("FROXEL_LAYERTEST");
            PassApplyInteg = mat.FindPass("FROXEL_APPLYINTEG");

            string skip = null;
            if (PassScatter < 0 || PassInteg < 0 || _passTest < 0 || PassApplyInteg < 0)
                skip = "shader bundle predates the named layered passes (rebuild volfog)";
            else if (!SystemInfo.supportsGeometryShaders) skip = "no geometry shaders";
            else if (!SystemInfo.supportsAsyncGPUReadback) skip = "no async GPU readback";
            else if (SystemInfo.graphicsShaderLevel < 50) skip = "shader model < 5.0";
            if (skip != null)
            {
                Plugin.MyLog.LogInfo("[FogSix] Layered froxels off: " + skip + " — using per-slice rendering.");
                return;
            }

            RenderTexture prev = RenderTexture.active;
            try
            {
                _testRT = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32)
                {
                    dimension = TextureDimension.Tex3D,
                    volumeDepth = TestSlices,
                    filterMode = FilterMode.Point,
                };
                _testRT.Create();

                Graphics.SetRenderTarget(_testRT, 0, CubemapFace.Unknown, -1);
                GL.Clear(false, true, Color.clear);
                // The GS reads the slice count from _FogVolParams.z; the real frame re-sets it right after.
                mat.SetVector(VolParamsId, new Vector4(0.5f, 200f, TestSlices, 1f));
                for (int b = 0; b < TestSlices; b += Batch)
                {
                    mat.SetFloat(SliceBaseId, b);
                    Graphics.Blit(null, mat, _passTest);
                }
                RenderTexture.active = prev;

                _state = State.Pending;
                AsyncGPUReadback.Request(_testRT, 0, TextureFormat.RGBA32, OnReadback);
            }
            catch (System.Exception ex)
            {
                RenderTexture.active = prev;
                Plugin.MyLog.LogWarning("[FogSix] Layered froxel test failed to run (" + ex.Message + ") — using per-slice rendering.");
                ReleaseTest();
                _state = State.Done;
            }
        }

        private static void OnReadback(AsyncGPUReadbackRequest req)
        {
            string fail = null;
            if (req.hasError) fail = "readback error";
            else
            {
                int layers = Mathf.Max(req.depth, req.layerCount);
                if (layers < TestSlices) fail = "readback returned " + layers + " slices";
                else
                {
                    try
                    {
                        for (int k = 0; k < TestSlices && fail == null; k++)
                        {
                            NativeArray<Color32> px = req.GetData<Color32>(k);
                            // first and last texel of the slice: the whole slice must carry its own id
                            if (px.Length < 16 || px[0].r != k + 1 || px[15].r != k + 1)
                                fail = "slice " + k + " holds " + (px.Length > 0 ? px[0].r - 1 : -1);
                        }
                    }
                    catch (System.Exception ex) { fail = ex.Message; }
                }
            }

            Ok = fail == null;
            if (Ok) Plugin.MyLog.LogInfo("[FogSix] Layered froxels verified — volume + integration render in one draw per 32 slices.");
            else Plugin.MyLog.LogWarning("[FogSix] Layered froxels off (" + fail + ") — using per-slice rendering.");
            ReleaseTest();
            _state = State.Done;
        }

        private static void ReleaseTest()
        {
            if (_testRT == null) return;
            _testRT.Release();
            Object.Destroy(_testRT);
            _testRT = null;
        }
    }
}
