using HarmonyLib;
using SPT.Reflection.Patching;
using System.Reflection;
using FogSix.Source;

namespace FogSix.Patches
{
    // FirearmController.Spawn fires whenever a weapon becomes a character's active hand controller — the
    // initial raid-start loadout, a weapon switch, and a re-equip after an in-raid mod change — for BOTH the
    // player and every bot. It's the same lifecycle hook the base SPT-VR mod already relies on elsewhere for
    // "this weapon is now equipped" (EmptyHandsController/KnifeController.Spawn). That makes it the one
    // reliable moment a weapon's tac-light/laser Light components are guaranteed to exist in its hierarchy,
    // so lights register themselves here instead of the fog periodically scanning the whole scene for them.
    internal class FogLightSpawnPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(EFT.Player.FirearmController), "Spawn");
        }

        [PatchPostfix]
        private static void Postfix(EFT.Player.FirearmController __instance)
        {
            FogLights.RegisterWeaponLights(__instance.WeaponRoot);
        }
    }
}
