using System;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;

namespace FogSix.Source
{
    // Bakes the fog's noise fields into a small TILING 3D texture once at load, so the shader fetches them
    // (one trilinear tap) instead of recomputing value-noise fbm on every march step (24-88 hash evals).
    // The march is ALU-bound and the texture units sit idle — the same trade the CloudSix cloud shader is
    // built on, and the reason it affords 4x the steps.
    //   R = 3-octave value fbm         (the wisp noise; the dust samples the same field at the dust scale)
    //   G = domain-warped 5-octave fbm (the mist; the warp is baked in, so a Mist Detail change rebakes)
    // Tiling requirements bend the procedural functions slightly: octave ratios are exactly 2 (was
    // 2.02/2.13) and the mist warp field runs at 0.5x base frequency (was 0.6x) — statistically the same
    // look. The tile period is Period noise units; at Noise Scale 0.01 that's an 800 m world repeat.
    internal static class FogNoiseTex
    {
        public const float Period = 8f;  // noise units per texture repeat (= base-octave lattice cells)
        private const int Size = 128;    // 128^3 RGBA32 = 8 MB + mips; base octave gets 16 texels per cell

        public static Texture3D Tex { get; private set; }
        private static float _bakedMistDetail = float.NaN;
        private static bool _failed;

        // (Re)bake if never baked or the mist warp amplitude changed. ~2M texels, parallel across slices;
        // called from Plugin.Awake (prewarm — a mid-raid first bake would hitch a frame) and per render.
        public static void EnsureBaked(float mistDetail)
        {
            if (_failed || (Tex != null && Mathf.Approximately(_bakedMistDetail, mistDetail))) return;
            try
            {
                var sw = Stopwatch.StartNew();
                var pixels = new Color32[Size * Size * Size];
                float inv = Period / Size; // texel -> noise units
                Parallel.For(0, Size, z =>
                {
                    int zOff = z * Size * Size;
                    float pz = z * inv;
                    for (int y = 0; y < Size; y++)
                    {
                        int yOff = zOff + y * Size;
                        float py = y * inv;
                        for (int x = 0; x < Size; x++)
                        {
                            float px = x * inv;
                            byte r = (byte)(Mathf.Clamp01(Fbm(px, py, pz, 3, 0x1B873593u)) * 255f + 0.5f);
                            byte g = (byte)(Mathf.Clamp01(Mist(px, py, pz, mistDetail)) * 255f + 0.5f);
                            pixels[yOff + x] = new Color32(r, g, 0, 255);
                        }
                    }
                });
                if (Tex == null)
                    Tex = new Texture3D(Size, Size, Size, TextureFormat.RGBA32, mipChain: true)
                    {
                        wrapMode = TextureWrapMode.Repeat,
                        filterMode = FilterMode.Trilinear,
                        name = "FogSixNoise3D",
                    };
                Tex.SetPixels32(pixels);
                Tex.Apply(updateMipmaps: true);
                _bakedMistDetail = mistDetail;
                Plugin.MyLog.LogInfo($"[FogSix] baked {Size}^3 noise texture in {sw.ElapsedMilliseconds} ms (mist detail {mistDetail:0.00})");
            }
            catch (Exception e)
            {
                Tex = null;
                _failed = true; // renderer falls back to the procedural shader path; don't retry every frame
                Plugin.MyLog.LogError("[FogSix] noise texture bake failed, using procedural noise: " + e);
            }
        }

        private static float Hash(int x, int y, int z, uint seed)
        {
            uint n = (uint)x * 1597334677u ^ (uint)y * 3812015801u ^ (uint)z * 2798796415u ^ seed;
            n = (n ^ (n >> 16)) * 2246822519u;
            n ^= n >> 13;
            return (n >> 8) * (1f / 16777216f);
        }

        // Periodic 3D value noise — the C# twin of the shader's vnoise(), except the integer lattice wraps
        // to `cells` (a power of two, via & mask) so the field tiles. Coords are in cell units.
        private static float VNoise(float x, float y, float z, int cells, uint seed)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y), zi = Mathf.FloorToInt(z);
            float fx = x - xi, fy = y - yi, fz = z - zi;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            fz = fz * fz * (3f - 2f * fz);
            int m = cells - 1;
            int x0 = xi & m, x1 = (xi + 1) & m;
            int y0 = yi & m, y1 = (yi + 1) & m;
            int z0 = zi & m, z1 = (zi + 1) & m;
            float x00 = Mathf.Lerp(Hash(x0, y0, z0, seed), Hash(x1, y0, z0, seed), fx);
            float x10 = Mathf.Lerp(Hash(x0, y1, z0, seed), Hash(x1, y1, z0, seed), fx);
            float x01 = Mathf.Lerp(Hash(x0, y0, z1, seed), Hash(x1, y0, z1, seed), fx);
            float x11 = Mathf.Lerp(Hash(x0, y1, z1, seed), Hash(x1, y1, z1, seed), fx);
            return Mathf.Lerp(Mathf.Lerp(x00, x10, fy), Mathf.Lerp(x01, x11, fy), fz);
        }

        // Matches the shader's fbm(): amplitudes 0.5/0.25/..., octave ratio 2 (exact, so octaves co-tile).
        // Coords in noise units; at octave 0 one cell = one unit, like the shader's unit lattice.
        private static float Fbm(float x, float y, float z, int octaves, uint seed)
        {
            float a = 0.5f, s = 0f;
            int cells = (int)Period;
            for (int k = 0; k < octaves; k++)
            {
                s += a * VNoise(x, y, z, cells, seed + (uint)k * 0x9E3779B9u);
                x *= 2f; y *= 2f; z *= 2f;
                cells <<= 1;
                a *= 0.5f;
            }
            return s;
        }

        // Matches the shader's mistNoise(): a low-frequency domain warp (three independent periodic fields
        // stand in for the shader's offset trick) + 5 octaves.
        private static float Mist(float x, float y, float z, float detail)
        {
            float hx = x * 0.5f, hy = y * 0.5f, hz = z * 0.5f;
            float wx = VNoise(hx, hy, hz, 4, 0xA511E9B3u) - 0.5f;
            float wy = VNoise(hx, hy, hz, 4, 0x63D68D51u) - 0.5f;
            float wz = VNoise(hx, hy, hz, 4, 0x8F31DF69u) - 0.5f;
            return Fbm(x + wx * detail, y + wy * detail, z + wz * detail, 5, 0x85EBCA6Bu);
        }
    }
}
