using HarmonyLib;
using SPT.Reflection.Patching;
using System.Reflection;
using UnityEngine;
using FogSix.Source;

namespace FogSix.Patches
{
    // Tarkov's atmospheric scattering (TOD_Scattering) renders the screen-space fog. We hook its image
    // effect: when the mod is enabled we DISABLE it (return false) and draw our own volumetric fog in its
    // place, using TOD only as the live value source. OnRenderImageNormalMode runs at [ImageEffectOpaque]
    // (post-opaque, pre-resolve) with the depth texture available — the right place for depth-based fog.
    // (Like vanilla TOD fog, transparents draw after this, so glass/windows render over the fog.)
    internal class FogScatteringPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(TOD_Scattering), "OnRenderImageNormalMode");
        }

        // A "VR player" = the SPT-VR mod is loaded. Cached; the plugin list is fixed after startup.
        private static bool? _vrModLoaded;
        private static bool VrModLoaded
        {
            get
            {
                if (_vrModLoaded == null)
                    _vrModLoaded = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.matsix.sptvr");
                return _vrModLoaded.Value;
            }
        }

        [PatchPrefix]
        private static bool Prefix(TOD_Scattering __instance, RenderTexture source, RenderTexture destination)
        {
            if (!FogConfig.Enabled.Value)
            {
                // Disabled. In VR, TOD's scattering renders wrong in stereo, so kill it entirely (pass the
                // scene straight through, no fog) like the VR mod used to. In flatscreen vanilla TOD fog is
                // fine, so let it run.
                if (VrModLoaded)
                {
                    Graphics.Blit(source, destination);
                    return false;
                }
                return true;
            }

            // Enabled: draw our fog (Render returns false when it handled it, true to fall back to vanilla
            // if the bundle/camera isn't ready).
            return FogRenderer.Render(__instance, source, destination);
        }
    }
}
