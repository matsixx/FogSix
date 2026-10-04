using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace FogSix.Source
{
    // Volumetric glass: swaps FogSix's port of the game's window shader (FogSixGlass.shader) onto the window
    // materials, so windows fog from the froxel volume at their own depth instead of the game's analytic
    // vertex fog. The port keeps the game's property names, so material values carry over the swap.
    // Applied from the raid-load scans (FogRenderer.FixGlassQueues), restored on the config toggle.
    internal static class FogGlass
    {
        private const string GamePrefix = "Global Fog/";
        private const string OurPrefix = "FogSix/Glass/";
        private const string Canary = "_FogSixGlass";

        private static readonly Dictionary<string, Shader> _ours = new Dictionary<string, Shader>();   // suffix -> port
        private static readonly Dictionary<int, Shader> _orig = new Dictionary<int, Shader>();        // material id -> game shader
        private static readonly Dictionary<int, Material> _swapped = new Dictionary<int, Material>();
        private static bool _broken;
        private static int _histogramScene = -1;

        public static void TakeShaders(Shader[] shaders)
        {
            if (shaders == null) return;
            foreach (Shader s in shaders)
            {
                if (s == null || !s.name.StartsWith(OurPrefix)) continue;
                _ours[s.name.Substring(OurPrefix.Length)] = s;
            }
            if (_ours.Count > 0)
                Plugin.MyLog.LogInfo("[FogSix] Glass ports in bundle: " + string.Join(", ", _ours.Keys));
        }

        // Hit every material once per scan pass. `mats` is the scan FixGlassQueues already did.
        public static void Apply(Material[] mats)
        {
            if (mats == null) return;
            if (FogConfig.Debug.Value) LogHistogram(mats);
            if (!FogConfig.VolumetricGlass.Value) { RestoreAll(); return; }
            if (_broken || _ours.Count == 0) return;
            int swapped = 0;
            for (int i = 0; i < mats.Length; i++)
            {
                Material m = mats[i];
                if (m == null || m.shader == null) continue;
                string name = m.shader.name;
                if (!name.StartsWith(GamePrefix)) continue;
                if (!_ours.TryGetValue(name.Substring(GamePrefix.Length), out Shader port)) continue;
                int queue = m.renderQueue;   // the swap resets the queue to the shader's default
                Shader previous = m.shader;
                m.shader = port;
                m.renderQueue = queue;
                // A variant-stripped bundle shader has no native property data in the player (the POMSix
                // lesson): revert and stop, so a bad bundle fails safe to the game's glass.
                if (!m.HasProperty(Canary))
                {
                    m.shader = previous;
                    m.renderQueue = queue;
                    _broken = true;
                    Plugin.MyLog.LogError("[FogSix] Glass port '" + port.name + "' has no usable compiled data in-game "
                        + "(shader stripping at bundle build?). Windows keep the game's shader.");
                    RestoreAll();
                    return;
                }
                int id = m.GetInstanceID();
                _orig[id] = previous;
                _swapped[id] = m;
                swapped++;
            }
            if (swapped > 0)
                Plugin.MyLog.LogInfo("[FogSix] Volumetric glass shader swapped onto " + swapped + " materials (" + _swapped.Count + " total)");
        }

        public static void RestoreAll()
        {
            int n = 0;
            foreach (KeyValuePair<int, Material> kv in _swapped)
            {
                Material m = kv.Value;
                if (m == null || !_orig.TryGetValue(kv.Key, out Shader orig) || orig == null) continue;
                if (!m.shader.name.StartsWith(OurPrefix)) continue;
                int queue = m.renderQueue;
                m.shader = orig;
                m.renderQueue = queue;
                n++;
            }
            _swapped.Clear();
            _orig.Clear();
            if (n > 0) Plugin.MyLog.LogInfo("[FogSix] Restored the game's glass shader on " + n + " materials");
        }

        // Config toggle: re-run over the live materials.
        public static void OnToggle()
        {
            if (FogConfig.VolumetricGlass.Value) Apply(Resources.FindObjectsOfTypeAll<Material>());
            else RestoreAll();
        }

        // Which transparent shaders this map actually uses, once per scene — tells us what else needs a port.
        private static void LogHistogram(Material[] mats)
        {
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (scene == _histogramScene) return;
            _histogramScene = scene;
            var counts = new Dictionary<string, int>();
            for (int i = 0; i < mats.Length; i++)
            {
                Material m = mats[i];
                if (m == null || m.shader == null || m.renderQueue < 2500) continue;
                counts.TryGetValue(m.shader.name, out int c);
                counts[m.shader.name] = c + 1;
            }
            var list = new List<KeyValuePair<string, int>>(counts);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new StringBuilder("[FogSix] Transparent-queue shaders on this map (materials): ");
            int shown = 0;
            foreach (KeyValuePair<string, int> kv in list)
            {
                if (shown++ >= 30) { sb.Append("..."); break; }
                bool ported = kv.Key.StartsWith(GamePrefix) && _ours.ContainsKey(kv.Key.Substring(GamePrefix.Length));
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append(ported ? "[ported] " : " ");
            }
            Plugin.MyLog.LogInfo(sb.ToString());
        }

        // Called right after the froxel volume is built (FogFroxels.Render): hand the glass the integration
        // volume + the same distance/interior parameters the opaque apply pass uses. `integ` null = the
        // volume isn't available this render (per-slice froxels) -> the glass uses the game's analytic fog.
        public static void Publish(Material mat, RenderTexture integ)
        {
            bool on = integ != null && FogConfig.VolumetricGlass.Value;
            Shader.SetGlobalFloat("_FogSixGlassOn", on ? 1f : 0f);
            if (!on) return;
            Shader.SetGlobalTexture("_FogSixIntegVol", integ);
            Shader.SetGlobalVector("_FogSixGlassParams", new Vector4(
                mat.GetFloat("_FogStartDist"), mat.GetFloat("_FogMaxDist"),
                mat.GetFloat("_FogInteriorOn"), mat.GetFloat("_FogInteriorReduction")));
            Shader.SetGlobalVector("_FogSixGlassParams2", new Vector4(
                mat.GetFloat("_FogInteriorMaxDist"), mat.GetFloat("_FogInteriorInsideness"),
                mat.GetFloat("_FogInteriorSoftness"), mat.GetFloat("_FogInteriorChannel")));
        }

        // Any render that doesn't build the volume (legacy path, clear-weather early-out) must clear the flag,
        // or the glass would read a stale volume from an earlier camera/frame.
        public static void Off()
        {
            Shader.SetGlobalFloat("_FogSixGlassOn", 0f);
        }
    }
}
