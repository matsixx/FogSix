using UnityEngine;

// Editor test driver for Hidden/SPTVR/VolumetricFog. Attach to a camera, assign a Material that uses
// the fog shader, and tune live in the inspector. Mirrors what WeatherPatches.FixTODScattering feeds
// the shader in-game (here mono instead of per-eye). Put a Time of Day "Sky Dome" in the scene so the
// TOD_FogColor / TOD_SunSkyColor / TOD_SunDirection globals are live — otherwise use the fallbacks below.
[ExecuteInEditMode]
[RequireComponent(typeof(Camera))]
[ImageEffectAllowedInSceneView]
public class VolFogTester : MonoBehaviour
{
    public Material fogMat;          // a Material whose shader is "Hidden/SPTVR/VolumetricFog"

    [Header("Shape")]
    public float density = 0.02f;
    public float baseHeight = 0f;
    public float heightFalloff = 0.04f;
    public float startDist = 5f;
    public float maxDist = 2000f;
    [Range(1, 128)] public int steps = 32;
    public float noiseScale = 0.01f;
    [Range(0, 1)] public float noiseStrength = 0.6f;
    public float windSpeed = 2f;
    [Range(0, 0.9f)] public float anisotropy = 0.5f;
    public float sunIntensity = 1f;

    [Header("Fallbacks (used only if there is no TOD_Sky in the scene)")]
    public bool overrideTodGlobals = false;
    public Light sun;
    public Color fogColor = new Color(0.6f, 0.65f, 0.72f);

    private Camera cam;

    private void OnEnable()
    {
        cam = GetComponent<Camera>();
        cam.depthTextureMode |= DepthTextureMode.Depth;   // the shader needs _CameraDepthTexture
    }

    private void OnRenderImage(RenderTexture src, RenderTexture dst)
    {
        if (fogMat == null) { Graphics.Blit(src, dst); return; }

        cam.depthTextureMode |= DepthTextureMode.Depth;

        // Mono equivalent of the per-eye matrices the game feeds (GL-convention projection).
        Matrix4x4 invVP = (cam.projectionMatrix * cam.worldToCameraMatrix).inverse;
        fogMat.SetMatrix("_FogInvVP", invVP);
        fogMat.SetVector("_FogCamPos", cam.transform.position);

        fogMat.SetFloat("_FogDensity", density);
        fogMat.SetFloat("_FogBaseHeight", baseHeight);
        fogMat.SetFloat("_FogHeightFalloff", heightFalloff);
        fogMat.SetFloat("_FogStartDist", startDist);
        fogMat.SetFloat("_FogMaxDist", maxDist);
        fogMat.SetFloat("_FogNoiseScale", noiseScale);
        fogMat.SetFloat("_FogNoiseStrength", noiseStrength);
        fogMat.SetFloat("_FogAnisotropy", anisotropy);
        fogMat.SetFloat("_FogSunIntensity", sunIntensity);
        fogMat.SetInt("_FogSteps", Mathf.Max(1, steps));

        float wt = Time.time * windSpeed;
        fogMat.SetVector("_FogWind", new Vector4(wt, wt * 0.3f, wt * 0.5f, 0f));

        // Fog + sun colour are material params now (the shader stopped reading the TOD_*Color globals).
        // Prefer the live TOD_Sky values; fall back to the inspector fields if there's no sky / override on.
        TOD_Sky todSky = TOD_Sky.Instance;
        Color fc = fogColor;
        Color sc = (sun != null) ? sun.color * sun.intensity : Color.white;
        if (todSky != null && !overrideTodGlobals) { fc = todSky.SampleFogColor(); sc = todSky.SunSkyColor; }
        fogMat.SetColor("_FogColor", fc);
        fogMat.SetColor("_FogSunColor", sc);

        // Sun DIRECTION still rides the TOD_LightDirection global. TOD_Sky sets it when present; otherwise
        // (or when overriding) drive it from the assigned light.
        if ((todSky == null || overrideTodGlobals) && sun != null)
            Shader.SetGlobalVector("TOD_LightDirection", -sun.transform.forward);

        Graphics.Blit(src, dst, fogMat);
    }
}
