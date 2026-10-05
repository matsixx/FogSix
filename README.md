# FogSix

Volumetric fog for Single Player Tarkov. FogSix replaces Tarkov's flat screen-space fog with real volumetric
fog that fills the world: it pools in valleys, glows around the sun, catches god rays through the trees and
lights up around your flashlight. Works on flatscreen and in VR.

Support my work on Ko-fi: https://ko-fi.com/matsix

The fog follows the game's own weather. It only gets thick when the weather calls for fog, which is the same
value the game and SAIN use to cut bot vision. By default your visibility in heavy fog roughly matches the
bots', so the fog stays fair.

Releases here include a server side mod I made specifically for FogSix: https://github.com/matsixx/SPT-DynamicFog

This is included because base SPT has fog pretty much disabled. You can use the base game fog if you re-enable it in the SPT config instead of using
this server side mod that's included. I would recommend using the mod though because it makes the fog a lot more dynamic and helps to make the fog shine more.

## Features

- Froxel based volumetric fog that is height aware
- Fog reduction/removal in interiors
- Comes with a server mod that controls fog and makes it more dynamic to work better and make the fog stand out how it should (probably still needs tweaking, you can adjust probabilities in the config.json)
- Connected to SPT's weather so it follows base game fog density and wind
- Fog color shaded based on sky/sun/moon color
- God rays with dust/mist in the shafts when there is fog
- Lights light up the fog, so flashlights will project a cone in fog, same for any other lights around the world
- Glass/transparents are properly occluded by fog
- Compatible with VR

## Requirements

- SPT 4.0 or 4.1.

## Install

1. Extract the release into your SPT folder. You should end up with:
   ```
   BepInEx/plugins/FogSix/FogSix.dll
   BepInEx/plugins/FogSix/Assets/volfog
   ```
2. Launch the game.

## Settings

Open the in-game configuration manager (F12) or edit `BepInEx/config/com.matsix.fogsix.cfg`.
These are the main ones. The Advanced view has many more.

| Setting | Default | What it does |
|---|---|---|
| Enabled | On | Off restores Tarkov's fog on flatscreen. In VR it removes the fog. |
| Density Scale | 0.03 | How thick the fog gets at full fog. Raising it makes fog thicker than what the bots experience. |
| Ground Fog | On | Anchors the fog layer to the map's ground. Off makes it follow your height. |
| Height Falloff | 0.04 | How quickly the fog thins with altitude. Smaller makes a taller layer. |
| Height Offset | 0 | Raises or lowers the fog layer. |
| Wispiness | 1 | 0 is a uniform haze, 1 is fully wispy. |
| Sun Glow / Moon Glow | 1 / 1 | Strength of the glow around the sun and moon. |
| Ground Mist | Off | The low mist layer, with its own height and density settings. |
| Local Lights | On | Lights glowing in the fog. |
| Max Lights | 24 | How many lights can glow at once. The most important nearby lights win. |
| Sun Shadows | On | God rays. |
| Interior Fog Thinning | On | Thins the fog indoors. |
| Interior Reduction | 0.8 | How much it thins. 1 is fully clear indoors. |
| Volumetric Glass | On | Windows fog from the volumetric fog. |
| Max Distance | 2000 | How far the fog reaches, in metres. |
| Froxel Resolution | High | Detail of the fog volume. Medium is usually plenty. |
| Temporal Smoothing | 0.95 | Frame-to-frame smoothing. 0.9 to 0.95 is recommended. |

## Performance

The fog volume is small, so the cost is mostly fixed. If you need frames back:

- Set **Froxel Resolution** to Medium or Low.
- Lower **Max Lights**, which matters most on light-heavy maps like Streets.
- Turn off **Sun Shadows**.

## Compatibility

- **SAIN:** the default density is matched to SAIN's fog penalty on bot vision.
- **SPT-VR:** FogSix is the fog SPT-VR uses.

## Building from source

```sh
dotnet build FogSix.csproj -c Release
```

The project references the game's DLLs from a local `libs/` folder that isn't in the repository. Copy these
into it from your SPT install:

- From `EscapeFromTarkov_Data/Managed`: `Assembly-CSharp.dll`, `Comfort.dll`, `Comfort.Unity.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `UnityEngine.AssetBundleModule.dll`
- From `BepInEx/core`: `0Harmony.dll`, `BepInEx.dll`
- From `BepInEx/plugins/spt`: `spt-reflection.dll`

The fog and glass shaders ship compiled in the `volfog` bundle in each release. Their source isn't part of
this repository.

## Credits

- The froxel technique follows Sébastien Hillaire's "Physically Based and Unified Volumetric Rendering in Frostbite" (2015).

## License

The code in this repository is licensed under the GNU General Public License v3.0. See `LICENSE.txt`.
