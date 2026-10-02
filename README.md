# FogSix

A BepInEx plugin for **Single Player Tarkov (SPT)** that replaces Tarkov's screen-space atmospheric fog
(TOD_Scattering) with a custom **volumetric raymarched fog**. Flatscreen mod — the VR build lives in SPT-VR.
(Sibling to [CloudSix](../SPT-Cloud-Revamp); the repo folder is `SPT-VolumetricFog`, the mod is `FogSix`.)

It disables TOD's scattering and draws its own fog, reading the live values straight off the game:
- **Density** from the weather (`WeatherController.WeatherCurve.Fog`) — clear weather = no fog, foggy weather
  = thick. This is the *same* value SAIN/EFT use to reduce bot vision in fog, so the fog you see only appears
  when the AI's sight is already cut — it stays fair instead of fogging you in while bots see clear.
  (`TOD_Scattering.GlobalDensity` is pinned static in this build because MBOIT — the real volumetric fog — is
  disabled and never receives the weather value, so it can't be used as the source.)
- **Colour** from the sky (`SunSkyColor` by day / `MoonSkyColor` by night) — properly exposed, matches the
  horizon haze. (TOD's raw atmosphere/`SampleFogColor` values are tiny pre-exposure numbers that read grey.)
- **Wind** from `WeatherController.WeatherCurve.Wind` — the fog drifts with the weather.
- **Sun/moon glow direction** from TOD's `TOD_LightDirection` global.

The fog is a world-space raymarch with height falloff (thick low, thinning with altitude — clear straight
up, foggy toward the horizon), a 3-octave value-noise for wispiness, and an HG sun in-scatter glow.

## Install

1. Copy `FogSix.dll` to `BepInEx/plugins/FogSix/`.
2. Copy the `volfog` AssetBundle to `BepInEx/plugins/FogSix/Assets/volfog`.
3. Launch. Tune in the BepInEx config (`com.matsix.fogsix.cfg`) or a config manager.

## Build

### The plugin DLL
```sh
dotnet build -c Release
```
Output: `bin/Release/netstandard2.1/FogSix.dll`. References resolve from the local `libs/` folder (DLLs
copied from the game's `Managed` + BepInEx). If you prefer CloudSix's convention (referencing
`..\..\BepInEx` / `..\..\EscapeFromTarkov_Data`), swap the HintPaths in `FogSix.csproj` and place the
project inside the SPT install tree.

### The shader bundle (`volfog`)
The fog shader can't be compiled at runtime, so it ships as an AssetBundle:
1. In a Unity project, drop `Assets/VolumetricFog.shader` in.
2. Create a Material from it named **`volFogMat`**.
3. Assign the shader + material to an AssetBundle named **`volfog`** and build it.
4. Put the built `volfog` file in `BepInEx/plugins/FogSix/Assets/`.

`Assets/VolFogTester.cs` is an editor-only driver: attach it to a camera with the material assigned to
tune the look live in the Unity editor (drop in a Time of Day sky dome so the colours are live). It's the
same bundle SPT-VR uses, so a single shader build serves both mods.

## Layout

- **`Plugin.cs`** — BepInEx entry; binds config, enables the patch.
- **`Patches/FogScatteringPatch.cs`** — hooks `TOD_Scattering.OnRenderImageNormalMode` (SPT `ModulePatch`).
- **`Source/FogRenderer.cs`** — loads the bundle, reads TOD values, does the fog blit.
- **`Source/FogConfig.cs`** — BepInEx config.
- **`Assets/`** — the shader + editor tester (source for the bundle; not compiled into the DLL).
