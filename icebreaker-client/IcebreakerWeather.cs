using System;
using System.Collections.Generic;
using System.Linq;
using SysIoPath = System.IO.Path;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Icebreaker
{
    // resurrection #3: the weather/sky stack (WeatherController + TOD_Sky + rain/snow).
    // the retail Weather + Sky Dome GO trees SHIPPED in our bundle with engine components
    // intact — only the 24 driving MonoBehaviours were stripped. this rebuilds them from
    // the sidecar (analysis/extract_weather.py; all 24 parsed byte-exact, zero drift),
    // reconstructs the 4 settings ScriptableObjects from recovered values, resolves the
    // engine assets by name (Hidden/* shaders via Shader.Find; meshes/materials/textures
    // via FindObjectsOfTypeAll among bundle-loaded assets — missing ones get a census log
    // so the editor carrier pass knows what to add).
    //
    // once alive, VANILLA systems drive everything: server weather -> WeatherController
    // curve -> wind/rain/clouds; season Winter -> RainController snow state. the only
    // non-vanilla nudge is Patch_ForceWinterSeason (icebreaker-only winter).
    internal static class IcebreakerWeather
    {
        private static JObject _sidecar;
        private static bool _sidecarTried;
        private static GameObject _marker; // liveness — dies with the raid scenes

        private static readonly Dictionary<long, Component> _comps = new Dictionary<long, Component>();
        private static readonly Dictionary<string, Transform> _pathIndex = new Dictionary<string, Transform>();
        private static readonly Dictionary<string, UnityEngine.Object> _assetCache = new Dictionary<string, UnityEngine.Object>();
        private static readonly Dictionary<string, UnityEngine.Object> _settingsCache = new Dictionary<string, UnityEngine.Object>();
        private static readonly List<string> _missingAssets = new List<string>();

        private static string DataDir =>
            SysIoPath.Combine(SysIoPath.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "weather");

        private static JObject Sidecar()
        {
            if (_sidecarTried) return _sidecar;
            _sidecarTried = true;
            try
            {
                var path = SysIoPath.Combine(DataDir, "icebreaker_weather.json");
                if (!System.IO.File.Exists(path))
                {
                    Plugin.Log.LogDebug($"[Weather] no sidecar at {path} — weather stays off");
                    return null;
                }
                _sidecar = JObject.Parse(System.IO.File.ReadAllText(path));
                Plugin.Log.LogInfo("[Weather] sidecar loaded");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Weather] sidecar parse failed: {e.Message}");
            }
            return _sidecar;
        }

        public static bool TryBuild()
        {
            if (_marker != null) return true;
            var sc = Sidecar();
            var comps = sc?["components"] as JObject;
            if (comps == null || !IcebreakerAcoustics.IcebreakerLoaded()) return false;

            // per-raid snow/obstacle state reset — these statics survived into raid 2+ of a
            // session, where the fresh scene components then never got sizes/StormFactor/
            // obstacle applied (_sf held raid-1's destroyed component = unity fake-null)
            _snowShaderFixed = false; _snowRebindTick = 0; _snowRebindTries = 0; _snowSkipLogs = 0;
            _snowCopiesCached = false; _sf = null; _snowMats = null;
            _obstacleFixed = false; _blizzardTicks = 0; _stormRaised = false;

            try
            {
                _comps.Clear(); _pathIndex.Clear(); _assetCache.Clear();
                _settingsCache.Clear(); _missingAssets.Clear();

                // the two subtree roots survive in the bundled Scripts scene
                var roots = new List<Transform>();
                foreach (var rootName in new[] { "Weather", "Sky Dome" })
                {
                    var t = FindSceneGo(rootName);
                    if (t == null) { Plugin.Log.LogDebug($"[Weather] root '{rootName}' not in scene — aborting"); return false; }
                    roots.Add(t);
                    IndexSubtree(t, rootName);
                }

                var reactivate = new List<GameObject>();
                foreach (var r in roots)
                    if (r.gameObject.activeSelf) { r.gameObject.SetActive(false); reactivate.Add(r.gameObject); }

                // pass 1 — create all components (fields land before any Awake runs).
                // ORDER + REUSE MATTER: TOD_Sky is [RequireComponent(TOD_Resources,
                // TOD_Components)] — adding it first made unity auto-add BLANK ones, our
                // filled copies stacked as duplicates, and TOD_Sky.Initialize's
                // GetComponent grabbed the blanks (null Quad -> the NRE cascade that
                // killed the first live run). so: dependees added LAST, and every add is
                // get-or-add so a RequireComponent auto-add gets FILLED, never duplicated.
                int created = 0, missingGo = 0;
                var rows = new List<(JToken row, Component comp)>();
                var deferred = new[] { "TOD_Sky", "EFT.Weather.WeatherController" };
                var ordered = comps.Properties()
                    .SelectMany(p => ((p.Value as JArray) ?? new JArray()).Cast<JToken>())
                    .OrderBy(r => Array.IndexOf(deferred, r.Value<string>("class")) >= 0 ? 1 : 0)
                    .ToList();
                foreach (var row in ordered)
                {
                    var type = AccessTools.TypeByName(row.Value<string>("class"));
                    if (type == null) { Plugin.Log.LogWarning($"[Weather] type not found: {row.Value<string>("class")}"); continue; }
                    var go = _pathIndex.TryGetValue(row.Value<string>("go") ?? "", out var t) ? t.gameObject : null;
                    if (go == null) { missingGo++; continue; }
                    var c = go.GetComponent(type) ?? go.AddComponent(type);
                    if (c == null) continue; // unity refused (abstract/invalid)
                    _comps[row.Value<long>("path_id")] = c;
                    rows.Add((row, c));
                    created++;
                }
                if (missingGo > 0) Plugin.Log.LogWarning($"[Weather] {missingGo} component GOs not found in bundled tree");

                // pass 2 — fields + refs
                foreach (var (row, c) in rows)
                    if (row["fields"] is JObject f)
                        FillFields(c, f);

                // pass 3 — wake it all up (unity logs+continues on any individual Awake throw)
                foreach (var go in reactivate) go.SetActive(true);

                // the sidecar carries retail's MBOIT=true on TOD_Scattering — method_4's
                // MBOIT branch needs GameDateTime which wont exist for many frames yet.
                // without this preemptive disarm, method_4 NREs ~7 frames before
                // TickBlizzard runs, aborting before method_8..14 push weather shader
                // globals and leaving transparent objects (smoke, rotor blur) black.
                var earlyWc = EFT.Weather.WeatherController.Instance;
                if (earlyWc != null)
                {
                    earlyWc.MBOITFogRemapDataV2 = null;
                    earlyWc.MBOITFogRemapData = null;
                    var earlyScat = AccessTools.Field(typeof(EFT.Weather.WeatherController), "tod_Scattering_0")?.GetValue(earlyWc) as TOD_Scattering;
                    if (earlyScat != null && earlyScat.MBOIT)
                    {
                        earlyScat.MBOIT = false;
                        Plugin.Log.LogInfo("[Weather] MBOIT pre-disarmed at build time (startup NRE race eliminated)");
                    }
                }

                _marker = new GameObject("Icebreaker_WeatherStaged");
                var scr = SceneManager.GetSceneByName("Icebreaker_Scripts");
                if (scr.IsValid() && scr.isLoaded) SceneManager.MoveGameObjectToScene(_marker, scr);

                if (_missingAssets.Count > 0)
                    Plugin.Log.LogWarning($"[Weather] MISSING ASSETS ({_missingAssets.Count}) — add via editor carrier pass: {string.Join(", ", _missingAssets.Distinct())}");
                WarmSnowEarly(); // shader+settings onto Close/Far NOW, during load — Start()'s
                                 // runtime copies inherit them, so flakes are valid from their
                                 // first visible frame (was: pop-in seconds into the raid)
                Plugin.Log.LogDebug($"[Weather] WEATHER STACK REBUILT: {created} components — vanilla weather/seasons live"
                    + (EFT.Weather.WeatherController.Instance != null ? " (WeatherController.Instance OK)" : " (WARNING: Instance still null!)"));
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Weather] rebuild failed: {e}");
                return false;
            }
        }

        // ------------------------------------------------------------- blizzard pin
        // WeatherDebug.Enabled bypasses the server weather curve wholesale — the sanctioned
        // override surface. in winter the Rain value renders as SNOW intensity, and debug
        // mode also drives the TOD clock from WeatherDebug.Date (fixes "really dark":
        // nothing else syncs the raid hour into the rebuilt sky, so it rendered whatever
        // hour the recovered Date held). StormStartedEvent escalates the winter state to
        // SetupWinterStorm — BSG's actual blizzard.
        private static bool _stormRaised;
        private static bool _mboitChecked;

        // 0.16.9 never uses MBOIT so no camera prefab carries the component — but ALL
        // its parts ship in resources.assets (the 4 compute shaders under their real
        // spaced names + the bayer_matrix dither). construct it with retail level525's
        // serialized settings (analysis/icebreaker_mboit_component.json).
        internal static MBOIT_Scattering BuildMboit(Camera cam)
        {
            var cs = Resources.FindObjectsOfTypeAll<ComputeShader>();
            ComputeShader Find(string n)
            {
                foreach (var c in cs) if (c != null && c.name == n) return c;
                return null;
            }
            var slices = Find("Scattering Slices");
            var moments = Find("Scattering MBOIT Moments");
            var momentsRcp = Find("Scattering MBOIT MomentsRcp");
            var finalPass = Find("Scattering MBOIT FinalPass");
            if (slices == null || moments == null || momentsRcp == null || finalPass == null)
            {
                Plugin.Log.LogWarning($"[Weather] MBOIT computes missing (slices={slices != null} moments={moments != null} rcp={momentsRcp != null} final={finalPass != null})");
                return null;
            }
            Texture2D dither = null;
            foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>())
                if (t != null && t.name == "bayer_matrix") { dither = t; break; }

            var mb = cam.gameObject.AddComponent<MBOIT_Scattering>();
            mb.ScatteringSlicesComputeShader = slices;
            mb.ScatteringMBOITComputeShader = moments;
            mb.ScatteringMBOITRCPComputeShader = momentsRcp;
            mb.ScatteringMBOITFinalPassComputeShader = finalPass;
            if (dither != null) mb.DitheringTexture = dither;
            // retail 1.0 RUNTIME values — straight from the 1.0 WeatherController.Awake
            // decompile (the il2cpp WIP dump), not the optic-camera copy we used before
            mb.Lighten = false;
            mb.FromLevelSettings = true;
            mb.ScatterColorMultiplier = new Color(1f, 1f, 1f, 0.4705f);
            mb.ScatterGreyscale = 1f;
            mb.ScatterDensityMultiplier = 2.15f;
            mb.ScatterDensityPower = 3.19f;
            mb.ScatterDensityBias = 0.56f;
            mb.HeightFalloffFactor = 1f;
            mb.SOFT_SCALE = 1627f;
            mb.SOFT_CONTRAST_POWER = 1f;
            mb.GlobalDensity = 0.001f;
            mb.HeightFalloff = 0.001f;
            mb.SunrizeGlow = 0.95f;
            mb.MBOITDitherScale = 1f;
            mb.ZeroLevel = 0f;
            mb.SlicesDistributionExponent = 1.27f;
            // SlicesFarDistance: retail 0.006 vs 0.16.9 default 0.02 (3.3x) — the froxel
            // slice distribution knob, prime suspect for the hard fog wall. private field.
            AccessTools.Field(typeof(MBOIT_Scattering), "float_0")?.SetValue(mb, 0.006f);

            // the Scattering Slices compute reads the _StencilShadow global that BSG's
            // AmbientLight command buffer normally provides per-frame — that system isnt
            // running on the backport, the global is unbound, and unity refuses the whole
            // kernel dispatch ("Property (_StencilShadow) ... is not set" = zero fog
            // output). bind a 1x1 no-shadow texture (AmbientLight clears its RT to
            // 0,0,0,0) so the kernel runs unshadowed.
            var noShadow = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            noShadow.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f));
            noShadow.Apply();
            Shader.SetGlobalTexture("_StencilShadow", noShadow);

            // same story, next dependency: the slices compute wants the CameraParameters
            // buffer (near/far/frustum corner rays) that WindowsManager's glass system
            // uploads per camera — also not running here. build it ourselves with
            // WindowsManager.method_7's exact layout and refresh per frame (fov changes
            // when aiming).
            if (_camParamsBuf != null) { try { _camParamsBuf.Release(); } catch { } }
            _camParamsBuf = new ComputeBuffer(20, sizeof(float));
            _camParamsCam = cam;
            TickCameraParams();
            Shader.SetGlobalBuffer("CameraParameters", _camParamsBuf);
            Plugin.Log.LogDebug("[Weather] MBOIT_Scattering CONSTRUCTED — retail 1.0 values + _StencilShadow & CameraParameters globals bound (compute can dispatch)");
            return mb;
        }
        private static ComputeBuffer _camParamsBuf;
        private static Camera _camParamsCam;
        private static readonly float[] _camParams = new float[20];

        // WindowsManager.method_7 reproduction — the CameraParameters layout the MBOIT
        // slices compute reads: [near, far, far-near, backgroundSplit(30), then 4 far-
        // plane corner rays as float4 (BL, BR? — matching bsg's corner order exactly)]
        internal static void TickCameraParams()
        {
            var cam = _camParamsCam;
            if (cam == null || _camParamsBuf == null) return;
            float far = cam.farClipPlane;
            _camParams[0] = cam.nearClipPlane;
            _camParams[1] = far;
            _camParams[2] = far - cam.nearClipPlane;
            _camParams[3] = 30f; // WindowsManager.BackgroundSplitDistance default
            float half = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            var fwd = new Vector3(0f, 0f, -1f) * far;
            var right = new Vector3(1f, 0f, 0f) * (Mathf.Tan(half) * cam.aspect * far);
            var up = new Vector3(0f, 1f, 0f) * (Mathf.Tan(half) * far);
            Corner(4, fwd - right + up);
            Corner(8, fwd + right + up);
            Corner(12, fwd + right - up);
            Corner(16, fwd - right - up);
            _camParamsBuf.SetData(_camParams);
        }

        private static void Corner(int i, Vector3 v)
        {
            _camParams[i] = v.x; _camParams[i + 1] = v.y; _camParams[i + 2] = v.z; _camParams[i + 3] = 1f;
        }

        private static int _blizzardTicks;
        private static bool _snowShaderFixed;
        private static int _snowRebindTries;
        private static bool _obstacleFixed;
        private static bool _snowCopiesCached;
        private static List<Material> _snowMats;
        private static SnowFlakes _sf;
        private static Vector2 _origSizeMin, _origSizeMax;
        private static bool _origSizeCaptured;
        private static readonly int _fvId = Shader.PropertyToID("_FallingVector");
        private static readonly int _scaleId = Shader.PropertyToID("_Scale");

        private static Behaviour _tonemap;
        private static bool _tonemapSearched;

        public static void TickBlizzard()
        {
            var wc = EFT.Weather.WeatherController.Instance;
            if (wc == null || wc.WeatherDebug == null) return;
            IcebreakerSky.TryApply(); // retail atmosphere — one-shot once the sky singleton exists
            var wd = wc.WeatherDebug;

            // MBOIT VOLUMETRIC FOG (the retail-1.0 haze). history: the sidecar-filled remap
            // objects were drifted junk (non-null) which armed tod_Scattering.MBOIT while
            // method_4's branch deref'd a never-created GameDateTime — 3k NREs/raid, dead
            // weather tick, the pitch-black raid. the pieces have moved since: SetDebugDate
            // (below, every blizzard tick) CREATES that GameDateTime, and the branch's only
            // other requirement is a valid FogRemapDataV2 — whose default-constructed record
            // ships complete identity curves. 0.16.9 never uses this path only because no
            // remap ASSETS exist in its files; the code+shaders are all present.
            var scat = AccessTools.Field(typeof(EFT.Weather.WeatherController), "tod_Scattering_0")?.GetValue(wc) as TOD_Scattering;
            bool dateReady = false;
            try { dateReady = GClass4.Instance?.CurrentTime?.GameDateTime != null; } catch { }
            if (false) // MBOIT dead end — no WindowsManager on this map; VolumetricFog&Mist2 carries the fog now
            {
                wc.MBOITFogRemapData = null; // v1 stays dead — only junk ever lived there
                if (wc.MBOITFogRemapDataV2 == null || wc.MBOITFogRemapDataV2.name != "manimal_fog_remap")
                {
                    var remap = ScriptableObject.CreateInstance<EFT.Weather.FogRemapDataV2>();
                    remap.name = "manimal_fog_remap";
                    // the class's default record is authoring scaffolding (density ramps
                    // evaluated at tiny fog values ≈ invisible) — load retail 1.0's tuned
                    // curves instead
                    remap.Record = RetailFogRemap.Build();
                    wc.MBOITFogRemapDataV2 = remap;
                    Plugin.Log.LogDebug("[Weather] VOLUMETRIC fog armed — retail 1.0 remap record + MBOIT scattering");
                }
                if (scat != null && !scat.MBOIT) scat.MBOIT = true;

                // the tuned params only reach the shader through the camera's
                // MBOIT_Scattering image effect (method_9 target) — verify it's bound
                if (!_mboitChecked && scat != null)
                {
                    _mboitChecked = true;
                    var mb = AccessTools.Field(typeof(EFT.Weather.WeatherController), "mboit_Scattering_0")?.GetValue(wc) as MBOIT_Scattering;
                    if (mb == null)
                    {
                        var cam = scat.GetComponent<Camera>();
                        mb = cam != null ? cam.GetComponent<MBOIT_Scattering>() : null;
                        if (mb == null && cam != null)
                            mb = BuildMboit(cam); // 0.16.9 ships no camera prefab with it — construct from parts
                        if (mb != null)
                        {
                            // TOD_Scattering one-shot-cached GetComponent<MBOIT_Scattering>()
                            // long before we added it — its composite reads a stale null and
                            // the volumetric output never reaches the screen. refresh it.
                            AccessTools.Field(typeof(TOD_Scattering), "mboit_Scattering_0")?.SetValue(scat, mb);
                            mb.Sky = MonoBehaviourSingleton<TOD_Sky>.Instance;
                            AccessTools.Field(typeof(EFT.Weather.WeatherController), "mboit_Scattering_0")?.SetValue(wc, mb);
                            Plugin.Log.LogDebug("[Weather] MBOIT_Scattering bound to wc — volumetric params flowing");
                        }
                        else
                            Plugin.Log.LogDebug("[Weather] MBOIT_Scattering unavailable — volumetric params cant flow (prefab-default rendering)");
                    }
                    else
                        Plugin.Log.LogDebug($"[Weather] MBOIT_Scattering bound ok (enabled={mb.enabled})");
                }
            }
            else
            {
                // fallback = the proven plain-fog path
                wc.MBOITFogRemapDataV2 = null;
                wc.MBOITFogRemapData = null;
                if (scat != null && scat.MBOIT)
                {
                    scat.MBOIT = false;
                    Plugin.Log.LogWarning("[Weather] MBOIT disarmed (VolumetricFog off or date not ready)");
                }
            }
            // FOG, decoupled from the sky: TOD_Scattering fogs toward the night sky's
            // scattering color (= black -> eats the lamps). GlobalFog is the same
            // depth-based screen fog but blends toward RenderSettings.fogColor — OUR
            // color. gray gloom the lamps fade into instead of a black veil.
            TickFog(scat);

            // the carrier bundle's flake materials point at a ripped shader unity replaced
            // with InternalErrorShader — WarmSnowEarly rebinds to native 'Custom/SnowFlakes'
            // (sees inactive GOs, sweeps child renderers = post-Start clones too) and applies
            // all settings. usually done at load; this is the retry if load-time skipped.
            // %8 not %64 — the old cadence left flakes invisible ~38s into the raid.
            if (!_snowShaderFixed && (_snowRebindTick++ % 8) == 0)
            {
                if (++_snowRebindTries <= 400) WarmSnowEarly();
                else
                {
                    _snowShaderFixed = true; // ~never: give up rather than scan forever
                    Plugin.Log.LogWarning("[Weather] snow rebind gave up after 400 tries — SnowFlakes/shader never appeared");
                }
            }

            // the rebuilt Tonemapping screen pass is the washed-out-colors suspect —
            // config-gated, default OFF (live). type lives in Assembly-CSharp-firstpass,
            // so resolve by name rather than referencing another assembly.
            if (!_tonemapSearched)
            {
                _tonemapSearched = true;
                var tt = AccessTools.TypeByName("UnityStandardAssets.ImageEffects.Tonemapping");
                if (tt != null) _tonemap = UnityEngine.Object.FindObjectOfType(tt) as Behaviour;
                // "the setting does nothing" — find out WHERE the pass actually sits: if
                // FindObjectOfType grabbed one off a non-rendering camera the toggle is
                // cosmetic, and if the shader is unsupported the effect no-ops silently
                if (_tonemap != null)
                {
                    var cam = _tonemap.GetComponent<Camera>();
                    Plugin.Log.LogDebug($"[Weather] Tonemapping instance on '{_tonemap.gameObject.name}' camEnabled={(cam != null && cam.enabled)} camDepth={(cam != null ? cam.depth.ToString("0") : "n/a")}");
                }
            }
            if (_tonemap != null && _tonemap.enabled != Plugin.WeatherTonemap.Value)
            {
                _tonemap.enabled = Plugin.WeatherTonemap.Value;
                Plugin.Log.LogDebug($"[Weather] Tonemapping pass {(_tonemap.enabled ? "ENABLED" : "disabled")} (washed-out colors lever)");
            }

            if (!Plugin.Blizzard.Value)
            {
                if (wd.Enabled) { wd.Enabled = false; }
                return;
            }

            wd.Rain = Plugin.SnowIntensity.Value;
            wd.WindMagnitude = Plugin.BlizzardWind.Value;
            wd.CloudDensity = 1f;
            wd.Fog = Plugin.BlizzardFog.Value;
            wd.Temperature = -20f;
            wd.LightningThunderProbability = 0f;
            wd.SetHour(Plugin.TodHour.Value);
            wd.Enabled = true;
            // SetHour only edits WeatherDebug.Date — nothing was pushing it into the TOD
            // cycle (SunHeight sat at -0.27: night). SetDebugDate applies it AND creates the
            // GameDateTime that half the weather code null-checks. every tick = time frozen
            // at the pinned hour, which is exactly what a pinned blizzard wants.
            wc.SetDebugDate();

            // the winter-STORM escalation — raise once, a few ticks in so the seasons
            // controller has finished subscribing
            _blizzardTicks++;
            if (!_stormRaised && _blizzardTicks > 10)
            {
                _stormRaised = true;
                try
                {
                    GlobalEventHandlerClass.CreateEvent<EFT.GlobalEvents.StormStartedEvent>().Invoke();
                    Plugin.Log.LogDebug("[Weather] BLIZZARD: WeatherDebug pinned (snow/wind/fog/hour) + StormStartedEvent raised");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Weather] storm event raise failed: {e.Message}"); }
            }

            // live fall-vector drive: retail 1.0 material said (1, -2, 0.2) and flipping the
            // sign didn't cure the upward drift, so the y component is a config slider —
            // dial it in-raid until the flakes fall. x/z kept from retail (side drift).
            // IMPORTANT: the renderers use COPIES cloned in SnowFlakes.Start, not Close/Far —
            // last raid the cache only held the sources and the slider drove nothing visible.
            // keep sweeping the child renderers until their copies are in the list.
            if (_snowMats != null)
            {
                if (!_snowCopiesCached && (_snowSearchTick++ % 64) == 0)
                {
                    var sfm = UnityEngine.Object.FindObjectOfType<SnowFlakes>();
                    if (sfm != null)
                    {
                        var mrs = sfm.GetComponentsInChildren<MeshRenderer>(true);
                        foreach (var mr in mrs)
                            if (mr.sharedMaterial != null && !_snowMats.Contains(mr.sharedMaterial))
                                _snowMats.Add(mr.sharedMaterial);
                        if (mrs.Length > 0)
                        {
                            _snowCopiesCached = true;
                            _sf = sfm;
                            // only cache pristine sizes ONCE — WarmSnowEarly already scaled the
                            // component, so re-reading here compounds the factor (0.3 -> 0.09:
                            // the flakes-shrink-to-invisible-mid-raid bug)
                            if (!_origSizeCaptured)
                            {
                                _origSizeMin = sfm.FlakesSizeMin;
                                _origSizeMax = sfm.FlakesSizeMax;
                                _origSizeCaptured = true;
                            }
                            Plugin.Log.LogDebug($"[Weather] snow fall-vector now driving {_snowMats.Count} material(s) incl renderer copies");
                        }
                    }
                }
                var fv = new Vector4(1f, Plugin.SnowFallY.Value, 0.2f, 0f);
                foreach (var m in _snowMats)
                    if (m != null)
                    {
                        m.SetVector(_fvId, fv);
                        m.SetFloat(_scaleId, Plugin.SnowSpread.Value); // smaller volume = same flakes packed denser
                    }
                if (_sf != null)
                {
                    // size: scale the retail min/max pair; density: the storm layers are
                    // +12k flakes the base winter state never enables (StormStarted is a
                    // no-op in winter season) — drive StormFactor from config instead
                    float fs = Plugin.SnowFlakeSize.Value;
                    _sf.FlakesSizeMin = _origSizeMin * fs;
                    _sf.FlakesSizeMax = _origSizeMax * fs;
                    _sf.StormFactor = Plugin.SnowStormDensity.Value ? 1f : 0f;
                }
            }

            // WeatherObstacle rehydration: the flake shader clips against a top-down depth
            // mask of a dedicated roof mesh — retail's is 'Icebreaker_DryPlanes_Snow' (138
            // DryPlane children carrying just a Mesh ref, good bundle-survival odds). the
            // winter season path that renders the mask bails on the missing visual ref, so
            // build it ourselves: activate GO, add WeatherObstacle (Awake combines the
            // DryPlanes into a MeshCollider), force the singleton, hand DepthPhotograper
            // its bounds (LevelSettings.RainBounds doesn't exist here), render the mask.
            if (!_obstacleFixed && _blizzardTicks > 20)
            {
                _obstacleFixed = true;
                try
                {
                    var t = FindSceneGo("Icebreaker_DryPlanes_Snow") ?? FindSceneGo("Icebreaker_DryPlanes");
                    if (t == null)
                        Plugin.Log.LogDebug("[Weather] no DryPlanes GO in scene — snow stays unmasked indoors");
                    else
                    {
                        var dryCount = t.GetComponentsInChildren<EFT.EnvironmentEffect.DryPlane>(true).Length;

                        // the DryPlane COMPONENTS were stripped in the rip, but the roof
                        // geometry survived as ~90 quad MeshFilters under the same GO (unit
                        // Quad meshes shaped by their transforms). combine those — exactly
                        // what WeatherObstacle.Awake would have done with DryPlane.Mesh.
                        // MUST happen before AddComponent: Awake destroys all children.
                        Mesh combined = null;
                        if (dryCount == 0)
                        {
                            var cis = new List<CombineInstance>();
                            foreach (var mf in t.GetComponentsInChildren<MeshFilter>(true))
                                if (mf.sharedMesh != null)
                                    cis.Add(new CombineInstance { mesh = mf.sharedMesh, transform = mf.transform.localToWorldMatrix });
                            if (cis.Count > 0)
                            {
                                combined = new Mesh { name = "IcebreakerDryPlanes Combined" };
                                combined.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                                combined.CombineMeshes(cis.ToArray(), true, true);
                            }
                        }

                        t.gameObject.SetActive(true);
                        var wo = t.GetComponent<WeatherObstacle>();
                        if (wo == null) wo = t.gameObject.AddComponent<WeatherObstacle>(); // combines dryCount (maybe 0) + clears children
                        AccessTools.Field(typeof(WeatherObstacle), "weatherObstacle_0")?.SetValue(null, wo);

                        if (combined != null && (wo.MeshCollider == null || wo.MeshCollider.sharedMesh == null
                            || wo.MeshCollider.sharedMesh.vertexCount == 0))
                        {
                            var mc = wo.GetComponent<MeshCollider>();
                            if (mc == null) mc = wo.gameObject.AddComponent<MeshCollider>();
                            mc.sharedMesh = combined;
                            wo.MeshCollider = mc;
                        }

                        var dp = DepthPhotograper.Instance ?? UnityEngine.Object.FindObjectOfType<DepthPhotograper>();
                        if (dp != null && wo.MeshCollider != null && wo.MeshCollider.sharedMesh != null
                            && wo.MeshCollider.sharedMesh.vertexCount > 0)
                        {
                            var b = wo.MeshCollider.bounds;
                            b.Expand(new Vector3(20f, 10f, 20f));
                            AccessTools.Field(typeof(DepthPhotograper), "bounds_0")?.SetValue(dp, b);
                            dp.Render(); // one-time top-down render of JUST the obstacle mesh
                            Plugin.Log.LogDebug($"[Weather] WEATHER OBSTACLE LIVE: '{t.name}' src={(dryCount > 0 ? dryCount + " DryPlanes" : "quad MeshFilters")} -> {wo.MeshCollider.sharedMesh.vertexCount} verts, mask over {b.size.x:0}x{b.size.z:0}m — indoor snow clipped");
                        }
                        else if (dp != null)
                        {
                            // no quads either — last resort: one-time top-down render of the
                            // real ship geometry (the ship is its own roof). not per-frame.
                            RenderSceneDepthMask(dp);
                        }
                        else
                            Plugin.Log.LogWarning("[Weather] obstacle rebuild incomplete: no DepthPhotograper");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Weather] obstacle rebuild threw: {e.Message}"); }
            }

            // one-shot snow autopsy a while after the storm raise: the value chain checked
            // out statically (weather tick clean, Rain pinned to 1) yet nothing renders —
            // dump the flake pipeline state so the next raid log tells us which stage died
            // (renderers off = intensity never arrived; shader unsupported = carrier
            // material's ripped shader didn't rebind; neither = depth-mask/shader-global)
            if (!_snowDiag && _blizzardTicks > 40)
            {
                _snowDiag = true;
                try
                {
                    var sf = UnityEngine.Object.FindObjectOfType<SnowFlakes>();
                    if (sf == null) { Plugin.Log.LogDebug("[SnowDiag] NO SnowFlakes component in scene"); }
                    else
                    {
                        Plugin.Log.LogDebug($"[SnowDiag] go='{sf.gameObject.name}' active={sf.gameObject.activeInHierarchy} enabled={sf.enabled} Intensity={sf.Intensity:0.00} StormFactor={sf.StormFactor:0.00} pos={sf.transform.position}");
                        var close = AccessTools.Field(typeof(SnowFlakes), "Close")?.GetValue(sf) as Material;
                        var far = AccessTools.Field(typeof(SnowFlakes), "Far")?.GetValue(sf) as Material;
                        Plugin.Log.LogDebug($"[SnowDiag] Close={(close ? close.name : "NULL")} shader={(close && close.shader ? close.shader.name + " sup=" + close.shader.isSupported : "NULL")} | Far={(far ? far.name : "NULL")} shader={(far && far.shader ? far.shader.name + " sup=" + far.shader.isSupported : "NULL")}");
                        var mrs = sf.GetComponentsInChildren<MeshRenderer>(true);
                        foreach (var mr in mrs)
                            Plugin.Log.LogDebug($"[SnowDiag] renderer '{mr.gameObject.name}' enabled={mr.enabled} visible={mr.isVisible} mat={(mr.sharedMaterial ? mr.sharedMaterial.shader.name : "NULL")}");
                        if (mrs.Length == 0) Plugin.Log.LogWarning("[SnowDiag] ZERO child renderers — Start() never built the flake meshes");
                        var rc = UnityEngine.Object.FindObjectOfType<RainController>();
                        Plugin.Log.LogDebug($"[SnowDiag] RainController={(rc != null)} wc.RainController={(wc.RainController != null)} wcRain={wc.WeatherCurve.Rain:0.00}");
                        // sun/darkness probe: SunHeight is written by method_4 each frame, so
                        // it doubles as proof the tick is alive AND that the hour synced (day
                        // = positive). ambient tells us what indoor pixels have to work with.
                        var scat2 = AccessTools.Field(typeof(EFT.Weather.WeatherController), "tod_Scattering_0")?.GetValue(wc) as TOD_Scattering;
                        Plugin.Log.LogDebug($"[SnowDiag] SunHeight={wc.SunHeight:0.000} scattering={(scat2 != null ? (scat2.enabled ? $"ON density={scat2.GlobalDensity:0.0000}" : "off") : "none")} ambient={RenderSettings.ambientLight} ambientIntensity={RenderSettings.ambientIntensity:0.00} ambientMode={RenderSettings.ambientMode}");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[SnowDiag] failed: {e.Message}"); }
            }
        }
        private static bool _snowDiag;

        // fog driver. preferred: UnityStandardAssets GlobalFog (firstpass image effect) —
        // depth fog toward RenderSettings.fogColor, fully sky-independent. AddComponent
        // leaves its fogShader null (inspector-wired normally) so it must be assigned or
        // the effect silently no-ops. fallback when the type/shader is missing: keep
        // TOD_Scattering and brighten the night sky's ColorMultiplier so its fog color
        // stops being pure black.
        private static Behaviour _globalFog;
        private static bool _fogSearched;
        private static Camera _opticCam;
        private static bool _opticLogged;
        private static int _snowSearchTick;

        // load-time snow warmup: rebind + configure the SOURCE materials (Close/Far)
        // before SnowFlakes.Start clones them — the clones then carry the right shader,
        // fall vector, spread and sizes from frame one. the per-tick drive still sweeps
        // the clones later for live slider changes.
        private static void WarmSnowEarly()
        {
            try
            {
                // FindObjectOfType skips inactive — and at rebuild time the SnowFlakes GO
                // isnt live yet (07-07 log: "waiting for SnowFlakes to appear", then error
                // shader visible in SnowDiag until t≈38s = the pop-in). FindObjectsOfTypeAll
                // sees inactive scene objects; filter out prefab/asset hits via the scene.
                SnowFlakes sf = null;
                foreach (var cand in Resources.FindObjectsOfTypeAll<SnowFlakes>())
                    if (cand != null && cand.gameObject.scene.IsValid()) { sf = cand; break; }
                var native = GClass872.Find("Custom/SnowFlakes");
                if (sf == null || native == null || !native.isSupported)
                {
                    if ((_snowSkipLogs++ % 50) == 0)
                        Plugin.Log.LogWarning($"[Weather] snow warmup skipped: snowFlakes={(sf != null)} shader={(native != null)} supported={(native != null && native.isSupported)} — tick path will retry");
                    return;
                }

                _snowMats = new List<Material>();
                var fv = new Vector4(1f, Plugin.SnowFallY.Value, 0.2f, 0f);
                void Warm(Material m)
                {
                    if (m == null || _snowMats.Contains(m)) return;
                    m.shader = native;
                    m.SetVector(_fvId, fv);
                    m.SetFloat(_scaleId, Plugin.SnowSpread.Value);
                    _snowMats.Add(m);
                }
                foreach (var fname in new[] { "Close", "Far" })
                    Warm(AccessTools.Field(typeof(SnowFlakes), fname)?.GetValue(sf) as Material);
                // storm-layer renderers carry their own material slots — sweep them too so
                // enabling StormFactor later doesnt surface error-shader quads
                foreach (var mr in sf.GetComponentsInChildren<MeshRenderer>(true))
                    Warm(mr.sharedMaterial);
                _sf = sf;
                if (!_origSizeCaptured)
                {
                    _origSizeMin = sf.FlakesSizeMin;
                    _origSizeMax = sf.FlakesSizeMax;
                    _origSizeCaptured = true;
                }
                float fs = Plugin.SnowFlakeSize.Value;
                sf.FlakesSizeMin = _origSizeMin * fs;
                sf.FlakesSizeMax = _origSizeMax * fs;
                sf.StormFactor = Plugin.SnowStormDensity.Value ? 1f : 0f;
                _snowShaderFixed = true;
                Plugin.Log.LogDebug($"[Weather] snow warmed at load: {_snowMats.Count} source material(s) shader+settings ready pre-Start");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Weather] snow warmup threw: {e.Message}"); }
        }
        private static int _fogDiagTick;
        private static int _snowRebindTick;
        private static int _snowSkipLogs;

        private static void TickFog(TOD_Scattering scat)
        {
            bool want = Plugin.WeatherFogPass.Value;

            // magnified scopes render through a SECOND camera (BaseOpticCamera prefab) that
            // ships its OWN TOD_Scattering. OpticComponentUpdater only mirrors the main
            // cam's enabled-state when the main cam has the component — ours didn't for
            // most raids, so the optic pass ran its prefab default and fogged the whole
            // magnified view toward the night sky: pitch-black scopes. keep it dead.
            // Camera.allCameras (a handful of entries), NOT GameObject.Find: the name scan
            // walked all ~150k objects every ~2s for the WHOLE raid when the player never
            // scoped in — the last uninstrumented random-hitch suspect in our own code.
            // the optic cam only registers while aiming, so poll every tick until cached.
            if (_opticCam == null)
            {
                var cams = Camera.allCameras;
                for (int i = 0; i < cams.Length; i++)
                    if (cams[i] != null && cams[i].name == "BaseOpticCamera(Clone)") { _opticCam = cams[i]; break; }
            }
            if (_opticCam != null)
            {
                var os = _opticCam.GetComponent<TOD_Scattering>();
                if (os != null && os.enabled)
                {
                    os.enabled = false;
                    Plugin.Log.LogDebug("[Weather] optic camera TOD_Scattering disabled (black magnified scopes)");
                }
                if (!_opticLogged)
                {
                    _opticLogged = true;
                    var parts = new List<string>();
                    foreach (var beh in _opticCam.GetComponents<Behaviour>())
                        if (beh != null) parts.Add($"{beh.GetType().Name}={(beh.enabled ? "on" : "off")}");
                    Plugin.Log.LogDebug("[Weather] optic camera stack: " + string.Join(", ", parts));
                }
            }

            if (!_fogSearched && want)
            {
                _fogSearched = true;
                try
                {
                    var gfType = AccessTools.TypeByName("UnityStandardAssets.ImageEffects.GlobalFog");
                    var gfShader = GClass872.Find("Hidden/GlobalFog");
                    var cam = scat != null ? scat.GetComponent<Camera>() : Camera.main; // scat rides the render camera
                    if (gfType != null && gfShader != null && gfShader.isSupported && cam != null)
                    {
                        _globalFog = (cam.GetComponent(gfType) ?? cam.gameObject.AddComponent(gfType)) as Behaviour;
                        AccessTools.Field(gfType, "fogShader")?.SetValue(_globalFog, gfShader);
                        AccessTools.Field(gfType, "distanceFog")?.SetValue(_globalFog, true);
                        AccessTools.Field(gfType, "useRadialDistance")?.SetValue(_globalFog, true);
                        AccessTools.Field(gfType, "heightFog")?.SetValue(_globalFog, false);
                        AccessTools.Field(gfType, "startDistance")?.SetValue(_globalFog, 5f);
                        Plugin.Log.LogDebug("[Weather] GlobalFog attached — fog color now OURS, not the night sky's");
                    }
                    else
                        Plugin.Log.LogWarning($"[Weather] GlobalFog unavailable (type={(gfType != null)} shader={(gfShader != null && gfShader.isSupported)}) — falling back to brightened TOD scattering");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Weather] GlobalFog setup threw: {e.Message}"); }
            }

            // MBOIT renders THROUGH the TOD_Scattering image-effect pass — the volumetric
            // can only show if the component itself runs. when armed, it carries the fog
            // and GlobalFog stands down; otherwise the proven plain-fog split below.
            bool volumetric = false; // MBOIT retired — see IcebreakerVolFog
            if (volumetric)
            {
                if (_globalFog != null && _globalFog.enabled) _globalFog.enabled = false;
                // the passes are EXCLUSIVE (TOD_Scattering.method_2): MBOIT on means the
                // classic component turns itself OFF and MBOIT_Scattering renders instead.
                // we used to force scat.enabled=true here — the classic veil painted over
                // the volumetric every frame and no mboit param could ever show.
                if (scat.enabled) scat.enabled = false;
                var vmb = AccessTools.Field(typeof(EFT.Weather.WeatherController), "mboit_Scattering_0")?.GetValue(EFT.Weather.WeatherController.Instance) as MBOIT_Scattering;
                if (vmb != null && !vmb.enabled)
                {
                    vmb.enabled = true;
                    Plugin.Log.LogDebug("[Weather] MBOIT_Scattering pass ENABLED, classic scattering standing down (exclusive handoff)");
                }
                // night: the volumetric media is lit by the sky — our rebuilt night sky is
                // near-black, so lift it with the same FogBrightness lever the fallback
                // branch uses (this is the pitch-black-at-23:00 fix; tune live)
                var vsky = MonoBehaviourSingleton<TOD_Sky>.Instance;
                if (vsky != null && vsky.Night != null && Plugin.FogBrightness.Value > 0.001f)
                    vsky.Night.ColorMultiplier = 1f + Plugin.FogBrightness.Value * 20f;
                if ((++_fogDiagTick % 600) == 0)
                    Plugin.Log.LogDebug($"[FogDiag] VOLUMETRIC scat.enabled={scat.enabled} MBOIT={scat.MBOIT} wdFog={Plugin.BlizzardFog.Value:0.000}");
                return;
            }

            if (_globalFog != null)
            {
                // sky-scattering pass stays off; GlobalFog carries the fog
                if (scat != null && scat.enabled) scat.enabled = false;
                if (_globalFog.enabled != want) _globalFog.enabled = want;
                if (want)
                {
                    RenderSettings.fog = true;
                    RenderSettings.fogMode = FogMode.ExponentialSquared;
                    RenderSettings.fogDensity = Plugin.BlizzardFog.Value;
                    // color picker is the SOLE color control — multiplying by FogBrightness
                    // crushed every picked tint to near-black ("color slider does nothing").
                    // darkness now comes from the picker's own value slider.
                    RenderSettings.fogColor = Plugin.FogColor.Value;

                    // GlobalFog's CheckResources can self-disable SILENTLY (info-level log
                    // only). periodic diag so a dead fog pass is visible in OUR log.
                    if ((++_fogDiagTick % 600) == 0)
                        Plugin.Log.LogDebug($"[FogDiag] globalFog.enabled={_globalFog.enabled} density={RenderSettings.fogDensity:0.000} color={RenderSettings.fogColor}");
                }
            }
            else if (scat != null)
            {
                if (scat.enabled != want)
                {
                    scat.enabled = want;
                    Plugin.Log.LogDebug($"[Weather] TOD_Scattering fog pass {(scat.enabled ? "ENABLED" : "disabled")}");
                }
                if (want)
                {
                    // fallback lever: lift the night sky out of pure black so the
                    // scattering fog color isn't a light-eating void
                    var sky = MonoBehaviourSingleton<TOD_Sky>.Instance;
                    if (sky != null && sky.Night != null && Plugin.FogBrightness.Value > 0.001f)
                        sky.Night.ColorMultiplier = 1f + Plugin.FogBrightness.Value * 20f;
                }
            }
        }

        // approximate precipitation mask when the authored roof mesh is unavailable:
        // top-down ortho render of the REAL ship geometry into the photographer's depth
        // texture, then publish via its own method_3 (sets the 3 shader globals the flake
        // shader samples). imperfect vs BSG's hand-authored DryPlanes but the ship IS the
        // roof here. bounds come from the AI voxel grid (tight around walkable ship).
        private static void RenderSceneDepthMask(DepthPhotograper dp)
        {
            var vox = UnityEngine.Object.FindObjectOfType<AIVoxelesData>();
            Bounds b;
            if (vox != null)
            {
                var min = vox.MinVoxelesValues;
                var size = new Vector3(vox.MaxVoxelesValues.x * 10f, vox.MaxVoxelesValues.y * 5f, vox.MaxVoxelesValues.z * 10f);
                b = new Bounds(min + size * 0.5f, size);
                b.Expand(new Vector3(40f, 0f, 40f));
                b.max = new Vector3(b.max.x, b.max.y + 45f, b.max.z); // masts/superstructure sit above the bot grid
            }
            else
                b = new Bounds(new Vector3(0f, 30f, 80f), new Vector3(260f, 120f, 420f)); // eyeballed ship envelope

            AccessTools.Field(typeof(DepthPhotograper), "bounds_0").SetValue(dp, b);
            dp.method_0(); // (re)creates _depthRT at the photographer's own dimension

            var dimField = AccessTools.Field(typeof(DepthPhotograper), "_depthTextureDimension");
            int dim = dimField != null ? Convert.ToInt32(dimField.GetValue(dp)) : 1024;
            var depthRt = AccessTools.Field(typeof(DepthPhotograper), "_depthRT").GetValue(dp) as RenderTexture;

            // flake quads have infinite bounds — they'd render into their own mask
            var sf = UnityEngine.Object.FindObjectOfType<SnowFlakes>();
            bool sfWasActive = sf != null && sf.gameObject.activeSelf;
            if (sfWasActive) sf.gameObject.SetActive(false);

            RenderTexture tmp = null;
            GameObject camGo = null;
            try
            {
                tmp = new RenderTexture(dim, dim, 16) { format = RenderTextureFormat.Depth, wrapMode = TextureWrapMode.Clamp };
                camGo = new GameObject("Icebreaker_SnowMaskCamera");
                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.aspect = 1f;
                cam.orthographic = true;
                cam.orthographicSize = 0.5f * Mathf.Max(b.size.x, b.size.z);
                cam.cullingMask = -1;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = b.size.y;
                cam.useOcclusionCulling = false;
                cam.clearFlags = CameraClearFlags.Color;
                cam.backgroundColor = Color.black;
                cam.renderingPath = RenderingPath.Forward;
                cam.depthTextureMode = DepthTextureMode.Depth;
                cam.targetTexture = tmp;
                cam.transform.position = new Vector3(b.center.x, b.max.y, b.center.z);
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                cam.Render();
                cam.targetTexture = null;

                var prev = RenderTexture.active;
                Graphics.Blit(tmp, depthRt);
                RenderTexture.active = prev;
                dp.method_3(); // publish mask origin/inv-size/texture shader globals
                Plugin.Log.LogDebug($"[Weather] SCENE DEPTH MASK rendered ({dim}x{dim} over {b.size.x:0}x{b.size.z:0}m) — ship geometry clips indoor snow");
            }
            finally
            {
                if (sfWasActive) sf.gameObject.SetActive(true);
                if (camGo != null) UnityEngine.Object.Destroy(camGo);
                if (tmp != null) { tmp.Release(); UnityEngine.Object.Destroy(tmp); }
            }
        }

        // ------------------------------------------------------------- scene lookup
        private static Transform FindSceneGo(string name)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scn = SceneManager.GetSceneAt(i);
                if (!scn.isLoaded || !scn.name.StartsWith("Icebreaker")) continue;
                foreach (var root in scn.GetRootGameObjects())
                {
                    if (root.name == name) return root.transform;
                    var t = FindChildRecursive(root.transform, name);
                    if (t != null) return t;
                }
            }
            return null;
        }

        private static Transform FindChildRecursive(Transform t, string name)
        {
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c.name == name) return c;
                var r = FindChildRecursive(c, name);
                if (r != null) return r;
            }
            return null;
        }

        private static void IndexSubtree(Transform t, string path)
        {
            _pathIndex[path] = t;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                IndexSubtree(c, path + "/" + c.name);
            }
        }

        // ------------------------------------------------------------- ref resolution
        private static UnityEngine.Object ResolveRef(JObject tok, Type want)
        {
            long id = tok.Value<long?>("ref") ?? 0;
            int file = tok.Value<int?>("file") ?? 0;
            if (id == 0) return null;

            if (file == 0)
            {
                // scene-local: a rebuilt component, or an engine object from the table
                if (_comps.TryGetValue(id, out var c))
                    return want.IsInstanceOfType(c) ? c
                        : want == typeof(GameObject) ? (UnityEngine.Object)c.gameObject
                        : c.GetComponent(want);
                var so = _sidecar?["scene_objects"]?[id.ToString()];
                if (so != null && _pathIndex.TryGetValue(so.Value<string>("path") ?? "", out var t))
                {
                    if (want == typeof(GameObject)) return t.gameObject;
                    if (want == typeof(Transform) || want.IsAssignableFrom(typeof(Transform))) return t;
                    if (typeof(Component).IsAssignableFrom(want)) return t.GetComponent(want);
                }
                return null;
            }

            // external asset: resolve by recovered type+name
            var ext = _sidecar?["external_assets"]?[$"{file}:{id}"];
            if (ext == null) return null;
            string etype = ext.Value<string>("type"), name = ext.Value<string>("name");
            if (string.IsNullOrEmpty(name)) return null;

            if (etype == "Shader") return Shader.Find(name);
            if (etype == "MonoBehaviour") return GetOrBuildSettingsAsset(name);
            return FindAssetByName(etype, name);
        }

        private static UnityEngine.Object FindAssetByName(string unityType, string name)
        {
            string key = unityType + ":" + name;
            if (_assetCache.TryGetValue(key, out var cached)) return cached;
            Type t = unityType == "Material" ? typeof(Material)
                : unityType == "Mesh" ? typeof(Mesh)
                : unityType == "Texture2D" ? typeof(Texture2D)
                : unityType == "ComputeShader" ? typeof(ComputeShader)
                : null;
            UnityEngine.Object found = null;
            if (t != null)
                foreach (var o in Resources.FindObjectsOfTypeAll(t))
                    if (o.name == name) { found = o; break; }
            if (found == null) _missingAssets.Add($"{unityType} '{name}'");
            _assetCache[key] = found;
            return found;
        }

        // the 4 recovered settings assets (fog remap tables, cloud settings) — rebuilt
        // from values, exactly like the acoustics occlusion preset
        private static UnityEngine.Object GetOrBuildSettingsAsset(string name)
        {
            if (_settingsCache.TryGetValue(name, out var cached)) return cached;
            UnityEngine.Object result = null;
            var entry = _sidecar?["settings_assets"]?[name];
            if (entry != null)
            {
                var type = AccessTools.TypeByName(entry.Value<string>("class"));
                if (type != null && typeof(ScriptableObject).IsAssignableFrom(type))
                {
                    var so = ScriptableObject.CreateInstance(type);
                    so.name = name;
                    if (entry["fields"] is JObject f) FillFields(so, f);
                    // a null cloud map NREs EVERY camera pre-render (8500+/raid, real fps
                    // cost) — substitute a uniform gray coverage map so clouds render
                    // *something* until the bundle carries the real texture
                    var layerA = AccessTools.Field(type, "LayerA")?.GetValue(so);
                    if (layerA != null)
                    {
                        var texF = AccessTools.Field(layerA.GetType(), "CloudTexture");
                        if (texF != null && texF.GetValue(layerA) == null)
                        {
                            texF.SetValue(layerA, Texture2D.grayTexture);
                            Plugin.Log.LogWarning($"[Weather] '{name}' cloud map missing from bundle — gray fallback (rerun Author 9 + rebuild for real clouds)");
                        }
                    }
                    result = so;
                }
            }
            if (result == null) _missingAssets.Add($"SettingsAsset '{name}'");
            _settingsCache[name] = result;
            return result;
        }

        // ------------------------------------------------------------- generic filler
        private static void FillFields(object target, JObject fields)
        {
            var type = target.GetType();
            foreach (var prop in fields.Properties())
            {
                var fi = AccessTools.Field(type, prop.Name);
                if (fi == null) continue;
                try
                {
                    var v = ConvertToken(prop.Value, fi.FieldType, fi.GetValue(target));
                    if (v != null || !fi.FieldType.IsValueType) fi.SetValue(target, v);
                }
                catch { /* field drifted or unresolvable — keep the class default */ }
            }
        }

        private static object ConvertToken(JToken tok, Type type, object existing)
        {
            if (tok == null || tok.Type == JTokenType.Null) return null;

            if (type == typeof(string)) return tok.Value<string>();
            if (type == typeof(bool)) return tok.Type == JTokenType.Boolean ? tok.Value<bool>() : tok.Value<int>() != 0;
            if (type == typeof(short)) return (short)tok.Value<int>();
            if (type.IsEnum) return Enum.ToObject(type, tok.Value<long>());
            if (type.IsPrimitive) return Convert.ChangeType(((JValue)tok).Value, type,
                System.Globalization.CultureInfo.InvariantCulture);

            if (tok is JObject o)
            {
                // by-name external token (settings assets whose refs use a different
                // externals table than level698 — rewritten during extraction)
                if (o["extname"] != null)
                    return FindAssetByName(o.Value<string>("exttype"), o.Value<string>("extname"));
                if (o["ref"] != null && o.Count <= 2)
                    return typeof(UnityEngine.Object).IsAssignableFrom(type) ? ResolveRef(o, type) : null;
                if (type == typeof(Vector2)) return new Vector2(o.Value<float>("x"), o.Value<float>("y"));
                if (type == typeof(Vector3)) return new Vector3(o.Value<float>("x"), o.Value<float>("y"), o.Value<float>("z"));
                if (type == typeof(Vector4)) return new Vector4(o.Value<float>("x"), o.Value<float>("y"), o.Value<float>("z"), o.Value<float>("w"));
                if (type == typeof(Quaternion)) return new Quaternion(o.Value<float>("x"), o.Value<float>("y"), o.Value<float>("z"), o.Value<float>("w"));
                if (type == typeof(Color)) return new Color(o.Value<float>("r"), o.Value<float>("g"), o.Value<float>("b"), o.Value<float>("a"));
                if (type == typeof(Bounds)) return new Bounds(
                    (Vector3)ConvertToken(o["m_Center"], typeof(Vector3), null),
                    (Vector3)ConvertToken(o["m_Extent"], typeof(Vector3), null) * 2f);
                if (type == typeof(LayerMask)) return (LayerMask)(o.Value<int?>("m_Bits") ?? 0);
                if (type == typeof(AnimationCurve)) return BuildCurve(o) ?? existing;
                if (type == typeof(Gradient)) return BuildGradient(o) ?? existing;
                if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return null; // unresolvable object shape
                var inst = existing ?? Activator.CreateInstance(type);
                FillFields(inst, o);
                return inst;
            }

            if (tok is JArray arr)
            {
                Type elem = type.IsArray ? type.GetElementType()
                    : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0]
                    : null;
                if (elem == null) return null;
                var items = new List<object>(arr.Count);
                foreach (var e in arr)
                    items.Add(ConvertToken(e, elem, null));
                if (type.IsArray)
                {
                    var a = Array.CreateInstance(elem, items.Count);
                    for (int i = 0; i < items.Count; i++) a.SetValue(items[i], i);
                    return a;
                }
                var list = (System.Collections.IList)Activator.CreateInstance(type);
                foreach (var it in items) list.Add(it);
                return list;
            }
            return null;
        }

        private static AnimationCurve BuildCurve(JObject o)
        {
            var keys = o["m_Curve"] as JArray;
            if (keys == null) return null;
            var kf = new Keyframe[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                kf[i] = new Keyframe(
                    k.Value<float?>("time") ?? 0f, k.Value<float?>("value") ?? 0f,
                    k.Value<float?>("inSlope") ?? 0f, k.Value<float?>("outSlope") ?? 0f);
            }
            var c = new AnimationCurve(kf);
            c.preWrapMode = (WrapMode)(o.Value<int?>("m_PreInfinity") ?? 8);
            c.postWrapMode = (WrapMode)(o.Value<int?>("m_PostInfinity") ?? 8);
            return c;
        }

        // unity's serialized gradient: key0..key7 colors, ctime0..7 / atime0..7 as 0..65535
        private static Gradient BuildGradient(JObject o)
        {
            if (o["ctime0"] == null && o["key0"] == null) return null;
            int nc = o.Value<int?>("m_NumColorKeys") ?? 2;
            int na = o.Value<int?>("m_NumAlphaKeys") ?? 2;
            var colors = new GradientColorKey[Mathf.Clamp(nc, 1, 8)];
            var alphas = new GradientAlphaKey[Mathf.Clamp(na, 1, 8)];
            for (int i = 0; i < colors.Length; i++)
            {
                var col = o[$"key{i}"] is JObject k
                    ? new Color(k.Value<float>("r"), k.Value<float>("g"), k.Value<float>("b"), k.Value<float>("a"))
                    : Color.white;
                colors[i] = new GradientColorKey(col, (o.Value<int?>($"ctime{i}") ?? 0) / 65535f);
            }
            for (int i = 0; i < alphas.Length; i++)
            {
                var col = o[$"key{i}"] is JObject k ? k.Value<float>("a") : 1f;
                alphas[i] = new GradientAlphaKey(col, (o.Value<int?>($"atime{i}") ?? 0) / 65535f);
            }
            var g = new Gradient();
            g.SetKeys(colors, alphas);
            g.mode = (GradientMode)(o.Value<int?>("m_Mode") ?? 0);
            return g;
        }
    }

    // BLUE SKY IN POWERED SCOPES (field report 08-08, fixed 08-12). magnified optics
    // render through a second camera (BaseOpticCamera prefab) carrying its OWN
    // TOD_Scattering. this map freezes the sky — the TOD dome painters are switched off
    // and RenderSettings.skybox is the authored skybox_night — but the optic's private
    // scattering never got that memo, so it paints live atmospheric scattering over the
    // magnified view: a daylight-blue sky inside the scope on a night map.
    //
    // we already disabled that component from the fog tick, but OpticComponentUpdater
    // .LateUpdate re-mirrors the MAIN camera's scattering onto the optic EVERY FRAME
    // (tod_Scattering_1.enabled = tod_Scattering_0.enabled), so a once-per-tick disable
    // was always racing it — which is exactly why the report said "sometimes". patch the
    // sync itself and the race is gone: whatever the updater just copied, we re-clear on
    // the same frame, before anything renders.
    [HarmonyPatch(typeof(EFT.CameraControl.OpticComponentUpdater), nameof(EFT.CameraControl.OpticComponentUpdater.LateUpdate))]
    internal static class Patch_OpticScatteringOff
    {
        private static System.Reflection.FieldInfo _fScat, _fMboit;

        [HarmonyPostfix]
        private static void Postfix(EFT.CameraControl.OpticComponentUpdater __instance)
        {
            if (!IceGate.On) return; // vanilla maps keep their scattering
            try
            {
                if (_fScat == null)
                    _fScat = AccessTools.Field(typeof(EFT.CameraControl.OpticComponentUpdater), "tod_Scattering_1");
                if (_fMboit == null)
                    _fMboit = AccessTools.Field(typeof(EFT.CameraControl.OpticComponentUpdater), "mboit_Scattering_1");

                var scat = _fScat?.GetValue(__instance) as Behaviour;
                if (scat != null && scat.enabled) scat.enabled = false;
                // MBOIT rides the same sync and needs WindowsManager, which this map has
                // none of (icebreaker fog lesson) — leaving it on is a second way to
                // repaint the scope
                var mb = _fMboit?.GetValue(__instance) as Behaviour;
                if (mb != null && mb.enabled) mb.enabled = false;
            }
            catch { }
        }
    }
}
