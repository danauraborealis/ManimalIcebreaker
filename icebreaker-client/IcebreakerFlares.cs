using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using MultiFlare;
using Newtonsoft.Json.Linq;
using UnityEngine;
using SysIoPath = System.IO.Path;

namespace Manimal.Icebreaker
{
    // retail lens flares: 1300 MultiFlare.FlareLight components (per-lamp flare sprites)
    // rebuilt from the extraction sidecar with the GAME's own MultiFlare classes — the
    // scheduler already exists (AbstractApplication.CreateTechnicalSystems), so we only
    // recreate what the scene carried: one FlareSceneSettings (atlas + batch materials,
    // Awake -> SetupRenderer) and the FlareLights (OnEnable self-registers). data is
    // all-primitive (texId into the atlas, distances, colors) — no asset refs to chase
    // beyond the atlas texture, which ships as a PNG next to the plugin.
    internal static class IcebreakerFlares
    {
        private static bool _built;

        private static string Dir =>
            SysIoPath.Combine(SysIoPath.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "flares");

        internal static void ResetForRaid() => _built = false;

        internal static void TryBuild()
        {
            if (_built) return;
            if (!Plugin.LensFlares.Value)
            {
                _built = true; // dont re-log every caller
                Plugin.Log.LogInfo("[Flares] disabled by config (perf A/B lever) — no flares this raid");
                return;
            }
            var jsonPath = SysIoPath.Combine(Dir, "flares.json");
            if (!File.Exists(jsonPath)) { Plugin.Log.LogWarning("[Flares] no flares.json sidecar — lens flares skipped"); return; }
            _built = true;

            var root = JObject.Parse(File.ReadAllText(jsonPath));

            // ---- textures (PNG -> Texture2D) ----
            var atlasTex = LoadPng(SysIoPath.Combine(Dir, "flare_atlas.png"));
            var shitTex = LoadPng(SysIoPath.Combine(Dir, "flare_shit.png"));
            if (atlasTex == null) { Plugin.Log.LogWarning("[Flares] atlas png missing/unreadable — skipped"); return; }

            // ---- atlas SO: private nested Container[] via reflection ----
            var atlas = ScriptableObject.CreateInstance<ProFlareAtlas>();
            atlas.name = "FlareAtlas";
            var contType = typeof(ProFlareAtlas).GetNestedType("Container", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var conts = root["atlas"]?["containers"] as JArray ?? new JArray();
            var arr = Array.CreateInstance(contType, conts.Count);
            var fName = AccessTools.Field(contType, "_name");
            var fRect = AccessTools.Field(contType, "_rect");
            for (int i = 0; i < conts.Count; i++)
            {
                var c = Activator.CreateInstance(contType);
                fName.SetValue(c, conts[i].Value<string>("name") ?? $"c{i}");
                var r = conts[i]["rect"];
                fRect.SetValue(c, new Rect(r.Value<float>("x"), r.Value<float>("y"), r.Value<float>("width"), r.Value<float>("height")));
                arr.SetValue(c, i);
            }
            AccessTools.Field(typeof(ProFlareAtlas), "_containers").SetValue(atlas, arr);

            // ---- batch materials on the game's own shaders ----
            Material MakeMat(string shaderName, string matName)
            {
                Shader sh = null;
                try { sh = GClass872.Find(shaderName); } catch { }
                if (sh == null || !sh.isSupported) sh = Shader.Find(shaderName);
                if (sh == null || !sh.isSupported)
                {
                    Plugin.Log.LogWarning($"[Flares] shader '{shaderName}' not found — flares degraded");
                    return null;
                }
                var m = new Material(sh) { name = matName, mainTexture = atlasTex };
                return m;
            }
            // retail's tuned property sets ride the sidecar — critically _DepthOffset=0.1
            // (10cm depth tolerance: the flares SIT ON the flat lamp surfaces, and a
            // zero-default material lost the depth test to its own lamp on any angle
            // change — the vanishing-flare bug)
            Material MakeSlot(string slot, string fallbackShader, string matName)
            {
                var spec = root["materials"]?[slot];
                var m = MakeMat(spec?.Value<string>("shader") ?? fallbackShader, matName);
                if (m == null) return null;
                if (spec?["floats"] is JObject fo)
                    foreach (var kv in fo)
                        if (m.HasProperty(kv.Key)) m.SetFloat(kv.Key, kv.Value.Value<float>());
                if (spec?["colors"] is JObject co)
                    foreach (var kv in co)
                        if (m.HasProperty(kv.Key))
                        {
                            var a = kv.Value as JArray;
                            m.SetColor(kv.Key, new Color(a.Value<float>(0), a.Value<float>(1), a.Value<float>(2), a.Value<float>(3)));
                        }
                return m;
            }
            var normalMat = MakeSlot("normal", "Custom/multiFlare", "MultiFlare");
            var shitMat = MakeSlot("shit", "Custom/ShitOnScreen", "ShitOnScreen");
            var overlapMat = MakeSlot("overlap", "Custom/multiFlare", "FlashLightFlares");
            if (shitMat != null && shitTex != null) shitMat.SetTexture("_ShitTex", shitTex);
            if (normalMat == null) return; // no flare shader = nothing to render

            // name the shader's knobs once — if Custom/multiFlare exposes a depth bias,
            // that's the clean fix for surface z-fighting and we want to know it exists
            try
            {
                var fsh = normalMat.shader;
                var props = new List<string>();
                for (int i = 0; i < fsh.GetPropertyCount(); i++)
                    props.Add($"{fsh.GetPropertyName(i)}({fsh.GetPropertyType(i)})");
                Plugin.Log.LogDebug($"[Flares] shader '{fsh.name}' props: {string.Join(", ", props)}");
            }
            catch { }

            // ---- FlareSettings from the extracted scene payload ----
            var s = root["settings"]?["_settings"];
            BatchSettings Batch(JToken t, Material mat, int fallbackCap)
            {
                var b = new BatchSettings
                {
                    _capacity = t?.Value<int?>("_capacity") ?? fallbackCap,
                    _material = mat,
                    _blindProtectionAlphaFactor = t?.Value<float?>("_blindProtectionAlphaFactor") ?? 1f,
                    _blindProtectionSizeFactor = t?.Value<float?>("_blindProtectionSizeFactor") ?? 1f,
                    AlphaMultiplier_1 = t?.Value<float?>("AlphaMultiplier_1") ?? 1f,
                    SizeMultiplier_1 = t?.Value<float?>("SizeMultiplier_1") ?? 1f,
                };
                return b;
            }
            var overlapT = s?["_flareOverlapSettings"];
            var overlap = new FlareOverlapSettings
            {
                _capacity = overlapT?.Value<int?>("_capacity") ?? 256,
                _material = overlapMat,
                _blindProtectionAlphaFactor = overlapT?.Value<float?>("_blindProtectionAlphaFactor") ?? 1f,
                _blindProtectionSizeFactor = overlapT?.Value<float?>("_blindProtectionSizeFactor") ?? 1f,
                AlphaMultiplier_1 = overlapT?.Value<float?>("AlphaMultiplier_1") ?? 1f,
                SizeMultiplier_1 = overlapT?.Value<float?>("SizeMultiplier_1") ?? 1f,
                _maxNeighborCount = overlapT?.Value<int?>("_maxNeighborCount") ?? 5,
                _searchRange = overlapT?.Value<float?>("_searchRange") ?? 0.5f,
                _maxScaleMultiplier = overlapT?.Value<float?>("_maxScaleMultiplier") ?? 1f,
            };
            var settings = new FlareSettings
            {
                _atlas = atlas,
                _normalBatch = Batch(s?["_normalBatch"], normalMat, 2048),
                _shitBatch = Batch(s?["_shitBatch"], shitMat ?? normalMat, 256),
                _flareOverlapSettings = overlap,
            };

            // ---- FlareSceneSettings: fields must land BEFORE Awake (inactive-GO trick) ----
            var host = new GameObject("Icebreaker_FlareSettings");
            host.SetActive(false);
            var fss = host.AddComponent<FlareSceneSettings>();
            AccessTools.Field(typeof(FlareSceneSettings), "_settings").SetValue(fss, settings);
            host.SetActive(true); // Awake -> GClass1023.SetupRenderer(settings)

            // ---- the 1300 lights: ordinal-path match into the bundled hierarchy ----
            var index = BuildPathIndex();
            int placed = 0, missing = 0, drifted = 0;
            var fBool = AccessTools.Field(typeof(FlareLight), "bool_0");
            var fScale = AccessTools.Field(typeof(FlareLight), "_totalScale");
            var fAlpha = AccessTools.Field(typeof(FlareLight), "_totalAlpha");
            var fFlares = AccessTools.Field(typeof(FlareLight), "_flares");
            foreach (var row in root["flares"] as JArray ?? new JArray())
            {
                var path = row.Value<string>("go");
                if (path == null || !index.TryGetValue(path, out var t)) { missing++; continue; }
                var w = row["world"] as JArray;
                if (w != null)
                {
                    var expect = new Vector3(w.Value<float>(0), w.Value<float>(1), w.Value<float>(2));
                    if ((t.position - expect).sqrMagnitude > 1f) { drifted++; continue; } // wrong twin
                }
                var f = row["fields"];
                var jFlares = f?["_flares"] as JArray ?? new JArray();
                var flares = new MultiFlare.Flare[jFlares.Count];
                for (int i = 0; i < jFlares.Count; i++)
                {
                    var jf = jFlares[i];
                    flares[i] = new MultiFlare.Flare
                    {
                        _scale = new Vector2(jf["_scale"].Value<float>("x"), jf["_scale"].Value<float>("y")),
                        _alpha = jf.Value<float?>("_alpha") ?? 1f,
                        _color = new Color(jf["_color"].Value<float>("r"), jf["_color"].Value<float>("g"),
                                           jf["_color"].Value<float>("b"), jf["_color"].Value<float>("a")),
                        _flareType = (FlareType)(jf.Value<int?>("_flareType") ?? 0),
                        _minDist = jf.Value<float?>("_minDist") ?? 0f,
                        _maxDist = jf.Value<float?>("_maxDist") ?? 100f,
                        _minScale = jf.Value<float?>("_minScale") ?? 1f,
                        _maxScale = jf.Value<float?>("_maxScale") ?? 1f,
                        _minAlpha = jf.Value<float?>("_minAlpha") ?? 1f,
                        _maxAlpha = jf.Value<float?>("_maxAlpha") ?? 1f,
                        _texId = jf.Value<int?>("_texId") ?? 0,
                    };
                }
                // fill BEFORE OnEnable registers it: deactivate, add, set, reactivate
                var go = t.gameObject;
                bool wasActive = go.activeSelf;
                if (wasActive) go.SetActive(false);
                var light = go.AddComponent<FlareLight>();
                fBool?.SetValue(light, f?.Value<bool?>("bool_0") ?? true);
                fScale?.SetValue(light, f?.Value<float?>("_totalScale") ?? 1f);
                fAlpha?.SetValue(light, f?.Value<float?>("_totalAlpha") ?? 1f);
                fFlares?.SetValue(light, flares);
                if (wasActive) go.SetActive(true);
                placed++;

                // the vanishing-flare autopsy: if the parent lamp's material renders on
                // the OPAQUE queue (<2500) it writes depth ON the flare point and eats it
                // by view angle — retail lightplanes are transparent glow quads (no depth
                // write). three samples tell us whether the lamp queue is the culprit.
                if (placed <= 3)
                {
                    var pr = t.parent != null ? t.parent.GetComponent<Renderer>() : null;
                    var pm = pr != null ? pr.sharedMaterial : null;
                    if (pm != null)
                        Plugin.Log.LogDebug($"[Flares] lamp sample '{t.parent.name}': shader '{pm.shader.name}' renderQueue={pm.renderQueue} zwrite={(pm.HasProperty("_ZWrite") ? pm.GetFloat("_ZWrite").ToString() : "n/a")}");
                }
            }
            Plugin.Log.LogWarning($"[Flares] LENS FLARES LIVE: {placed} placed, {missing} paths unmatched, {drifted} position-drifted (skipped)");
        }

        private static Texture2D LoadPng(string path)
        {
            if (!File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            return ImageConversion.LoadImage(tex, File.ReadAllBytes(path)) ? tex : null;
        }

        // ordinal-path index over EVERY loaded scene ('name~k' disambiguator, same
        // convention as the extraction side) — one walk, then O(1) lookups for 1300 rows
        private static Dictionary<string, Transform> BuildPathIndex()
        {
            var index = new Dictionary<string, Transform>(1 << 17);
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scn = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scn.isLoaded) continue;
                foreach (var rgo in scn.GetRootGameObjects())
                    Walk(rgo.transform, rgo.name, index);
            }
            return index;
        }

        private static void Walk(Transform t, string path, Dictionary<string, Transform> index)
        {
            index[path] = t;
            var seen = new Dictionary<string, int>();
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                seen.TryGetValue(c.name, out var k);
                seen[c.name] = k + 1;
                Walk(c, path + "/" + (k == 0 ? c.name : c.name + "~" + k), index);
            }
        }
    }
}
