using EFT.EnvironmentEffect;
using System.Collections.Generic;
using UnityEngine;

namespace FogSix.Source
{
    // Thins the fog inside buildings. Two cooperating models:
    //
    //  1. SHELTER VOLUMES (primary): the game's IndoorTrigger boxes (its rain/exposure/reverb shelters) are
    //     oriented unit cubes (Check() = |InverseTransformPoint| < 0.5). We upload the nearest MAXVOL as
    //     worldToLocal matrices; the shader intersects each view ray with them ONCE and reduces the fog
    //     DENSITY along the inside t-spans of the march. Per-SEGMENT clearing is correct from every
    //     viewpoint: a big room clears to its far wall, a window seen from outside keeps the outdoor fog in
    //     front of it (and shows clear air behind the glass), another building through a window keeps the
    //     fog between you and it. None of that is expressible in the per-pixel model below.
    //
    //  2. STENCIL PIXELS (fallback): IndoorTriggers don't cover every interior, so pixels whose END surface
    //     the game's per-pixel interior mask (_StencilShadow) marks as interior — and which no volume
    //     already handled — keep the old model: clear if the surface is within Interior Max Clear Distance.
    //     Same tradeoffs as before, now confined to the trigger-coverage gaps.
    internal static class FogInteriors
    {
        private const int MAXVOL = 16;    // must match FOG_MAX_VOLS in the shader
        private const float RANGE = 200f; // ignore shelter volumes farther than this (fog hides them anyway)

        private static readonly Matrix4x4[] _w2l = new Matrix4x4[MAXVOL];
        private static readonly IndoorTrigger[] _best = new IndoorTrigger[MAXVOL];
        private static readonly float[] _bestD = new float[MAXVOL];
        private static readonly List<IndoorTrigger> _triggers = new List<IndoorTrigger>();
        private static bool _scanned;

        // One-time bootstrap at raid load (Plugin's load scan) — the triggers are static scene objects.
        public static void Rescan()
        {
            _scanned = true;
            _triggers.Clear();
            foreach (IndoorTrigger t in Object.FindObjectsOfType<IndoorTrigger>())
            {
                if (t == null) continue;
                if (t.Bounds.size.magnitude < Mathf.Epsilon) t.Reinit(); // bounds not built until its Awake
                _triggers.Add(t);
            }
        }

        private static int _frame = -1;

        public static void Update(Material mat, Camera cam)
        {
            // Once per frame: the volumes are world-space and the material is shared, so the second VR eye
            // and the scope camera reuse the first render's upload instead of re-walking every trigger.
            if (Time.frameCount == _frame) return;
            _frame = Time.frameCount;

            bool on = FogConfig.InteriorFog.Value;
            mat.SetFloat("_FogInteriorOn", on ? 1f : 0f);
            if (!on)
            {
                mat.SetFloat("_FogInteriorDebug", 0f);
                mat.SetFloat("_FogVolDebug", 0f);
                mat.SetInt("_FogVolCount", 0);
                mat.SetFloat("_FogCoverReduction", 0f);
                return;
            }

            mat.SetFloat("_FogInteriorReduction", FogConfig.InteriorFogReduction.Value);
            mat.SetFloat("_FogInteriorInsideness", 1f); // fallback model: no camera-inside gate
            mat.SetFloat("_FogInteriorMaxDist", Mathf.Max(1f, FogConfig.InteriorMaxClearDistance.Value));
            mat.SetFloat("_FogInteriorSoftness", FogConfig.InteriorSoftness.Value);
            mat.SetFloat("_FogInteriorChannel", FogConfig.InteriorChannel.Value);
            mat.SetFloat("_FogInteriorDebug", FogConfig.InteriorDebug.Value ? 1f : 0f);

            mat.SetInt("_FogVolCount", SelectVolumes(cam));
            mat.SetMatrixArray("_FogVolW2L", _w2l);
            mat.SetFloat("_FogVolPad", FogConfig.InteriorVolumePadding.Value);
            mat.SetFloat("_FogVolDebug", FogConfig.InteriorVolumeDebug.Value ? 1f : 0f);

            // Rain-cover shelter: sample the game's rain-occlusion depth map in the march. Only when the map
            // was actually photographed this raid (DepthPhotograper binds the global _WeatherDepthMap once at
            // load) — an unbound global samples garbage, so gate hard when it's absent.
            float coverRed = (DepthPhotograper.Instance != null) ? FogConfig.RainCoverReduction.Value : 0f;
            mat.SetFloat("_FogCoverReduction", coverRed);
            mat.SetFloat("_FogCoverFlip", FogConfig.RainCoverFlip.Value ? 1f : 0f);
            mat.SetFloat("_FogCoverBias", FogConfig.RainCoverBias.Value);
        }

        // Keep the MAXVOL nearest shelter volumes, by distance to their world AABB (0 when inside one).
        private static int SelectVolumes(Camera cam)
        {
            if (!FogConfig.InteriorVolumes.Value) return 0;
            if (!_scanned) Rescan(); // bootstrap fallback only; Plugin's raid-load scan does this normally

            Vector3 camPos = cam.transform.position;
            int count = 0;
            foreach (IndoorTrigger t in _triggers)
            {
                if (t == null) continue; // destroyed (scene tear-down)
                float d2 = t.Bounds.SqrDistance(camPos);
                if (d2 > RANGE * RANGE) continue;

                if (count < MAXVOL) { _best[count] = t; _bestD[count] = d2; count++; }
                else
                {
                    int worst = 0;
                    for (int k = 1; k < MAXVOL; k++) if (_bestD[k] > _bestD[worst]) worst = k;
                    if (d2 < _bestD[worst]) { _best[worst] = t; _bestD[worst] = d2; }
                }
            }
            for (int i = 0; i < count; i++)
                _w2l[i] = _best[i].transform.worldToLocalMatrix;
            return count;
        }
    }
}
