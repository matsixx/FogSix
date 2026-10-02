using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FogSix.Source
{
    // Frostbite-style froxel volumetrics (Hillaire, SIGGRAPH 2015 "Physically Based and Unified
    // Volumetric Rendering in Frostbite"). Fog density + lighting are evaluated ONCE per view-frustum
    // voxel into a small 3D scattering volume (shader pass 7, one blit per depth slice), temporally
    // accumulated IN VOLUME SPACE (uniform per-frame Halton jitter + reprojected exponential blend — the
    // paper's scheme, no clamp needed), and each pixel then just integrates the tiny volume front-to-back
    // (pass 8). There is NO per-pixel jitter anywhere: the IGN ladders, step jitter, half-res upsample and
    // screen-space temporal all belong to the legacy path and simply don't run in froxel mode.
    //
    // Layered mode (FogLayered.Ok, proven by a startup readback): the volume is written in one draw per 32
    // slices (pass 10), a second layered draw builds the running integral (pass 11), and the apply +
    // glass passes take ONE tap of that instead of looping over every slice (passes 13 / 1).
    internal static class FogFroxels
    {
        // Set true (UnityExplorer, live) if the fog renders vertically MIRRORED (e.g. dense ground fog
        // hanging overhead): slice rendering and screen blits can disagree about V on some platforms;
        // this flips the volume's V convention for the writer and both readers together.
        public static bool flipSliceV = false;

        private const float Near = 0.5f;   // first slice distance, metres
        private const int PassFroxel = 7;
        private const int PassApply = 8;

        private static readonly int SliceId = Shader.PropertyToID("_FogSlice");
        private static readonly int SliceBaseId = Shader.PropertyToID("_FogSliceBase");
        private static readonly int ScatterVolId = Shader.PropertyToID("_FogScatterVol");
        private static readonly int IntegVolId = Shader.PropertyToID("_FogIntegVol");
        private static readonly int UseIntegId = Shader.PropertyToID("_FogUseInteg");

        // Per camera AND eye (mono / left / right). The scope camera has its own TOD_Scattering and renders
        // fog too — keyed by eye alone, flatscreen main + scope shared one volume and each reprojected the
        // other's frustum as its history every frame.
        private class EyeVolumes
        {
            public Camera cam;
            public RenderTexture read, write, integ;
            public Matrix4x4 prevVP;
            public Vector3 prevCamPos;
            public bool valid;
        }
        private static readonly Dictionary<long, EyeVolumes> _vols = new Dictionary<long, EyeVolumes>();
        private static readonly List<long> _dead = new List<long>();

        // Camera + eye -> dictionary key without boxing (eye enum is 0..2).
        internal static long Key(Camera cam, Camera.MonoOrStereoscopicEye eye)
        {
            return ((long)cam.GetInstanceID() << 2) | (long)(int)eye;
        }

        internal static void DestroyRT(RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Object.Destroy(rt);   // Release alone frees the GPU memory but leaks the RenderTexture object
        }

        public static void Render(Material mat, Camera cam, Camera.MonoOrStereoscopicEye eye, Matrix4x4 vp,
            Vector3 camPos, RenderTexture source, RenderTexture destination)
        {
            int w, h;
            GridSize(out w, out h);
            int slices = Mathf.Clamp(FogConfig.FroxelSlices.Value, 16, 128);
            float far = Mathf.Max(50f, FogConfig.FroxelFar.Value);

            FogLayered.EnsureTested(mat);   // one-time; runs before the params below (it borrows _FogVolParams)
            bool layered = FogLayered.Ok && FogLayered.enabled;

            EyeVolumes v = Get(cam, eye, w, h, slices, layered);

            mat.SetVector("_FogVolParams", new Vector4(Near, far, slices, 1f / Mathf.Log(far / Near)));
            // Uniform per-frame Halton jitter of the sample position within each slice — successive frames
            // sample different depths and the volume history averages them (the "same offset for all
            // samples along the view ray" rule from the paper).
            mat.SetFloat("_FogFroxelJitter", Halton2(Time.frameCount & 63));
            float smooth = Mathf.Clamp(FogConfig.TemporalSmoothing.Value, 0f, 0.95f);
            mat.SetFloat("_FogVolTemporal", v.valid ? smooth : 0f);
            mat.SetMatrix("_FogPrevVP", v.prevVP);
            mat.SetVector("_FogPrevCamPos", v.prevCamPos);
            mat.SetFloat("_FogVolFlipY", flipSliceV ? 1f : 0f);
            if (v.valid) mat.SetTexture("_FogHistVol", v.read);

            // Populate the scattering volume. Graphics.Blit with no destination renders into the CURRENTLY
            // ACTIVE target: all slices at once (layered, one draw per 32) or one slice per draw.
            if (layered)
                BlitLayered(mat, v.write, slices, FogLayered.PassScatter);
            else
            {
                for (int s = 0; s < slices; s++)
                {
                    mat.SetFloat(SliceId, s);
                    Graphics.SetRenderTarget(v.write, 0, CubemapFace.Unknown, s);
                    Graphics.Blit(null, mat, PassFroxel);
                }
            }

            v.prevVP = vp;
            v.prevCamPos = camPos;
            v.valid = true;
            RenderTexture t = v.read; v.read = v.write; v.write = t;

            mat.SetTexture(ScatterVolId, v.read);
            // Publish for other mods (SSRSix fogs its reflections' reflected-path segment with two taps
            // of this volume — zero assembly dependency, the CloudSix sky-map pattern). The stamp carries
            // freshness (frameCount) and the V-flip convention so consumers can gate and sample correctly.
            // Always the SCATTERING volume (eye-aligned, per metre) — the integral is internal.
            Shader.SetGlobalTexture("_FogSixScatterVol", v.read);
            Shader.SetGlobalVector("_FogSixVolParams", new Vector4(Near, far, slices, 1f / Mathf.Log(far / Near)));
            Shader.SetGlobalVector("_FogSixVolStamp", new Vector4(Time.frameCount, flipSliceV ? 1f : 0f, 0f, 0f));
            if (layered)
            {
                BlitLayered(mat, v.integ, slices, FogLayered.PassInteg);   // reads _FogScatterVol = v.read
                mat.SetTexture(IntegVolId, v.integ);
                mat.SetFloat(UseIntegId, 1f);
                FogGlass.Publish(mat, v.integ);   // windows tap this same integral at their own depth
                Graphics.Blit(source, destination, mat, FogLayered.PassApplyInteg);
            }
            else
            {
                mat.SetFloat(UseIntegId, 0f);
                FogGlass.Publish(mat, null);
                Graphics.Blit(source, destination, mat, PassApply);
            }
        }

        private static void BlitLayered(Material mat, RenderTexture target, int slices, int pass)
        {
            Graphics.SetRenderTarget(target, 0, CubemapFace.Unknown, -1);   // all slices
            for (int b = 0; b < slices; b += FogLayered.Batch)
            {
                mat.SetFloat(SliceBaseId, b);
                Graphics.Blit(null, mat, pass);
            }
        }

        internal static void GridSize(out int w, out int h)
        {
            switch (FogConfig.FroxelRes.Value)
            {
                case EFroxelRes.Low:  w = 128; h = 72;  break;
                case EFroxelRes.High: w = 240; h = 136; break;
                default:              w = 160; h = 92;  break;
            }
        }

        private static EyeVolumes Get(Camera cam, Camera.MonoOrStereoscopicEye eye, int w, int h, int slices, bool layered)
        {
            long key = Key(cam, eye);
            if (!_vols.TryGetValue(key, out EyeVolumes v))
            {
                PruneDestroyedCameras();   // only when a new camera/eye shows up (raid load, first scope)
                v = new EyeVolumes { cam = cam };
                _vols[key] = v;
            }
            if (v.read == null || v.read.width != w || v.read.height != h || v.read.volumeDepth != slices)
            {
                DestroyRT(v.read);
                DestroyRT(v.write);
                DestroyRT(v.integ);
                v.integ = null;
                v.read = MakeVolume(w, h, slices);
                v.write = MakeVolume(w, h, slices);
                v.valid = false;   // grid changed -> stale history, restart accumulation
            }
            if (layered && v.integ == null)
                v.integ = MakeVolume(w, h, slices);
            return v;
        }

        private static void PruneDestroyedCameras()
        {
            _dead.Clear();
            foreach (KeyValuePair<long, EyeVolumes> kv in _vols)
                if (kv.Value.cam == null) _dead.Add(kv.Key);
            foreach (long k in _dead)
            {
                EyeVolumes v = _vols[k];
                DestroyRT(v.read);
                DestroyRT(v.write);
                DestroyRT(v.integ);
                _vols.Remove(k);
            }
        }

        private static RenderTexture MakeVolume(int w, int h, int slices)
        {
            RenderTexture rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = slices,
                filterMode = FilterMode.Bilinear,   // = trilinear across the volume
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        // Van der Corput base-2 (Halton) sequence: maximally stratified per-frame offsets, so the
        // accumulated slice samples cover each froxel's depth extent evenly.
        private static float Halton2(int i)
        {
            i++;
            float f = 0.5f, r = 0f;
            while (i > 0)
            {
                if ((i & 1) == 1) r += f;
                f *= 0.5f;
                i >>= 1;
            }
            return r;
        }
    }
}
