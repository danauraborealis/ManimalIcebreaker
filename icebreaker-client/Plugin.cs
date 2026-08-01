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
        internal static ConfigEntry<float> DoorSoundBoost;
        internal static ConfigEntry<bool> AmbientBeds;
        internal static ConfigEntry<float> WindIndoorFraction;
        internal static ConfigEntry<bool> WeatherSystem;
        internal static ConfigEntry<bool> ForceWinter;
        internal static ConfigEntry<bool> HardBots;
        internal static ConfigEntry<bool> DiagHotkeys;
        // fog + weather tuning left the config on 07-30 — the tuned values ARE the map
        // look now, hardcoded as defaults here. Val<T> keeps the .Value shape so the
        // F9 tuner still writes them live; changes just dont persist (DUMP + hardcode
        // is the tuning loop). only the on/off feature toggles stay config-bound.
        internal static readonly Val<float> WeatherDesaturate = new Val<float>(0.295f);
        internal static ConfigEntry<bool> VolFog;
        internal static readonly Val<float> VolFogDensity = new Val<float>(0.28f);
        internal static readonly Val<float> VolFogNoise = new Val<float>(0.35f);
        internal static readonly Val<float> VolFogNoiseScale = new Val<float>(3.373f);
        internal static readonly Val<float> VolFogHeight = new Val<float>(162.4f);
        internal static readonly Val<float> VolFogBaseline = new Val<float>(-27.96f);
        internal static readonly Val<float> VolFogMaxLength = new Val<float>(659.4445f);
        internal static readonly Val<float> VolFogMaxLengthFallOff = new Val<float>(0f);
        internal static readonly Val<float> VolFogWallShade = new Val<float>(0.1388889f);
        internal static readonly Val<float> VolFogIndoorFade = new Val<float>(1.5f);
        internal static readonly Val<float> VolFogVoidFalloff = new Val<float>(5.54f);
        internal static readonly Val<bool> VolFogVoidDebug = new Val<bool>(false);
        internal static readonly Val<float> VolFogVoidBlend = new Val<float>(0.711f);
        internal static readonly Val<float> VolFogDeepObscurance = new Val<float>(0.867f);
        internal static readonly Val<float> VolFogSkyHaze = new Val<float>(0f);
        internal static readonly Val<float> VolFogAlpha = new Val<float>(0.9611111f);
        internal static readonly Val<float> VolFogSpeed = new Val<float>(0.156f);
        internal static readonly Val<int> VolFogDownsampling = new Val<int>(1);
        internal static readonly Val<Color> VolFogColor = new Val<Color>(new Color(0.467f, 0.459f, 0.424f));
        internal static ConfigEntry<bool> Blizzard;
        internal static readonly Val<float> SnowIntensity = new Val<float>(1.0f);
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
        internal static readonly Val<float> SnowGustsDensity = new Val<float>(120f);
        internal static readonly Val<float> SnowGustsSize = new Val<float>(20f);
        internal static readonly Val<float> SnowGustsSpeed = new Val<float>(1.6f);
        internal static readonly Val<float> SnowGustsOpacity = new Val<float>(0.25f);
        internal static readonly Val<float> SnowGustsReach = new Val<float>(150f);
        internal static ConfigEntry<float> TransitRotX;
        internal static ConfigEntry<float> TransitRotY;
        internal static ConfigEntry<float> TransitRotZ;
        internal static ConfigEntry<float> TripwireChance;
        internal static ConfigEntry<string> TripwireTpl;
        // cutscene fog PROFILE: twin values for every look-affecting fog value. while
        // IcebreakerVolFog.CutsceneProfile is on, Fog()/FogColorEntry route reads (and
        // the F9 tuner's writes) to these instead — tune the cutscene without touching
        // the raid look. values = the user's baked cutscene look (2026-07-22).
        private static readonly System.Collections.Generic.Dictionary<Val<float>, Val<float>>
            _fogTwin = new System.Collections.Generic.Dictionary<Val<float>, Val<float>>();
        private static Val<Color> _csVolFogColor;

        internal static Val<float> Fog(Val<float> main)
            => IcebreakerVolFog.CutsceneProfile && _fogTwin.TryGetValue(main, out var twin) ? twin : main;

        internal static Val<Color> FogColorEntry
            => IcebreakerVolFog.CutsceneProfile && _csVolFogColor != null ? _csVolFogColor : VolFogColor;
        internal static readonly Val<float> BlizzardWind = new Val<float>(2.0f);
        internal static readonly Val<float> BlizzardFog = new Val<float>(0.015f);
        internal static readonly Val<bool> RetailSky = new Val<bool>(false);
        internal static readonly Val<bool> StaticSkybox = new Val<bool>(true);
        internal static readonly Val<float> TodHour = new Val<float>(23f);
        internal static readonly Val<bool> WeatherTonemap = new Val<bool>(false);
        internal static readonly Val<bool> WeatherFogPass = new Val<bool>(false);
        internal static readonly Val<float> SnowFallY = new Val<float>(-2f);
        internal static readonly Val<float> FogBrightness = new Val<float>(0.12f);
        internal static readonly Val<Color> FogColor = new Val<Color>(new Color(0.45f, 0.50f, 0.58f));
        internal static readonly Val<float> SnowFlakeSize = new Val<float>(0.3f);
        internal static readonly Val<bool> SnowStormDensity = new Val<bool>(false);
        internal static readonly Val<float> SnowSpread = new Val<float>(14f);
        internal static ConfigEntry<bool> RetailAIBake;
        internal static ConfigEntry<string> SynthZones;
        internal static ConfigEntry<bool> PasscodeTerminals;
        internal static ConfigEntry<bool> InteriorCrossCull;
        internal static ConfigEntry<float> CrossCullDistance;
        // plain live-value holder — the .Value shape ConfigEntry consumers already use,
        // for tuned values that left the config (fog/weather 07-30). mutable at runtime
        // (the F9 tuner writes these) but never persisted.
        internal sealed class Val<T> { public T Value; public Val(T v) { Value = v; } }

        // per-zone tone multipliers (on top of the ZoneToneVolume master). used to be
        // 15 config sliders — retired 07-30 once the tuned values became the shipped
        // truth; ZoneToneVolume remains the one live audio knob. names match the
        // Amb_<zone> sources authored by Author 8; unknown zones stay at 1.
        internal static float ZoneToneMultiplier(string zone)
        {
            switch (zone)
            {
                case "BotZoneEngineCenter": return 1.0f;
                case "BotZoneEngineHide": return 0.5f;
                case "BotZoneRooms": case "BotZoneFront": case "BotZoneInside_t4":
                case "BotZoneRoomsThirdKitchen": case "BotZoneMash_t1": case "BotZoneRoomsFour":
                case "BotZoneKitchen": case "BotZoneKorrDown1": case "BotZoneKorr_t2":
                case "BotZoneFront2": case "BotZoneRoomsThird": case "BotZoneRoom_Eng":
                case "BotZoneRoom_Eng2": return 0.25f;
                default: return 1f;
            }
        }

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
            DoorSoundBoost = Config.Bind("Icebreaker", "DoorSoundBoost", 4.0f,
                new ConfigDescription("gain multiplier for door open/close/squeak foley (ripped clips are mixed quiet; play volume clamps at 1.0)",
                    new AcceptableValueRange<float>(0.5f, 4f)));
            AmbientBeds = Config.Bind("Icebreaker", "AmbientBeds", false,
                "OUR hand-placed ambient beds (the Amb_<zone> room tones + outdoor wind bed from the Author 8 pass). " +
                "off by default now that retail's own per-room tones are restored onto the SpatialAudioRooms — " +
                "leaving both on stacks two room tones. turn it back ON if the authored tones fail to bind " +
                "(watch the '[Acoustics] bound N authored room tones' line) (live)");
            // ZoneToneVolume is GONE (user call 07-30): the room tones play at retail's
            // authored 1.0. the whole "deafening at 1.0" era traced back to a volume-
            // boosted living_roomtone wav in the SDK — missed in the first clip-restore
            // pass, caught by the byte-compare re-export — not to the authored level.
            WindIndoorFraction = Config.Bind("Icebreaker", "WindIndoorFraction", 0.04f,
                new ConfigDescription("how much of the outdoor wind bleeds through indoors (live; 0 = silent inside, 1 = full)",
                    new AcceptableValueRange<float>(0f, 1f)));
            WeatherSystem = Config.Bind("Icebreaker", "WeatherSystem", true,
                "resurrect BSG's weather/sky stack (clouds, wind, rain/snow, time-of-day) from the recovered retail components (needs the Author 9 weather-asset bundle)");
            ForceWinter = Config.Bind("Icebreaker", "ForceWinter", true,
                "force Winter season on icebreaker only (snowfall; other maps keep the server's season; needs WeatherSystem)");
            HardBots = Config.Bind("Icebreaker", "HardBots", true,
                "force ALL icebreaker AI (waves + crew + black division) to HARD difficulty");
            Blizzard = Config.Bind("Icebreaker", "Blizzard", true,
                "permanent blizzard: pins WeatherDebug (snow/wind/fog) + raises the winter STORM state; also pins the TOD hour (live)");
            VolFog = Config.Bind("VolumetricFog2", "Enabled", true,
                "the REAL volumetric fog (Volumetric Fog & Mist 2, raymarched) — needs volumetricfog.bundle next to the plugin dll (live)");

            // cutscene fog profile twins — routed to by Fog()/FogColorEntry while the
            // cutscene profile is live: thinner, lower-noise fog with a closer wall and
            // much lighter desat, so the helicopter shots read clearly through the storm.
            _fogTwin[VolFogDensity] = new Val<float>(0.156f);
            _fogTwin[VolFogNoise] = new Val<float>(0.233f);
            _fogTwin[VolFogNoiseScale] = new Val<float>(2.066f);
            _fogTwin[VolFogHeight] = new Val<float>(200f);
            _fogTwin[VolFogBaseline] = new Val<float>(-27.96f);
            _fogTwin[VolFogMaxLength] = new Val<float>(617.3f);
            _fogTwin[VolFogMaxLengthFallOff] = new Val<float>(0f);
            _fogTwin[VolFogWallShade] = new Val<float>(0f);
            _fogTwin[VolFogVoidFalloff] = new Val<float>(5.54f);
            _fogTwin[VolFogVoidBlend] = new Val<float>(0.711f);
            _fogTwin[VolFogDeepObscurance] = new Val<float>(0.867f);
            _fogTwin[VolFogSkyHaze] = new Val<float>(0f);
            _fogTwin[VolFogAlpha] = new Val<float>(1f);
            _fogTwin[VolFogSpeed] = new Val<float>(0.156f);
            _fogTwin[WeatherDesaturate] = new Val<float>(0.128f);
            _csVolFogColor = new Val<Color>(new Color(0.482f, 0.455f, 0.420f));
            DiagHotkeys = Config.Bind("Icebreaker", "DiagHotkeys", false,
                "enable the F1-F12 diagnostic hotkey suite (lights toggle, geo hide, batching tests, probes). OFF for normal play — F9 doubles as the fog tuner toggle and MUST not fight the lights toggle (live)");
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
            SnowGusts = Config.Bind("Icebreaker", "SnowGusts", true,
                "wind-driven sheets of blown snow around the player, on top of the native flakes (live)");
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
            TripwireChance = Config.Bind("Icebreaker", "TripwireChance", 0.5f,
                new ConfigDescription("per-marker chance a tripwire is armed this raid — each marker rolls independently, so the layout differs every raid (in coop the host's roll wins, broadcast by the fika sync addon)", new AcceptableValueRange<float>(0f, 1f)));
            TripwireTpl = Config.Bind("Icebreaker", "TripwireTpl", "6a5d6a5f4ed8c025a0a2cff0",
                "grenade template planted on the wires (default: Manimal CS gas grenade)");
            InteriorCrossCull = Config.Bind("Icebreaker", "InteriorCrossCull", true,
                "cull interior volumes (Indoor_01/02/03) wholesale when the camera is outside them, beyond CrossCullDistance — the ship-center fps fix (live)");
            CrossCullDistance = Config.Bind("Icebreaker", "CrossCullDistance", 20f,
                new ConfigDescription("how close an out-of-volume interior group must be to still render (doorway/window sightlines) (live)", new AcceptableValueRange<float>(10f, 80f)));
            PasscodeTerminals = Config.Bind("Icebreaker", "PasscodeTerminals", true,
                "activate the authored passcode terminals + post-it code notes (needs the Author 9 passcode sidecar next to the dll)");
            RetailAIBake = Config.Bind("Icebreaker", "RetailAIBake", true,
                "load BSG's recovered baked AI data (covers/voxels/cores/patrols) instead of runtime generation; off or fill-failure = synthesized graph");
            // HYBRID knob: retail bake everywhere, our generated cover+patrol data inside
            // these zones only. empty = pure retail, which is the behaviour that has always
            // shipped. names are BotZone object names, comma separated, case insensitive.
            SynthZones = Config.Bind("Icebreaker", "SynthZones", "",
                "comma-separated BotZone names to rebuild with GENERATED cover+patrol data while the rest of the map keeps the retail bake (needs RetailAIBake on); empty = all retail");

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

            // BUNDLES FIRST, and PatchAll wrapped. twice on 07-31 a single bad diagnostic
            // patch threw inside PatchAll, aborted Awake, and took the bundle host with it —
            // the map then 404'd on maps/icebreaker.bundle and the whole mod looked dead,
            // which is a wildly misleading symptom for "one probe had an ambiguous overload".
            // the host is load-bearing; patches are not. it goes up first and PatchAll is no
            // longer allowed to kill the process.
            //
            // serve the map bundles out of the plugin folder — nothing is copied into
            // EscapeFromTarkov_Data (this replaced the old materializer), then sweep away
            // whatever that materializer left behind on installs that ran it
            IcebreakerBundleHost.Init(harmony);
            IcebreakerBundleHost.CleanLegacyStreamingAssets();

            try { harmony.PatchAll(); }
            catch (System.Exception e)
            {
                // a partial patch set is survivable and obvious in play; a dead mod is not
                Log.LogError($"PatchAll FAILED — some patches did not apply, the map still loads: {e}");
            }
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
