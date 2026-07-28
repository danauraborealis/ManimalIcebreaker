using System;
using Comfort.Common;
using HarmonyLib;
using UnityEngine;
using VolumetricFogAndMist;

namespace Manimal.Icebreaker
{
    // OUR volumetric fog — Volumetric Fog & Mist 2 (builtin) embedded in the plugin,
    // shaders/textures from volumetricfog.bundle. the native MBOIT path is a dead end
    // on this map (no WindowsManager = no volumetric composite, and retail never ran
    // it here either), so the raymarched store asset delivers the look instead.
    // rides the render camera as an image effect; the optic camera never gets it.
    internal static class IcebreakerVolFog
    {
        // cutscene look profile: while true, Plugin.Fog()/FogColorEntry route every
        // look read (and the F9 tuner) to the VolumetricFog2.Cutscene twin entries.
        // set/cleared by the cutscene driver around playback.
        internal static bool CutsceneProfile;

        private static VolumetricFogAndMist.VolumetricFog _fog;
        private static bool _dead; // bundle missing/broken — stop retrying this raid

        // authored fog AREAS: the SDK scene ships empty marker GOs named
        // 'manimal_fogarea*' (position = box center, scale = box size in meters).
        // each becomes a raymarched fog BOX at raid start — fog exists ONLY inside
        // the boxes, so interiors are excluded spatially and no indoor fade is needed.
        private static readonly System.Collections.Generic.List<VolumetricFogAndMist.VolumetricFog> _areas
            = new System.Collections.Generic.List<VolumetricFogAndMist.VolumetricFog>();
        private static bool _areasBuilt;

        // fog EXCLUSION voids — world-space boxes where the shader zeroes the global
        // fog (custom _MVoid* extension in our VolumetricFog.cginc, 16 max). this is
        // the interiors answer: global fog everywhere, true 3d holes inside the ship.
        private static readonly Vector4[] _voidPos = new Vector4[16];
        private static readonly Vector4[] _voidInvHalf = new Vector4[16];
        private static int _voidCount;

        private static void AddVoid(string name, Vector3 center, Vector3 size)
        {
            if (_voidCount >= 16) { Plugin.Log.LogWarning("[VolFog] void limit (16) reached — ignoring " + name); return; }
            _voidPos[_voidCount] = center;
            _voidInvHalf[_voidCount] = new Vector3(
                1f / Mathf.Max(size.x * 0.5f, 0.01f),
                1f / Mathf.Max(size.y * 0.5f, 0.01f),
                1f / Mathf.Max(size.z * 0.5f, 0.01f));
            _voidCount++;
            Plugin.Log.LogWarning($"[VolFog] fog VOID '{name}' @ {center} size {size}");
        }

        private static void PushVoids()
        {
            // float, not int — SetGlobalInt stores a float and int-typed shader
            // uniforms read garbage on some unity versions (the voids did nothing)
            Shader.SetGlobalFloat("_MVoidCount", _voidCount);
            // global arrays lock their size on first set — always push the full 16
            Shader.SetGlobalVectorArray("_MVoidPos", _voidPos);
            Shader.SetGlobalVectorArray("_MVoidInvHalf", _voidInvHalf);
            // negative falloff flips the shader into red-void debug visualization
            float falloff = Plugin.Fog(Plugin.VolFogVoidFalloff).Value;
            Shader.SetGlobalFloat("_MVoidFallOff", Plugin.VolFogVoidDebug.Value ? -falloff : falloff);
            Shader.SetGlobalFloat("_MVoidBlend", Plugin.Fog(Plugin.VolFogVoidBlend).Value);
        }

        private static string AreasJsonPath =>
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(IcebreakerVolFog).Assembly.Location), "fog_areas.json");
        private static DateTime _areasJsonTime;

        private static void BuildAreas()
        {
            _areasBuilt = true;
            foreach (var a in _areas)
                if (a != null) UnityEngine.Object.Destroy(a.gameObject);
            _areas.Clear();

            // preferred source: fog_areas.json next to the dll (exported from the SDK via
            // Manimal -> Export Fog Areas, or hand-edited) — hot-reloaded on change, no
            // scene bundle rebuild in the loop
            _voidCount = 0;
            // the SDK markers ship in the scene bundle WITH their editor-visualization
            // BoxColliders — strip them ALWAYS, even when the json is the data source
            // (the old strip lived only in the marker-parsing path below, so with a
            // json present the colliders survived = 15 invisible walls in the ship)
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || !t.gameObject.scene.isLoaded) continue;
                if (!t.name.StartsWith("manimal_fogvoid", StringComparison.OrdinalIgnoreCase) &&
                    !t.name.StartsWith("manimal_fogarea", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var col in t.GetComponents<Collider>())
                {
                    UnityEngine.Object.Destroy(col);
                    Plugin.Log.LogWarning($"[VolFog] stripped marker collider on '{t.name}'");
                }
            }
            if (System.IO.File.Exists(AreasJsonPath))
            {
                try
                {
                    _areasJsonTime = System.IO.File.GetLastWriteTimeUtc(AreasJsonPath);
                    var root = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(AreasJsonPath));
                    foreach (var a in root["areas"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray())
                    {
                        var p = a["position"];
                        var s = a["size"];
                        MakeArea((string)a["name"] ?? "unnamed",
                            new Vector3((float)p[0], (float)p[1], (float)p[2]),
                            new Vector3((float)s[0], (float)s[1], (float)s[2]));
                    }
                    foreach (var v in root["voids"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray())
                    {
                        var p = v["position"];
                        var s = v["size"];
                        AddVoid((string)v["name"] ?? "unnamed",
                            new Vector3((float)p[0], (float)p[1], (float)p[2]),
                            new Vector3((float)s[0], (float)s[1], (float)s[2]));
                    }
                    PushVoids();
                    Plugin.Log.LogWarning($"[VolFog] fog_areas.json: {_areas.Count} area(s), {_voidCount} void(s)" +
                        (_areas.Count == 0 ? " — GLOBAL fog + exclusion voids" : " — area boxes carry the fog"));
                    return;
                }
                catch (Exception e) { Plugin.Log.LogError($"[VolFog] fog_areas.json parse failed: {e.Message}"); }
            }

            // fallback: scene-bundle markers
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || !t.gameObject.scene.isLoaded) continue;
                bool isVoid = t.name.StartsWith("manimal_fogvoid", StringComparison.OrdinalIgnoreCase);
                if (!isVoid && !t.name.StartsWith("manimal_fogarea", StringComparison.OrdinalIgnoreCase)) continue;
                // markers may carry a BoxCollider for editor visualization — only the
                // bounds matter at runtime, so nothing may collide with them
                Vector3 center = t.position, size = t.lossyScale;
                var bc = t.GetComponent<BoxCollider>();
                if (bc != null)
                {
                    center = t.TransformPoint(bc.center);
                    size = Vector3.Scale(bc.size, t.lossyScale);
                }
                foreach (var col in t.GetComponents<Collider>())
                    UnityEngine.Object.Destroy(col);
                if (isVoid) AddVoid(t.name, center, size);
                else MakeArea(t.name, center, size);
            }
            PushVoids();
            if (_areas.Count > 0 || _voidCount > 0)
                Plugin.Log.LogWarning($"[VolFog] scene markers: {_areas.Count} area(s), {_voidCount} void(s)");
        }

        private static void MakeArea(string name, Vector3 center, Vector3 size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterials = new Material[0]; // bounds proxy only — fog draws itself
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            go.name = "VolFogArea_" + name;
            go.transform.position = center;
            go.transform.localScale = size;
            var f = go.AddComponent<VolumetricFogAndMist.VolumetricFog>();
            f.fogRenderer = _fog;
            f.fogAreaTopology = VolumetricFogAndMist.FOG_AREA_TOPOLOGY.Box;
            // the box confines the fog horizontally, but the vertical DENSITY profile
            // is height+baseline — left at the class defaults (4m slab at world Y0) the
            // box renders near-empty. fill the whole box, like CreateFogArea does.
            f.height = size.y * 0.98f;
            f.baselineHeight = center.y - size.y * 0.5f;
            _areas.Add(f);
            Plugin.Log.LogWarning($"[VolFog] fog area '{name}' @ {center} size {size}");
        }

        // json hot reload — re-export from the SDK (or save a hand edit) and the boxes
        // rebuild in the live raid within a second
        private static void CheckAreasReload()
        {
            if (!_areasBuilt || !System.IO.File.Exists(AreasJsonPath)) return;
            var t = System.IO.File.GetLastWriteTimeUtc(AreasJsonPath);
            if (t != _areasJsonTime)
            {
                Plugin.Log.LogWarning("[VolFog] fog_areas.json changed — rebuilding areas");
                BuildAreas();
            }
        }

        // statics survive across raids in one game session — a dead first raid must
        // not lock the fog out of raid 2+
        internal static void ResetForRaid()
        {
            _dead = false;
            _fog = null;
            _indoor = false;
            _areasBuilt = false;
            _areas.Clear();
        }

        internal static void Tick()
        {
            if (_dead) return;
            if (!Plugin.VolFog.Value)
            {
                if (_fog != null && _fog.enabled) _fog.enabled = false;
                return;
            }

            if (_fog == null)
            {
                var wc = EFT.Weather.WeatherController.Instance;
                var scat = wc != null ? AccessTools.Field(typeof(EFT.Weather.WeatherController), "tod_Scattering_0")?.GetValue(wc) as TOD_Scattering : null;
                var cam = scat != null ? scat.GetComponent<Camera>() : Camera.main;
                if (cam == null) return;
                if (!VolFogAssets.EnsureBundle()) { _dead = true; return; }
                try
                {
                    _fog = cam.GetComponent<VolumetricFogAndMist.VolumetricFog>() ?? cam.gameObject.AddComponent<VolumetricFogAndMist.VolumetricFog>();
                    // sun ref gives the fog its light direction; TOD sun GO if present
                    var sky = MonoBehaviourSingleton<TOD_Sky>.Instance;
                    if (sky != null && sky.Components != null && sky.Components.Sun != null)
                        _fog.sun = sky.Components.Sun;
                    Plugin.Log.LogWarning("[VolFog] VolumetricFog attached to render camera");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[VolFog] attach failed: {e}");
                    _dead = true;
                    return;
                }
            }

            if (!_areasBuilt) BuildAreas();
            else CheckAreasReload();

            try { Apply(); }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[VolFog] apply failed: {e.Message}");
                _dead = true;
            }

            // authored areas/voids exclude interiors spatially — the camera-position
            // fade is only the fallback when NEITHER exists. (voids + fade together
            // made entering an interior kill ALL fog, reading as broken voids)
            if (_areas.Count == 0 && _voidCount == 0) TickIndoorFade();
        }

        // ship interiors: fade the fog out when the camera is inside an Indoor_* volume
        // (bounds shared with the cross-cull system), fade back outdoors. a smooth
        // alpha transition instead of a pop; fog seen INSIDE rooms from outside still
        // renders (no true 3d exclusion in a screen-space raymarcher) but night +
        // deep obscurance keep that subtle.
        private static bool _indoor;

        private static void TickIndoorFade()
        {
            if (_fog == null) return;
            var cam = _fog.fogCamera;
            if (cam == null) return;
            bool indoorNow = RenderEnvProbe.CameraInsideInterior(cam.transform.position);
            if (indoorNow == _indoor) return;
            _indoor = indoorNow;
            _fog.useFogVolumes = true; // arms the transition machinery
            float fade = Plugin.VolFogIndoorFade.Value;
            if (indoorNow) _fog.SetTargetAlpha(0f, 0f, fade);
            else _fog.ClearTargetAlpha(fade);
            Plugin.Log.LogWarning($"[VolFog] {(indoorNow ? "INDOORS — fog fading out" : "outdoors — fog fading back")} ({fade:0.#}s)");
        }

        // live config -> fog params, cheap enough to run every tick
        private static void Apply()
        {
            if (!_fog.enabled) _fog.enabled = true;
            if (_areas.Count > 0)
            {
                // areas carry the fog; the camera component stays as the RENDERER host
                // with its own global fog muted
                _fog.renderBeforeTransparent = true;
                _fog.density = 0f;
                _fog.skyHaze = 0f;
                for (int i = 0; i < _areas.Count; i++)
                {
                    var a = _areas[i];
                    if (a == null) continue;
                    ApplyParams(a);
                }
                return;
            }
            ApplyParams(_fog);
            // re-push every tick — engine/scene transitions can reset global state,
            // and this also makes the falloff slider live
            if (_voidCount > 0) PushVoids();
            PushDistanceWall();
        }

        // maps MaxFogLength/MaxLengthFallOff onto the shader's distance WALL: fog
        // opacity ramps UP with pixel distance, fully opaque at MaxFogLength — the far
        // field (and the snow terrain border) disappears into it. falloff = ramp width
        // as a fraction of max length. WallShade darkens the wall vs the lit fog color
        // so the far field reads as gloom, not glow.
        private static void PushDistanceWall()
        {
            float end = Plugin.Fog(Plugin.VolFogMaxLength).Value;
            float start = end * (1f - Mathf.Clamp01(Plugin.Fog(Plugin.VolFogMaxLengthFallOff).Value));
            Shader.SetGlobalFloat("_MDistWallStart", start);
            Shader.SetGlobalFloat("_MDistWallInvRange", 1f / Mathf.Max(end - start, 1f));
            Shader.SetGlobalFloat("_MDistWallShade", Plugin.Fog(Plugin.VolFogWallShade).Value);
        }

        // shared param push — the camera fog in global mode, or each authored area
        private static void ApplyParams(VolumetricFogAndMist.VolumetricFog f)
        {
            bool isCamera = f == _fog;
            if (isCamera)
            {
                // render in the [ImageEffectOpaque] stage (VolumetricFogPreT) — same
                // slot as EFTs own fog effects, BEFORE the transparent pass and BEFORE
                // bsg's upscale/composite chain. the post-transparency path ran after
                // that chain and drew the whole frame flipped into a corner sub-rect.
                f.renderBeforeTransparent = true;
                // the downsampled COMPOSITION path is what flips/quadrants the screen
                // under EFT's render pipeline — inline rendering only
                f.downsampling = Plugin.VolFogDownsampling.Value;
                f.forceComposition = false;
                f.skyHaze = Plugin.Fog(Plugin.VolFogSkyHaze).Value;
            }
            f.preset = FOG_PRESET.Custom;
            f.density = Plugin.Fog(Plugin.VolFogDensity).Value;
            f.noiseStrength = Plugin.Fog(Plugin.VolFogNoise).Value;
            f.noiseScale = Plugin.Fog(Plugin.VolFogNoiseScale).Value;
            if (isCamera)
            {
                // areas take height/baseline from their authored box; only the global
                // layer uses the sliders
                f.height = Plugin.Fog(Plugin.VolFogHeight).Value;
                f.baselineHeight = Plugin.Fog(Plugin.VolFogBaseline).Value;
            }
            f.heightFallOff = 0.6f;
            f.distance = 0f;
            f.maxFogLength = Plugin.Fog(Plugin.VolFogMaxLength).Value;
            f.maxFogLengthFallOff = Plugin.Fog(Plugin.VolFogMaxLengthFallOff).Value;
            f.deepObscurance = Plugin.Fog(Plugin.VolFogDeepObscurance).Value;
            f.color = Plugin.FogColorEntry.Value;
            f.skyColor = Plugin.FogColorEntry.Value;
            f.alpha = Plugin.Fog(Plugin.VolFogAlpha).Value;
            f.speed = Plugin.Fog(Plugin.VolFogSpeed).Value;
            f.windDirection = new Vector3(-1f, 0f, -0.35f); // blizzard wind heading
            // area instances render the dither pattern way stronger than the global
            // layer did — heavy visible grain inside the boxes. no dither for areas.
            f.dithering = isCamera;
            f.sunCopyColor = false; // our sun is frozen/night — keep the authored tint
            f.lightIntensity = 0f;  // no direct sun scatter at night
        }
    }
}
