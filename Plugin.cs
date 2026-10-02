using BepInEx;
using BepInEx.Logging;
using FogSix.Patches;
using FogSix.Source;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FogSix
{
    [BepInPlugin("com.matsix.fogsix", "FogSix", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource MyLog;

        private void Awake()
        {
            MyLog = Logger;
            MyLog.LogInfo("FogSix loaded!");

            FogConfig.Bind(Config);
            // Prewarm the noise-texture bake during startup — a first-use bake would hitch a frame mid-raid
            // the moment the weather turns foggy.
            if (FogConfig.BakedNoise.Value)
                FogNoiseTex.EnsureBaked(FogConfig.MistDetail.Value);
            new FogScatteringPatch().Enable();
            new FogLightSpawnPatch().Enable();
            new FogLevelSettingsAwakePatch().Enable();
            new FogLevelSettingsDestroyPatch().Enable();

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        // At raid LOAD, do the heavy scans (glass render queues, static map lights, the sun, the IndoorTrigger
        // shelter boxes) a few times over the first seconds — the map streams in after the scene "loads", so a
        // few passes catch late arrivals. This is the ONLY FindObjectsOfType scanning FogSix ever does — no
        // periodic re-scan runs during the raid. Dynamic per-weapon tac-lights (incl. bots that spawn
        // mid-raid) don't need one either: they self-register the moment their weapon is equipped
        // (FogLightSpawnPatch). Harmless on non-raid scenes.
        private Coroutine _loadScan;
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_loadScan != null) StopCoroutine(_loadScan);
            _loadScan = StartCoroutine(RaidLoadScans());
        }

        private IEnumerator RaidLoadScans()
        {
            float[] delays = { 0f, 3f, 8f, 15f }; // ~0/3/11/26s after load, then done
            foreach (float d in delays)
            {
                if (d > 0f) yield return new WaitForSeconds(d);
                FogRenderer.FixGlassQueues();
                FogLights.Rescan();
                FogShadows.Rescan();
                FogInteriors.Rescan();
            }
            _loadScan = null;
        }
    }
}
