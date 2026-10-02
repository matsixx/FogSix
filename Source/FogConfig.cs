using BepInEx;
using BepInEx.Configuration;

namespace FogSix.Source
{
    internal sealed class ConfigurationManagerAttributes { public bool? IsAdvanced; }

    internal enum EFroxelRes { Low, Medium, High }   // froxel grid: 128x72 / 160x92 / 240x136

    internal static class FogConfig
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> Debug;

        // Look
        public static ConfigEntry<float> DensityScale;
        public static ConfigEntry<bool> GroundFog;
        public static ConfigEntry<float> HeightOffset;
        public static ConfigEntry<float> HeightFalloff;
        public static ConfigEntry<float> NoiseScale;
        public static ConfigEntry<float> NoiseStrength;
        public static ConfigEntry<float> SunGlow;
        public static ConfigEntry<float> MoonGlow;
        public static ConfigEntry<bool> AtmosphereSunColor;
        public static ConfigEntry<float> SunColorWarmth;
        public static ConfigEntry<float> BaseWarmth;
        public static ConfigEntry<float> Anisotropy;
        public static ConfigEntry<float> WindSpeed;

        // Ground mist
        public static ConfigEntry<bool> GroundMist;
        public static ConfigEntry<float> MistDensity;
        public static ConfigEntry<float> MistHeight;
        public static ConfigEntry<float> MistThickness;
        public static ConfigEntry<float> MistNoiseScale;
        public static ConfigEntry<float> MistContrast;
        public static ConfigEntry<float> MistDetail;

        // Local lights
        public static ConfigEntry<bool> LocalLights;
        public static ConfigEntry<float> LocalLightScatter;
        public static ConfigEntry<float> LocalLightRange;
        public static ConfigEntry<float> LocalLightFocus;
        public static ConfigEntry<float> LocalLightFalloff;
        public static ConfigEntry<float> LocalLightConeSoftness;
        public static ConfigEntry<int> MaxLights;

        // Sun shadows (god rays)
        public static ConfigEntry<bool> SunShadows;
        public static ConfigEntry<float> SunShadowStrength;
        public static ConfigEntry<float> SunShadowBias;
        public static ConfigEntry<bool> SunShadowFlip;
        public static ConfigEntry<float> SunShadowDistance;
        public static ConfigEntry<float> SunShadowSoftness;
        public static ConfigEntry<float> ShaftDust;
        public static ConfigEntry<float> ShaftDustScale;
        public static ConfigEntry<float> DustDistance;

        // Interior fog
        public static ConfigEntry<bool> InteriorFog;
        public static ConfigEntry<float> InteriorFogReduction;
        public static ConfigEntry<bool> InteriorVolumes;
        public static ConfigEntry<float> InteriorVolumePadding;
        public static ConfigEntry<bool> InteriorVolumeDebug;
        public static ConfigEntry<float> RainCoverReduction;
        public static ConfigEntry<bool> RainCoverFlip;
        public static ConfigEntry<float> RainCoverBias;
        public static ConfigEntry<float> InteriorMaxClearDistance;
        public static ConfigEntry<float> InteriorSoftness;
        public static ConfigEntry<bool> BakedNoise;
        public static ConfigEntry<bool> HalfResMarch;
        public static ConfigEntry<int> InteriorChannel;
        public static ConfigEntry<bool> InteriorDebug;

        public static ConfigEntry<bool> TransparentFog;
        public static ConfigEntry<float> TransparentFogScale;
        public static ConfigEntry<float> TransparentFogBrightness;
        public static ConfigEntry<bool> VolumetricGlass;

        public static ConfigEntry<float> FogFullAt;

        // Range / performance
        public static ConfigEntry<float> StartDist;
        public static ConfigEntry<float> MaxDist;
        public static ConfigEntry<int> Steps;
        public static ConfigEntry<float> StepJitter;
        public static ConfigEntry<float> WispFadeDistance;
        public static ConfigEntry<float> TemporalSmoothing;

        // Froxel mode (the Frostbite architecture)
        public static ConfigEntry<bool> FroxelMode;
        public static ConfigEntry<EFroxelRes> FroxelRes;
        public static ConfigEntry<int> FroxelSlices;
        public static ConfigEntry<float> FroxelFar;

        private static ConfigDescription Adv(string desc, AcceptableValueBase range = null)
        {
            return new ConfigDescription(desc, range, new ConfigurationManagerAttributes { IsAdvanced = true });
        }

        public static void Bind(ConfigFile config)
        {
            // Wipe the config on a version change (mirrors CloudSix) so renamed/retuned keys don't linger.
            string currentVersion = MetadataHelper.GetMetadata(typeof(Plugin)).Version.ToString();
            var version = config.Bind("Internal", "ConfigVersion", "", "Do not modify");
            if (version == null || version.Value != currentVersion)
            {
                config.Clear();
                System.IO.File.WriteAllText(config.ConfigFilePath, "");
                config.Reload();
                version = config.Bind("Internal", "ConfigVersion", currentVersion, "Do not modify");
                version.Value = currentVersion;
                config.Save();
                Plugin.MyLog.LogInfo($"Config reset for version {currentVersion}");
            }

            Enabled = config.Bind("General", "Enabled", true,
                "Replace Tarkov's fog with volumetric fog. Off: flatscreen restores Tarkov's vanilla fog; " +
                "in VR (SPT-VR loaded) it disables Tarkov's fog entirely instead, since it renders wrong in stereo.");
            Debug = config.Bind("General", "Debug Log", false,
                Adv("Log the live weather Fog value + computed density and the light/fog CPU timings once a second."));

            DensityScale = config.Bind("Look", "Density Scale", 0.03f,
                new ConfigDescription("Fog extinction at FULL fog (when WeatherCurve.Fog reaches 'Full Fog At'). " +
                    "~0.03 cuts player visibility to roughly what SAIN cuts bot vision to in heavy fog (~40%), so " +
                    "it stays fair; raise for thicker fog (but then you see less far than the bots do).",
                    new AcceptableValueRange<float>(0f, 0.2f)));
            FogFullAt = config.Bind("Look", "Full Fog At", 0.075f,
                Adv("The WeatherCurve.Fog value treated as FULL (max-density) fog. 0.075 = Tarkov's 'Heavy fog', " +
                    "so the in-game weather settings actually scale the fog (Fog lighter, Heavy fog full), with " +
                    "Heavy fog matched to SAIN's max bot-vision penalty. Drop it to 0.018 (SAIN's cap) for strict " +
                    "fairness at every fog level — but then 'Fog' and 'Heavy fog' look identical (both already " +
                    "max the bots' penalty, so the visual can't tell them apart).",
                    new AcceptableValueRange<float>(0.001f, 0.2f)));
            GroundFog = config.Bind("Look", "Ground Fog", true,
                new ConfigDescription("World-anchored ground fog: the dense layer sits at the map's ground level " +
                    "and thins with ALTITUDE, so it pools in valleys/trees and you can climb hills or buildings " +
                    "above it and look down on it. Off = the fog follows your camera height (always around you, " +
                    "can't get above it). When on, use Height Offset to position the layer and Height Falloff for " +
                    "its thickness.", null));
            HeightFalloff = config.Bind("Look", "Height Falloff", 0.04f,
                new ConfigDescription("How fast the fog thins with altitude. Smaller = taller fog layer (reaches " +
                    "higher). With Ground Fog on, this is the layer's thickness — lower it if fog is too thin at " +
                    "head height, raise it to escape the fog at a lower altitude.",
                    new AcceptableValueRange<float>(0f, 0.3f)));
            HeightOffset = config.Bind("Look", "Height Offset", 0f,
                new ConfigDescription("Raises (+) / lowers (-) the full-density fog floor. Relative to the camera " +
                    "normally; relative to the map's ground level (LevelSettings.ZeroLevel) when Ground Fog is on " +
                    "— raise it until fog is dense at your feet, then hills above that poke out of the fog.",
                    new AcceptableValueRange<float>(-300f, 300f)));
            NoiseStrength = config.Bind("Look", "Wispiness", 1f,
                new ConfigDescription("0 = uniform haze, 1 = fully wispy fog.",
                    new AcceptableValueRange<float>(0f, 1f)));
            NoiseScale = config.Bind("Look", "Noise Scale", 0.052f,
                Adv("Size of the wisps (smaller = larger features).",
                    new AcceptableValueRange<float>(0.001f, 0.1f)));
            SunGlow = config.Bind("Look", "Sun Glow", 1f,
                new ConfigDescription("Strength of the sun's glow through the fog by day (0 = none).",
                    new AcceptableValueRange<float>(0f, 4f)));
            MoonGlow = config.Bind("Look", "Moon Glow", 1f,
                new ConfigDescription("Strength of the moon's glow through the fog at night (0 = none).",
                    new AcceptableValueRange<float>(0f, 4f)));
            AtmosphereSunColor = config.Bind("Look", "Atmospheric Sun Color", true,
                "Colour the sun's glow through the fog from a physically-based atmosphere (white sun reddened " +
                "along the sun ray) instead of Tarkov's TOD colours — matches the sun on the CloudSix clouds. " +
                "On = physical; off = the old TOD colour.");
            SunColorWarmth = config.Bind("Look", "Sun Color Warmth", 1f,
                Adv("How red the fog's sun glow goes at sunrise/sunset. 1 = orange (default); lower = more " +
                    "neutral; higher = deeper red. Only when 'Atmospheric Sun Color' is on.",
                    new AcceptableValueRange<float>(0f, 2f)));
            BaseWarmth = config.Bind("Look", "Base Warmth", 0.4f,
                Adv("How much the base fog haze warms toward the sunset colour near the horizon (golden hour), " +
                    "so the distant fog doesn't read a clinical white while the sky and clouds go gold. " +
                    "0 = flat neutral base; higher = warmer. Uses 'Sun Color Warmth' for how warm.",
                    new AcceptableValueRange<float>(0f, 1f)));
            Anisotropy = config.Bind("Look", "Sun Glow Focus", 0.7f,
                Adv("How tightly the glow concentrates toward the sun.",
                    new AcceptableValueRange<float>(0f, 0.9f)));
            WindSpeed = config.Bind("Look", "Wind Speed", 10f,
                new ConfigDescription("Multiplier on the game's live wind for fog drift.",
                    new AcceptableValueRange<float>(0f, 50f)));

            GroundMist = config.Bind("Ground Mist", "Ground Mist", false,
                "A thin, wispy mist hugging the ground that drifts between the trees — a separate sharp low " +
                "layer on top of the main height fog. Anchored to the map's ground (use Mist Height to sit it " +
                "on the terrain you walk on).");
            MistDensity = config.Bind("Ground Mist", "Mist Density", 0.05f,
                new ConfigDescription("How thick the ground mist is. Raise for soupier mist.",
                    new AcceptableValueRange<float>(0f, 1f)));
            MistHeight = config.Bind("Ground Mist", "Mist Height", -10f,
                new ConfigDescription("World height of the mist relative to the map's ground level. Raise/lower " +
                    "until the mist sits at your feet (the ground reference is often below play height).",
                    new AcceptableValueRange<float>(-300f, 300f)));
            MistThickness = config.Bind("Ground Mist", "Mist Thickness", 3f,
                new ConfigDescription("How tall the mist band is, in metres — it fades out above this. Small = " +
                    "low-lying ground mist; larger = a deeper bank.",
                    new AcceptableValueRange<float>(0.5f, 30f)));
            MistNoiseScale = config.Bind("Ground Mist", "Mist Wisp Size", 0.05f,
                Adv("Size of the wisps. Smaller value = bigger, smoother patches.",
                    new AcceptableValueRange<float>(0.005f, 0.2f)));
            MistContrast = config.Bind("Ground Mist", "Mist Patchiness", 3f,
                new ConfigDescription("Higher = patchier, with clearer gaps between the wisps; lower = more even.",
                    new AcceptableValueRange<float>(1f, 6f)));
            MistDetail = config.Bind("Ground Mist", "Mist Detail", 0.8f,
                new ConfigDescription("Turbulence/detail of the wisps. 0 = smooth round blobs; higher = swirly, " +
                    "organic, wispy structure inside each clump.", new AcceptableValueRange<float>(0f, 3f)));

            LocalLights = config.Bind("Lights", "Local Lights", true,
                "Let realtime point/spot lights (your flashlight, lamps, flares) brighten and beam through the " +
                "fog. No shadows, so light passes through walls (a known limitation for world lights).");
            LocalLightScatter = config.Bind("Lights", "Light Brightness", 1.5f,
                new ConfigDescription("How strongly local lights light up the fog. Raise for brighter beams/glow.",
                    new AcceptableValueRange<float>(0f, 2f)));
            LocalLightRange = config.Bind("Lights", "Light Range", 1f,
                new ConfigDescription("Multiplier on each light's reach into the fog (how far the glow extends).",
                    new AcceptableValueRange<float>(0.2f, 3f)));
            LocalLightFocus = config.Bind("Lights", "Light Focus", 0.5f,
                new ConfigDescription("How forward-scattering local lights are. HIGH = a tight beam that only " +
                    "glows when you look into it and barely veils objects seen through it from the side; LOW = " +
                    "a broad, hazy glow that washes the background. Raise this if distant objects light up when " +
                    "you look through a beam.", new AcceptableValueRange<float>(0f, 0.9f)));
            LocalLightFalloff = config.Bind("Lights", "Light Falloff", 25f,
                new ConfigDescription("How fast a light's glow dims away from the bulb. HIGH = realistic " +
                    "inverse-square look: bright core, fades fast, no visible boundary. LOW = the glow stays " +
                    "strong out to its full range — but against the sky the range boundary can read as a soft " +
                    "disc/circle around the light. Lower it if lights feel too dim after this change (and/or " +
                    "raise Light Brightness).", new AcceptableValueRange<float>(0f, 100f)));
            LocalLightConeSoftness = config.Bind("Lights", "Light Edge Softness", 0f,
                new ConfigDescription("Feathers the edge of spotlight beams in the fog: brightness ramps in " +
                    "from the cone edge toward the beam core instead of cutting at the surface. 0 = hard-edged " +
                    "cone (sharpest, and skips the extra math entirely).",
                    new AcceptableValueRange<float>(0f, 1f)));
            MaxLights = config.Bind("Lights", "Max Lights", 24,
                new ConfigDescription("How many lights can glow in the fog at once (the most important nearby ones " +
                    "win). Higher = farther lamps glow too on light-dense maps like Streets; costs GPU per light.",
                    new AcceptableValueRange<int>(1, FogLights.MAX)));

            SunShadows = config.Bind("Sun Shadows", "Sun Shadows", true,
                "EXPERIMENTAL god rays: shadow the sun's glow in the fog where trees/buildings block it, so you " +
                "get shafts of light. Samples the sun's shadow map. Off by default while it's being dialed in.");
            SunShadowStrength = config.Bind("Sun Shadows", "Shadow Strength", 1f,
                new ConfigDescription("How dark the shadowed shafts are. 0 = no shadowing, 1 = full.",
                    new AcceptableValueRange<float>(0f, 1f)));
            SunShadowBias = config.Bind("Sun Shadows", "Shadow Bias", 0.002f,
                new ConfigDescription("Depth bias to kill shadow acne (self-shadowing speckle). Raise if the fog " +
                    "is speckled with shadow noise; lower if shafts look detached from the trees.",
                    new AcceptableValueRange<float>(-0.05f, 0.05f)));
            SunShadowFlip = config.Bind("Sun Shadows", "Flip Shadow", true,
                "Flips the shadow depth comparison for the platform's Z convention. ON is correct on this build " +
                "(reversed-Z); untick only if shafts come out inverted (lit where it should be dark).");
            SunShadowDistance = config.Bind("Sun Shadows", "Shadow Distance", 300f,
                new ConfigDescription("How far the god rays reach (metres). The sun's shadow map only has good " +
                    "data near the camera; beyond this the fog is just lit (no shadow). LOWER this if you see a " +
                    "soft circle/disc in the distance — that's the shadow map going unreliable past its range.",
                    new AcceptableValueRange<float>(10f, 300f)));
            SunShadowSoftness = config.Bind("Sun Shadows", "Shadow Softness", 0.002f,
                new ConfigDescription("Blurs the shadow shafts (PCF). Higher = softer, hazier shafts; 0 = razor " +
                    "sharp (every leaf visible). Real god rays are soft, so a little goes a long way.",
                    new AcceptableValueRange<float>(0f, 0.01f)));
            ShaftDust = config.Bind("Sun Shadows", "Shaft Dust", 3f,
                new ConfigDescription("Dust motes drifting/glowing inside the sun shafts. 0 = off. Raise for a " +
                    "dustier, particle-filled beam.", new AcceptableValueRange<float>(0f, 3f)));
            ShaftDustScale = config.Bind("Sun Shadows", "Dust Grain Size", 1.5f,
                new ConfigDescription("Size of the dust specks. Higher = finer grain — but too fine flickers " +
                    "(the fog is only sampled every metre or so).", new AcceptableValueRange<float>(0.05f, 1.5f)));
            DustDistance = config.Bind("Sun Shadows", "Dust Distance", 40f,
                new ConfigDescription("How far from you the dust motes show (m): full strength to half this " +
                    "distance, gone by it. Farther out the fog cells get too coarse for motes and the dust just " +
                    "reads as shimmer — lower this if it does.", new AcceptableValueRange<float>(5f, 150f)));

            InteriorFog = config.Bind("Interiors", "Interior Fog Thinning", true,
                "Thin the fog inside buildings. Primary: the game's own indoor shelter volumes, cleared exactly " +
                "along each view ray (correct through windows, any room size). Fallback for interiors those " +
                "don't cover: Tarkov's per-pixel interior mask, distance-gated. Off = fog fills interiors.");
            InteriorFogReduction = config.Bind("Interiors", "Interior Reduction", 0.8f,
                new ConfigDescription("How much to thin the fog inside buildings. 0 = no change, 1 = fully clear " +
                    "indoors.", new AcceptableValueRange<float>(0f, 1f)));
            InteriorVolumes = config.Bind("Interiors", "Interior Shelter Volumes", true,
                "Clear fog along the exact indoor SEGMENTS of each view ray, using the game's IndoorTrigger " +
                "shelter boxes (what it uses for rain/reverb indoors). Fixes the per-pixel model's failure " +
                "cases: big rooms clear to the far wall, and looking in a window no longer punches a hole in " +
                "the outdoor fog. Off = the old per-pixel model everywhere.");
            InteriorVolumePadding = config.Bind("Interiors", "Interior Volume Padding", 1.015f,
                Adv("Scales the shelter boxes. Below 1 shrinks them (a film of fog appears at walls/ceilings if " +
                    "a box under-fills its room); above 1 grows them (clearing can leak just outside doorways " +
                    "and windows). 1 = the game's exact boxes.", new AcceptableValueRange<float>(0.5f, 1.5f)));
            InteriorVolumeDebug = config.Bind("Interiors", "Interior Volume Debug View", false,
                Adv("Recon overlay: GREEN where the view ray passes through a shelter volume (brighter = more " +
                    "of the ray covered), RED where the game's pixel mask marks interior, BLUE where the " +
                    "surface is under the map's rain cover. Red-ONLY rooms have no volumetric signal at all " +
                    "(pixel fallback). Needs Interior Fog Thinning on. Turn off after checking."));
            RainCoverReduction = config.Bind("Interiors", "Rain-Cover Reduction", 0.4f,
                new ConfigDescription("Thin the fog wherever the map's RAIN can't reach, sampled from the " +
                    "game's own rain-occlusion depth map (the WeatherObstacle mesh). Catches interiors that " +
                    "have no shelter volume — if rain can't get into a building, its fog thins. Note it " +
                    "applies under ANY authored rain cover (overhangs, possibly tree canopies — check the " +
                    "debug view's BLUE tint). 0 = off.", new AcceptableValueRange<float>(0f, 1f)));
            RainCoverFlip = config.Bind("Interiors", "Rain-Cover Flip", true,
                Adv("Depth convention of the rain-cover map. ON is correct on this build (reversed-Z); flip " +
                    "only if cover reads inverted (fog thins in the open, stays thick under roofs)."));
            RainCoverBias = config.Bind("Interiors", "Rain-Cover Bias", 1f,
                Adv("Metres below the rain-cover surface over which the shelter fades in. Small = crisp under " +
                    "roof edges; larger = softer.", new AcceptableValueRange<float>(0.1f, 5f)));
            InteriorMaxClearDistance = config.Bind("Interiors", "Interior Max Clear Distance", 5f,
                new ConfigDescription("FALLBACK-model reach (interiors with no shelter volume): only clear the " +
                    "fog on interior surfaces within this distance (m) of you. Lower = tighter (fewer stray " +
                    "clears from outside a window), but a big fallback room may keep fog on its far wall.",
                    new AcceptableValueRange<float>(0f, 150f)));
            InteriorSoftness = config.Bind("Interiors", "Interior Edge Softness", 0f,
                Adv("Feathers the interior mask's hard rectangular edges so the fog thins with a soft transition " +
                    "instead of a crisp cutout. 0 = off (sharp).", new AcceptableValueRange<float>(0f, 0.02f)));
            InteriorChannel = config.Bind("Interiors", "Interior Mask Channel", 3,
                Adv("Which channel of the game's interior mask marks 'indoors'. 3 = alpha (the usual one, since " +
                    "the interior tint is often black so only alpha carries coverage); 4 = brightest of R/G/B; " +
                    "0/1/2 = R/G/B. If interiors aren't thinning, turn on Interior Debug View to see which " +
                    "channel lights up.", new AcceptableValueRange<int>(0, 4)));
            InteriorDebug = config.Bind("Interiors", "Interior Debug View", false,
                Adv("Replace the screen with the game's raw interior mask: RED where its alpha is set, GREEN " +
                    "where any RGB is set. Building interiors should light up. If they're RED set Mask Channel " +
                    "to 3; if GREEN set it to 4. Turn this off once picked."));

            TransparentFog = config.Bind("Look", "Fog Transparents", false,
                "Fog glass/windows. They fog analytically from the game's _Density global, which our fog patch " +
                "leaves stale; we drive it ourselves, weather-matched to the volumetric fog.");
            TransparentFogScale = config.Bind("Look", "Transparent Fog Density", 0.025f,
                new ConfigDescription("Density of the fog on glass at FULL fog. Raise = thicker glass haze. " +
                    "(The game's own value is pinned near-zero, so we drive it ourselves.) 0 = off.",
                    new AcceptableValueRange<float>(0f, 0.3f)));
            TransparentFogBrightness = config.Bind("Look", "Transparent Fog Brightness", 1f,
                new ConfigDescription("Brightness of the glass fog colour. The sky-derived colour is HDR-bright and " +
                    "blows out the glass's additive in-scatter — lower this if the glass over-brightens.",
                    new AcceptableValueRange<float>(0f, 2f)));

            VolumetricGlass = config.Bind("Look", "Volumetric Glass", true,
                "Windows fog from the volumetric fog itself, at their own distance (FogSix's port of the game's " +
                "window shader, swapped in at raid load). Off = the game's window shader with its analytic fog.");
            VolumetricGlass.SettingChanged += (s, e) => FogGlass.OnToggle();

            StartDist = config.Bind("Range", "Start Distance", 0f,
                Adv("Fog starts this many metres from the camera.", new AcceptableValueRange<float>(0f, 100f)));
            MaxDist = config.Bind("Range", "Max Distance", 2000f,
                new ConfigDescription("How far the fog reaches (also caps the march for the sky).",
                    new AcceptableValueRange<float>(100f, 10000f)));
            FroxelMode = config.Bind("Performance", "Froxel Fog", true,
                "Frostbite-style froxel volumetrics (Hillaire 2015): fog density and lighting are computed " +
                "once per view-frustum voxel into a small 3D volume, accumulated over frames IN the volume, " +
                "and each pixel just integrates that tiny volume — there is NO per-pixel jitter or noise " +
                "anywhere in this mode, by construction. Most stable AND cheapest mode. While on, Raymarch " +
                "Steps / Step Jitter / Half-Resolution Marching don't apply (they belong to the legacy " +
                "raymarch, which Off restores).");
            FroxelRes = config.Bind("Performance", "Froxel Resolution", EFroxelRes.High,
                "Froxel grid size: Low 128x72, Medium 160x92, High 240x136 screen tiles. Higher = crisper " +
                "fog shapes/shaft edges for more GPU. Fog is soft — Medium is usually plenty.");
            FroxelSlices = config.Bind("Performance", "Froxel Depth Slices", 32,
                Adv("Depth slices in the froxel volume (exponentially spaced, 0.5m to Froxel Range). More = " +
                    "finer near/mid detail (mist bands, shaft edges).",
                    new AcceptableValueRange<int>(16, 128)));
            FroxelFar = config.Bind("Performance", "Froxel Range", 200f,
                Adv("Distance the froxel volume covers, metres. Beyond it the fog switches to the exact " +
                    "analytic height-fog integral (perfectly smooth; wisps/god rays/dust are near-field " +
                    "effects and only exist inside this range).",
                    new AcceptableValueRange<float>(50f, 500f)));
            // Legacy raymarch (Froxel Fog off; the interior debug views also run on it) — own section, advanced.
            Steps = config.Bind("Legacy", "Raymarch Steps (Legacy)", 64,
                Adv("LEGACY RAYMARCH ONLY — no effect while Froxel Fog is on. Steps per ray of the old " +
                    "screen-space march. Higher = smoother (less banding), more GPU.",
                    new AcceptableValueRange<int>(8, 128)));
            StepJitter = config.Bind("Legacy", "Step Jitter (Legacy)", 1f,
                Adv("LEGACY RAYMARCH ONLY — no effect while Froxel Fog is on. Fixed per-pixel march offset " +
                    "that dissolves coarse-step banding into grain. Froxel mode has no per-pixel jitter at " +
                    "all, which is the point of it.", new AcceptableValueRange<float>(0f, 1f)));
            HalfResMarch = config.Bind("Legacy", "Half-Resolution Marching (Legacy)", false,
                Adv("LEGACY RAYMARCH ONLY — no effect while Froxel Fog is on. Marches the old path at half " +
                    "resolution with a depth-aware upsample."));
            TemporalSmoothing = config.Bind("Performance", "Temporal Smoothing", 0.95f,
                new ConfigDescription("Frame-to-frame accumulation of the fog. In Froxel Fog mode this is " +
                    "the volume history blend (the Frostbite temporal integration): higher = smoother, more " +
                    "converged fog; 0.9-0.95 recommended, and changes to weather/lights still track within " +
                    "a few frames. 0 = no accumulation (raw per-frame volume). In legacy mode it drives the " +
                    "old screen-space history resolve instead.",
                    new AcceptableValueRange<float>(0f, 0.95f)));
            BakedNoise = config.Bind("Performance", "Baked Noise Texture", true,
                "Fetches the wisp/mist/dust noise from a pre-baked tiling 3D texture (one texture tap per " +
                "step) instead of recomputing it per raymarch step — the single biggest per-step GPU cost. " +
                "Same look (the pattern repeats every ~800 m, hidden by wind drift). Off = the exact " +
                "procedural noise, for A/B.");
            WispFadeDistance = config.Bind("Legacy", "Wispiness Fade Distance (Legacy)", 400f,
                Adv("LEGACY RAYMARCH ONLY — no effect while Froxel Fog is on. Distance where wisp noise fades " +
                    "to uniform fog and stops being computed. 0 = never fade.",
                    new AcceptableValueRange<float>(0f, 2000f)));
        }
    }
}
