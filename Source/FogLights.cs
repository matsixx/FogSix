using System.Collections.Generic;
using UnityEngine;

namespace FogSix.Source
{
    // Feeds the fog shader realtime point/spot lights (flashlights, lamps, flares) so they scatter through
    // the fog. No periodic scanning: static map lights (lamps) are caught once by Rescan() at raid load, and
    // per-weapon tactical lights (flashlights/lasers) register themselves the instant their weapon is
    // equipped — including bots that spawn mid-raid — via RegisterWeaponLights (called from
    // FogLightSpawnPatch on FirearmController.Spawn). No shadows -> light passes through geometry.
    //
    // Cost model (measured on Streets, 6862 tracked lights): every property read is an engine call, and the
    // old design did ~5 per light for 500-650 nearby lights every frame (0.2 ms) plus a full-set sweep twice a
    // second (3-5 ms spikes). Now:
    //  - each light's position/range/intensity/enabled is CACHED; a COLD pass refreshes a slice of the set
    //    every frame so the whole set is re-read once per ColdCycleSeconds (no spikes);
    //  - the NEAR list (static lights whose reach touches the camera) is rebuilt from the cache only — no
    //    engine calls — when the camera moves or a light registers;
    //  - per frame, only the best-scoring near lamps (by cached values) plus the DYNAMIC lights (weapon lights,
    //    and any "static" light seen moving) get live checks, so a flashlight toggling or a nearby lamp
    //    switching off responds the same frame. A far lamp switching on can lag up to a cycle.
    // View-frustum culling of the pick was tried (2026-09-15) and REJECTED in-game: lights vanished that
    // should have shown. The "Max Lights" slider is the lever instead.
    internal static class FogLights
    {
        internal const int MAX = 32;                 // array capacity — must match FOG_MAX_LIGHTS in the shader
        private const int HOT_CAP = 64;              // capacity of the live-checked static set
        private const float ReachPad = 50f;          // glow beyond a light's own range still worth keeping
        private const float CandidateMargin = 15f;   // slack for movement between near-list rebuilds
        private const float ColdCycleSeconds = 1f;   // every tracked light's cache is refreshed once per cycle
        private const float MovedSq = 0.25f;         // a static light that moved >0.5m is treated as dynamic

        private sealed class Tracked
        {
            public Light light;
            public LightType type;
            public Vector3 pos;
            public float range, intensity;
            public bool enabled, dynamic, near, dead, hasPos;
        }

        private static readonly Vector4[] _pos = new Vector4[MAX];   // xyz pos, w range
        private static readonly Vector4[] _col = new Vector4[MAX];   // rgb colour*intensity
        private static readonly Vector4[] _dir = new Vector4[MAX];   // xyz spot fwd, w cos(half-angle); <0 = point
        private static readonly Tracked[] _best = new Tracked[MAX];
        private static readonly Vector3[] _bestPos = new Vector3[MAX];
        private static readonly float[] _bestScore = new float[MAX];
        private static readonly Tracked[] _hot = new Tracked[HOT_CAP];
        private static readonly float[] _hotScore = new float[HOT_CAP];

        private static readonly Dictionary<Light, Tracked> _known = new Dictionary<Light, Tracked>();
        private static readonly List<Tracked> _all = new List<Tracked>();
        private static readonly List<Tracked> _near = new List<Tracked>();
        private static readonly List<Tracked> _dynamic = new List<Tracked>();
        private static readonly List<Light> _found = new List<Light>();
        private static int _cursor;
        private static bool _scanned;
        private static bool _nearDirty = true;
        private static Vector3 _nearCamPos;
        private static float _nearRangeMul = -1f;
        private static int _frame = -1;
        private static readonly int CapacityId = Shader.PropertyToID("_FogLightCapacity");

        // Measurement (Debug Log): CPU ms per frame of the live pick (hot) and the cache refresh (cold), reset by
        // FogRenderer each time it logs.
        internal static double StatHotTotal, StatHotMax, StatColdTotal, StatColdMax;
        internal static int StatFrames, StatColdLights, StatSelected, StatHotCount;
        internal static int TrackedCount => _all.Count;
        internal static int NearCount => _near.Count;
        internal static int DynamicCount => _dynamic.Count;
        internal static void ResetStats()
        {
            StatHotTotal = StatHotMax = StatColdTotal = StatColdMax = 0.0;
            StatFrames = StatColdLights = 0;
        }
        private static double MsSince(long t0)
        {
            return (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        // One-time bootstrap for STATIC map lights (streetlamps, room lights) — called at raid load
        // (Plugin) only, never repeated mid-raid.
        public static void Rescan()
        {
            _scanned = true;
            foreach (Light l in Object.FindObjectsOfType<Light>())
                Register(l, false);
        }

        // Called from FogLightSpawnPatch whenever a weapon becomes a character's active hand controller — the one
        // moment a weapon's tac-light/laser Light components are guaranteed to exist. Bounded to that weapon.
        public static void RegisterWeaponLights(Transform weaponRoot)
        {
            if (weaponRoot == null) return;
            _found.Clear();
            weaponRoot.GetComponentsInChildren(true, _found);
            foreach (Light l in _found)
            {
                if (l != null && _known.TryGetValue(l, out Tracked t)) MakeDynamic(t);   // caught by a load scan
                else Register(l, true);
            }
            _found.Clear();
        }

        private static void Register(Light l, bool dynamic)
        {
            if (l == null || (l.type != LightType.Point && l.type != LightType.Spot) || _known.ContainsKey(l)) return;
            Tracked t = new Tracked { light = l, type = l.type };
            _known[l] = t;
            ReadCache(t);
            _all.Add(t);
            if (dynamic) MakeDynamic(t);
            _nearDirty = true;
        }

        private static void MakeDynamic(Tracked t)
        {
            if (t.dynamic) return;
            t.dynamic = true;
            _dynamic.Add(t);
        }

        private static void ReadCache(Tracked t)
        {
            Light l = t.light;
            Vector3 p = l.transform.position;
            if (!t.dynamic && t.hasPos && (p - t.pos).sqrMagnitude > MovedSq) MakeDynamic(t);   // it moves: go live
            t.pos = p;
            t.hasPos = true;
            t.range = l.range;
            t.intensity = l.intensity;
            t.enabled = l.isActiveAndEnabled;
        }

        public static void Update(Material mat, Camera cam)
        {
            if (!FogConfig.LocalLights.Value)
            {
                mat.SetInt("_FogLightCount", 0);
                _frame = -1;
                return;
            }

            if (Time.frameCount == _frame) return;   // already uploaded this frame (other eye / scope camera)
            _frame = Time.frameCount;
            long tStart = System.Diagnostics.Stopwatch.GetTimestamp();

            // Bootstrap fallback only (Plugin's raid-load scan already does this) — never a recurring timer.
            if (!_scanned) Rescan();

            Vector3 camPos = cam.transform.position;
            float rangeMul = FogConfig.LocalLightRange.Value;
            // bundles built before the 32-slot arrays hold 8 lights and lack the marker property
            int capacity = mat.HasProperty(CapacityId) ? MAX : 8;
            int maxLights = Mathf.Clamp(FogConfig.MaxLights.Value, 1, capacity);
            int hotLimit = Mathf.Clamp(maxLights * 2, 32, HOT_CAP);   // live-checked lamps: slack above the pick

            long tCold = System.Diagnostics.Stopwatch.GetTimestamp();
            ColdStep(camPos, rangeMul);
            double coldMs = MsSince(tCold);

            if (_nearDirty || rangeMul != _nearRangeMul
                || (camPos - _nearCamPos).sqrMagnitude > CandidateMargin * CandidateMargin * 0.25f)
                RebuildNear(camPos, rangeMul);

            // Static hot set: the best near lamps by CACHED score (no engine calls).
            int hotCount = 0;
            for (int i = 0; i < _near.Count; i++)
            {
                Tracked t = _near[i];
                if (t.dead || t.dynamic || !t.enabled || t.intensity <= 0f || t.range <= 0f) continue;
                float dist = Vector3.Distance(camPos, t.pos);
                if (dist > t.range * rangeMul + ReachPad) continue;
                float score = t.intensity * t.range / (10f + dist);
                if (hotCount < hotLimit) { _hot[hotCount] = t; _hotScore[hotCount] = score; hotCount++; }
                else
                {
                    int worst = 0;
                    for (int k = 1; k < hotLimit; k++) if (_hotScore[k] < _hotScore[worst]) worst = k;
                    if (score > _hotScore[worst]) { _hot[worst] = t; _hotScore[worst] = score; }
                }
            }

            // Final pick: keep the maxLights most IMPORTANT enabled, in-range lights — ranked by brightness ×
            // reach, weighted toward near (ranking by raw distance popped bright prominent lights in and out).
            int count = 0;
            for (int i = 0; i < hotCount; i++)
            {
                Tracked t = _hot[i];
                Light l = t.light;
                if (l == null) { t.dead = true; continue; }
                if (!l.isActiveAndEnabled) { t.enabled = false; continue; }
                float intensity = l.intensity;
                t.intensity = intensity;
                if (intensity <= 0f) continue;
                Consider(t, t.pos, t.range, intensity, camPos, rangeMul, maxLights, ref count);
            }
            for (int i = _dynamic.Count - 1; i >= 0; i--)
            {
                Tracked t = _dynamic[i];
                Light l = t.light;
                if (l == null)
                {
                    t.dead = true;
                    _dynamic[i] = _dynamic[_dynamic.Count - 1];
                    _dynamic.RemoveAt(_dynamic.Count - 1);
                    continue;
                }
                // cached position (this frame's or the cold pass's) rules out far weapon lights without engine calls
                float farReach = t.range * rangeMul + ReachPad + CandidateMargin;
                if ((t.pos - camPos).sqrMagnitude > farReach * farReach) continue;
                if (!l.isActiveAndEnabled) { t.enabled = false; continue; }
                float intensity = l.intensity, range = l.range;
                Vector3 lp = l.transform.position;
                t.intensity = intensity; t.range = range; t.pos = lp; t.enabled = true;
                if (intensity <= 0f || range <= 0f) continue;
                Consider(t, lp, range, intensity, camPos, rangeMul, maxLights, ref count);
            }

            for (int i = 0; i < count; i++)
            {
                Tracked t = _best[i];
                Light l = t.light;
                Vector3 lp = _bestPos[i];
                _pos[i] = new Vector4(lp.x, lp.y, lp.z, t.range * rangeMul);
                Color c = l.color * t.intensity; // raw; overall brightness tuned by _FogLightScatter in-shader
                _col[i] = new Vector4(c.r, c.g, c.b, 0f);
                if (t.type == LightType.Spot)
                {
                    Vector3 f = l.transform.forward;
                    _dir[i] = new Vector4(f.x, f.y, f.z, Mathf.Cos(l.spotAngle * 0.5f * Mathf.Deg2Rad));
                }
                else
                {
                    _dir[i] = new Vector4(0f, 0f, 1f, -1f); // point light: w < 0 (no cone)
                }
            }

            mat.SetVectorArray("_FogLightPos", _pos);
            mat.SetVectorArray("_FogLightColor", _col);
            mat.SetVectorArray("_FogLightDir", _dir);
            mat.SetInt("_FogLightCount", count);
            mat.SetFloat("_FogLightScatter", FogConfig.LocalLightScatter.Value);
            mat.SetFloat("_FogLightAnisotropy", FogConfig.LocalLightFocus.Value);
            mat.SetFloat("_FogLightFalloff", FogConfig.LocalLightFalloff.Value);
            mat.SetFloat("_FogLightConeSoft", FogConfig.LocalLightConeSoftness.Value);

            double hotMs = MsSince(tStart) - coldMs;
            StatFrames++;
            StatHotTotal += hotMs;
            if (hotMs > StatHotMax) StatHotMax = hotMs;
            StatColdTotal += coldMs;
            if (coldMs > StatColdMax) StatColdMax = coldMs;
            StatSelected = count;
            StatHotCount = hotCount;
        }

        private static void Consider(Tracked t, Vector3 lp, float range, float intensity, Vector3 camPos,
            float rangeMul, int maxLights, ref int count)
        {
            float dist = Vector3.Distance(camPos, lp);
            if (dist > range * rangeMul + ReachPad) return; // its glow can't reach the fog near the camera
            float score = intensity * range / (10f + dist);  // brighter + longer reach + nearer = keep it
            int slot = -1;
            if (count < maxLights) slot = count++;
            else
            {
                int worst = 0;
                for (int k = 1; k < maxLights; k++) if (_bestScore[k] < _bestScore[worst]) worst = k;
                if (score > _bestScore[worst]) slot = worst;
            }
            if (slot >= 0) { _best[slot] = t; _bestPos[slot] = lp; _bestScore[slot] = score; }
        }

        // Refresh a slice of the cache so the whole set is re-read once per ColdCycleSeconds. Destroyed lights
        // (dead bots' weapons, scene tear-down) are dropped here.
        private static void ColdStep(Vector3 camPos, float rangeMul)
        {
            int n = _all.Count;
            if (n == 0) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);   // a load hitch must not turn into a full sweep
            int batch = Mathf.Clamp(Mathf.CeilToInt(n * dt / ColdCycleSeconds), 1, n);
            for (int i = 0; i < batch && _all.Count > 0; i++)
            {
                if (_cursor >= _all.Count) _cursor = 0;
                Tracked t = _all[_cursor];
                if (t.light == null)
                {
                    t.dead = true;
                    _known.Remove(t.light);
                    int last = _all.Count - 1;
                    _all[_cursor] = _all[last];
                    _all.RemoveAt(last);
                    continue;   // the swapped-in light is examined at this cursor next
                }
                ReadCache(t);
                if (!t.dynamic && !t.near)
                {
                    float reach = t.range * rangeMul + ReachPad + CandidateMargin;
                    if ((t.pos - camPos).sqrMagnitude <= reach * reach) { t.near = true; _near.Add(t); }
                }
                _cursor++;
                StatColdLights++;
            }
        }

        // Static lights whose reach (+ margin) touches the camera, from the CACHE only.
        private static void RebuildNear(Vector3 camPos, float rangeMul)
        {
            _nearDirty = false;
            _nearCamPos = camPos;
            _nearRangeMul = rangeMul;
            _near.Clear();
            for (int i = 0; i < _all.Count; i++)
            {
                Tracked t = _all[i];
                t.near = false;
                if (t.dead || t.dynamic) continue;
                float reach = t.range * rangeMul + ReachPad + CandidateMargin;
                if ((t.pos - camPos).sqrMagnitude <= reach * reach) { t.near = true; _near.Add(t); }
            }
        }
    }
}
