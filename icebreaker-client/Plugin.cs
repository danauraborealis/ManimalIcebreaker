using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // dumps the fully-restored AICoversData (cover graph, core points, voxel grid,
    // patrol/loot/exfil points, bot zones) to json at raid load. ground truth for
    // reverse-engineering BSG's ai point baker so we can build one for custom maps.
    [BepInPlugin(BuildInfo.ModGuid, "Manimal-Icebreaker", BuildInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> AutoDump;
        internal static ConfigEntry<KeyboardShortcut> DumpKey;
        internal static ConfigEntry<KeyboardShortcut> GenerateKey;
        internal static ConfigEntry<bool> IndentJson;
        internal static ConfigEntry<bool> InjectCovers;
        internal static ConfigEntry<float> LampIntensity;
        internal static ConfigEntry<float> AmbientIntensity;
        internal static ConfigEntry<bool> LampShadows;
        internal static ConfigEntry<float> CullDistanceScale;
        internal static ConfigEntry<bool> PcDriverEnabled;
        internal static ConfigEntry<bool> NativeCulling;
        internal static ConfigEntry<bool> ShadowProxyFix;
        internal static ConfigEntry<int> CrewRoguesMin;
        internal static ConfigEntry<int> CrewRoguesMax;
        internal static ConfigEntry<bool> CrewKnight;
        internal static ConfigEntry<bool> CrewBlackDiv;
        internal static ConfigEntry<bool> SpatialAudio;
        internal static ConfigEntry<bool> EnvTriggers;
        internal static ConfigEntry<float> QuietBotRatio;
        internal static ConfigEntry<float> DoorSoundBoost;
        internal static ConfigEntry<float> ZoneToneVolume;
        internal static ConfigEntry<float> WindIndoorFraction;
        internal static ConfigEntry<bool> WeatherSystem;
        internal static ConfigEntry<bool> ForceWinter;
        internal static ConfigEntry<bool> HardBots;
        internal static ConfigEntry<bool> EventSpawns;
        internal static ConfigEntry<bool> DiagHotkeys;
        internal static ConfigEntry<float> WeatherDesaturate;
        internal static ConfigEntry<bool> VolFog;
        internal static ConfigEntry<float> VolFogDensity;
        internal static ConfigEntry<float> VolFogNoise;
        internal static ConfigEntry<float> VolFogNoiseScale;
        internal static ConfigEntry<float> VolFogHeight;
        internal static ConfigEntry<float> VolFogBaseline;
        internal static ConfigEntry<float> VolFogMaxLength;
        internal static ConfigEntry<float> VolFogMaxLengthFallOff;
        internal static ConfigEntry<float> VolFogIndoorFade;
        internal static ConfigEntry<float> VolFogVoidFalloff;
        internal static ConfigEntry<bool> VolFogVoidDebug;
        internal static ConfigEntry<float> VolFogVoidBlend;
        internal static ConfigEntry<float> VolFogDeepObscurance;
        internal static ConfigEntry<float> VolFogSkyHaze;
        internal static ConfigEntry<float> VolFogAlpha;
        internal static ConfigEntry<float> VolFogSpeed;
        internal static ConfigEntry<int> VolFogDownsampling;
        internal static ConfigEntry<Color> VolFogColor;
        internal static ConfigEntry<bool> Blizzard;
        internal static ConfigEntry<float> SnowIntensity;
        internal static ConfigEntry<float> CutsceneVolume;
        internal static ConfigEntry<float> BlizzardWind;
        internal static ConfigEntry<float> BlizzardFog;
        internal static ConfigEntry<bool> RetailSky;
        internal static ConfigEntry<bool> StaticSkybox;
        internal static ConfigEntry<float> TodHour;
        internal static ConfigEntry<bool> WeatherTonemap;
        internal static ConfigEntry<bool> WeatherFogPass;
        internal static ConfigEntry<float> SnowFallY;
        internal static ConfigEntry<float> FogBrightness;
        internal static ConfigEntry<Color> FogColor;
        internal static ConfigEntry<float> SnowFlakeSize;
        internal static ConfigEntry<bool> SnowStormDensity;
        internal static ConfigEntry<float> SnowSpread;
        internal static ConfigEntry<bool> RetailAIBake;
        internal static ConfigEntry<bool> RetailDoorLinks;
        internal static ConfigEntry<bool> PasscodeTerminals;
        internal static ConfigEntry<bool> InteriorCrossCull;
        internal static ConfigEntry<float> CrossCullDistance;
        // per-zone-tone volume multipliers (on top of the ZoneToneVolume master)
        internal static readonly System.Collections.Generic.Dictionary<string, ConfigEntry<float>> ToneVolumes
            = new System.Collections.Generic.Dictionary<string, ConfigEntry<float>>();

        private void Awake()
        {
            Log = Logger;
            AutoDump = Config.Bind("General", "AutoDumpOnRaidLoad", true,
                "dump automatically right after the game restores the ai data at raid load");
            DumpKey = Config.Bind("General", "DumpHotkey", new KeyboardShortcut(KeyCode.F9),
                "re-dump on demand mid-raid");
            GenerateKey = Config.Bind("General", "GenerateHotkey", new KeyboardShortcut(KeyCode.F10),
                "run the prototype cover scanner on the current map and dump the result — freezes the game for a bit");
            IndentJson = Config.Bind("General", "IndentJson", false,
                "pretty-print the json — files get roughly 3x bigger");
            InjectCovers = Config.Bind("Experimental", "InjectGeneratedCovers", false,
                "EXPERIMENT: at raid load, replace the map's baked cover points with scanner-generated ones. " +
                "adds ~10-30s to load. bots then use OUR points — for validating the custom-map baker");

            // Icebreaker's interior lamps serialize at intensity 0 (retail baked them into
            // lightmaps we don't have), so we revive them realtime. these apply live in-raid.
            LampIntensity = Config.Bind("Icebreaker", "LampIntensity", 3.0f,
                new ConfigDescription("brightness of the revived interior lamp lights",
                    new AcceptableValueRange<float>(0f, 12f)));
            AmbientIntensity = Config.Bind("Icebreaker", "AmbientIntensity", 0.8f,
                new ConfigDescription("flat ambient fill light — lifts shadowed areas out of black (no real bounce without a bake)",
                    new AcceptableValueRange<float>(0f, 3f)));
            LampShadows = Config.Bind("Icebreaker", "LampShadows", false,
                "let the revived lamps cast realtime shadows — much prettier, much heavier (1500+ lights)");
            CullDistanceScale = Config.Bind("Icebreaker", "CullDistanceScale", 1.0f,
                new ConfigDescription("distance-culling aggressiveness for small props (lower = culls closer = more fps) (live)",
                    new AcceptableValueRange<float>(0.25f, 3f)));
            NativeCulling = Config.Bind("Icebreaker", "NativeCulling", false,
                "use BSG's restored retail culling bake (Culling_Data) instead of our own sidecar bakes — the retail bake cost fps, so OFF: our .pcbake volumes drive culling; restart raid to switch");
            PcDriverEnabled = Config.Bind("Icebreaker", "PcDriverEnabled", true,
                "occlusion culling driver — flip OFF (live) to un-cull everything; the pop-in isolation tool: if pops stop with this off, the bake's sightline data is the culprit");
            // NOTE: the old LodBiasClamp knob is gone — forcing QualitySettings.lodBias
            // overrode the player's own Object LOD quality setting and halved loose
            // loot visibility (loot LODGroups cull on lodBias). vanilla settings stay
            // whatever the player configured.
            ShadowProxyFix = Config.Bind("Icebreaker", "ShadowProxyFix", true,
                "enforce the retail shadow split at load: _SHADOW_ proxy meshes cast-only, their visual siblings cast nothing (double-cast shadows otherwise)");
            // proof-of-life in the log when the culling knobs move — 'slider does nothing'
            // reports need to distinguish dead knob from wrong suspect
            CullDistanceScale.SettingChanged += (_, __) => Log.LogWarning($"[DistCull] scale -> {CullDistanceScale.Value:F2} (live)");
            PcDriverEnabled.SettingChanged += (_, __) => Log.LogWarning($"[Culling] PcDriverEnabled -> {PcDriverEnabled.Value}");
            CrewRoguesMin = Config.Bind("Icebreaker", "CrewRoguesMin", 9,
                new ConfigDescription("min rogue crew size — each raid rolls a random target in [min,max] (retail: 9-15)",
                    new AcceptableValueRange<int>(0, 20)));
            CrewRoguesMax = Config.Bind("Icebreaker", "CrewRoguesMax", 15,
                new ConfigDescription("max rogue crew size — each raid rolls a random target in [min,max] (retail: 9-15)",
                    new AcceptableValueRange<int>(0, 20)));
            CrewKnight = Config.Bind("Icebreaker", "CrewKnight", true,
                "spawn solo Knight among the rogues");
            CrewBlackDiv = Config.Bind("Icebreaker", "CrewBlackDivision", true,
                "spawn Black Division squads when the start-cutscene trigger is hit (needs the BlackDiv mod)");
            SpatialAudio = Config.Bind("Icebreaker", "SpatialAudio", true,
                "resurrect BSG's spatial audio (room/portal occlusion) from the recovered retail bake — needs the acoustics sidecar next to the dll");
            EnvTriggers = Config.Bind("Icebreaker", "EnvironmentTriggers", true,
                "rebuild the retail indoor/outdoor switcher volumes (indoor sound banks, exposure, rain muffling)");
            QuietBotRatio = Config.Bind("Icebreaker", "QuietBotRatio", 0.5f,
                new ConfigDescription("chance each non-boss bot spawns voice-muted (cuts the constant voiceline spam; knight + wedge always talk)",
                    new AcceptableValueRange<float>(0f, 1f)));
            DoorSoundBoost = Config.Bind("Icebreaker", "DoorSoundBoost", 2.0f,
                new ConfigDescription("gain multiplier for door open/close/squeak foley (ripped clips are mixed quiet; play volume clamps at 1.0)",
                    new AcceptableValueRange<float>(0.5f, 4f)));
            ZoneToneVolume = Config.Bind("Icebreaker", "ZoneToneVolume", 0.35f,
                new ConfigDescription("volume scale for the indoor zone room-tones (live; tames the loud living-room tone)",
                    new AcceptableValueRange<float>(0f, 1f)));
            WindIndoorFraction = Config.Bind("Icebreaker", "WindIndoorFraction", 0.20f,
                new ConfigDescription("how much of the outdoor wind bleeds through indoors (live; 0 = silent inside, 1 = full)",
                    new AcceptableValueRange<float>(0f, 1f)));
            WeatherSystem = Config.Bind("Icebreaker", "WeatherSystem", true,
                "resurrect BSG's weather/sky stack (clouds, wind, rain/snow, time-of-day) from the recovered retail components (needs the Author 9 weather-asset bundle)");
            ForceWinter = Config.Bind("Icebreaker", "ForceWinter", true,
                "force Winter season on icebreaker only (snowfall; other maps keep the server's season; needs WeatherSystem)");
            HardBots = Config.Bind("Icebreaker", "HardBots", true,
                "force ALL icebreaker AI (waves + crew + black division) to HARD difficulty");
            EventSpawns = Config.Bind("Icebreaker", "EventSpawns", true,
                "use BSG's resurrected spawn-trigger system (tier triggers + group-size BD spawns via server botEvent waves); off = legacy client-side watchers");
            Blizzard = Config.Bind("Icebreaker", "Blizzard", true,
                "permanent blizzard: pins WeatherDebug (snow/wind/fog) + raises the winter STORM state; also pins the TOD hour (live)");
            SnowIntensity = Config.Bind("Icebreaker", "SnowIntensity", 1.0f,
                new ConfigDescription("snowfall intensity (WeatherDebug.Rain in winter; live)", new AcceptableValueRange<float>(0f, 1f)));
            VolFog = Config.Bind("VolumetricFog2", "Enabled", true,
                "the REAL volumetric fog (Volumetric Fog & Mist 2, raymarched) — needs volumetricfog.bundle next to the plugin dll (live)");
            VolFogDensity = Config.Bind("VolumetricFog2", "Density", 0.585f,
                new ConfigDescription("fog density (live)", new AcceptableValueRange<float>(0f, 1f)));
            VolFogNoise = Config.Bind("VolumetricFog2", "NoiseStrength", 0.734f,
                new ConfigDescription("how uneven/wispy the fog is — 0 = uniform soup (live)", new AcceptableValueRange<float>(0f, 1f)));
            VolFogNoiseScale = Config.Bind("VolumetricFog2", "NoiseScale", 2.943f,
                new ConfigDescription("size of the fog billows (live)", new AcceptableValueRange<float>(0.2f, 5f)));
            VolFogHeight = Config.Bind("VolumetricFog2", "Height", 93.1f,
                new ConfigDescription("fog layer thickness in meters above baseline (live)", new AcceptableValueRange<float>(1f, 200f)));
            VolFogBaseline = Config.Bind("VolumetricFog2", "BaselineHeight", -7.1f,
                new ConfigDescription("world Y the fog layer sits on — icebreaker deck is around sea level (live)", new AcceptableValueRange<float>(-50f, 100f)));
            VolFogMaxLength = Config.Bind("VolumetricFog2", "MaxFogLength", 1006f,
                new ConfigDescription("raymarch reach in meters (live)", new AcceptableValueRange<float>(100f, 2000f)));
            VolFogMaxLengthFallOff = Config.Bind("VolumetricFog2", "MaxFogLengthFallOff", 0.6f,
                new ConfigDescription("soft rolloff of fog contribution toward MaxFogLength — kills the bright far-field glow that silhouettes the map border (live)", new AcceptableValueRange<float>(0f, 1f)));
            VolFogVoidFalloff = Config.Bind("VolumetricFog2", "VoidFalloff", 6f,
                new ConfigDescription("edge sharpness of fog exclusion voids (manimal_fogvoid boxes) — higher = crisper cutoff at the box faces (live)", new AcceptableValueRange<float>(1f, 20f)));
            VolFogVoidBlend = Config.Bind("VolumetricFog2", "VoidBlend", 0.25f,
                new ConfigDescription("smooth-min blend between nearby voids — bridges small fog slivers in gaps between adjacent void boxes (units are relative to box size) (live)", new AcceptableValueRange<float>(0f, 1f)));
            VolFogVoidDebug = Config.Bind("VolumetricFog2", "VoidDebug", false,
                "DEBUG: paint fog void interiors bright red instead of clearing them — shows exactly where the exclusion boxes land (live)");
            VolFogIndoorFade = Config.Bind("VolumetricFog2", "IndoorFadeSeconds", 1.5f,
                new ConfigDescription("fog fade duration when entering/leaving ship interiors (Indoor_* volume bounds) (live)", new AcceptableValueRange<float>(0f, 6f)));
            VolFogDeepObscurance = Config.Bind("VolumetricFog2", "DeepObscurance", 1.4f,
                new ConfigDescription("darkens fog with depth — higher = distance melts into the night instead of glowing (live)", new AcceptableValueRange<float>(0f, 3f)));
            VolFogSkyHaze = Config.Bind("VolumetricFog2", "SkyHaze", 0f,
                new ConfigDescription("haze height against the skybox (live)", new AcceptableValueRange<float>(0f, 500f)));
            VolFogAlpha = Config.Bind("VolumetricFog2", "Alpha", 0.612f,
                new ConfigDescription("overall fog opacity (live)", new AcceptableValueRange<float>(0f, 1f)));
            VolFogSpeed = Config.Bind("VolumetricFog2", "WindSpeed", 0.081f,
                new ConfigDescription("fog drift speed (live)", new AcceptableValueRange<float>(0f, 0.5f)));
            VolFogDownsampling = Config.Bind("VolumetricFog2", "Downsampling", 1,
                new ConfigDescription("render at 1/N resolution — KEEP AT 1: the downsampled composite path flips/quadrants the screen under EFTs pipeline (live)", new AcceptableValueRange<int>(1, 4)));
            VolFogColor = Config.Bind("VolumetricFog2", "FogColor", new Color(0.534f, 0.564f, 0.638f),
                "fog tint — cold blue-gray default (live)");
            WeatherDesaturate = Config.Bind("Icebreaker", "WeatherDesaturate", 0.23f,
                new ConfigDescription("override the weather desaturation post effect (blizzard cloudiness pins it to max = the gray washed-out look). -1 = vanilla behavior, 0 = full color, 1 = full gray (live)", new AcceptableValueRange<float>(-1f, 1f)));
            DiagHotkeys = Config.Bind("Icebreaker", "DiagHotkeys", false,
                "enable the F1-F12 diagnostic hotkey suite (lights toggle, geo hide, batching tests, probes). OFF for normal play — F9 doubles as the fog tuner toggle and MUST not fight the lights toggle (live)");
            BlizzardWind = Config.Bind("Icebreaker", "BlizzardWind", 1.5f,
                new ConfigDescription("blizzard wind magnitude (live)", new AcceptableValueRange<float>(0f, 3f)));
            BlizzardFog = Config.Bind("Icebreaker", "BlizzardFog", 0.015f,
                new ConfigDescription("blizzard fog density — EFT fog values are tiny (clear ~0.0013, heavy ~0.03) (live)", new AcceptableValueRange<float>(0f, 0.1f)));
            TodHour = Config.Bind("Icebreaker", "TodHour", 23f,
                new ConfigDescription("pinned time-of-day hour while Blizzard is on — icebreaker is authored as a NIGHT map (skybox_night, lamp-lit); day exposes skydome seams + the out-of-map snow plane (live)", new AcceptableValueRange<float>(0f, 24f)));
            WeatherTonemap = Config.Bind("Icebreaker", "WeatherTonemap", false,
                "enable the rebuilt weather Tonemapping screen pass (OFF fixes washed-out colors; live)");
            StaticSkybox = Config.Bind("Icebreaker", "StaticSkybox", true,
                "FACTORY-STYLE sky: apply the authored skybox_night material and disable the procedural dome painters (the flat white/black atmosphere + horizon seam). restart raid to A/B");
            RetailSky = Config.Bind("Icebreaker", "RetailSky", false,
                "apply retail 1.0's TOD_Sky atmosphere values at raid start — caused visual artifacts on the rebuilt sky, OFF by default (the StaticSkybox freeze is independent and stays on); restart raid to A/B");
            SnowFallY = Config.Bind("Icebreaker", "SnowFallY", -2f,
                new ConfigDescription("snow flake fall vector Y — dial live until flakes fall DOWN (retail 1.0 authored -2, the 0.16.9 shader seems to read it inverted) (live)", new AcceptableValueRange<float>(-6f, 6f)));
            FogBrightness = Config.Bind("Icebreaker", "FogBrightness", 0.12f,
                new ConfigDescription("fog color brightness — 0 = black veil (eats lights), ~0.1-0.2 = moonlit gray gloom (live)", new AcceptableValueRange<float>(0f, 1f)));
            FogColor = Config.Bind("Icebreaker", "FogColor", new Color(0.45f, 0.50f, 0.58f),
                "fog tint (multiplied by FogBrightness) — cold gray-blue default (live)");
            SnowFlakeSize = Config.Bind("Icebreaker", "SnowFlakeSize", 0.3f,
                new ConfigDescription("flake size multiplier vs retail (1 = retail; smaller = finer snow) (live)", new AcceptableValueRange<float>(0.15f, 2f)));
            SnowStormDensity = Config.Bind("Icebreaker", "SnowStormDensity", true,
                "enable the storm flake layers (+12k flakes, ~double density) (live)");
            CutsceneVolume = Config.Bind("Icebreaker", "CutsceneVolume", 1f,
                new ConfigDescription("cutscene video volume (live — adjustable mid-video via F12)", new AcceptableValueRange<float>(0f, 1f)));
            SnowSpread = Config.Bind("Icebreaker", "SnowSpread", 14f,
                new ConfigDescription("flake volume spread around the camera — retail 20; SMALLER packs the same flakes denser (live)", new AcceptableValueRange<float>(8f, 30f)));
            WeatherFogPass = Config.Bind("Icebreaker", "WeatherFogPass", false,
                "enable the TOD_Scattering depth-fog screen pass — with the rebuilt sky's colors this fogged the whole map to black (no WeatherObstacle = no indoor masking either), so OFF until the sky proves out (live)");
            InteriorCrossCull = Config.Bind("Icebreaker", "InteriorCrossCull", true,
                "cull interior volumes (Indoor_01/02/03) wholesale when the camera is outside them, beyond CrossCullDistance — the ship-center fps fix (live)");
            CrossCullDistance = Config.Bind("Icebreaker", "CrossCullDistance", 25f,
                new ConfigDescription("how close an out-of-volume interior group must be to still render (doorway/window sightlines) (live)", new AcceptableValueRange<float>(10f, 80f)));
            RetailDoorLinks = Config.Bind("Icebreaker", "RetailDoorLinks", true,
                "rebuild retail 1.0 NavMeshDoorLinks — the ONLY link source on this map (Waypoints' generator NREs here, so OFF means zero bot-door support)");
            PasscodeTerminals = Config.Bind("Icebreaker", "PasscodeTerminals", true,
                "activate the authored passcode terminals + post-it code notes (needs the Author 9 passcode sidecar next to the dll)");
            RetailAIBake = Config.Bind("Icebreaker", "RetailAIBake", true,
                "load BSG's recovered baked AI data (covers/voxels/cores/patrols) instead of runtime generation; off or fill-failure = synthesized graph");

            // one live slider per indoor zone tone — multiplies the ZoneToneVolume master.
            // names match the Amb_<zone> sources authored by Author 8.
            foreach (var zone in new[]
            {
                "BotZoneRooms", "BotZoneEngineCenter", "BotZoneFront", "BotZoneInside_t4",
                "BotZoneRoomsThirdKitchen", "BotZoneMash_t1", "BotZoneRoomsFour", "BotZoneKitchen",
                "BotZoneKorrDown1", "BotZoneKorr_t2", "BotZoneFront2", "BotZoneRoomsThird",
                "BotZoneRoom_Eng", "BotZoneRoom_Eng2", "BotZoneEngineHide",
            })
            {
                ToneVolumes[zone] = Config.Bind("IcebreakerToneVolumes", zone, 1.0f,
                    new ConfigDescription("volume multiplier for this zone's indoor tone (live)",
                        new AcceptableValueRange<float>(0f, 2f)));
            }

            // kill the origin-stacked zone tones the instant the sound scene loads —
            // during loading the AudioListener sits at origin inside all 13 of them
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) =>
            {
                try { IcebreakerAcoustics.OnSceneLoaded(scene); } catch { }
            };

            // preload the self-hosted Perfect Culling runtime so the icebreaker scene
            // bundle's PerfectCullingVolume components bind at scene load (unity resolves
            // bundle MonoBehaviours by assembly+class name against loaded assemblies).
            // sits next to this dll; harmless no-op if absent.
            try
            {
                var pcPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "PerfectCullingRuntime.dll");
                if (System.IO.File.Exists(pcPath))
                {
                    System.Reflection.Assembly.LoadFrom(pcPath);
                    Log.LogInfo("PerfectCullingRuntime.dll preloaded (icebreaker occlusion culling)");
                }
            }
            catch (System.Exception e) { Log.LogWarning($"PerfectCullingRuntime preload failed: {e.Message}"); }

            new Harmony(BuildInfo.ModGuid).PatchAll();
            Log.LogInfo($"Manimal-Icebreaker {BuildInfo.Version} loaded");
        }

        private void Update()
        {
            if (DumpKey.Value.IsDown())
                AiDump.TryDump(Object.FindObjectOfType<AICoversData>(), "hotkey");
            if (GenerateKey.Value.IsDown())
                CoverScanner.TryGenerate();
        }
    }

    // CachePoints is the last step of the covers restore chain in BotsController.Init
    // (CreateOrFind -> RestoreData -> CachePoints), so a postfix here sees the graph
    // exactly as bots will use it — ids rewired, manual points merged, voxels linked.
    [HarmonyPatch(typeof(AICoversData), nameof(AICoversData.CachePoints))]
    internal static class Patch_CachePoints
    {
        [HarmonyPostfix]
        private static void Postfix(AICoversData __instance)
        {
            if (Plugin.AutoDump.Value)
                AiDump.TryDump(__instance, "raid-load");
        }
    }

    // swap in generated covers BEFORE the game restores/wires anything, so the whole
    // restore chain (voxel rewire, way targets, patrol restore) runs on our data the
    // same way it runs on baked data
    [HarmonyPatch(typeof(AICoversData), nameof(AICoversData.RestoreData))]
    internal static class Patch_RestoreData
    {
        [HarmonyPrefix]
        private static void Prefix(AICoversData __instance)
        {
            if (Plugin.InjectCovers.Value)
                CoverScanner.TryInjectIntoCovers(__instance);
        }
    }

    // loose-loot points cant be generated at RestoreData time — factory-style maps have
    // no loose-loot scene markers, only backend records, and those arrive here. postfix
    // so the game's own container matching has already run on our container points.
    [HarmonyPatch(typeof(AIPatrolsData), nameof(AIPatrolsData.RestoreLoot))]
    internal static class Patch_RestoreLoot
    {
        [HarmonyPostfix]
        private static void Postfix(AIPatrolsData __instance, [HarmonyArgument(0)] GClass1404 lootData)
        {
            try
            {
                LootScanner.OnRestoreLoot(__instance, lootData);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"loose loot generation failed: {e}");
            }
        }
    }
}
