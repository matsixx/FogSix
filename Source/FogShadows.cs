using UnityEngine;
using UnityEngine.Rendering;

namespace FogSix.Source
{
    // Captures the SUN's cascade shadow map into a global texture (_FogSunShadowMap) so the fog shader can
    // sample it per march step for god rays. This replicates the game's own ShadowDepthMapToGlobal (a command
    // buffer on the directional light at AfterShadowMap binding BuiltinRenderTextureType.CurrentActive), so we
    // don't depend on whether BSG's dormant VolumetricLight is active. The matrices to sample it with are
    // Unity's own global cascade vars (unity_WorldToShadow etc.), declared in the shader.
    internal static class FogShadows
    {
        private static Light _sun;
        private static CommandBuffer _cmd;

        // True while a shadow-casting directional light (sun OR moon) is attached and bound. The fog skips
        // shadowing when false, so a moon with no shadow map doesn't sample a stale (daytime sun) map.
        public static bool Active { get; private set; }

        private static bool _bootstrapped;

        public static void Update(TOD_Sky sky)
        {
            if (!FogConfig.SunShadows.Value) { Detach(); Active = false; return; }

            // The sun is scanned once at raid load (Plugin) — never a recurring timer. TOD reuses the SAME
            // directional light for sun<->moon (just repoints it), so a single find stays valid all raid.
            // Bootstrap scan ONCE, in case Update() runs before the load scan. (It used to be `if (_sun == null)
            // Rescan()`, which re-ran a whole-scene FindObjectsOfType on EVERY render whenever no shadow-casting
            // directional light existed — e.g. a night raid with a shadowless moon.)
            if (_sun == null && !_bootstrapped) { _bootstrapped = true; Rescan(); }

            // Still none: adopt TOD's own directional light the moment it casts shadows (dawn during a night
            // raid) — a component reference, no scan.
            if (_sun == null && sky != null && sky.Components != null)
            {
                Light l = sky.Components.LightSource;
                if (l != null && l.type == LightType.Directional && l.shadows != LightShadows.None)
                {
                    Detach();
                    _sun = l;
                    Attach();
                }
            }
            Active = _sun != null;
        }

        // Heavy scan (FindObjectsOfType) — called once at raid load (Plugin). NOT periodic.
        public static void Rescan()
        {
            Light found = FindSun();
            if (found != _sun) { Detach(); _sun = found; Attach(); }
        }

        private static Light FindSun()
        {
            Light best = null;
            foreach (Light l in Object.FindObjectsOfType<Light>())
            {
                if (l == null || l.type != LightType.Directional || !l.isActiveAndEnabled) continue;
                if (l.shadows == LightShadows.None) continue;        // must cast shadows to have a shadow map
                if (best == null || l.intensity > best.intensity) best = l;
            }
            return best;
        }

        private static void Attach()
        {
            if (_sun == null) return;
            _cmd = new CommandBuffer { name = "FogSix SunShadowMap" };
            _cmd.SetGlobalTexture("_FogSunShadowMap", BuiltinRenderTextureType.CurrentActive);
            _sun.AddCommandBuffer(LightEvent.AfterShadowMap, _cmd);
        }

        private static void Detach()
        {
            if (_sun != null && _cmd != null) // Unity != null is false for a destroyed light
                _sun.RemoveCommandBuffer(LightEvent.AfterShadowMap, _cmd);
            _cmd = null;
        }
    }
}
