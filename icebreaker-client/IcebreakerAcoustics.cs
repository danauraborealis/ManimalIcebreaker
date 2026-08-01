using System;
using System.Collections.Generic;
using System.Linq;
using SysIoPath = System.IO.Path;
using System.Reflection;
using Audio.AmbientSubsystem;
using Audio.SpatialSystem;
using Audio.SpatialSystem.Data;
using Audio.SpatialSystem.Utils;
using EFT.EnvironmentEffect;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Icebreaker
{
    // resurrects BSG's spatial-audio + indoor/outdoor environment layer on the backported
    // icebreaker. the retail authoring survived only as raw serialized bytes in the retail
    // level files — analysis/extract_spatial_audio.py decodes them (typetrees generated
    // from the SPT assembly + hand-verified retail layout deltas) into a json sidecar
    // shipped next to the dll (acoustics/icebreaker_spatial_audio.json). two tiers:
    //   tier 2: rebuild the IndoorTrigger hierarchy under a real EnvironmentManager —
    //           0.16.9 POLLS transform-box triggers (the transform IS the box), so 51
    //           recovered world transforms are all it takes. drives indoor sound banks,
    //           exposure, rain muffling, BetterAudio.TransitToEnvironment.
    //   tier 3: rehydrate the 154 SpatialAudioRooms / 207 portals / 269 AudioTriggerAreas
    //           onto the bundled Sound-scene GOs (colliders survived as engine data),
    //           rebuild the location-info + occlusion ScriptableObjects from recovered
    //           values, drop BSG's own icebreaker_sound.audiobakedata into StreamingAssets,
    //           then let the REAL SpatialAudioSystem.Initialize run.
    internal static class IcebreakerAcoustics
    {
        private static JObject _sidecar;
        private static bool _sidecarTried;
        // liveness-tracked, not bools: the built objects die with the raid scenes, and a
        // second icebreaker raid in the same game session must rebuild (unity fake-null
        // makes the != null check do the staleness detection for free)
        private static GameObject _envRoot;
        private static GameObject _spatialMarker;

        private static string DataDir =>
            SysIoPath.Combine(SysIoPath.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "acoustics");

        internal static bool IcebreakerLoaded()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (sc.isLoaded && sc.name != null && sc.name.StartsWith("Icebreaker")) return true;
            }
            return false;
        }

        private static JObject Sidecar()
        {
            if (_sidecarTried) return _sidecar;
            _sidecarTried = true;
            try
            {
                var path = SysIoPath.Combine(DataDir, "icebreaker_spatial_audio.json");
                if (!System.IO.File.Exists(path))
                {
                    Plugin.Log.LogDebug($"[Acoustics] no sidecar at {path} — spatial audio + env triggers stay off");
                    return null;
                }
                _sidecar = JObject.Parse(System.IO.File.ReadAllText(path));
                Plugin.Log.LogInfo("[Acoustics] sidecar loaded");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Acoustics] sidecar parse failed: {e.Message}");
                _sidecar = null;
            }
            return _sidecar;
        }

        // ------------------------------------------------------------------ tier 2: env
        // build EnvironmentManager -> TriggerGroup -> 51 IndoorTriggers from recovered
        // world transforms. bottom-up creation so each Awake finds its children ready.
        // returns true if the real manager hierarchy got built (caller skips its bare
        // fallback manager).
        public static bool TryBuildEnvironmentTriggers()
        {
            if (_envRoot != null) return true;
            var sc = Sidecar();
            var triggers = sc?["scenes"]?["Icebreaker_Scripts"]?["IndoorTrigger"] as JArray;
            if (triggers == null || triggers.Count == 0) return false;

            try
            {
                var root = new GameObject("Icebreaker_EnvSwitch");
                var groupGo = new GameObject("TriggerGroup");
                groupGo.transform.SetParent(root.transform, false);

                int built = 0;
                foreach (var row in triggers)
                {
                    var trs = row["world_trs"] ?? row["trs"];
                    if (trs == null) continue;
                    var go = new GameObject($"IndoorTrigger_{built}");
                    go.transform.SetParent(groupGo.transform, false);
                    go.transform.position = V3(trs["pos"]);
                    go.transform.rotation = Q4(trs["rot"]);
                    go.transform.localScale = V3(trs["scale"]);
                    var it = go.AddComponent<IndoorTrigger>(); // Awake self-heals Bounds from transform
                    var f = row["fields"];
                    if (f != null)
                    {
                        it.IsBunker = f.Value<bool?>("IsBunker") ?? false;
                        it.FadeTime = f.Value<float?>("FadeTime") ?? 1f;
                        it.BunkerDepth = f.Value<float?>("BunkerDepth") ?? -15f;
                        it.BunkerLowPass = f.Value<float?>("BunkerLowPass") ?? 300f;
                        it.ExposureSpeed = f.Value<float?>("ExposureSpeed") ?? 4f;
                        it.ExposureOffset = f.Value<float?>("ExposureOffset") ?? 0.14f;
                        it.RainVolume = f.Value<float?>("RainVolume") ?? 0.7f;
                        it.Reinit(); // recompute bounds now that scale is final
                    }
                    built++;
                }

                groupGo.AddComponent<TriggerGroup>(); // Awake -> Reinit over the children above

                // manager last so InitIndoors finds the group. recovered tuning applied to the
                // private [SerializeField] fields BEFORE Awake via inactive-add (Init copies
                // OutdoorFadeTime etc. into its working floats during Awake).
                root.SetActive(false);
                var em = root.AddComponent<EnvironmentManager>();
                var emRow = (sc["scenes"]?["Icebreaker_Scripts"]?["EnvironmentManager"] as JArray)?.FirstOrDefault();
                if (emRow?["fields"] is JObject emFields)
                    FillFields(em, emFields, name => name != "Bounds");
                root.SetActive(true);

                _envRoot = root;
                Plugin.Log.LogDebug($"[Acoustics] environment switcher rebuilt: {built} indoor triggers (indoor/outdoor audio + exposure live)");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Acoustics] env trigger build failed: {e}");
                return false;
            }
        }

        // ------------------------------------------------------- ambient revival
        // the raid-start GClass2313.ResetAudioBuffer() does AudioSettings.Reset(), which
        // STOPS every playing AudioSource. our hand-placed ambient beds (Author 8, in the
        // bundled Sound scene) play during load then die at that reset and never restart
        // (playOnAwake already fired). a cheap periodic replay brings them back and keeps
        // them alive against any later stop. call from RenderEnvProbe's per-frame tick.
        private static AudioSource[] _ambientSources;
        private static bool _ambientLogged;
        private static readonly Dictionary<AudioSource, float> _ambientBaseVol = new Dictionary<AudioSource, float>();

        private static bool _bedsSilenced;
        private static bool _silencedWithOutdoor;

        public static void ResetAmbientCache()
        {
            _ambientSources = null;
            _ambientLogged = false;
            _bedsSilenced = false;
            _clipCache = null;          // clip instances are per raid
            _roomTonesBound = 0;
            _roomToneMisses.Clear();
            _ambientBaseVol.Clear();
            _windSrc = null;
            _toneSrcs.Clear();
            _toneAnchor.Clear();
            _activeTone = null;
            _lastToneLogged = null;
        }

        // NEVER index _ambientBaseVol directly from the blend tick. a missing key threw
        // KeyNotFoundException out of TickAmbientBlend, and because the wind read sits
        // ABOVE the zone-tone loop one absent entry killed the wind and all 15 tones in a
        // single shot — while every other sound in the raid carried on, which is exactly
        // what makes it look like a mixing problem instead of an exception (07-31, fika
        // headless client). degrade loudly to unity-default gain instead of dying.
        private static float BaseVol(AudioSource s)
        {
            if (_ambientBaseVol.TryGetValue(s, out var v)) return v;
            _ambientBaseVol[s] = 1f;
            Plugin.Log.LogDebug($"[Acoustics] no cached base volume for '{s.name}' — assuming 1.0; "
                                  + "the mute cache got cleared out from under the blend");
            return 1f;
        }

        // called by the outdoor-bed watchdog once it proves retail's bed is emitting
        // nothing. clearing _silencedWithOutdoor lets the revive loop below Play() our
        // Amb_OutdoorWind again, and TickAmbientBlend fades its volume back up from there.
        internal static void ReviveOurWindBed()
        {
            _silencedWithOutdoor = false;
            foreach (var s in _ambientSources ?? new AudioSource[0])
            {
                if (s == null || s.name != "Amb_OutdoorWind") continue;
                if (!s.isPlaying) s.Play();
            }
            Plugin.Log.LogWarning("[Acoustics] our outdoor wind bed revived — retail's never made a sound");
        }

        public static void KeepAmbientAlive()
        {
            IcebreakerAmbientAudio.WatchOutdoorBed();
            if (_ambientSources == null)
            {
                var host = GameObject.Find("Icebreaker_Ambient");
                if (host == null) return; // sound scene not up yet
                var found = host.GetComponentsInChildren<AudioSource>(true);
                if (found.Length == 0) return;
                _ambientSources = found;
                MuteTonesForLoad(found);
            }
            // switched OFF still means WORK: the beds carry playOnAwake in the bundle, so
            // walking away would leave 15 zone tones blaring from origin underneath
            // retail's authored ones. silence the TONES once.
            //
            // the OUTDOOR WIND bed only stands down once retail's own is proven playing —
            // that comes from a SeasonAmbientSoundDataSO we have to REBUILD (its layout
            // drifted between 1.0 and 4.0), so it can legitimately fail. keeping ours until
            // the real one reports in avoids trading a doubled bed for a silent deck.
            // re-runs once if the outdoor bed comes up AFTER the first silence pass — this
            // ticks every frame from raid start, long before StageTwoInit rebuilds the
            // ambient subsystem, so the first pass always sees OutdoorBedRestored false
            if (!Plugin.AmbientBeds.Value &&
                (!_bedsSilenced || (!_silencedWithOutdoor && IcebreakerAmbientAudio.OutdoorBedRestored)))
            {
                _bedsSilenced = true;
                bool dropWind = IcebreakerAmbientAudio.OutdoorBedRestored;
                _silencedWithOutdoor = dropWind;
                int stopped = 0;
                foreach (var s in _ambientSources)
                {
                    if (s == null) continue;
                    if (s.name == "Amb_OutdoorWind" && !dropWind) continue;
                    if (s.isPlaying) { s.Stop(); stopped++; }
                    s.volume = 0f;
                }
                Plugin.Log.LogDebug($"[Acoustics] AmbientBeds off — silenced {stopped} bed(s); " +
                                      (dropWind ? "retail's outdoor bed took over too"
                                                : "our outdoor wind bed stays (retail's didn't start)"));
            }
            if (Plugin.AmbientBeds.Value) _bedsSilenced = false;

            int revived = 0;
            foreach (var s in _ambientSources)
            {
                if (s == null || s.clip == null || !s.loop) continue;
                // with the beds off, only the outdoor wind is worth reviving — and not even
                // that once retail's own bed is carrying it
                if (!Plugin.AmbientBeds.Value &&
                    (s.name != "Amb_OutdoorWind" || _silencedWithOutdoor)) continue;
                if (!s.isPlaying) { s.Play(); revived++; }
            }
            if (revived > 0 && !_ambientLogged)
            {
                _ambientLogged = true;
                Plugin.Log.LogDebug($"[Acoustics] revived {revived}/{_ambientSources.Length} ambient beds (raid-start audio reset had stopped them)");
            }
        }

        // the 13 zone-tone GOs still sit at ORIGIN as authored (we fixed the PICKER
        // anchors, not the transforms) and stay 3D until the blend flattens them —
        // and during the loading screen the AudioListener is AT origin, so the player
        // loads in standing inside 13 stacked room-tone speakers ("indoor audio while
        // loading outside"). kill that instantly at cache time: capture authored volume,
        // flatten to 2D, mute — the blend fades the correct one in once the env resolves.
        internal static void MuteTonesForLoad(AudioSource[] sources)
        {
            foreach (var s in sources)
            {
                if (s == null || s.name == "Amb_OutdoorWind") continue;
                if (!_ambientBaseVol.ContainsKey(s)) _ambientBaseVol[s] = s.volume;
                s.spatialBlend = 0f;
                s.volume = 0f;
            }
        }

        // earliest possible hook — scene-load callback, fires before the raid tick loop
        // ever runs, so the origin-stack never gets a frame to be audible
        public static void OnSceneLoaded(Scene scene)
        {
            if (scene.name != "Icebreaker_Sound") return;
            var host = GameObject.Find("Icebreaker_Ambient");
            if (host == null) return;
            MuteTonesForLoad(host.GetComponentsInChildren<AudioSource>(true));

            // REVERTED 07-31 (user call). this used to also stop the whole AmbientAudioSystem
            // tree and zero our wind bed, to stop the "blast, cut, restart" on the loading
            // screen. it turned that into "blast, cut, NEVER restart" instead — the restore
            // was standing our bed down in favour of retail's inaudible one, so the cut was
            // permanent. the tidier load screen was never worth a silent raid, and the
            // origin-stack mute above is the part that was fixing a real problem anyway.
            // don't reintroduce this without first confirming the deck still has wind.
            Plugin.Log.LogInfo("[Acoustics] load-screen room tones muted (origin stack) — ambient tree left playing");
        }

        // ambient bed mixer (user-specified design): ALL beds are 2D/global — the zone
        // tones were authored as positional 3D sources but that gave "silent unless near
        // the invisible anchor"; instead exactly ONE zone tone plays at a time (the zone
        // whose anchor is nearest the player — anchors sit at BotZone centers so nearest
        // = the zone you're in), crossfaded on zone change. the wind bed ducks to a floor
        // indoors rather than fully out — the storm's still out there, you just have a
        // hull between you and it.
        private static AudioSource _windSrc;
        private static readonly List<AudioSource> _toneSrcs = new List<AudioSource>();
        private static readonly Dictionary<AudioSource, Vector3> _toneAnchor = new Dictionary<AudioSource, Vector3>();
        private static AudioSource _activeTone;
        private static string _lastToneLogged;

        private static void CacheAmbientRoles()
        {
            _windSrc = null;
            _toneSrcs.Clear();
            _toneAnchor.Clear();

            // 13 of 15 authored tone GOs sit at ORIGIN — the resurrected BotZone parent
            // transforms are (0,0,0) with the real content in child markers, and Author 8
            // anchored on the parent. a nearest-anchor zone pick against an origin cluster
            // is random. derive each zone's true center from its spawn-marker centroid.
            var zones = UnityEngine.Object.FindObjectsOfType<BotZone>();

            foreach (var s in _ambientSources)
            {
                if (s == null) continue;
                if (!_ambientBaseVol.ContainsKey(s)) _ambientBaseVol[s] = s.volume;
                if (s.name == "Amb_OutdoorWind") { _windSrc = s; continue; }

                var anchor = s.transform.position;
                var zoneName = s.name.StartsWith("Amb_") ? s.name.Substring(4) : s.name;
                foreach (var z in zones)
                {
                    if (z == null || z.name != zoneName || z.SpawnPointMarkers == null) continue;
                    Vector3 sum = Vector3.zero;
                    int n = 0;
                    foreach (var m in z.SpawnPointMarkers)
                        if (m != null) { sum += m.transform.position; n++; }
                    if (n > 0) anchor = sum / n;
                    break;
                }
                if (anchor.sqrMagnitude < 4f)
                    Plugin.Log.LogWarning($"[Acoustics] tone '{s.name}' has no usable anchor (zone not found?) — it will rarely win the zone pick");

                _toneAnchor[s] = anchor;
                s.spatialBlend = 0f; // global bed, not positional
                _toneSrcs.Add(s);
            }
        }

        public static void TickAmbientBlend(float dt)
        {
            if (_ambientSources == null) return; // KeepAmbientAlive caches first
            var em = EnvironmentManager.Instance;
            if (em == null) return;
            if (_toneSrcs.Count == 0 && _windSrc == null) CacheAmbientRoles();

            bool indoor = em.Environment == EnvironmentType.Indoor;
            var world = Comfort.Common.Singleton<EFT.GameWorld>.Instance;
            var player = world != null ? world.MainPlayer : null;

            // pick the current zone tone: nearest anchor, with 3m hysteresis so standing
            // on a boundary doesn't flap between two tones. skipped when the beds are off —
            // the wind block below still runs, it's the one thing retail can't give back.
            if (Plugin.AmbientBeds.Value && player != null && _toneSrcs.Count > 0)
            {
                var pos = player.Position;
                AudioSource best = null;
                float bestD = float.MaxValue;
                foreach (var t in _toneSrcs)
                {
                    if (t == null) continue;
                    float d = (_toneAnchor[t] - pos).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = t; }
                }
                if (best != null && best != _activeTone)
                {
                    float curD = _activeTone != null && _toneAnchor.ContainsKey(_activeTone)
                        ? (_toneAnchor[_activeTone] - pos).magnitude : float.MaxValue;
                    if (Mathf.Sqrt(bestD) + 3f < curD || _activeTone == null)
                    {
                        _activeTone = best;
                        if (indoor && _lastToneLogged != best.name)
                        {
                            _lastToneLogged = best.name;
                            Plugin.Log.LogInfo($"[Acoustics] zone tone -> {best.name}");
                        }
                    }
                }
            }

            float step = dt / 1.5f; // ~1.5s crossfade
            if (_windSrc != null)
            {
                float baseVol = BaseVol(_windSrc);
                float target = indoor ? baseVol * Plugin.WindIndoorFraction.Value : baseVol;
                _windSrc.volume = Mathf.MoveTowards(_windSrc.volume, target, step * Mathf.Max(baseVol, 0.2f));
            }
            if (!Plugin.AmbientBeds.Value) return;   // tones already stopped and muted
            foreach (var t in _toneSrcs)
            {
                if (t == null) continue;
                // master knob x per-zone knob, both live — tames individual loud clips
                // without touching the rest
                var zoneName = t.name.StartsWith("Amb_") ? t.name.Substring(4) : t.name;
                float perZone = Plugin.ZoneToneMultiplier(zoneName);
                float baseVol = BaseVol(t) * perZone;   // master knob retired — clips are retail-clean now
                float target = (indoor && t == _activeTone) ? baseVol : 0f;
                t.volume = Mathf.MoveTowards(t.volume, target, step * Mathf.Max(baseVol, 0.2f));
            }
        }

        // ------------------------------------------------- environment self-drive
        // the trigger boxes are verified correct (152/153 interior rooms covered), but
        // BSG's continuous driver is a single async polling task that dies SILENTLY on
        // the first exception from any half-spawned player (HandleExceptions swallows and
        // the loop never restarts) — with our force-spawner that's near-guaranteed. the
        // only other caller is Teleport. so drive it ourselves: cheap idempotent call
        // (SetTriggerForPlayer early-outs when nothing changed).
        private static EnvironmentType _lastEnv = (EnvironmentType)(-1);
        private static int _driveTick;

        public static void DriveEnvironment()
        {
            var em = EnvironmentManager.Instance;
            if (em == null) return;
            var world = Comfort.Common.Singleton<EFT.GameWorld>.Instance;
            if (world == null) return;
            _driveTick++;

            var main = world.MainPlayer;
            if (main != null)
            {
                try { em.UpdateEnvironmentForPlayer(main); } catch { }
                if (em.Environment != _lastEnv)
                {
                    _lastEnv = em.Environment;
                    Plugin.Log.LogDebug($"[Acoustics] environment -> {_lastEnv}");
                }
            }

            // bots at lower cadence — their env feeds AI hearing, not the mixer
            if ((_driveTick & 3) == 0)
            {
                var players = world.RegisteredPlayers;
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p == null || p.IsYourPlayer) continue;
                    try { em.UpdateEnvironmentForPlayer(p); } catch { }
                }
            }
        }

        // --------------------------------------------------------------- tier 3: rooms
        // stage everything the real SpatialAudioSystem.Initialize needs, return true to
        // let it run. any failure returns false -> caller falls back to the old skip.
        public static bool TryPrepareSpatialAudio(SpatialAudioSystem system)
        {
            if (_spatialMarker != null) return true; // already staged this raid

            var sc = Sidecar();
            var sound = sc?["scenes"]?["Icebreaker_Sound"] as JObject;
            if (sound == null) return false;

            try
            {
                if (!EnsureBakeFile(sc)) return false;

                var goIndex = BuildGoIndex();
                var comps = new Dictionary<long, Component>();
                var reactivate = new List<GameObject>();

                // pass 1 — create components on deactivated GOs (portal/room Awake reads
                // fields + kicks async Init; they must see real values when they wake)
                CreateAll<AudioTriggerArea>(sound, "AudioTriggerArea", goIndex, comps, reactivate);
                CreateAll<SpatialAudioPortal>(sound, "SpatialAudioPortal", goIndex, comps, reactivate);
                CreateAll<UniversalTriggerSpatialAudioPortal>(sound, "UniversalTriggerSpatialAudioPortal", goIndex, comps, reactivate);
                CreateAll<SpatialAudioRoom>(sound, "SpatialAudioRoom", goIndex, comps, reactivate);

                // pass 2 — fields + cross-refs while everything still sleeps
                foreach (var row in Rows(sound, "AudioTriggerArea"))
                    if (Comp<AudioTriggerArea>(comps, row) is AudioTriggerArea area)
                        FillArea(area, row);
                foreach (var row in Rows(sound, "SpatialAudioPortal"))
                    if (Comp<SpatialAudioPortal>(comps, row) is SpatialAudioPortal p)
                        FillPortal(p, row, comps);
                foreach (var row in Rows(sound, "UniversalTriggerSpatialAudioPortal"))
                    if (Comp<UniversalTriggerSpatialAudioPortal>(comps, row) is UniversalTriggerSpatialAudioPortal up)
                        FillPortal(up, row, comps);
                foreach (var row in Rows(sound, "SpatialAudioRoom"))
                    if (Comp<SpatialAudioRoom>(comps, row) is SpatialAudioRoom r)
                        FillRoom(r, row, comps);

                // pass 3 — wake everything with wired state
                foreach (var go in reactivate) go.SetActive(true);

                Plugin.Log.LogWarning($"[Acoustics] bound {_roomTonesBound} authored room tones" +
                    (_roomToneMisses.Count > 0
                        ? $"; MISSING from the bundle: {string.Join(", ", new List<string>(_roomToneMisses).ToArray())}"
                        : ""));

                // the room tracker (GClass1122) enumerates rooms ONLY through
                // SpatialAudioCrossSceneGroup.AllCrossGroups — NOT FindObjectsOfType.
                // without it the tracker has zero rooms and its spatial index stays null
                // (the FindActualCurrentRoom NRE storm). the class is fully self-healing
                // (Awake collects children rooms/portals), it just must exist on the
                // room-tree root AFTER our components do.
                var rootPath = Rows(sound, "SpatialAudioCrossSceneGroup").FirstOrDefault()?.Value<string>("go")
                               ?? "SpatialAudioSystem";
                if (goIndex.TryGetValue(rootPath, out var roots) && roots.Count > 0)
                {
                    var rootGo = roots[0].gameObject;
                    if (rootGo.GetComponent<SpatialAudioCrossSceneGroup>() == null)
                        rootGo.AddComponent<SpatialAudioCrossSceneGroup>();
                }
                else Plugin.Log.LogWarning($"[Acoustics] room-tree root '{rootPath}' not found — tracker will see no rooms");

                StampDoorIds(sound, comps);

                // hand the system the real per-location assets, rebuilt from recovered values
                var infoFields = sc["assets"]?["location_info"]?["fields"] as JObject;
                var info = ScriptableObject.CreateInstance<SpatialAudioLocationInfo>();
                if (infoFields != null) FillFields(info, infoFields, null);
                AccessTools.Field(typeof(SpatialAudioSystem), "_locationInfo").SetValue(system, info);

                if (system.OcclusionSettings == null)
                {
                    var occ = ScriptableObject.CreateInstance<AudioOcclusionSettings>();
                    if (sc["assets"]?["occlusion_settings"]?["fields"] is JObject occFields)
                        FillFields(occ, occFields, null);
                    system.OcclusionSettings = occ;
                }
                if (system.poolsConfig == null)
                {
                    system.poolsConfig = new SpatialAudioPoolsConfig();
                    if (sound["SpatialAudioSystem"] is JArray sysRows && sysRows.FirstOrDefault()?["fields"]?["poolsConfig"] is JObject pc)
                        FillFields(system.poolsConfig, pc, null);
                }

                // marker parented into the Sound scene so it dies with the raid
                _spatialMarker = new GameObject("Icebreaker_AcousticsStaged");
                var soundScene = SceneManager.GetSceneByName("Icebreaker_Sound");
                if (soundScene.IsValid() && soundScene.isLoaded)
                    SceneManager.MoveGameObjectToScene(_spatialMarker, soundScene);
                Plugin.Log.LogDebug($"[Acoustics] SPATIAL AUDIO STAGED: {comps.Count} components rehydrated — letting BSG's Initialize run for real");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Acoustics] spatial audio staging failed (falling back to no-spatial): {e}");
                return false;
            }
        }

        // BSG's own bake ships with the mod (acoustics/ dir); the game loads it from
        // StreamingAssets/AudioBakeData like every real map's bake
        private static bool EnsureBakeFile(JObject sc)
        {
            var rel = sc["assets"]?["location_info"]?["fields"]?.Value<string>("relativeBakeDataPath")
                      ?? "AudioBakeData\\icebreaker_sound.audiobakedata";
            var dest = SysIoPath.Combine(Application.streamingAssetsPath, rel.Replace('\\', SysIoPath.DirectorySeparatorChar));
            if (System.IO.File.Exists(dest)) return true;
            var src = SysIoPath.Combine(DataDir, SysIoPath.GetFileName(dest));
            if (!System.IO.File.Exists(src))
            {
                Plugin.Log.LogWarning($"[Acoustics] bake file missing: neither {dest} nor {src} exist");
                return false;
            }
            System.IO.Directory.CreateDirectory(SysIoPath.GetDirectoryName(dest) ?? ".");
            System.IO.File.Copy(src, dest);
            Plugin.Log.LogDebug($"[Acoustics] installed audio bake -> {dest}");
            return true;
        }

        // ------------------------------------------------------------- scene GO lookup
        // GO paths are not unique in the Sound scene (57 dup area paths) -> disambiguate
        // by local position, the PCBK3 lesson
        private static Dictionary<string, List<Transform>> BuildGoIndex()
        {
            var index = new Dictionary<string, List<Transform>>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scn = SceneManager.GetSceneAt(i);
                if (!scn.isLoaded || !scn.name.StartsWith("Icebreaker")) continue;
                foreach (var root in scn.GetRootGameObjects())
                    Walk(root.transform, root.name, index);
            }
            return index;
        }

        private static void Walk(Transform t, string path, Dictionary<string, List<Transform>> index)
        {
            if (!index.TryGetValue(path, out var list)) index[path] = list = new List<Transform>(1);
            list.Add(t);
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                Walk(c, path + "/" + c.name, index);
            }
        }

        private static Transform FindGo(Dictionary<string, List<Transform>> index, JToken row)
        {
            var path = row.Value<string>("go");
            if (path == null || !index.TryGetValue(path, out var list)) return null;
            if (list.Count == 1) return list[0];
            var pos = row["trs"]?["pos"];
            if (pos == null) return list[0];
            var want = V3(pos);
            Transform best = null;
            float bestD = float.MaxValue;
            foreach (var t in list)
            {
                float d = (t.localPosition - want).sqrMagnitude;
                if (d < bestD) { bestD = d; best = t; }
            }
            return best;
        }

        private static IEnumerable<JToken> Rows(JObject scene, string cls)
            => (scene[cls] as JArray) ?? Enumerable.Empty<JToken>();

        private static void CreateAll<T>(JObject scene, string cls, Dictionary<string, List<Transform>> goIndex,
            Dictionary<long, Component> comps, List<GameObject> reactivate) where T : Component
        {
            int missing = 0;
            foreach (var row in Rows(scene, cls))
            {
                var t = FindGo(goIndex, row);
                if (t == null) { missing++; continue; }
                var go = t.gameObject;
                if (go.activeSelf) { go.SetActive(false); reactivate.Add(go); }
                comps[row.Value<long>("path_id")] = go.AddComponent<T>();
            }
            if (missing > 0)
                Plugin.Log.LogWarning($"[Acoustics] {cls}: {missing} GOs not found in scene (bundle drift?)");
        }

        private static T Comp<T>(Dictionary<long, Component> comps, JToken row) where T : Component
            => comps.TryGetValue(row.Value<long>("path_id"), out var c) ? c as T : null;

        private static Component Ref(Dictionary<long, Component> comps, JToken refTok)
        {
            var id = (refTok as JObject)?.Value<long?>("ref");
            return id.HasValue && comps.TryGetValue(id.Value, out var c) ? c : null;
        }

        // ------------------------------------------------------------------- fillers
        private static void FillArea(AudioTriggerArea area, JToken row)
        {
            var f = row["fields"];
            AccessTools.Field(typeof(ServerRoomColliderArea), "areaCollider")
                ?.SetValue(area, area.GetComponent<BoxCollider>());
            var validate = f?.Value<int?>("_validatePlayerCenterInside");
            if (validate.HasValue)
                AccessTools.Field(typeof(ServerRoomColliderArea), "_validatePlayerCenterInside")
                    ?.SetValue(area, validate.Value != 0);
        }

        private static void FillPortal(BaseSpatialAudioPortal p, JToken row, Dictionary<long, Component> comps)
        {
            var f = row["fields"] as JObject;
            if (f == null) return;

            p.Depth = f.Value<float?>("Depth") ?? 2f;
            p.portalName = f.Value<string>("portalName") ?? p.gameObject.name;
            p.FitToGeometry = (f.Value<int?>("FitToGeometry") ?? 0) != 0;
            p.AutoRotate = (f.Value<int?>("AutoRotate") ?? 0) != 0;
            p.traversalMaxCost = f.Value<float?>("traversalMaxCost") ?? 0f;
            p.ToOutdoor = (f.Value<int?>("ToOutdoor") ?? 0) != 0;
            p.openFadeTime = f.Value<float?>("openFadeTime") ?? 0.1f;
            p.closeFadeTime = f.Value<float?>("closeFadeTime") ?? 0.1f;
            p.openEnvelope = Curve(f["openEnvelope"]) ?? p.openEnvelope;
            p.closeEnvelope = Curve(f["closeEnvelope"]) ?? p.closeEnvelope;
            p.portalCollider = p.GetComponent<BoxCollider>();

            AccessTools.Field(typeof(BaseSpatialAudioPortal), "_iD").SetValue(p, (short)(f.Value<int?>("_iD") ?? 0));
            var rooms = new List<SpatialAudioRoom>(2);
            foreach (var rr in (f["_connectedRooms"] as JArray) ?? new JArray())
                if (Ref(comps, rr) is SpatialAudioRoom room) rooms.Add(room);
            AccessTools.Field(typeof(BaseSpatialAudioPortal), "_connectedRooms").SetValue(p, rooms);

            // retail did NOT serialize portalType/state — at load they take the C# defaults
            // portalType=Opening(0), state=Open(0), and SpatialAudioPortal.SyncState (which
            // runs AFTER game start, when ClientWorld exists) syncs the ones with a real
            // matching door to that door's shut/open state. everything else stays OPEN.
            // matching this EXACTLY matters: the earlier Window/Static/Closed inference shut
            // all 206 "AudioWindowPortal_glass" portals (no WindowBreakers exist to reopen
            // them) — sealing the whole ship acoustically, so sound routed through 0.8-1.0
            // wall occlusion everywhere = "everything sounds 4 floors down". the portal
            // NAMES are misleading; they're BSG's generic acoustic portals, all run Open.
            string doorId = f.Value<string>("DoorID");
            if (p is SpatialAudioPortal sp)
            {
                sp.DoorID = doorId ?? "";
                sp.portalType = BaseSpatialAudioPortal.PortalType.Opening;
                sp.state = BaseSpatialAudioPortal.PortalState.Open;
            }
            else if (p is UniversalTriggerSpatialAudioPortal utp)
            {
                utp.portalType = BaseSpatialAudioPortal.PortalType.Opening;
                utp.state = BaseSpatialAudioPortal.PortalState.Open;
                AccessTools.Field(typeof(UniversalTriggerSpatialAudioPortal), "_openTriggerID")
                    ?.SetValue(utp, f.Value<string>("_openTriggerID") ?? "");
                AccessTools.Field(typeof(UniversalTriggerSpatialAudioPortal), "_closeTriggerID")
                    ?.SetValue(utp, f.Value<string>("_closeTriggerID") ?? "");
            }
        }

        // clip lookup by NAME — the bundle is the only place these live, and everything in
        // it is loaded by the time the rooms are filled. cache is per raid (cleared with
        // the ambient cache) because a new raid means new clip instances.
        private static Dictionary<string, AudioClip> _clipCache;
        private static int _roomTonesBound;
        private static readonly HashSet<string> _roomToneMisses = new HashSet<string>();

        private static AudioClip FindClip(string name)
        {
            if (_clipCache == null)
            {
                _clipCache = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>())
                    if (c != null && !string.IsNullOrEmpty(c.name)) _clipCache[c.name] = c;
            }
            return _clipCache.TryGetValue(name, out var clip) ? clip : null;
        }

        private static void FillRoom(SpatialAudioRoom r, JToken row, Dictionary<long, Component> comps)
        {
            var f = row["fields"] as JObject;
            if (f == null) return;

            r.priority = f.Value<int?>("priority") ?? 0;
            r.WallOcclusion = f.Value<float?>("WallOcclusion") ?? 0.5f;
            r.FitToGeometry = (f.Value<int?>("FitToGeometry") ?? 0) != 0;
            r.OnlyWired = (f.Value<int?>("OnlyWired") ?? 1) != 0;
            AccessTools.Field(typeof(SpatialAudioRoom), "Outdoor").SetValue(r, (f.Value<int?>("Outdoor") ?? 0) != 0);
            AccessTools.Field(typeof(SpatialAudioRoom), "Isolated").SetValue(r, (f.Value<int?>("Isolated") ?? 0) != 0);
            AccessTools.Field(typeof(SpatialAudioRoom), "_type").SetValue(r, (EAudioRoomTypeMask)(f.Value<int?>("_type") ?? 0));
            AccessTools.Field(typeof(SpatialAudioRoom), "_iD").SetValue(r, (short)(f.Value<int?>("_iD") ?? -1));
            AccessTools.Field(typeof(SpatialAudioRoom), "_roomSize").SetValue(r, f.Value<float?>("_roomSize") ?? 0f);
            if (f["_bounds"] is JObject b)
                AccessTools.Field(typeof(SpatialAudioRoom), "_bounds")
                    .SetValue(r, new Bounds(V3(b["m_Center"]), V3(b["m_Extent"]) * 2f));

            r.Areas = new List<AudioTriggerArea>();
            foreach (var a in (f["Areas"] as JArray) ?? new JArray())
                if (Ref(comps, a) is AudioTriggerArea area) r.Areas.Add(area);

            r.roomConnections = new List<SpatialAudioRoom.RoomConnection>();
            foreach (var c in (f["roomConnections"] as JArray) ?? new JArray())
            {
                var conn = new SpatialAudioRoom.RoomConnection
                {
                    connectedRoom = Ref(comps, c["connectedRoom"]) as SpatialAudioRoom,
                };
                var portals = new List<BaseSpatialAudioPortal>();
                foreach (var pp in (c["connectingPortals"] as JArray) ?? new JArray())
                    if (Ref(comps, pp) is BaseSpatialAudioPortal bp) portals.Add(bp);
                conn.connectingPortals = portals.ToArray();
                if (conn.connectedRoom != null) r.roomConnections.Add(conn);
            }

            // ROOM TONES — retail hangs the indoor bed on the ROOM, not on a player:
            // 78 rooms run living_roomtone, 49 technical, 25 near_engine, 2 engine. this
            // used to be skipped ("clip refs were external assets we don't ship") which is
            // exactly why we grew our own Amb_<zone> beds. the sidecar now carries the
            // clip NAME and the scene carries the clip, so the authored per-room tone
            // plays and our approximation can be switched off (Plugin.AmbientBeds).
            var amb = new RoomAmbientData();
            if (f["AmbientData"] is JObject ad)
            {
                FillFields(amb, ad, name => name != "RoomTone" && name != "SeasonSoundPreset");
                var toneName = ad.Value<string>("RoomToneName");
                if (!string.IsNullOrEmpty(toneName))
                {
                    var clip = FindClip(toneName);
                    if (clip != null) { amb.RoomTone = clip; _roomTonesBound++; }
                    else _roomToneMisses.Add(toneName);
                }
                // retail's authored RoomToneVolume (1.0) plays UNSCALED. the whole
                // "deafening at 1.0" saga was a volume-boosted living_roomtone wav in the
                // SDK — missed by the first clip-restore pass, caught by the byte-compare
                // re-export (07-30) — never the authored level. the ZoneToneVolume knob
                // died with that discovery.
            }
            r.AmbientData = amb;
        }

        // portals bind to doors by WorldInteractiveObject.Id + position (<=15m). our
        // authored doors carry synthetic ids — stamp the retail DoorID onto the matching
        // door. this binding is load-bearing beyond door-state sync: CheckOcclusion plays
        // a door's OWN sound unoccluded when the listener is in either connected room, but
        // a door whose Id resolves to NO portal falls through to generic occlusion —
        // muffled from a meter away ("door sounds are pretty quiet").
        //
        // v1 matched against ALL WorldInteractiveObjects with first-come claiming, so a
        // loot container near a doorway could steal the id from the actual door. v2:
        // Doors only, globally-nearest pairing (all candidate pairs sorted by distance).
        private static void StampDoorIds(JObject sound, Dictionary<long, Component> comps)
        {
            var doors = UnityEngine.Object.FindObjectsOfType<Door>();
            if (doors.Length == 0) return;

            var pairs = new List<(float d2, Door door, string doorId, string origId)>();
            var portalByRow = new List<(SpatialAudioPortal p, string doorId)>();
            foreach (var row in Rows(sound, "SpatialAudioPortal"))
            {
                var doorId = row["fields"]?.Value<string>("DoorID");
                if (string.IsNullOrEmpty(doorId)) continue;
                if (!(Comp<SpatialAudioPortal>(comps, row) is SpatialAudioPortal p)) continue;
                portalByRow.Add((p, doorId));
                foreach (var d in doors)
                {
                    float d2 = (d.transform.position - p.transform.position).sqrMagnitude;
                    if (d2 < 15f * 15f) pairs.Add((d2, d, doorId, d.Id));
                }
            }
            // MACHINE-INDEPENDENT order: fika syncs doors BY ID across peers, so every
            // machine must stamp identically. distance ties are real on this ship
            // (symmetric double doors flank portals at equal d2) and the old
            // distance-only sort broke ties by FindObjectsOfType enumeration order —
            // one flipped claim on one peer cascades and the by-id door sync then
            // drives the WRONG door on that peer. tiebreak on the authored ids, which
            // ship identically in the bundle on every machine.
            pairs.Sort((a, b) =>
            {
                int c = a.d2.CompareTo(b.d2);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.doorId, b.doorId);
                return c != 0 ? c : string.CompareOrdinal(a.origId, b.origId);
            });

            var doneDoors = new HashSet<Door>();
            var doneIds = new HashSet<string>();
            int stamped = 0, renamed = 0;
            foreach (var (d2, door, doorId, _) in pairs)
            {
                if (doneDoors.Contains(door) || doneIds.Contains(doorId)) continue;
                doneDoors.Add(door);
                doneIds.Add(doorId);
                if (door.Id != doorId)
                {
                    // every REAL rename logged (07-28 coop door-sync hunt): fika resolves
                    // door packets by Id, and unresolvable ids mean the wire ids and the
                    // world registry disagree — this shows exactly which doors changed
                    if (++renamed <= 12)
                        Plugin.Log.LogDebug($"[Acoustics] door id RENAME '{door.Id}' -> '{doorId}' at {door.transform.position}");
                    door.Id = doorId;
                }
                stamped++;
            }
            if (renamed > 12) Plugin.Log.LogDebug($"[Acoustics] ...{renamed} renames total");
            if (renamed == 0) Plugin.Log.LogDebug("[Acoustics] all stamped ids already matched the authored ids (no renames)");
            Plugin.Log.LogDebug($"[Acoustics] stamped retail DoorIDs: {stamped} doors bound "
                + $"({portalByRow.Count - stamped} portal ids unmatched, {doors.Length - stamped} doors unbound)");
        }

        // ------------------------------------------------------- generic json -> object
        // reflection filler for plain-value graphs (settings assets, tuning classes).
        // component refs are wired manually above — this skips anything it can't map.
        private static void FillFields(object target, JObject fields, Func<string, bool> fieldFilter)
        {
            var type = target.GetType();
            foreach (var prop in fields.Properties())
            {
                if (fieldFilter != null && !fieldFilter(prop.Name)) continue;
                var fi = AccessTools.Field(type, prop.Name);
                if (fi == null) continue;
                try
                {
                    var v = ConvertToken(prop.Value, fi.FieldType, fi.GetValue(target));
                    if (v != null || !fi.FieldType.IsValueType) fi.SetValue(target, v);
                }
                catch { /* field drifted — keep the class default */ }
            }
        }

        private static object ConvertToken(JToken tok, Type type, object existing)
        {
            if (tok == null || tok.Type == JTokenType.Null) return null;

            if (type == typeof(string)) return tok.Value<string>();
            if (type == typeof(bool)) return tok.Type == JTokenType.Boolean ? tok.Value<bool>() : tok.Value<int>() != 0;
            if (type == typeof(short)) return (short)tok.Value<int>();
            if (type.IsEnum) return Enum.ToObject(type, tok.Value<int>());
            if (type.IsPrimitive) return Convert.ChangeType(((JValue)tok).Value, type,
                System.Globalization.CultureInfo.InvariantCulture);

            if (tok is JObject o)
            {
                if (type == typeof(Vector3)) return V3(o);
                if (type == typeof(Vector2)) return new Vector2(o.Value<float>("x"), o.Value<float>("y"));
                if (type == typeof(Quaternion)) return Q4(o);
                if (type == typeof(Bounds)) return new Bounds(V3(o["m_Center"]), V3(o["m_Extent"]) * 2f);
                if (type == typeof(LayerMask)) return (LayerMask)(o.Value<int?>("m_Bits") ?? 0);
                if (o["ref"] != null) return null; // object ref — not for the generic filler
                var inst = existing ?? Activator.CreateInstance(type);
                FillFields(inst, o, null);
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
                {
                    var v = ConvertToken(e, elem, null);
                    if (v == null && elem.IsValueType) return null; // element failed — bail whole array
                    items.Add(v);
                }
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

        private static AnimationCurve Curve(JToken tok)
        {
            var keys = tok?["m_Curve"] as JArray;
            if (keys == null || keys.Count == 0) return null;
            var kf = new Keyframe[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                kf[i] = new Keyframe(
                    k.Value<float?>("time") ?? 0f, k.Value<float?>("value") ?? 0f,
                    k.Value<float?>("inSlope") ?? 0f, k.Value<float?>("outSlope") ?? 0f);
            }
            return new AnimationCurve(kf);
        }

        private static Vector3 V3(JToken t)
        {
            if (t is JArray a) return new Vector3(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>());
            return new Vector3(t.Value<float>("x"), t.Value<float>("y"), t.Value<float>("z"));
        }

        private static Quaternion Q4(JToken t)
        {
            if (t is JArray a) return new Quaternion(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>(), a[3].Value<float>());
            return new Quaternion(t.Value<float>("x"), t.Value<float>("y"), t.Value<float>("z"), t.Value<float>("w"));
        }
    }
}
