using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// the fika sync addon subscribes to the chain-door/sealed-door world-event hooks
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ManimalIcebreakerFika")]

namespace Manimal.Icebreaker
{
    // dumps the fully-restored AICoversData (cover graph, core points, voxel grid,
    // patrol/loot/exfil points, bot zones) to json at raid load. ground truth for
    // reverse-engineering BSG's ai point baker so we can build one for custom maps.
    [BepInPlugin(BuildInfo.ModGuid, "Manimal-Icebreaker", BuildInfo.Version)]
    // soft: loads AFTER fika when fika is installed (so the runtime compat patches can
    // resolve its types at Awake), changes nothing when it isn't
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
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
        internal static ConfigEntry<bool> CrewPreSpawnPool;
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
        internal static ConfigEntry<float> VolFogWallShade;
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
        internal static ConfigEntry<bool> TimelineCutscene;
        internal static ConfigEntry<bool> Tripwires;
        internal static ConfigEntry<bool> BotFriendlyFire;
        internal static ConfigEntry<bool> HovercraftTransit;
        internal static ConfigEntry<float> TransitX;
        internal static ConfigEntry<float> TransitY;
        internal static ConfigEntry<float> TransitZ;
        internal static ConfigEntry<float> TransitZoneX;
        internal static ConfigEntry<float> TransitZoneY;
        internal static ConfigEntry<float> TransitZoneZ;
        internal static ConfigEntry<float> TransitZoneSizeX;
        internal static ConfigEntry<float> TransitZoneSizeY;
        internal static ConfigEntry<float> TransitZoneSizeZ;
        internal static ConfigEntry<float> TransitZoneRotY;
        internal static ConfigEntry<int> TransitCost;
        internal static ConfigEntry<int> TransitActivateAfterSec;
        internal static ConfigEntry<int> HeliExfilCost;
        internal static ConfigEntry<bool> SnowGusts;
        internal static ConfigEntry<float> SnowGustsDensity;
        internal static ConfigEntry<float> SnowGustsSize;
        internal static ConfigEntry<float> SnowGustsSpeed;
        internal static ConfigEntry<float> SnowGustsOpacity;
        internal static ConfigEntry<float> SnowGustsReach;
        internal static ConfigEntry<float> TransitRotX;
        internal static ConfigEntry<float> TransitRotY;
        internal static ConfigEntry<float> TransitRotZ;
        internal static ConfigEntry<float> TripwireChance;
        internal static ConfigEntry<string> TripwireTpl;
        // cutscene fog PROFILE: twin entries for every look-affecting fog value. while
        // IcebreakerVolFog.CutsceneProfile is on, Fog()/FogColorEntry route reads (and
        // the F9 tuner's writes) to these instead — tune the cutscene without touching
        // the raid look. defaults copy the main entries' defaults at bind time.
        private static readonly System.Collections.Generic.Dictionary<ConfigEntry<float>, ConfigEntry<float>>
            _fogTwin = new System.Collections.Generic.Dictionary<ConfigEntry<float>, ConfigEntry<float>>();
        private static ConfigEntry<Color> _csVolFogColor;

        internal static ConfigEntry<float> Fog(ConfigEntry<float> main)
            => IcebreakerVolFog.CutsceneProfile && _fogTwin.TryGetValue(main, out var twin) ? twin : main;

        internal static ConfigEntry<Color> FogColorEntry
            => IcebreakerVolFog.CutsceneProfile && _csVolFogColor != null ? _csVolFogColor : VolFogColor;
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
            AutoDump = Config.Bind("General", "AutoDumpOnRaidLoad", false,
                "dump the ai data right after the game restores it at raid load (icebreaker only — dev diagnostic)");
            DumpKey = Config.Bind("General", "DumpHotkey", new KeyboardShortcut(KeyCode.None),
                "re-dump on demand mid-raid (icebreaker only; F9 is the fog tuner — bind something else)");
            GenerateKey = Config.Bind("General", "GenerateHotkey", new KeyboardShortcut(KeyCode.None),
                "run the prototype cover scanner on the current map and dump the result — freezes the game for a bit (icebreaker only)");
            IndentJson = Config.Bind("General", "IndentJson", false,
                "pretty-print the json — files get roughly 3x bigger");
            InjectCovers = Config.Bind("Experimental", "InjectGeneratedCovers", true,
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
            CullDistanceScale = Config.Bind("Icebreaker", "CullDistanceScale", 0.5f,
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
            // default OFF (user call 07-28): the pen cant actually hold bots — the mover
            // keeps a ship-side path and drags escapees back to the nearest navmesh edge
            // (black division surfacing on the starboard walkway). the spawn hitches the
            // pool was built for turned out to be init-burst/log-storm bugs, since fixed;
            // premake+prewarm makes on-demand spawns cheap enough.
            CrewPreSpawnPool = Config.Bind("Icebreaker", "CrewPreSpawnPool", false,
                "pre-spawn the trigger squads into an off-map pen during the early raid and TELEPORT them in when events fire. off (default) = profiles premade + bundles prewarmed, bots spawned on demand through the real pipeline");
            // defaults trimmed a squad below retail's 9-15 (user call 07-27) — the full
            // roster plus the trigger squads made the ship feel overcrowded
            CrewRoguesMin = Config.Bind("Icebreaker", "CrewRoguesMin", 6,
                new ConfigDescription("min rogue crew size — each raid rolls a random target in [min,max] (retail: 9-15)",
                    new AcceptableValueRange<int>(0, 20)));
            CrewRoguesMax = Config.Bind("Icebreaker", "CrewRoguesMax", 11,
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
            QuietBotRatio = Config.Bind("Icebreaker", "QuietBotRatio", 0.3f,
                new ConfigDescription("chance each non-boss bot spawns voice-muted (cuts the constant voiceline spam; knight + wedge always talk)",
                    new AcceptableValueRange<float>(0f, 1f)));
            DoorSoundBoost = Config.Bind("Icebreaker", "DoorSoundBoost", 4.0f,
                new ConfigDescription("gain multiplier for door open/close/squeak foley (ripped clips are mixed quiet; play volume clamps at 1.0)",
                    new AcceptableValueRange<float>(0.5f, 4f)));
            ZoneToneVolume = Config.Bind("Icebreaker", "ZoneToneVolume", 0.25f,
                new ConfigDescription("volume scale for the indoor zone room-tones (live; tames the loud living-room tone)",
                    new AcceptableValueRange<float>(0f, 1f)));
            WindIndoorFraction = Config.Bind("Icebreaker", "WindIndoorFraction", 0.10f,
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
            VolFogDensity = Config.Bind("VolumetricFog2", "Density", 0.356f,
                new ConfigDescription("fog density (live)", new AcceptableValueRange<float>(0f, 1f)));
            VolFogNoise = Config.Bind("VolumetricFog2", "NoiseStrength", 0.35f,
                new ConfigDescription("how uneven/wispy the fog is — 0 = uniform soup (live)", new AcceptableValueRange<float>(0f, 1f)));
            VolFogNoiseScale = Config.Bind("VolumetricFog2", "NoiseScale", 3.373f,
                new ConfigDescription("size of the fog billows (live)", new AcceptableValueRange<float>(0.2f, 5f)));
            VolFogHeight = Config.Bind("VolumetricFog2", "Height", 162.4f,
                new ConfigDescription("fog layer thickness in meters above baseline (live)", new AcceptableValueRange<float>(1f, 200f)));
            VolFogBaseline = Config.Bind("VolumetricFog2", "BaselineHeight", -27.96f,
                new ConfigDescription("world Y the fog layer sits on — icebreaker deck is around sea level (live)", new AcceptableValueRange<float>(-50f, 100f)));
            VolFogMaxLength = Config.Bind("VolumetricFog2", "MaxFogLength", 744f,
                new ConfigDescription("raymarch reach in meters (live)", new AcceptableValueRange<float>(100f, 2000f)));
            VolFogMaxLengthFallOff = Config.Bind("VolumetricFog2", "MaxFogLengthFallOff", 0f,
                new ConfigDescription("width of the distance-wall ramp as a fraction of MaxFogLength — fog climbs to FULLY OPAQUE at MaxFogLength, hiding the far field entirely (live)", new AcceptableValueRange<float>(0f, 1f)));
            VolFogWallShade = Config.Bind("VolumetricFog2", "WallShade", 0f,
                new ConfigDescription("brightness of the opaque distance wall as a fraction of the fog color — low = far field fades into dark gloom, 1 = full fog color (live)", new AcceptableValueRange<float>(0f, 1f)));
            VolFogVoidFalloff = Config.Bind("VolumetricFog2", "VoidFalloff", 5.54f,
                new ConfigDescription("edge sharpness of fog exclusion voids (manimal_fogvoid boxes) — higher = crisper cutoff at the box faces (live)", new AcceptableValueRange<float>(1f, 20f)));
            VolFogVoidBlend = Config.Bind("VolumetricFog2", "VoidBlend", 0.711f,
                new ConfigDescription("smooth-min blend between nearby voids — bridges small fog slivers in gaps between adjacent void boxes (units are relative to box size) (live)", new AcceptableValueRange<float>(0f, 1f)));
            VolFogVoidDebug = Config.Bind("VolumetricFog2", "VoidDebug", false,
                "DEBUG: paint fog void interiors bright red instead of clearing them — shows exactly where the exclusion boxes land (live)");
            VolFogIndoorFade = Config.Bind("VolumetricFog2", "IndoorFadeSeconds", 1.5f,
                new ConfigDescription("fog fade duration when entering/leaving ship interiors (Indoor_* volume bounds) (live)", new AcceptableValueRange<float>(0f, 6f)));
            VolFogDeepObscurance = Config.Bind("VolumetricFog2", "DeepObscurance", 0.867f,
                new ConfigDescription("darkens fog with depth — higher = distance melts into the night instead of glowing (live)", new AcceptableValueRange<float>(0f, 3f)));
            VolFogSkyHaze = Config.Bind("VolumetricFog2", "SkyHaze", 0f,
                new ConfigDescription("haze height against the skybox (live)", new AcceptableValueRange<float>(0f, 500f)));
            VolFogAlpha = Config.Bind("VolumetricFog2", "Alpha", 1f,
                new ConfigDescription("overall fog opacity (live)", new AcceptableValueRange<float>(0f, 1f)));
            VolFogSpeed = Config.Bind("VolumetricFog2", "WindSpeed", 0.156f,
                new ConfigDescription("fog drift speed (live)", new AcceptableValueRange<float>(0f, 0.5f)));
            VolFogDownsampling = Config.Bind("VolumetricFog2", "Downsampling", 1,
                new ConfigDescription("render at 1/N resolution — KEEP AT 1: the downsampled composite path flips/quadrants the screen under EFTs pipeline (live)", new AcceptableValueRange<int>(1, 4)));
            VolFogColor = Config.Bind("VolumetricFog2", "FogColor", new Color(0.467f, 0.459f, 0.424f),
                "fog tint — cold blue-gray default (live)");
            WeatherDesaturate = Config.Bind("Icebreaker", "WeatherDesaturate", 0.295f,
                new ConfigDescription("override the weather desaturation post effect (blizzard cloudiness pins it to max = the gray washed-out look). -1 = vanilla behavior, 0 = full color, 1 = full gray (live)", new AcceptableValueRange<float>(-1f, 1f)));

            // cutscene fog profile twins — tuned independently via F9 while the cutscene
            // is up (P pauses). defaults = the user's baked cutscene look (2026-07-22):
            // thinner, lower-noise fog with a closer wall and much lighter desat, so the
            // helicopter shots read clearly through the storm.
            var csBaked = new System.Collections.Generic.Dictionary<ConfigEntry<float>, float>
            {
                { VolFogDensity, 0.156f }, { VolFogNoise, 0.233f }, { VolFogNoiseScale, 2.066f },
                { VolFogHeight, 200f }, { VolFogBaseline, -27.96f }, { VolFogMaxLength, 617.3f },
                { VolFogMaxLengthFallOff, 0f }, { VolFogWallShade, 0f }, { VolFogVoidFalloff, 5.54f },
                { VolFogVoidBlend, 0.711f }, { VolFogDeepObscurance, 0.867f }, { VolFogSkyHaze, 0f },
                { VolFogAlpha, 1f }, { VolFogSpeed, 0.156f }, { WeatherDesaturate, 0.128f },
            };
            foreach (var kv in csBaked)
            {
                _fogTwin[kv.Key] = Config.Bind("VolumetricFog2.Cutscene",
                    kv.Key.Definition.Key, kv.Value,
                    $"cutscene-profile twin of {kv.Key.Definition.Section}.{kv.Key.Definition.Key} (live)");
            }
            _csVolFogColor = Config.Bind("VolumetricFog2.Cutscene", "FogColor",
                new Color(0.482f, 0.455f, 0.420f), "cutscene-profile fog tint (live)");
            DiagHotkeys = Config.Bind("Icebreaker", "DiagHotkeys", false,
                "enable the F1-F12 diagnostic hotkey suite (lights toggle, geo hide, batching tests, probes). OFF for normal play — F9 doubles as the fog tuner toggle and MUST not fight the lights toggle (live)");
            BlizzardWind = Config.Bind("Icebreaker", "BlizzardWind", 2.0f,
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
            SnowStormDensity = Config.Bind("Icebreaker", "SnowStormDensity", false,
                "enable the storm flake layers (+12k flakes, ~double density) (live)");
            CutsceneVolume = Config.Bind("Icebreaker", "CutsceneVolume", 1f,
                new ConfigDescription("cutscene video volume (live — adjustable mid-video via F12)", new AcceptableValueRange<float>(0f, 1f)));
            TimelineCutscene = Config.Bind("Icebreaker", "TimelineCutscene", true,
                "play the in-engine helicopter cutscene at the story trigger (falls back to the video if the cutscene scene/timeline is unavailable)");
            HovercraftTransit = Config.Bind("Icebreaker", "HovercraftTransit", true,
                "spawn the smugglers' hovercraft transit on the Shoreline coast once the Boreas chain is finished (takes you to the icebreaker)");
            TransitX = Config.Bind("Icebreaker", "TransitX", -312.0199f,
                new ConfigDescription("hovercraft transit position X on Shoreline (live)", new AcceptableValueRange<float>(-2000f, 2000f)));
            TransitY = Config.Bind("Icebreaker", "TransitY", -65.899f,
                new ConfigDescription("hovercraft transit position Y on Shoreline (live)", new AcceptableValueRange<float>(-500f, 500f)));
            TransitZ = Config.Bind("Icebreaker", "TransitZ", 527.0115f,
                new ConfigDescription("hovercraft transit position Z on Shoreline (live)", new AcceptableValueRange<float>(-2000f, 2000f)));
            // FULL euler, not just heading: the ripped prefab carries the OBJ axis
            // correction on X (270), so overwriting rotation with yaw alone lays it flat
            TransitRotX = Config.Bind("Icebreaker", "TransitRotX", 270f,
                new ConfigDescription("hovercraft rotation X (270 = the OBJ axis correction) (live)", new AcceptableValueRange<float>(0f, 360f)));
            TransitRotY = Config.Bind("Icebreaker", "TransitRotY", 40f,
                new ConfigDescription("hovercraft rotation Y, its heading (live)", new AcceptableValueRange<float>(0f, 360f)));
            TransitRotZ = Config.Bind("Icebreaker", "TransitRotZ", 0f,
                new ConfigDescription("hovercraft rotation Z (live)", new AcceptableValueRange<float>(0f, 360f)));
            // the boarding trigger has its own transform: it sits beside the craft rather
            // than inside it, and is a flattened box aligned to the hull, not a cube
            TransitZoneX = Config.Bind("Icebreaker", "TransitZoneX", -310.3848f,
                new ConfigDescription("transit trigger position X (live)", new AcceptableValueRange<float>(-2000f, 2000f)));
            TransitZoneY = Config.Bind("Icebreaker", "TransitZoneY", -63.49909f,
                new ConfigDescription("transit trigger position Y (live)", new AcceptableValueRange<float>(-500f, 500f)));
            TransitZoneZ = Config.Bind("Icebreaker", "TransitZoneZ", 531.0692f,
                new ConfigDescription("transit trigger position Z (live)", new AcceptableValueRange<float>(-2000f, 2000f)));
            TransitZoneSizeX = Config.Bind("Icebreaker", "TransitZoneSizeX", 8f,
                new ConfigDescription("transit trigger box size X in meters (live)", new AcceptableValueRange<float>(0.5f, 60f)));
            TransitZoneSizeY = Config.Bind("Icebreaker", "TransitZoneSizeY", 3f,
                new ConfigDescription("transit trigger box size Y in meters (live)", new AcceptableValueRange<float>(0.5f, 60f)));
            TransitZoneSizeZ = Config.Bind("Icebreaker", "TransitZoneSizeZ", 3f,
                new ConfigDescription("transit trigger box size Z in meters (live)", new AcceptableValueRange<float>(0.5f, 60f)));
            TransitCost = Config.Bind("Icebreaker", "TransitCost", 400000,
                new ConfigDescription("rouble price shown for the hovercraft crossing (live)", new AcceptableValueRange<int>(0, 5000000)));
            TransitZoneRotY = Config.Bind("Icebreaker", "TransitZoneRotY", 40f,
                new ConfigDescription("transit trigger heading, match the craft (live)", new AcceptableValueRange<float>(0f, 360f)));
            // all six are read every frame, so the gusts can be dialled in mid-raid
            SnowGusts = Config.Bind("Icebreaker", "SnowGusts", true,
                "wind-driven sheets of blown snow around the player, on top of the native flakes (live)");
            SnowGustsDensity = Config.Bind("Icebreaker", "SnowGustsDensity", 120f,
                new ConfigDescription("gust puffs spawned per second at full storm (live)", new AcceptableValueRange<float>(0f, 120f)));
            SnowGustsSize = Config.Bind("Icebreaker", "SnowGustsSize", 20f,
                new ConfigDescription("puff diameter in metres — big and soft is what reads as smoke (live)", new AcceptableValueRange<float>(0.5f, 40f)));
            SnowGustsSpeed = Config.Bind("Icebreaker", "SnowGustsSpeed", 1.6f,
                new ConfigDescription("multiplier on the weather's own wind speed (live)", new AcceptableValueRange<float>(0.1f, 8f)));
            SnowGustsOpacity = Config.Bind("Icebreaker", "SnowGustsOpacity", 0.16f,
                new ConfigDescription("peak alpha per puff — low is the whole trick, they stack (live)", new AcceptableValueRange<float>(0.01f, 1f)));
            SnowGustsReach = Config.Bind("Icebreaker", "SnowGustsReach", 45f,
                new ConfigDescription("how far upwind and how wide the gusts spawn, in metres (live)", new AcceptableValueRange<float>(5f, 150f)));
            HeliExfilCost = Config.Bind("Icebreaker", "HeliExfilCost", 2400,
                new ConfigDescription("euros the pilot charges to board the helicopter exfil, 0 for a free ride (read at raid start)",
                    new AcceptableValueRange<int>(0, 1000000)));
            TransitActivateAfterSec = Config.Bind("Icebreaker", "TransitActivateAfterSec", 30,
                new ConfigDescription("seconds before the crossing opens, listed red with a countdown until then (every vanilla transit uses 60)",
                    new AcceptableValueRange<int>(0, 600)));
            BotFriendlyFire = Config.Bind("Icebreaker", "BotFriendlyFire", false,
                "allow bots to damage ALLIED bots (default off: crew sprays through squadmates in tight corridors and revenge-aggro wipes whole squads — real bot-vs-bot fights are always lethal regardless)");
            Tripwires = Config.Bind("Icebreaker", "Tripwires", true,
                "plant the authored grenade tripwires (manimal_tripwire* markers) at raid start");
            TripwireChance = Config.Bind("Icebreaker", "TripwireChance", 1f,
                new ConfigDescription("per-marker chance a tripwire is armed this raid (labyrinth-style variety below 1)", new AcceptableValueRange<float>(0f, 1f)));
            TripwireTpl = Config.Bind("Icebreaker", "TripwireTpl", "6a5d6a5f4ed8c025a0a2cff0",
                "grenade template planted on the wires (default: Manimal CS gas grenade)");
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

            var harmony = new Harmony(BuildInfo.ModGuid);
            harmony.PatchAll();
            IcebreakerFikaCompat.TryApply(harmony); // no-op without fika
            Log.LogInfo($"Manimal-Icebreaker {BuildInfo.Version} loaded");
        }

        private void Update()
        {
            if (!IceGate.On) return; // every manimal-icebreaker diagnostic is map-gated
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
            if (IceGate.On && Plugin.AutoDump.Value)
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
            if (IceGate.On && Plugin.InjectCovers.Value)
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
