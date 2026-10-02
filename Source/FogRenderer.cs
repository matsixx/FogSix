using BepInEx;
using EFT.Weather;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FogSix.Source
{
    // Renders the volumetric fog: loads the shader bundle, reads the live weather/sky values off TOD, and
    // does a single full-screen raymarch blit. Handles BOTH flatscreen (mono) and VR (per-eye), so this is
    // the single fog implementation — SPT-VR defers to it instead of carrying its own copy.
    internal static class FogRenderer
    {
        private static Material _mat;
        private static bool _triedLoad;

        private static float _lastLog;   // throttle for the debug log
        private static float _lastMistLog;

        // Wind drift, accumulated once per frame from the game's live wind (WeatherController.WeatherCurve.Wind).
        private static Vector3 _windOffset;
        private static int _windFrame = -1;
        private static Vector2 _lastWindDir = new Vector2(1f, 0f);

        // Physical sun COLOUR reaching the fog: white sunlight attenuated by the atmosphere along the sun ray
        // (Rayleigh + Mie + ozone), normalized to a pure HUE so only the colour shifts (brightness stays on the
        // glow intensity). SAME model + coefficients as CloudSix's cloud sun colour, so the fog's sun glow
        // MATCHES the clouds — neutral-warm at noon, orange at sunrise/sunset. Self-contained (FogSix is a
        // standalone mod, so no dependency on CloudSix): fog sits at ground level, hence the full column; warmth
        // scales the sunset reddening. See CloudSix CustomCloudController.SunTransmittanceColor.
        private static readonly Vector3 SunBetaR = new Vector3(5.802e-3f, 13.558e-3f, 33.1e-3f);
        private const float SunBetaMExt = 8.396e-3f;   // Mie scattering 3.996e-3 + absorption 4.40e-3
        private static readonly Vector3 SunBetaO = new Vector3(0.650e-3f, 1.881e-3f, 0.085e-3f);
        private static Color SunTransmittanceColor(float sunElevSin, float warmth)
        {
            float mu = Mathf.Max(sunElevSin, 0.001f);
            float airmass = warmth / (mu + 0.05f);       // slant path, ~20 at the horizon (x warmth)
            Vector3 od = SunBetaR * (8.0f * airmass)                                             // Rayleigh (H_R=8km)
                       + new Vector3(SunBetaMExt, SunBetaMExt, SunBetaMExt) * (1.2f * airmass)   // Mie (H_M=1.2km)
                       + SunBetaO * (8.0f * airmass);                                            // ozone slab
            Color t = new Color(Mathf.Exp(-od.x), Mathf.Exp(-od.y), Mathf.Exp(-od.z), 1f);
            float m = Mathf.Max(t.r, Mathf.Max(t.g, t.b));
            return m > 1e-4f ? new Color(t.r / m, t.g / m, t.b / m, 1f) : Color.white;
        }

        // Double-precision 4x4 inverse (glMatrix cofactor scheme). Matrix4x4.inverse is float32; for a
        // world-scale VP matrix its cancellation error leaves invVP*VP measurably off identity — the
        // CloudSix temporal saga's root cause (2026-07-03). Fog's froxels are far coarser than the
        // sub-pixel error so no symptom was visible here, but the precision is free.
        internal static Matrix4x4 InverseD(Matrix4x4 m)
        {
            double a00 = m.m00, a01 = m.m01, a02 = m.m02, a03 = m.m03;
            double a10 = m.m10, a11 = m.m11, a12 = m.m12, a13 = m.m13;
            double a20 = m.m20, a21 = m.m21, a22 = m.m22, a23 = m.m23;
            double a30 = m.m30, a31 = m.m31, a32 = m.m32, a33 = m.m33;

            double b00 = a00 * a11 - a01 * a10;
            double b01 = a00 * a12 - a02 * a10;
            double b02 = a00 * a13 - a03 * a10;
            double b03 = a01 * a12 - a02 * a11;
            double b04 = a01 * a13 - a03 * a11;
            double b05 = a02 * a13 - a03 * a12;
            double b06 = a20 * a31 - a21 * a30;
            double b07 = a20 * a32 - a22 * a30;
            double b08 = a20 * a33 - a23 * a30;
            double b09 = a21 * a32 - a22 * a31;
            double b10 = a21 * a33 - a23 * a31;
            double b11 = a22 * a33 - a23 * a32;

            double det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
            if (System.Math.Abs(det) < 1e-30)
                return m.inverse;   // degenerate — fall back
            double id = 1.0 / det;

            Matrix4x4 r = default;
            r.m00 = (float)((a11 * b11 - a12 * b10 + a13 * b09) * id);
            r.m01 = (float)((a02 * b10 - a01 * b11 - a03 * b09) * id);
            r.m02 = (float)((a31 * b05 - a32 * b04 + a33 * b03) * id);
            r.m03 = (float)((a22 * b04 - a21 * b05 - a23 * b03) * id);
            r.m10 = (float)((a12 * b08 - a10 * b11 - a13 * b07) * id);
            r.m11 = (float)((a00 * b11 - a02 * b08 + a03 * b07) * id);
            r.m12 = (float)((a32 * b02 - a30 * b05 - a33 * b01) * id);
            r.m13 = (float)((a20 * b05 - a22 * b02 + a23 * b01) * id);
            r.m20 = (float)((a10 * b10 - a11 * b08 + a13 * b06) * id);
            r.m21 = (float)((a01 * b08 - a00 * b10 - a03 * b06) * id);
            r.m22 = (float)((a30 * b04 - a31 * b02 + a33 * b00) * id);
            r.m23 = (float)((a21 * b02 - a20 * b04 - a23 * b00) * id);
            r.m30 = (float)((a11 * b07 - a10 * b09 - a12 * b06) * id);
            r.m31 = (float)((a00 * b09 - a01 * b07 + a02 * b06) * id);
            r.m32 = (float)((a31 * b01 - a30 * b03 - a32 * b00) * id);
            r.m33 = (float)((a20 * b03 - a21 * b01 + a22 * b00) * id);
            return r;
        }

        // Measurement (Debug Log): main-thread ms of each fog render call (C# work + draw submission; the GPU
        // runs asynchronously and is NOT included), logged once a second alongside the light stats.
        private static double _statRenderTotal, _statRenderMax;
        private static int _statRenderCalls;
        private static float _lastStatLog;

        // Returns false when it drew our fog (skip vanilla), true to let vanilla TOD scattering run.
        public static bool Render(TOD_Scattering scattering, RenderTexture source, RenderTexture destination)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool result = RenderImpl(scattering, source, destination);
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _statRenderTotal += ms;
            _statRenderCalls++;
            if (ms > _statRenderMax) _statRenderMax = ms;

            if (FogConfig.Debug.Value && Time.unscaledTime - _lastStatLog > 1f)
            {
                _lastStatLog = Time.unscaledTime;
                int f = Mathf.Max(1, FogLights.StatFrames);
                Plugin.MyLog.LogInfo(
                    $"[FogSix] PERF lights tracked={FogLights.TrackedCount} near={FogLights.NearCount} " +
                    $"dynamic={FogLights.DynamicCount} hot={FogLights.StatHotCount} selected={FogLights.StatSelected} | " +
                    $"lightsHot avg={FogLights.StatHotTotal / f:0.000}ms max={FogLights.StatHotMax:0.000}ms over {FogLights.StatFrames}f | " +
                    $"cold lights/s={FogLights.StatColdLights} avg={FogLights.StatColdTotal / f:0.000}ms max={FogLights.StatColdMax:0.000}ms | " +
                    $"fogRender calls={_statRenderCalls} avg={_statRenderTotal / Mathf.Max(1, _statRenderCalls):0.000}ms " +
                    $"max={_statRenderMax:0.000}ms layered={FogLayered.Ok && FogLayered.enabled}");
                FogLights.ResetStats();
                _statRenderTotal = _statRenderMax = 0.0;
                _statRenderCalls = 0;
            }
            return result;
        }

        private static bool RenderImpl(TOD_Scattering scattering, RenderTexture source, RenderTexture destination)
        {
            if (_mat == null) LoadShader();
            if (_mat == null) return true;                  // bundle missing -> fall back to vanilla fog

            Camera cam = scattering.GetComponent<Camera>();
            if (cam == null) return true;

            // Density is driven by the WEATHER fog value (WeatherCurve.Fog) — the same value SAIN/EFT read to
            // cut bot vision in fog. So fog only shows up when the weather is genuinely foggy, i.e. exactly
            // when the AI is already half-blind: fair both ways, never the player squinting while bots see
            // clear. (TOD_Scattering.GlobalDensity is unusable here — MBOIT is off in this build so the
            // weather never drives it; it sits pinned at a static default.)
            float weatherFog = 0f;
            WeatherController wc = WeatherController.Instance;
            if (wc != null && wc.WeatherCurve != null)
                weatherFog = wc.WeatherCurve.Fog;
            // WeatherCurve.Fog is on a TINY scale — SAIN's FogModifier clamps it to 0.018 as "full fog" (its
            // max bot-vision penalty, VISION_WEATHER_FOG_MAXCOEF). Normalize over the SAME range so our fog
            // thickens in lockstep with the bot-vision cut: Fog 0 -> none, Fog >= FogFullAt -> full density,
            // and we clamp past it (no extra fog where bots stop losing sight). FogFullAt defaults to SAIN's 0.018.
            float fogFullAt = Mathf.Max(0.0001f, FogConfig.FogFullAt.Value);
            float fogValue01 = Mathf.Clamp(weatherFog, 0f, fogFullAt) / fogFullAt;
            float density = fogValue01 * FogConfig.DensityScale.Value;

            if (FogConfig.Debug.Value && Time.time - _lastLog > 1f)
            {
                _lastLog = Time.time;
                Plugin.MyLog.LogInfo($"[FogSix] weatherFog={weatherFog:0.0000} norm01={fogValue01:0.00} -> density={density:0.0000}");
            }

            // Clear weather -> no MAIN fog. But the ground mist is constant ambiance (not weather-driven), so
            // only skip the raymarch entirely when the mist is ALSO off; otherwise fall through and render the
            // mist with the main density at ~0.
            if (density <= 0.00001f && !FogConfig.GroundMist.Value)
            {
                DriveTransparentFog(scattering, cam, 0f); // glass clears with the weather too
                FogGlass.Off();                           // no volume this render -> analytic glass fog
                Graphics.Blit(source, destination);
                return false;
            }

            cam.depthTextureMode |= DepthTextureMode.Depth; // the shader reconstructs world pos from depth

            // Per-eye view/projection (mono in flatscreen, each eye in VR — the hook fires once per eye in
            // multipass). VR eye projections are off-axis, so they MUST come from GetStereoProjectionMatrix,
            // not the mono cam matrices, or the fog mis-reconstructs and the horizon splits between eyes.
            Camera.MonoOrStereoscopicEye eye = (Camera.current != null) ? Camera.current.stereoActiveEye : cam.stereoActiveEye;
            Matrix4x4 view, proj;
            if (eye == Camera.MonoOrStereoscopicEye.Mono)
            {
                view = cam.worldToCameraMatrix;
                proj = cam.projectionMatrix;
            }
            else
            {
                Camera.StereoscopicEye sEye = (eye == Camera.MonoOrStereoscopicEye.Right)
                    ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
                view = cam.GetStereoViewMatrix(sEye);
                proj = cam.GetStereoProjectionMatrix(sEye);
            }
            Matrix4x4 vp = proj * view;      // kept for the temporal resolve's history reprojection
            Matrix4x4 invVP = InverseD(vp);
            Vector3 camPos = view.inverse.MultiplyPoint3x4(Vector3.zero);

            // Fog floor: where density is full, thinning ABOVE it. Two anchors:
            //  - Ground Fog: WORLD-anchored at the map's ground (LevelSettings.ZeroLevel) + offset, so the fog
            //    pools in valleys/trees and you can climb above it (true vertical parallax).
            //  - else: CAMERA-anchored (follows your head) so the fog is always around you at any altitude.
            float baseHeight;
            if (FogConfig.GroundFog.Value)
            {
                float zeroLevel = LevelValues(out float zl, out _) ? zl : camPos.y;
                baseHeight = zeroLevel + FogConfig.HeightOffset.Value;
            }
            else
            {
                baseHeight = camPos.y + FogConfig.HeightOffset.Value;
            }

            Color fogColor = Color.gray, sunColor = Color.white, moonColor = Color.white;
            Vector3 sunDir = Vector3.up, moonDir = Vector3.down;
            float sunGlow = 0f, moonGlow = 0f;
            TOD_Sky sky = scattering.Sky;
            if (sky != null)
            {
                float sunHeight = sky.LocalSunDirection.y;

                // Base fog colour: bright, properly-exposed SunSkyColor by day / MoonSkyColor by night (TOD's
                // SampleFogColor/atmosphere values are tiny pre-exposure numbers that read grey).
                fogColor = Color.Lerp(sky.MoonSkyColor, sky.SunSkyColor, Mathf.InverseLerp(-0.1f, 0.1f, sunHeight));

                // Sun + moon glow, mirroring CloudSix (CustomCloudController): each glow has its OWN colour,
                // world-space direction, and an intensity that fades the sun out below the horizon and the
                // moon in as the sun drops — so the right body glows in the right place at the right time.
                float sunT = Mathf.Clamp01(Mathf.InverseLerp(-0.3f, 0f, sunHeight));
                float highSun = Mathf.Clamp01(sunHeight / 0.3f);
                sunGlow = sunT * Mathf.Lerp(1f, 1.5f, highSun) * FogConfig.SunGlow.Value;

                float moonT = Mathf.Clamp01(Mathf.InverseLerp(0f, -0.15f, sunHeight));
                moonT *= moonT;
                moonGlow = moonT * FogConfig.MoonGlow.Value;

                // Sun colour warms toward the sky tone near the horizon (CloudSix's _SunColor); the moon glow
                // uses the moon's own light colour so it reads cool/distinct, not a warm sun tint.
                float lowSun = 1f - Mathf.Clamp01(sunHeight / -0.3f);
                if (FogConfig.AtmosphereSunColor == null || FogConfig.AtmosphereSunColor.Value)
                {
                    // Physical sun colour (matches the CloudSix clouds). SunDirection.y = sin(sun elevation).
                    float warmth = FogConfig.SunColorWarmth != null ? FogConfig.SunColorWarmth.Value : 1f;
                    sunColor = SunTransmittanceColor(sky.SunDirection.y, warmth);
                }
                else
                {
                    sunColor = Color.Lerp(sky.SunLightColor, sky.SunSkyColor, lowSun * 0.9f);   // old TOD colour
                }
                moonColor = sky.MoonLightColor;

                // Warm the BASE haze toward the physical sun hue near the horizon (golden hour). _FogColor is
                // TOD's whole-sky-averaged SunSkyColor, which misses the warm horizon light — so off the sun-glow
                // lobe the distant fog reads a clinical white while the sky + clouds go gold. Multiplying by the
                // NORMALIZED sun transmittance hue (max channel = 1) tints warm WITHOUT darkening (only pulls G/B
                // below the warm peak) = warm-white, not brown. Envelope = sunT*(1-highSun): 0 when the sun is high,
                // ~1 at the horizon, gone once it's well below — so it warms exactly when that low warm light exists.
                // Same lever CloudSix uses on its clouds. BaseWarmth = 0 restores the flat SunSkyColor base.
                float baseWarm = FogConfig.BaseWarmth != null ? FogConfig.BaseWarmth.Value : 0f;
                if (baseWarm > 0f)
                {
                    float warmth = FogConfig.SunColorWarmth != null ? FogConfig.SunColorWarmth.Value : 1f;
                    Color warmHue = SunTransmittanceColor(sky.SunDirection.y, warmth);
                    float warmEnv = sunT * (1f - highSun);
                    fogColor = Color.Lerp(fogColor, fogColor * warmHue, warmEnv * baseWarm);
                }

                sunDir = sky.SunDirection;   // world-space, toward the sun
                moonDir = sky.MoonDirection; // world-space, toward the moon
            }

            DriveTransparentFog(scattering, cam, fogValue01);

            UpdateWind();

            _mat.SetMatrix("_FogInvVP", invVP);
            _mat.SetVector("_FogCamPos", camPos);
            _mat.SetColor("_FogColor", fogColor);
            _mat.SetColor("_FogSunColor", sunColor);
            _mat.SetColor("_FogMoonColor", moonColor);
            _mat.SetVector("_FogSunDir", sunDir);
            _mat.SetVector("_FogMoonDir", moonDir);
            _mat.SetFloat("_FogSunIntensity", sunGlow);
            _mat.SetFloat("_FogMoonIntensity", moonGlow);
            _mat.SetFloat("_FogDensity", density);
            _mat.SetFloat("_FogBaseHeight", baseHeight);
            _mat.SetFloat("_FogHeightFalloff", FogConfig.HeightFalloff.Value);
            _mat.SetFloat("_FogStartDist", FogConfig.StartDist.Value);
            _mat.SetFloat("_FogMaxDist", FogConfig.MaxDist.Value);
            _mat.SetFloat("_FogNoiseScale", FogConfig.NoiseScale.Value);
            _mat.SetFloat("_FogNoiseStrength", FogConfig.NoiseStrength.Value);
            _mat.SetFloat("_FogAnisotropy", FogConfig.Anisotropy.Value);
            _mat.SetInt("_FogSteps", Mathf.Max(1, FogConfig.Steps.Value));
            _mat.SetFloat("_FogJitter", FogConfig.StepJitter.Value);
            _mat.SetFloat("_FogNoiseFadeDist", FogConfig.WispFadeDistance.Value);
            _mat.SetVector("_FogWind", _windOffset);

            // Baked-noise texture: one fetch replaces the per-step procedural fbm/mist ALU (see FogNoiseTex).
            bool bakedNoise = FogConfig.BakedNoise.Value;
            if (bakedNoise)
            {
                FogNoiseTex.EnsureBaked(FogConfig.MistDetail.Value); // no-op unless first use / Mist Detail changed
                bakedNoise = FogNoiseTex.Tex != null;                // bake failure -> procedural fallback
                if (bakedNoise) _mat.SetTexture("_FogNoiseTex", FogNoiseTex.Tex);
            }
            _mat.SetFloat("_FogUseNoiseTex", bakedNoise ? 1f : 0f);
            _mat.SetFloat("_FogNoiseTexScale", 1f / FogNoiseTex.Period);

            // Ground mist band (world-anchored at the map ground, independent of the main fog's anchoring).
            if (FogConfig.GroundMist.Value)
            {
                float groundY = LevelValues(out float zl, out _) ? zl : camPos.y;
                float mistPlane = groundY + FogConfig.MistHeight.Value;
                _mat.SetFloat("_MistDensity", FogConfig.MistDensity.Value);
                _mat.SetFloat("_MistHeight", mistPlane);
                _mat.SetFloat("_MistFalloff", 1f / Mathf.Max(0.1f, FogConfig.MistThickness.Value)); // e-fold = thickness
                _mat.SetFloat("_MistNoiseScale", FogConfig.MistNoiseScale.Value);
                _mat.SetFloat("_MistContrast", FogConfig.MistContrast.Value);
                _mat.SetFloat("_MistDetail", FogConfig.MistDetail.Value);

                if (FogConfig.Debug.Value && Time.time - _lastMistLog > 1f)
                {
                    _lastMistLog = Time.time;
                    Plugin.MyLog.LogInfo($"[FogSix] mist ON: planeY={mistPlane:0.0} camY={camPos.y:0.0} " +
                        $"(diff={camPos.y - mistPlane:0.0}m above plane) density={FogConfig.MistDensity.Value:0.00} " +
                        $"thickness={FogConfig.MistThickness.Value:0.0}m");
                }
            }
            else
            {
                _mat.SetFloat("_MistDensity", 0f); // off -> the shader skips the mist term
            }

            FogLights.Update(_mat, cam);    // feed nearby point/spot lights so they scatter through the fog
            FogInteriors.Update(_mat, cam); // feed interior zones so the fog thins inside buildings

            FogShadows.Update(sky); // capture the sun/moon shadow map into _FogSunShadowMap (for god rays)
            bool shadowOn = FogConfig.SunShadows.Value && FogShadows.Active; // off if no shadow-caster (stale map)
            _mat.SetFloat("_FogSunShadow", shadowOn ? FogConfig.SunShadowStrength.Value : 0f);
            _mat.SetFloat("_FogShadowBias", FogConfig.SunShadowBias.Value);
            _mat.SetFloat("_FogShadowDir", FogConfig.SunShadowFlip.Value ? -1f : 1f);
            _mat.SetFloat("_FogShadowMaxDist", FogConfig.SunShadowDistance.Value);
            _mat.SetFloat("_FogShadowSoftness", FogConfig.SunShadowSoftness.Value);
            _mat.SetFloat("_FogDustAmount", FogConfig.ShaftDust.Value);
            _mat.SetFloat("_FogDustScale", FogConfig.ShaftDustScale.Value);
            _mat.SetFloat("_FogDustFadeDist", Mathf.Max(1f, FogConfig.DustDistance.Value));

            // Half-res marching (the CloudSix trick, plus a depth-aware upsample because fog meets geometry):
            // pass 2 marches at half res per axis (quarter the rays) into (in-scatter, transmittance), pass 3
            // re-weights the 2x2 taps per full-res pixel by depth similarity and composites. The debug views
            // need per-pixel output, so they force the classic full-res pass 0.
            //
            // Temporal accumulation (the Frostbite volumetrics recipe, same machinery as SSRSix): when
            // Temporal Smoothing > 0 the march jitter gets a per-frame golden-ratio phase, so successive
            // frames integrate DIFFERENT step ladders, and pass 5 averages them against a reprojected
            // per-eye history — low step counts accumulate into high-step quality, and the dust/wisp
            // point-sample walk-reroll stops strobing. 0 = the classic paths, bit-identical to before.
            bool debugView = FogConfig.InteriorDebug.Value || FogConfig.InteriorVolumeDebug.Value;

            // Froxel mode (the Frostbite architecture, see FogFroxels): all uniforms above (colours, glow,
            // lights, shadows, interiors, noise) feed the froxel passes too — the paths only differ in WHO
            // does the integration. Debug overlays are per-pixel march views, so they route to the legacy
            // pass 0 below.
            if (FogConfig.FroxelMode.Value && !debugView)
            {
                FogFroxels.Render(_mat, cam, eye, vp, camPos, source, destination);
                // Glass colour AFTER the volume update: pass 1 integrates the froxel volume, so windows
                // carry the same god rays / light glow / wisps as the air (not just a directional tint).
                _mat.SetFloat("_FogScatterFroxel", 1f);
                BuildScatterTex(cam, source);
                return false;
            }
            FogGlass.Off();   // legacy raymarch: no volume for the glass to tap

            _mat.SetFloat("_FogScatterFroxel", 0f);
            BuildScatterTex(cam, source);                // glass fog colour: pass 1 -> _ScatteringTex

            float smoothing = Mathf.Clamp(FogConfig.TemporalSmoothing.Value, 0f, 0.95f);
            if (debugView) smoothing = 0f;   // debug overlays are per-pixel sentinels — never accumulate them
            _mat.SetFloat("_FogFramePhase",
                smoothing > 0.001f ? Mathf.Repeat(Time.frameCount * 0.6180339887f, 1f) : 0f);

            if (smoothing > 0.001f)
            {
                // March into a full-res fog buffer (in-scatter, transmittance)...
                RenderTexture fogFull = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBHalf);
                fogFull.filterMode = FilterMode.Bilinear;
                if (FogConfig.HalfResMarch.Value)
                {
                    int hw = Mathf.Max(1, source.width / 2), hh = Mathf.Max(1, source.height / 2);
                    RenderTexture halfRT = RenderTexture.GetTemporary(hw, hh, 0, RenderTextureFormat.ARGBHalf);
                    halfRT.filterMode = FilterMode.Bilinear;
                    Graphics.Blit(source, halfRT, _mat, 2);      // the march
                    _mat.SetTexture("_FogHalfTex", halfRT);
                    Graphics.Blit(source, fogFull, _mat, 4);     // upsample only (no composite)
                    RenderTexture.ReleaseTemporary(halfRT);
                }
                else
                {
                    Graphics.Blit(source, fogFull, _mat, 2);     // the march pass, at full res
                }

                // ...resolve it against this eye's history (reproject + clamp + blend; the resolved output
                // doubles as next frame's history, ping-ponged so we never read and write the same RT)...
                EyeHistory h = GetHistory(cam, eye, source.width, source.height);
                if (h.valid)
                {
                    _mat.SetTexture("_FogHistTex", h.read);
                    _mat.SetMatrix("_FogPrevVP", h.prevVP);
                    _mat.SetFloat("_FogTemporal", smoothing);
                    Graphics.Blit(fogFull, h.write, _mat, 5);
                }
                else
                {
                    Graphics.Blit(fogFull, h.write);             // first frame: seed with the raw march
                }
                h.prevVP = vp;
                h.valid = true;
                RenderTexture t = h.read; h.read = h.write; h.write = t;

                // ...and composite the resolved fog over the scene.
                _mat.SetTexture("_FogResolvedTex", h.read);
                Graphics.Blit(source, destination, _mat, 6);
                RenderTexture.ReleaseTemporary(fogFull);
            }
            else if (FogConfig.HalfResMarch.Value && !debugView)
            {
                int hw = Mathf.Max(1, source.width / 2), hh = Mathf.Max(1, source.height / 2);
                RenderTexture halfRT = RenderTexture.GetTemporary(hw, hh, 0, RenderTextureFormat.ARGBHalf);
                halfRT.filterMode = FilterMode.Bilinear;
                Graphics.Blit(source, halfRT, _mat, 2);        // the march
                _mat.SetTexture("_FogHalfTex", halfRT);
                Graphics.Blit(source, destination, _mat, 3);   // depth-aware upsample + composite
                RenderTexture.ReleaseTemporary(halfRT);
            }
            else
                Graphics.Blit(source, destination, _mat, 0);   // classic full-res march + composite
            return false;
        }

        // Per camera + eye fog history for the temporal resolve (mono, left, right each keep their own pair —
        // in VR multipass the two eyes are separate renders with separate matrices — and the scope camera its
        // own, since it renders fog too at a different resolution). Ping-pong: `read` is last frame's resolved
        // output, `write` is this frame's target; swapped after each resolve.
        private class EyeHistory
        {
            public Camera cam;
            public RenderTexture read, write;
            public Matrix4x4 prevVP;
            public bool valid;
        }
        private static readonly Dictionary<long, EyeHistory> _history = new Dictionary<long, EyeHistory>();

        private static EyeHistory GetHistory(Camera cam, Camera.MonoOrStereoscopicEye eye, int w, int h)
        {
            long key = FogFroxels.Key(cam, eye);
            if (!_history.TryGetValue(key, out EyeHistory hb))
            {
                PruneDestroyedCameras();
                hb = new EyeHistory { cam = cam };
                _history[key] = hb;
            }
            if (hb.read == null || hb.read.width != w || hb.read.height != h)
            {
                FogFroxels.DestroyRT(hb.read);
                FogFroxels.DestroyRT(hb.write);
                hb.read = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { filterMode = FilterMode.Bilinear };
                hb.write = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { filterMode = FilterMode.Bilinear };
                hb.valid = false;   // resolution changed -> stale history, restart accumulation
            }
            return hb;
        }

        // Drop state for cameras that no longer exist (raid end, destroyed scope). Runs only when a new
        // camera/eye first renders, never per frame.
        private static readonly List<long> _dead = new List<long>();
        private static void PruneDestroyedCameras()
        {
            _dead.Clear();
            foreach (KeyValuePair<long, EyeHistory> kv in _history)
                if (kv.Value.cam == null) _dead.Add(kv.Key);
            foreach (long k in _dead)
            {
                FogFroxels.DestroyRT(_history[k].read);
                FogFroxels.DestroyRT(_history[k].write);
                _history.Remove(k);
            }

            _dead.Clear();
            foreach (KeyValuePair<long, ScatterTex> kv in _scatter)
                if (kv.Value.cam == null) _dead.Add(kv.Key);
            foreach (long k in _dead)
            {
                FogFroxels.DestroyRT(_scatter[k].rt);
                _scatter.Remove(k);
            }
        }

        // The glass/windows fog ANALYTICALLY from the global `_Density` (HeightFalloff, camY-ZeroLevel,
        // GlobalDensity, 0) — proven by the slider changing them; NO MBOIT volume needed. Vanilla
        // TOD_Scattering.OnRenderImageNormalMode sets it every frame, but our prefix skips that method, so it
        // goes stale and the glass renders clear. We drive it ourselves. The game's GlobalDensity is pinned
        // near-zero, so we weather-drive the density (fogValue01) to match our volumetric instead — and dim +
        // clamp the colour, since the raw HDR sky colour blows out the glass's additive in-scatter. Height
        // shape (HeightFalloff/ZeroLevel) comes from LevelSettings like vanilla (the component defaults to 0).
        // The map's LevelSettings VALUES, captured by FogLevelSettingsPatch. Not the object: SPT 4.1 destroys the
        // map's LevelSettings right after raid start (log-confirmed: Awake -> OnDestroy at spawn, no second
        // instance), so every object lookup — FindObjectOfType, Singleton<LevelSettings>, a tracked reference —
        // comes back null for the whole raid and the fog fell back to camPos.y: Ground Fog following the camera.
        // ZeroLevel/HeightFalloff are serialized map data with no runtime writers, so the last capture holds.
        private static LevelSettings _level;
        private static bool _haveLevel;
        private static float _levelZero, _levelFalloff;
        private static string _levelName = "";
        private static bool _warnedNoLevel;

        internal static void SetLevel(LevelSettings level)
        {
            _level = level;
            Capture(level);
        }

        internal static void ClearLevel(LevelSettings level)
        {
            if (!ReferenceEquals(_level, level)) return;
            Capture(level);   // keep its values: the raid continues after 4.1 destroys it
            _level = null;
        }

        private static void Capture(LevelSettings level)
        {
            _haveLevel = true;
            _levelZero = level.ZeroLevel;
            _levelFalloff = level.HeightFalloff;
            _levelName = level.name;
            _warnedNoLevel = false;
        }

        private static bool LevelValues(out float zeroLevel, out float heightFalloff)
        {
            if (_level != null) Capture(_level);
            zeroLevel = _levelZero;
            heightFalloff = _levelFalloff;
            if (!_haveLevel && !_warnedNoLevel)
            {
                _warnedNoLevel = true;
                Plugin.MyLog.LogWarning("[FogSix] No LevelSettings captured — world-anchored fog falls back to the camera height.");
            }
            return _haveLevel;
        }

        // Set the glass's fog DENSITY (`_Density`) — the global the game's transparent "Global Fog/..." shaders
        // read for HOW MUCH fog. Vanilla TOD_Scattering.OnRenderImageNormalMode sets it (line 176), but our
        // prefix skips that method, so it goes stale and the glass renders clear. We drive it ourselves,
        // weather-matched. HeightFalloff/ZeroLevel come from LevelSettings like vanilla (the component's own
        // default to 0). The COLOUR is handled by BuildScatterTex — the glass reads it from a texture, not a
        // colour global (ScatterColorMultiplier is ignored, hence the dead brightness slider earlier).
        private static void DriveTransparentFog(TOD_Scattering scattering, Camera cam, float fogValue01)
        {
            if (!FogConfig.TransparentFog.Value) return;
            float heightFalloff = scattering.HeightFalloff;
            float zeroLevel = scattering.ZeroLevel;
            if (scattering.FromLevelSettings)
            {
                if (LevelValues(out float zl, out float hf)) { heightFalloff = hf; zeroLevel = zl; }
            }
            float density = fogValue01 * FogConfig.TransparentFogScale.Value;
            Shader.SetGlobalVector("_Density",
                new Vector4(heightFalloff, cam.transform.position.y - zeroLevel, density, 0f));
        }

        // Render the per-pixel fog COLOUR (shader pass 1) into a screen-mapped RT and bind it as `_ScatteringTex`
        // — the texture the glass shaders sample for their fog colour (vanilla builds it in the method we skip).
        // Screen-space, so the glass gets the correct DIRECTIONAL colour and matches the volumetric.
        //
        // LOW-RES on purpose: in froxel mode pass 1 integrates the whole volume per texel, and the volume only
        // has froxel-grid resolution across the screen — so a texel per froxel column carries all the detail
        // there is, and the glass's bilinear sample does the rest. It used to render at full screen res, doing
        // up to a slice-count of volume taps for EVERY pixel (sky included) even with no glass on screen.
        // Legacy mode's pass 1 is direction-only (a smooth lobe), so a quarter of the screen is plenty.
        // One RT per camera: the scope renders fog at its own resolution, and a single shared RT used to be
        // destroyed + reallocated twice a frame while aiming.
        private class ScatterTex
        {
            public Camera cam;
            public RenderTexture rt;
        }
        private static readonly Dictionary<long, ScatterTex> _scatter = new Dictionary<long, ScatterTex>();

        private static void BuildScatterTex(Camera cam, RenderTexture source)
        {
            if (!FogConfig.TransparentFog.Value) return;
            _mat.SetFloat("_FogScatterBrightness", FogConfig.TransparentFogBrightness.Value);

            int w, h;
            if (FogConfig.FroxelMode.Value)
                FogFroxels.GridSize(out w, out h);
            else
            {
                w = Mathf.Max(64, source.width / 4);
                h = Mathf.Max(36, source.height / 4);
            }

            long key = FogFroxels.Key(cam, Camera.MonoOrStereoscopicEye.Mono);   // eyes share: rebuilt per eye
            if (!_scatter.TryGetValue(key, out ScatterTex st))
            {
                PruneDestroyedCameras();
                st = new ScatterTex { cam = cam };
                _scatter[key] = st;
            }
            if (st.rt == null || st.rt.width != w || st.rt.height != h)
            {
                FogFroxels.DestroyRT(st.rt);
                st.rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,   // the glass upsamples it
                };
            }
            Graphics.Blit(source, st.rt, _mat, 1); // pass 1 = fog scatter colour
            Shader.SetGlobalTexture("_ScatteringTex", st.rt);
        }

        // Some window glass uses OPAQUE reflective shaders (`p0/Reflective/...`) that BSG left in the
        // TRANSPARENT render queue (3000+), so they draw AFTER our [ImageEffectOpaque] fog and paint over it.
        // The Reflective family doesn't alpha-blend (it's solid glass with a reflection), so moving them to the
        // opaque queue (2000) renders them PIXEL-IDENTICAL but BEFORE the fog -> they get fogged (and it fixes
        // their transparent-sort glitches too). Called from Plugin at raid load (a few passes, since map
        // materials stream in). The self-fogging "Global Fog/..." glass isn't matched (it fogs itself).
        public static void FixGlassQueues()
        {
            Material[] mats = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < mats.Length; i++)
            {
                Material m = mats[i];
                if (m == null || m.renderQueue < 2500) continue; // already opaque (incl. ones we've fixed) -> skip
                Shader sh = m.shader;
                if (sh != null && sh.name.StartsWith("p0/Reflective/"))
                    m.renderQueue = 2000;
            }
            FogGlass.Apply(mats);
        }

        public static void LoadShader()
        {
            if (_mat != null || _triedLoad) return;
            _triedLoad = true;
            try
            {
                string bundlePath = Path.Combine(BepInEx.Paths.PluginPath, "FogSix", "Assets", "volfog");
                AssetBundle bundle = AssetBundle.LoadFromFile(bundlePath);
                if (bundle == null)
                {
                    Plugin.MyLog.LogError("[VolFog] Failed to load AssetBundle at " + bundlePath);
                    return;
                }
                _mat = bundle.LoadAsset<Material>("volFogMat");
                if (_mat == null)
                    Plugin.MyLog.LogError("[VolFog] 'volFogMat' material not found in bundle.");
                else
                    Plugin.MyLog.LogInfo("[VolFog] Material loaded.");
                FogGlass.TakeShaders(bundle.LoadAllAssets<Shader>());   // the window-shader port, if the bundle has it
                bundle.Unload(false); // keep the shader/material in memory
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError("[VolFog] " + ex.Message);
            }
        }

        private static void UpdateWind()
        {
            if (Time.frameCount == _windFrame) return; // OnRenderImage can fire more than once a frame
            _windFrame = Time.frameCount;

            Vector2 wind = Vector2.zero;
            WeatherController wc = WeatherController.Instance;
            if (wc != null && wc.WeatherCurve != null)
                wind = wc.WeatherCurve.Wind;

            float mag = wind.magnitude;
            if (mag > 0.0001f)
                _lastWindDir = wind / mag; // hold last heading when calm

            float spd = mag * FogConfig.WindSpeed.Value;
            _windOffset.x += _lastWindDir.x * spd * Time.deltaTime;
            _windOffset.z += _lastWindDir.y * spd * Time.deltaTime;
            _windOffset.y += spd * 0.05f * Time.deltaTime;
        }
    }
}
