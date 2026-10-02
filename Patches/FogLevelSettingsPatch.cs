using HarmonyLib;
using SPT.Reflection.Patching;
using System.Reflection;
using FogSix.Source;

namespace FogSix.Patches
{
    // Captures the map's LevelSettings values (ZeroLevel/HeightFalloff for world-anchored Ground Fog + the glass
    // _Density) from its own lifecycle. SPT 4.1 DESTROYS the map's LevelSettings right after raid start
    // (log-confirmed), so no object lookup works mid-raid — FindObjectOfType, Singleton<LevelSettings> (whose
    // Release also clears unconditionally) — and the fog fell back to the camera height ("Ground Fog follows my
    // height"). FogRenderer keeps the captured values after OnDestroy.
    internal class FogLevelSettingsAwakePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(LevelSettings), "Awake");
        }

        [PatchPostfix]
        private static void Postfix(LevelSettings __instance)
        {
            FogRenderer.SetLevel(__instance);
        }
    }

    internal class FogLevelSettingsDestroyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(LevelSettings), "OnDestroy");
        }

        [PatchPostfix]
        private static void Postfix(LevelSettings __instance)
        {
            FogRenderer.ClearLevel(__instance);
        }
    }
}
