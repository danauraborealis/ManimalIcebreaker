using System;
using System.Collections.Generic;
using System.Linq;
using SysIoPath = System.IO.Path;
using System.Reflection;
using System.Threading.Tasks;
using Audio.SpatialSystem;
using EFT;
using EFT.EnvironmentEffect;
using EFT.Interactive;
using EFT.Game.Spawning;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // backported maps (Icebreaker) hijack a location slot that lacks some backend/prefab
    // data real maps ship, so a handful of non-essential raid-init subsystems NRE and abort
    // the raid. these finalizers swallow the exception so init CONTINUES past the leaf call
    // (patching the leaf, not the async raid-init method, so nothing downstream is skipped).
    //
    // finalizers only run when the target actually threw — real maps never hit these paths,
    // so they're inert there. losing audio-occlusion setup / breakable-window pooling is
    // fine for a walkable map. add more [HarmonyPatch] blocks here as later init stages
    // surface more shells. TODO: migrate into a proper ManimalIcebreaker client plugin,
    // gated to the icebreaker location, before distribution.

    // spatial audio needs per-location baked data + room/portal scene components + the
    // occlusion/pool config assets. IcebreakerAcoustics rehydrates ALL of that from the
    // sidecar (BSG's own recovered authoring + the retail audiobakedata file), and when
    // staging succeeds we let BSG's REAL Initialize run — full room/portal occlusion.
    // if staging fails (missing sidecar/bake), fall back to the old behavior: skip init,
    // leave Initialized=false, the game's guards degrade audio gracefully.
    // NOTE: the old version of this patch skipped Initialize UNCONDITIONALLY, which was
    // silently killing spatial audio on real maps too — the icebreaker gate fixes that.
    [HarmonyPatch(typeof(SpatialAudioSystem), "Initialize")]
    internal static class Patch_SpatialAudioInit
    {
        private static bool Prefix(SpatialAudioSystem __instance, ref Task __result)
        {
            if (!IcebreakerAcoustics.IcebreakerLoaded())
                return true; // real maps: untouched

            if (Plugin.SpatialAudio.Value && IcebreakerAcoustics.TryPrepareSpatialAudio(__instance))
                return true; // staged — run the real init

            Plugin.Log.LogWarning("[RaidFix] skipped SpatialAudioSystem.Initialize (acoustics staging unavailable)");
            __result = Task.CompletedTask;
            return false; // skip original
        }
    }

    // followup to the Initialize skip: BetterAudio.PlayAtPoint still routes every impact/
    // gunshot sound through ProcessSourceOcclusion, which NREs on the never-initialized
    // internals — thousands of NREs per mag dump, and Effects.Update replays the impact
    // sound forever because PlaySound keeps throwing. gate all overloads on Initialized:
    // return -1, the same "no occlusion" result BSG's own EOcclusionTest.None path uses
    // (the sound itself already Play()ed before occlusion — it just stays unoccluded).
    // silent on purpose: this fires per sound, logging would be its own spam.
    [HarmonyPatch]
    internal static class Patch_OcclusionWhenUninitialized
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(SpatialAudioSystem).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "ProcessSourceOcclusion");
        }

        private static bool Prefix(ref int __result)
        {
            if (SpatialAudioSystem.Initialized)
                return true; // real maps: run the original untouched
            __result = -1;
            return false;
        }
    }

    // the resurrected RainController wires screen effects on camera set — but our
    // fallback camera is Cam2, whose EffectsController predates RainScreenDrops (the
    // FrostbiteEffect story again, except this one needs prefab-wired assets we don't
    // have, so it can't be AddComponent'd). the crash: vmethod_8 -> RainScreenDrops.Init
    // NRE -> aborts the whole raid init through PlayerCameraController.Create. swallow:
    // every later use of rainScreenDrops_0/screenWater_0 is null-guarded (verified), so
    // the only loss is raindrops-on-visor — on a snow map.
    [HarmonyPatch(typeof(RainController), "method_0")]
    internal static class Patch_RainScreenOnCam2
    {
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!IceGate.On) return __exception;
            Plugin.Log.LogWarning($"[RaidFix] RainController camera hookup failed on fallback cam (visor drops off): {__exception.Message}");
            return null;
        }
    }

    // weapon-sway effector NREs (GClass908.Process via ProceduralWeaponAnimation) —
    // cosmetic sway on some weapon/bot combos; a throw here escapes into Player.LateUpdate.
    // same family as the MotionEffector.FixedTracking swallow.
    [HarmonyPatch(typeof(GClass908), nameof(GClass908.Process))]
    internal static class Patch_SwayEffectorNeverThrows
    {
        // silent only on the icebreaker (per-frame); vanilla keeps its exceptions
        private static Exception Finalizer(Exception __exception)
            => __exception == null || IceGate.On ? null : __exception;
    }

    // stutter forensics companion: bots path via SYNCHRONOUS NavMesh.CalculatePath on the
    // main thread — several repaths landing in one frame on a big navmesh is the classic
    // unattributed 30-60ms hitch. count + time every call so spike lines can name it.
    [HarmonyPatch(typeof(UnityEngine.AI.NavMesh), nameof(UnityEngine.AI.NavMesh.CalculatePath),
        typeof(Vector3), typeof(Vector3), typeof(int), typeof(UnityEngine.AI.NavMeshPath))]
    internal static class Patch_NavPathTiming
    {
        private static void Prefix(out long __state)
        {
            __state = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static void Postfix(long __state)
        {
            RenderEnvProbe.NavCalls++;
            RenderEnvProbe.NavMs += (System.Diagnostics.Stopwatch.GetTimestamp() - __state)
                * 1000f / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    // belt-and-braces for the resurrected spatial path: an exception ESCAPING occlusion
    // processing is catastrophic out of proportion — Effects.Update replays the sound
    // forever (the infinite impact/gun-loop bug) and it aborts whatever caller flow was
    // mid-sound (bot death drops left guns floating). degrade to -1 = unoccluded, same
    // as BSG's own EOcclusionTest.None path. inert unless the original actually threw.
    [HarmonyPatch]
    internal static class Patch_OcclusionNeverThrows
    {
        private static int _logged;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(SpatialAudioSystem).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "ProcessSourceOcclusion");
        }

        private static Exception Finalizer(Exception __exception, ref int __result)
        {
            if (__exception == null) return null;
            if (!IceGate.On) return __exception;
            __result = -1;
            if (_logged++ < 5)
                Plugin.Log.LogWarning($"[RaidFix] occlusion threw (sound degraded to unoccluded): {__exception.Message}");
            return null;
        }
    }

    // door foley gain: the ripped clips are mixed quiet (open peaks -10dB, squeak -14dB)
    // and BSG's hardcoded play volumes (open 0.6-0.75, shut 0.2-0.3) assume retail's
    // gain staging — result "really quiet" doors. boost the volume arg for IB_metal_*
    // clips (clamped at 1.0 — the normalized wavs in the next bundle carry the rest).
    // keeps the diagnostic log so the pipeline stays observable.
    [HarmonyPatch]
    internal static class Patch_DoorSoundBoost
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(BetterAudio).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m =>
                {
                    if (m.Name != "PlayAtPoint") return false;
                    var ps = m.GetParameters();
                    return ps.Length >= 5 && ps[1].ParameterType == typeof(AudioClip)
                        && ps[4].ParameterType == typeof(float);
                });
        }

        private static void Prefix(AudioClip __1, ref float __4)
        {
            if (__1 == null || !__1.name.StartsWith("IB_metal", StringComparison.OrdinalIgnoreCase)) return;
            __4 = Mathf.Min(__4 * Plugin.DoorSoundBoost.Value, 1f);
        }

        // diag retired: it proved the pool accepts our clips long ago, and a console+disk
        // write on EVERY door sound all raid is measurable frame tax. the volume-boost
        // prefix above is the part that still earns its keep.
    }

    [HarmonyPatch(typeof(WindowBreakerManager), "method_0")]
    internal static class Patch_WindowBreakerPrewarm
    {
        private static Exception Finalizer(Exception __exception, WindowBreakerManager __instance)
        {
            if (__exception == null) return null;
            // scene-based gate (transit-gate-blindness): this manager Awakes during the
            // transit preload window where IceGate still answers the old map — a player
            // log caught this exact swallow NOT firing on a shoreline->icebreaker transit
            bool ours;
            try { ours = __instance != null && __instance.gameObject.scene.name != null
                          && __instance.gameObject.scene.name.StartsWith("Icebreaker", StringComparison.OrdinalIgnoreCase); }
            catch { ours = false; }
            if (!ours && !IceGate.On) return __exception;
            Plugin.Log.LogWarning($"[RaidFix] swallowed WindowBreakerManager.method_0: {__exception.Message}");
            return null;
        }
    }

    // SpawnPointManagerClass.smethod_3 sets each BotZone.HasPmcBotSpawns by scanning its
    // SpawnPointMarkers' categories. a marker in that list with a null SpawnPoint NREs the
    // whole raid init here. HasPmcBotSpawns only matters for bot-PMC spawning (off for our
    // walkable milestone; our player spawns are zone-less Player points), so swallow it.
    [HarmonyPatch(typeof(SpawnPointManagerClass), "smethod_3")]
    internal static class Patch_SpawnPmcScan
    {
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!IceGate.On) return __exception;
            Plugin.Log.LogWarning($"[RaidFix] swallowed SpawnPointManagerClass.smethod_3: {__exception.Message}");
            return null;
        }
    }

    // Player.Init dereferences EnvironmentManager.Instance, which is null on our map — our
    // strip pass removed the dead ripped EnvironmentManager shell (missing script). the
    // class self-registers as a MonoBehaviourSingleton on Awake. FIRST CHOICE: rebuild
    // the FULL retail hierarchy (manager -> TriggerGroup -> 51 IndoorTriggers from the
    // recovered sidecar transforms) so indoor/outdoor switching actually works — footstep
    // banks, exposure, rain muffling. fallback: the old bare manager (always Outdoor).
    [HarmonyPatch(typeof(LocalPlayer), "Create")]
    internal static class Patch_EnsureEnvironmentManager
    {
        // ALSO re-anchored onto FikaPlayer.Create by IcebreakerFikaCompat: fika's player
        // never goes through LocalPlayer.Create, so this prefix alone left coop raids
        // with no EnvironmentManager — Player.Init NRE'd on it and loading froze at 25%
        // (07-28 coop test, found by the InitDiag sweep)
        internal static void EnsureEnvAndWeather()
        {
            // weather stack first — LocalGame's weather/seasons block runs after player
            // creation and is gated on WeatherController.Instance != null, so the rebuild
            // must land here at the latest
            if (Plugin.WeatherSystem.Value && IcebreakerAcoustics.IcebreakerLoaded())
                IcebreakerWeather.TryBuild();

            if (!IceGate.On) return; // vanilla maps own their EnvironmentManager

            // ObservedCullingManager drives observed BODY visibility (bots solo, remote
            // players AND bots on fika clients — same observed-body path). retail scenes
            // ship it; ours doesn't. the original fix created it in the
            // BotsController.Init prefix, which only runs where BOTS run — the host —
            // so fika clients had no manager, every observed body's visibility resolved
            // invisible, and clients got the floating-gear ghosts (07-29 probe:
            // forceRenderingOff on body skins, gear clean, damage un-hides). this
            // anchor runs on every peer before any player body builds.
            if (!Comfort.Common.Singleton<ObservedCullingManager>.Instantiated)
            {
                new GameObject("Icebreaker_ObservedCullingManager_Fix").AddComponent<ObservedCullingManager>();
                Plugin.Log.LogWarning("[RaidFix] created missing ObservedCullingManager (observed body visibility)");
            }

            if (EnvironmentManager.Instance != null)
                return;
            if (Plugin.EnvTriggers.Value && IcebreakerAcoustics.TryBuildEnvironmentTriggers())
                return; // full switcher built (manager included)
            new GameObject("Icebreaker_EnvManager_Fix").AddComponent<EnvironmentManager>();
            Plugin.Log.LogWarning("[RaidFix] created missing EnvironmentManager singleton (bare — no indoor triggers)");
        }

        private static void Prefix() => EnsureEnvAndWeather();
    }

    // UNIVERSAL safety net for the same fix: fika keeps inventing player-creation paths
    // (LocalPlayer.Create solo, FikaPlayer.Create coop, a third branch for headless
    // hosts — 07-29 headless test: envMgr=NULL yet again) and anchoring per-path is
    // whack-a-mole. every one of them funnels through Player.Init, which is also
    // exactly where EnvironmentManager gets dereferenced — and the ensure is
    // idempotent, so running it per-player (bots included) costs one null check.
    [HarmonyPatch(typeof(Player), nameof(Player.Init))]
    internal static class Patch_EnsureEnvBeforeAnyPlayerInit
    {
        [HarmonyPrefix]
        private static void Prefix() => Patch_EnsureEnvironmentManager.EnsureEnvAndWeather();
    }

    // icebreaker is an arctic map — force the seasons pipeline to Winter for THIS map
    // only (retail it's always frozen; the global SPT overrideSeason stays untouched so
    // every other map keeps whatever season the server rolled). Winter is what flips
    // RainController into its snow states, so server rain values fall as snow.
    [HarmonyPatch(typeof(Class444), nameof(Class444.Run))]
    internal static class Patch_ForceWinterSeason
    {
        private static void Prefix(ref ESeason season)
        {
            // pointless without the weather stack (no WeatherController -> seasons no-op)
            if (!Plugin.WeatherSystem.Value || !Plugin.ForceWinter.Value || !IcebreakerAcoustics.IcebreakerLoaded()) return;
            if (season == ESeason.Winter) return;
            Plugin.Log.LogDebug($"[Weather] forcing season {season} -> Winter (icebreaker only)");
            season = ESeason.Winter;
        }
    }

    // ROOT CAUSE of the whole camera mess: the scene's LevelSettings supplies the FPS
    // camera prefab (GInterface465.CameraPrefab), and OUR scene's copy is an assetripper'd
    // shell — the top-level refs LOOK populated (fingerprinting _effectsPrefab wasn't
    // enough), but somewhere in EffectsController.method_3's ~15 raw derefs a stripped
    // serialized field NREs mid-Awake, the camera comes out half-built and the screen
    // renders black with HUD burn-in. so don't fingerprint — gate on the map: if an
    // Icebreaker scene is loaded, discard the scene settings entirely, which makes
    // SetCameraFromPrefab fall back to the game's own built-in "Cam2" from InGameResources.
    [HarmonyPatch(typeof(CameraClass), "SetCameraFromSettings")]
    internal static class Patch_RejectShellCameraPrefab
    {
        private static void Prefix(ref CameraClass.GInterface465 settings)
        {
            // gate on GameWorld.LocationId (authoritative; "Suburbs" is our hijacked
            // slot). vanilla maps: not even a log line — this mod stays silent off-map.
            var world = Comfort.Common.Singleton<GameWorld>.Instance;
            var loc = world != null ? world.LocationId : null;
            if (!string.Equals(loc, "Suburbs", StringComparison.OrdinalIgnoreCase)) return;

            var prefab = settings != null && settings.CameraPrefab != null ? settings.CameraPrefab.name : "<null>";
            Plugin.Log.LogDebug($"[RaidFix] SetCameraFromSettings on icebreaker: prefab={prefab}");
            if (settings == null || settings.CameraPrefab == null)
                return; // already headed for the Cam2 fallback

            // ALWAYS discard the scene camera prefab — including the Author 21 retail
            // import. shipping a camera through the bundle was tried and measured dead:
            // the rip's serialized DATA does not survive (null shaders/materials, empty
            // curves crashed NightVision/ThermalVision/DistortCameraFX in Awake), and the
            // un-shippable SSAA left CameraClass.SetSSR to NRE inside
            // PlayerCameraController.Create — error screen, no spawn. the camera story is
            // now: Cam2 as the CHASSIS (valid core data, boots reliably) + the donor
            // graft (IcebreakerCameraDonor) adding a real 0.16.9 map camera's components
            // and data at runtime, where every ref resolves against live game assets.
            Plugin.Log.LogDebug("[RaidFix] discarding scene camera prefab — Cam2 chassis + donor graft owns the camera");
            settings = null;
        }
    }

    // GRENADE FLASH HEAL on the imported retail camera. its Awake derefs a serialized
    // same-prefab component ref (this.PrismEffects.toneValues) that did not survive the
    // export->SDK->bundle trip — but the PrismEffects component ITSELF ships fine and
    // sits on the same GameObject, one GetComponent away. re-point the field before
    // Awake reads it. OnEnable re-calls Awake, so this prefix fires more than once;
    // the null check makes the second and later passes free. flashbang blindness is
    // gameplay, which is why this is healed rather than dropped like the ref-dead trio.
    [HarmonyPatch(typeof(GrenadeFlashScreenEffect), "Awake")]
    internal static class Patch_GrenadeFlashPrismRef
    {
        [HarmonyPrefix]
        private static void Prefix(GrenadeFlashScreenEffect __instance)
        {
            if (!IceGate.On) return; // real maps ship a real ref — never touch it
            try
            {
                if (__instance.PrismEffects == null)
                {
                    __instance.PrismEffects = __instance.GetComponent<PrismEffects>();
                    if (__instance.PrismEffects != null)
                        Plugin.Log.LogDebug("[RaidFix] GrenadeFlash PrismEffects ref healed from sibling component");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[RaidFix] grenade flash heal failed: {e.Message}"); }
        }
    }

    // THE actual camera bug: our scene ships no camera prefab (assetripper stripped the
    // LevelSettings field), so the game falls back to "Cam2" from Resources — and BSG's
    // EffectsController.method_3 has exactly ONE unguarded GetComponent: FrostbiteEffect
    // (the arctic cold effect, newer than Cam2). every retail map ships a modern camera
    // prefab so the fallback path never runs — we're the first to hit it. give the camera
    // the missing component before EffectsController.Awake needs it; method_3 immediately
    // disables it anyway. FrostbiteEffect.Awake only caches a component ref, safe to add.
    [HarmonyPatch(typeof(EffectsController), "Awake")]
    internal static class Patch_EffectsControllerFrostbite
    {
        private static void Prefix(EffectsController __instance)
        {
            if (!IceGate.On) return; // vanilla camera prefabs ship the component
            if (__instance.GetComponent<FrostbiteEffect>() == null)
            {
                __instance.gameObject.AddComponent<FrostbiteEffect>();
                Plugin.Log.LogWarning("[RaidFix] added missing FrostbiteEffect to fallback camera (Cam2 predates it)");
            }

            // Cam2 gap #3: retail FPS cameras carry TOD_Camera, which scales the sky dome
            // to the far clip, parks it on the camera every OnPreCull, and switches clear
            // flags off Skybox. without it the resurrected TOD dome sits tiny at its scene
            // position while the static skybox draws over everything ("i dont see anything
            // about the sky being different"). fully self-contained (self-finds TOD_Sky,
            // no prefab assets) — safe to add; inert when no TOD_Sky exists.
            // Cam2 gap #5 — the DLSS/menu-load black screen (2026-08-03). Cam2's
            // PostProcessLayer carries no PostProcessResources, which was cosmetic-only
            // (volume effects just dont run)... until a DLSS mod: TarkovDLSS45 IL-patches
            // Unity.Postprocessing.Runtime and routes the FINAL UPSCALE through the layer,
            // so a dead layer means the quarter-res DLSS frame never reaches the backbuffer
            // — black screen + HUD burn-in, zero exceptions (autopsy: camera rect 0.5x0.5).
            // transit raids dodge it because the camera survives from the origin map with a
            // vanilla-inited layer — which is also why this stayed 'optional' for so long.
            // heal: hand the layer the game's own live PostProcessResources and re-enable
            // so OnEnable re-inits its bundles. no-op when resources are already present.
            HealPostProcessLayer(__instance.gameObject);

            if (Plugin.WeatherSystem.Value && IcebreakerAcoustics.IcebreakerLoaded()
                && __instance.GetComponent<TOD_Camera>() == null)
            {
                __instance.gameObject.AddComponent<TOD_Camera>();
                Plugin.Log.LogWarning("[RaidFix] added TOD_Camera to fallback camera (sky dome now follows the camera)");
            }

            // Cam2 gap #4: FOG. WeatherController.method_9 pushes fog exclusively into
            // TOD_Scattering.GlobalDensity on the camera and returns early when it's absent —
            // so the blizzard fog pin was a no-op all along. self-contained like TOD_Camera
            // (finds its own shader via GClass872, WeatherController grabs it on the
            // OnCameraChanged that fires AFTER this prefix — verified by log order).
            if (Plugin.WeatherSystem.Value && IcebreakerAcoustics.IcebreakerLoaded()
                && __instance.GetComponent<TOD_Scattering>() == null)
            {
                // a full-screen blit with a broken shader = black raid — verify first.
                // ALWAYS add (so WeatherController.method_1 picks it up and method_9 keeps
                // feeding it density) but start it per config — the TickBlizzard kill-switch
                // syncs enabled to WeatherFogPass every tick, making fog live-A/B-able in F12.
                var scatterShader = GClass872.Find("Hidden/Time of Day/Scattering");
                if (scatterShader != null && scatterShader.isSupported)
                {
                    var sc = __instance.gameObject.AddComponent<TOD_Scattering>();
                    sc.Sky = MonoBehaviourSingleton<TOD_Sky>.Instance;
                    sc.enabled = Plugin.WeatherFogPass.Value;
                    Plugin.Log.LogWarning($"[RaidFix] added TOD_Scattering to fallback camera (enabled={sc.enabled} — WeatherFogPass toggles it live)");
                }
                else
                    Plugin.Log.LogWarning("[RaidFix] TOD_Scattering skipped — scattering shader missing/unsupported (fog stays sky-haze only)");
            }
        }

        // reflection-only: the client project doesnt reference Unity.Postprocessing.Runtime,
        // and TarkovDLSS45 IL-patches that assembly anyway — resolve at runtime, touch nothing
        // when the layer is absent or already fed.
        private static void HealPostProcessLayer(GameObject go)
        {
            try
            {
                var ppType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.PostProcessLayer");
                if (ppType == null) return;
                var layer = go.GetComponent(ppType) as Behaviour;
                if (layer == null) { Plugin.Log.LogDebug("[RaidFix] no PostProcessLayer on fallback camera"); return; }
                var resField = AccessTools.Field(ppType, "m_Resources");
                if (resField == null) { Plugin.Log.LogWarning("[RaidFix] PostProcessLayer.m_Resources not found — PP version drift?"); return; }
                var res = resField.GetValue(layer) as UnityEngine.Object;
                if (res != null) { Plugin.Log.LogDebug("[RaidFix] PostProcessLayer resources already present — no heal needed"); return; }

                var resType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.PostProcessResources");
                var found = resType != null ? Resources.FindObjectsOfTypeAll(resType).FirstOrDefault() : null;
                if (found == null)
                {
                    Plugin.Log.LogWarning("[RaidFix] PostProcessLayer has NO resources and none found in memory — layer stays dead (DLSS/FSR upscale will not run)");
                    return;
                }
                // disable around Init so OnEnable re-runs bundle init with valid resources
                bool wasEnabled = layer.enabled;
                layer.enabled = false;
                var init = AccessTools.Method(ppType, "Init", new[] { resType });
                if (init != null) init.Invoke(layer, new object[] { found });
                else resField.SetValue(layer, found); // older PP: field only, OnEnable does the rest
                layer.enabled = wasEnabled;
                Plugin.Log.LogWarning($"[RaidFix] HEALED PostProcessLayer: fed '{found.name}' resources (was null) — DLSS/FSR final pass can run");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[RaidFix] PostProcessLayer heal failed: {e.Message}"); }
        }
    }

    // the per-frame weather tick. it died 3600+ times/raid once tod_Scattering_0 went
    // non-null (MBOIT branch derefs) and a dead tick means no TOD hour sync -> night sun ->
    // pitch black map. the MBOIT disarm in TickBlizzard is the real fix; this logs the FULL
    // stack once if anything in here ever throws again (BSG's log only keeps the top frame)
    // and silences the rest of the storm.
    // blizzard cloudiness pins the CC_Sharpen WeatherDesaturate post effect to max —
    // the gray/muted look the moment Blizzard turns on. method_13 is the per-tick
    // writer; land after it so the config wins the frame. -1 = hands off.
    [HarmonyPatch(typeof(EFT.Weather.WeatherController), "method_13")]
    internal static class Patch_WeatherDesatOverride
    {
        private static void Postfix(EFT.Weather.WeatherController __instance)
        {
            if (!IceGate.On) return; // the desat lever is an icebreaker look control
            float v = Plugin.Fog(Plugin.WeatherDesaturate).Value; // cutscene twin when profiled
            if (v < 0f) return;
            var cc = HarmonyLib.AccessTools.Field(typeof(EFT.Weather.WeatherController), "cc_Sharpen_0")?.GetValue(__instance) as CC_Sharpen;
            if (cc != null) cc.WeatherDesaturate = v;
        }
    }

    [HarmonyPatch(typeof(EFT.Weather.WeatherController), "method_4")]
    internal static class Patch_WeatherTickDiag
    {
        private static int _throws;
        private static float _nextLog;

        private static Exception Finalizer(Exception __exception, EFT.Weather.WeatherController __instance)
        {
            if (__exception == null) return null;
            if (!IceGate.On) return __exception; // vanilla weather keeps its real exceptions
            _throws++;
            // a swallowed-every-tick method_4 means NO weather params ever push — fog
            // renders un-parameterized defaults no matter what we tune upstream. name
            // the null so the crash is fixable instead of invisible.
            if (UnityEngine.Time.unscaledTime >= _nextLog)
            {
                _nextLog = UnityEngine.Time.unscaledTime + 10f;
                string diag;
                try
                {
                    var scat = HarmonyLib.AccessTools.Field(typeof(EFT.Weather.WeatherController), "tod_Scattering_0")?.GetValue(__instance) as TOD_Scattering;
                    bool date = false;
                    try { date = GClass4.Instance?.CurrentTime?.GameDateTime != null; } catch { }
                    diag = $"throws={_throws} scat={scat != null} scatMBOIT={scat != null && scat.MBOIT} remapV2={__instance.MBOITFogRemapDataV2 != null} date={date} cloudsRemap={__instance.CloudsRemap != null} todCtrl={__instance.TimeOfDayController != null}";
                }
                catch (Exception e) { diag = "diag failed: " + e.Message; }
                Plugin.Log.LogWarning($"[Weather] method_4 threw ({diag}): {__exception.Message}");
            }
            return null;
        }
    }

    // Cam2's NightVision wakes half-initialized and NREs in OnPreCull EVERY FRAME (7184
    // last raid). self-heal: first throw disables the component. NVG activation re-enables
    // it, and if its internals are still broken it just disables again. KNOWN USER-FACING
    // COST: goggles show the mask overlay but no effect, and the overlay used to STICK
    // after toggling off (the disabled component cant run its off-transition) — so the
    // guard now also kills the TextureMask overlay, and logs the FULL stack once so the
    // actual null site can be healed instead of muted.
    [HarmonyPatch(typeof(BSG.CameraEffects.NightVision), "OnPreCull")]
    internal static class Patch_NightVisionNeverSpams
    {
        private static bool _logged;
        private static Exception Finalizer(Exception __exception, BSG.CameraEffects.NightVision __instance)
        {
            if (__exception != null && !IceGate.On) return __exception; // Cam2-era gap is icebreaker-only
            if (__exception != null && __instance != null)
            {
                __instance.enabled = false;
                // dont strand the player behind the goggle vignette: the off-transition
                // lives in the component we just disabled
                try
                {
                    if (__instance.TextureMask != null)
                    {
                        __instance.TextureMask.Mask = null;
                        __instance.TextureMask.enabled = false;
                    }
                }
                catch { }
                if (!_logged)
                {
                    _logged = true;
                    Plugin.Log.LogWarning($"[RaidFix] NightVision.OnPreCull threw — component disabled, mask cleared. FULL STACK (once): {__exception}");
                }
            }
            return null;
        }
    }

    // safety net: if method_3 trips on yet another Cam2-era gap, don't let it kill Awake —
    // the rest of Awake (sharpen bind, event subscriptions) still runs and the camera lives.
    // a partially-initialized effects stack just means some per-frame effect NREs (non-fatal).
    [HarmonyPatch(typeof(EffectsController), "method_3")]
    internal static class Patch_EffectsControllerInit
    {
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!IceGate.On) return __exception;
            Plugin.Log.LogWarning($"[RaidFix] swallowed EffectsController.method_3: {__exception.Message}\n{__exception.StackTrace}");
            return null;
        }
    }

    // ground truth for the black screen: log exactly what the final camera carries the
    // moment it's set, so the next debugging round reads facts instead of theories.
    [HarmonyPatch(typeof(CameraClass), "SetCamera", typeof(Camera))]
    internal static class Patch_LogCameraInventory
    {
        private static void Postfix(Camera camera)
        {
            // attach a probe so a full render-env dump can be triggered on demand (F8) once
            // the scene is fully settled — on ANY map. load a working map, press F8, load
            // icebreaker, press F8, diff the two dumps. also auto-dumps once here at setup.
            if (RenderEnvProbe.Instance == null)
            {
                var go = new GameObject("Manimal_RenderEnvProbe");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<RenderEnvProbe>();
            }
            RenderEnvProbe.CameraRef = camera;
            // the automatic env dump is a diagnostic — icebreaker only, and only with
            // the diag suite armed (a raid on ANY map used to print this block)
            if (Plugin.DiagHotkeys.Value && IceGate.On) RenderEnvProbe.Dump("at-SetCamera");
        }
    }

    // comprehensive render-environment dumper. logs the camera flags, scene lighting
    // (RenderSettings ambient/fog/skybox), brightest lights, and every PostProcessVolume's
    // profile with its OVERRIDDEN parameters (tonemapper, exposure, grading, bloom...).
    // this is what "check the camera settings on a working map" actually needs — the
    // difference is almost never the camera itself, it's the scene's post volume + ambient.
    internal class RenderEnvProbe : MonoBehaviour
    {
        internal static RenderEnvProbe Instance;
        internal static Camera CameraRef;

        private void Awake() { Instance = this; }

        // the scene now provides its own environment (repaired skybox_night + skybox ambient,
        // baked into the scene file), so NO per-frame forcing here — that would clobber it.
        // F8 dumps the render env; F7/F6 scale the scene's ambient INTENSITY live (cooperates
        // with skybox ambient instead of overriding it) in case it needs a brightness nudge.
        private void Update()
        {
            if (Plugin.DiagHotkeys.Value && IceGate.On && Input.GetKeyDown(KeyCode.F8))
            {
                Dump("F8-manual");
                // native-light autopsy: the 10 nearest CullingLightObjects with their
                // full state — separates "intensity faded" from "disabled" from
                // "manager thinks invisible" when an area reads dark
                try
                {
                    var cam = CameraRef != null ? CameraRef.transform.position : Vector3.zero;
                    var clos = new List<CullingLightObject>(UnityEngine.Object.FindObjectsOfType<CullingLightObject>());
                    clos.Sort((a, b) => (a.transform.position - cam).sqrMagnitude
                        .CompareTo((b.transform.position - cam).sqrMagnitude));
                    for (int i = 0; i < Mathf.Min(10, clos.Count); i++)
                    {
                        var clo = clos[i];
                        var l = clo.GetLight();
                        float mi = -1f;
                        try { mi = (float)(_cloMaxIntensity ?? (_cloMaxIntensity =
                            HarmonyLib.AccessTools.Field(typeof(CullingLightObject), "_maxLightIntensity"))).GetValue(clo); }
                        catch { }
                        Plugin.Log.LogDebug($"[LightAutopsy] {clo.name} d={(clo.transform.position - cam).magnitude:F0}m " +
                            $"light={(l == null ? "NULL" : $"en={l.enabled} int={l.intensity:F2} range={l.range:F0}")} " +
                            $"max={mi:F2} visFlag={clo.IsLightEnabled} isVis={clo.IsVisible}");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[LightAutopsy] failed: {e.Message}"); }
            }

            // INSERT — the LODGroup.enabled semantics probe (08-08 retail-parity hunt).
            // bsg's dormant cell autocull culls by `lodgroup.enabled = false` per cell;
            // unity's docs are ambiguous on whether a disabled LODGroup HIDES its
            // renderers or renders them unmanaged. one keypress settles it: toggle every
            // map LODGroup within 40m and look — props vanish = disable culls (resurrect
            // the cell system), props stay = disable un-culls (dead end, build our own).
            if (Plugin.DiagHotkeys.Value && IceGate.On && Input.GetKeyDown(KeyCode.Insert))
            {
                try
                {
                    var cam = CameraRef != null ? CameraRef.transform.position : Vector3.zero;
                    _lodProbeOff = !_lodProbeOff;
                    int hit = 0;
                    foreach (var g in UnityEngine.Object.FindObjectsOfType<LODGroup>())
                    {
                        if (g == null) continue;
                        var sc = g.gameObject.scene.name;
                        if (sc == null || !sc.StartsWith("Icebreaker")) continue;
                        if ((g.transform.position - cam).sqrMagnitude > 40f * 40f) continue;
                        g.enabled = !_lodProbeOff;
                        hit++;
                    }
                    Plugin.Log.LogWarning($"[LodProbe] {hit} LODGroups within 40m now enabled={!_lodProbeOff} — do the props VANISH or STAY?");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[LodProbe] failed: {e.Message}"); }
            }

            var onIce = false;
            try { var w = Comfort.Common.Singleton<GameWorld>.Instance; onIce = w != null && string.Equals(w.LocationId, "Suburbs", StringComparison.OrdinalIgnoreCase); } catch { }
            if (!onIce)
            {
                _iceFrames = 0; _autoRebindStage = 0; _amandsStateLogged = false; _ieapiLogged = false; _ieapiEmptyStreak = 0; _ieapiNextSweep = 0f; _lamps.Clear(); _lastLamp = -1f; _lastAmbient = -1f; _rendererPosMap = null; _volProbeIdx = 0; _rebindDone.Clear();
                // ONLY when the map is genuinely gone. the sound scene loads (and gets its
                // authored volumes cached + muted for the load screen) well before GameWorld
                // reports a location, and every frame in that gap lands here — wiping the
                // cache while the sources sit at volume 0, which strands them silent for the
                // raid. the window is a blink when you host, but a fika HEADLESS client sits
                // in it waiting on the host: silent wind + dead zone tones, 07-31
                if (!IcebreakerAcoustics.IcebreakerLoaded())
                    IcebreakerAcoustics.ResetAmbientCache();
                TickVanillaPerfCensus(); // any-map native-perf inventory (vanilla comparison)
                _mixNextSweep = 0f; _mixAdopted = 0; // fresh adoption count per raid
                try { IceHoldLogic.MutedSpeakers.Clear(); } catch { } // dead speakers out of the muzzle set
                var crew = GetComponent<IcebreakerCrew>();
                if (crew != null) Destroy(crew); // fresh spawner per raid
                IceCrewJobs.Reset(); // stale profile-keyed jobs must not leak across raids
                var heli = GetComponent<IcebreakerHeliExfil>();
                if (heli != null) Destroy(heli);
                return;
            }

            TickSpikeProbe(); // stutter forensics — logs spiked frames with per-tick attribution
            TickFrameSplit(); // steady-state forensics — 10s main/render/gpu/wait split
            TickQualityClamps(); // live LOD bias/maxLOD caps + the one-shot quality census
            var camPos = CameraRef != null ? CameraRef.transform.position : Vector3.zero;
            IcebreakerLodCullFloor.Tick(camPos); // cell-tiered cull floors
            TickMixerRoute(); // unrouted ambient sources -> master mixer (volume sliders)
            // every tick is stopwatched into _tickMs so a spiked frame can say WHICH of
            // our systems (if any) ate it — "counters all zero" exonerated only the four
            // counted systems, not this list (08-08 lesson)
            TickBegin(); TickCameraAutopsy(); TickEnd(0);
            TickBegin(); TickChainPeeler(); TickEnd(1);
            TickBegin(); TickDeadEffectGuard(); TickEnd(2);
            TickBegin(); TickAmandsReapply(); TickEnd(3);
            TickBegin(); TickIeapiDisable(); TickEnd(4);
            TickBegin(); DrainPcGroupToggles(); TickEnd(5);
            TickBegin(); DrainPcToggles(); TickEnd(6);

            // AUTOMATIC shader rebind: our bundle's p0/* SMap shaders are broken copies; the
            // game has the working ones. rebind at ~2s (bulk geometry loaded) and again at ~6s
            // (catch streamed-in stragglers) so no F5 needed. two passes then done.
            _iceFrames++;
            if (_autoRebindStage == 0 && _iceFrames > 120) { _autoRebindStage = 1; StartCoroutine(RebindShadersSliced()); }
            else if (_autoRebindStage == 1 && _iceFrames > 360)
            {
                // flag first so a slow init can't re-enter; the stage-2 tickers no-op
                // until their builders fill the lists
                _autoRebindStage = 2;
                StartCoroutine(StageTwoInit());
            }

            // size-classed distance culling of small props — the residual 27k draws are
            // mostly distant clutter contributing zero pixels. same for far lights.
            if (_autoRebindStage == 2)
            {
                TickBegin(); TickDistanceCuller(); TickEnd(7);
                TickBegin(); TickLightCuller(); TickEnd(8);
                TickBegin(); TickCrossCull(); TickEnd(9);
                TickBegin(); TickPcDriver(); TickEnd(10);
            }

            // aliased shader targets live in bundles that may load late — keep retrying
            // until every stand-in material lands on its real shader. the retry only
            // walks the pending-materials list now; it used to re-run the FULL scene
            // sweep every 300 frames, a steady 5-second heartbeat of hitches
            if (_aliasRetryNeeded && (_iceFrames % 300) == 43)
            { TickBegin(); RetryAliasPending(); TickEnd(11); }

            // keep the ambient beds alive — the raid-start audio reset stops them once, so
            // check every ~30 frames and replay any that stopped (cheap: cached source list)
            if (Plugin.EnvTriggers.Value && (_iceFrames % 30) == 0)
            { TickBegin(); IcebreakerAcoustics.KeepAmbientAlive(); TickEnd(12); }

            // self-drive indoor/outdoor detection — BSG's polling task dies silently on the
            // first half-spawned player it trips over; without this the env never switches
            if (Plugin.EnvTriggers.Value && (_iceFrames % 15) == 0)
            { TickBegin(); IcebreakerAcoustics.DriveEnvironment(); TickEnd(13); }

            // crossfade the ambient beds by environment: wind outside, room tones inside
            if (Plugin.EnvTriggers.Value)
            { TickBegin(); IcebreakerAcoustics.TickAmbientBlend(Time.deltaTime); TickEnd(14); }

            // live config: whenever the BepInEx sliders change, apply immediately in-raid
            if (Plugin.LampIntensity.Value != _lastLamp || Plugin.LampShadows.Value != _lastShadows)
            {
                _lastShadows = Plugin.LampShadows.Value;
                ApplyLamps();
            }
            if (Plugin.AmbientIntensity.Value != _lastAmbient)
            {
                // NOTE: TOD_Sky.UpdateAmbient is EMPTY in 0.16.9 — TOD does NOT own
                // RenderSettings.ambient. BUT the restored retail LevelSettings DOES:
                // its Awake subscribes method_0 to Camera.onPreCull, re-applying its
                // OWN ambient fields every frame (retail authored black/0 — the map
                // relied on baked lightmaps we don't have). so when the singleton
                // exists we write our fill THROUGH its fields and let the native
                // applier do the work; direct RenderSettings writes are the fallback
                // for pre-restore bundles.
                _lastAmbient = Plugin.AmbientIntensity.Value;
                float a = _lastAmbient;
                var fill = new Color(0.15f * a, 0.15f * a, 0.18f * a, 1f);
                var ls = Comfort.Common.Singleton<LevelSettings>.Instance;
                if (ls != null)
                {
                    ls.AmbientMode = UnityEngine.Rendering.AmbientMode.Flat;
                    ls.SkyColor = fill;      // Flat mode: method_0 writes SkyColor into ambientLight
                    ls.EquatorColor = fill;
                    ls.GroundColor = fill;
                    ls.AmbientIntensity = a;
                    Plugin.Log.LogDebug($"[Ambient] flat ambient -> {fill} (via LevelSettings, native per-frame apply)");
                }
                else
                {
                    RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                    RenderSettings.ambientLight = fill;
                    Plugin.Log.LogDebug($"[Ambient] flat ambient -> {fill} (direct — no LevelSettings in scene)");
                }
            }

            // blizzard pin — every ~30 frames is plenty (WeatherDebug values are read
            // continuously once Enabled)
            if (Plugin.WeatherSystem.Value && (_iceFrames % 30) == 7)
            { TickBegin(); IcebreakerWeather.TickBlizzard(); TickEnd(15); }

            // per-frame: the MBOIT compute's CameraParameters buffer tracks fov (ads
            // zoom) — self-gates to a no-op until the volumetric constructed it
            TickBegin(); IcebreakerWeather.TickCameraParams(); TickEnd(16);

            // our raymarched volumetric (VolumetricFog & Mist 2) — every 30 frames,
            // config live-applied
            if ((_iceFrames % 30) == 13)
            { TickBegin(); IcebreakerVolFog.Tick(); TickEnd(17); }

            // the F1-F12 suite below is perf-hunt archaeology — it hijacks keys real
            // features want (F9 lights-off collided with the fog tuner) so its dead
            // unless explicitly re-armed
            if (!Plugin.DiagHotkeys.Value) return;

            if (Input.GetKeyDown(KeyCode.F7)) { RenderSettings.ambientIntensity *= 1.3f; Plugin.Log.LogDebug($"[RenderEnv] ambientIntensity -> {RenderSettings.ambientIntensity:F2}"); }
            if (Input.GetKeyDown(KeyCode.F6)) { RenderSettings.ambientIntensity *= 0.77f; Plugin.Log.LogDebug($"[RenderEnv] ambientIntensity -> {RenderSettings.ambientIntensity:F2}"); }

            // F5 = THE shader-rebind test, run reliably from the plugin (console Debug.Log
            // never reached the log). our scene bundle carries a broken copy of the p0/* SMap
            // shaders (deferred lighting/stencil variants stripped at bundle build → renders
            // unlit-bright). the GAME has the real working copies loaded at startup, so
            // Shader.Find(name) returns THOSE. rebinding every material to them should fix the
            // lighting while keeping textures (identical property names). logs counts reliably.
            if (Input.GetKeyDown(KeyCode.F5))
                RebindShadersToGame();

            // ISOLATION TESTS:
            // F9 = toggle every scene light on/off. lights OFF isolates ambient. if interiors
            //      become readable with lights off, the culprit is shadowless light-bleed.
            // F10 = force ambient to near-zero (0.02 flat black-ish) to isolate the lights.
            // F11 = bottleneck split test, 4 modes cycling:
            //   0 normal -> 1 hide ALL -> 2 hide only BAKED (culling-managed) -> 3 hide only
            //   UNBAKED (unmanaged) -> 0. tells us whether the frame cost lives inside or
            //   outside the culling system's reach.
            if (Input.GetKeyDown(KeyCode.F11))
            {
                _geoMode = (_geoMode + 1) % 4;
                var baked = CollectBakedRenderers();
                int all = 0, hid = 0;
                foreach (var mr in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
                {
                    all++;
                    bool hide = _geoMode == 1
                        || (_geoMode == 2 && baked.Contains(mr))
                        || (_geoMode == 3 && !baked.Contains(mr));
                    mr.forceRenderingOff = hide;
                    if (hide) hid++;
                }
                string[] names = { "NORMAL", "hide ALL", "hide BAKED-only", "hide UNBAKED-only" };
                Plugin.Log.LogDebug($"[RenderEnv] F11 mode={names[_geoMode]}: hid {hid}/{all} meshRenderers (baked set={baked.Count})");
            }

            // F2 = per-volume attribution: cycles normal -> hide ALL of volume 1 -> volume 2
            // -> ... -> normal. the PC camera is paused during the test so it can't overwrite
            // our toggles. whichever volume gives the biggest fps jump when hidden is where
            // the frame cost lives — then we dig into THAT bake.
            if (Input.GetKeyDown(KeyCode.F2))
            {
                try
                {
                    var volType = System.Type.GetType("Koenigz.PerfectCulling.PerfectCullingVolume, PerfectCullingRuntime");
                    var camT = System.Type.GetType("Koenigz.PerfectCulling.PerfectCullingCamera, PerfectCullingRuntime");
                    var volsArr = UnityEngine.Object.FindObjectsOfType(volType);
                    var fGroups = volType.GetField("bakeGroups", BindingFlags.Public | BindingFlags.Instance);
                    var groupT = volType.Assembly.GetType("Koenigz.PerfectCulling.PerfectCullingBakeGroup");
                    var fRs = groupT.GetField("renderers");
                    var pcc = (CameraRef != null ? CameraRef : Camera.main)?.GetComponent(camT) as Behaviour;

                    // direct renderer.enabled — no PC plumbing, so the probe can't be
                    // defeated by inlining/batching quirks.
                    void SetVolume(object volume, bool visible, out int count)
                    {
                        count = 0;
                        var groups = fGroups.GetValue(volume) as System.Array;
                        if (groups == null) return;
                        foreach (var g in groups)
                        {
                            var rs = fRs.GetValue(g) as Renderer[];
                            if (rs == null) continue;
                            foreach (var r in rs) if (r != null) { r.enabled = visible; count++; }
                        }
                    }

                    _volProbeIdx++;
                    if (_volProbeIdx > volsArr.Length) _volProbeIdx = 0;

                    if (_volProbeIdx == 0)
                    {
                        foreach (var v in volsArr) SetVolume(v, true, out _);
                        if (pcc != null) pcc.enabled = true;
                        Plugin.Log.LogDebug("[VolProbe] normal culling resumed");
                    }
                    else
                    {
                        if (pcc != null) pcc.enabled = false;
                        int hidden = 0;
                        for (int i = 0; i < volsArr.Length; i++)
                        {
                            SetVolume(volsArr[i], i != _volProbeIdx - 1, out var c);
                            if (i == _volProbeIdx - 1) hidden = c;
                        }
                        var target = volsArr[_volProbeIdx - 1] as Component;
                        Plugin.Log.LogDebug($"[VolProbe] {_volProbeIdx}/{volsArr.Length}: HIDING '{target?.name}' ({hidden} renderers set enabled=false) — note your fps");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[VolProbe] failed: {e.Message}"); }
            }

            // F3 = LOD forensics. prime suspect for the 156k live renderers: ripped
            // LODGroups lost their renderer refs, so every LOD level draws simultaneously
            // (3-4x the intended geometry). counts groups, how many have EMPTY lod renderer
            // lists, and how many renderers live under LODGroups.
            if (Input.GetKeyDown(KeyCode.F3))
            {
                // refs + transition heights survived the rip (verified in scene files), so
                // native unity LOD culling SHOULD be erasing distant props. if groups are
                // DISABLED (bsg's autocull system toggles lodGroup.enabled and the rip may
                // have serialized that state), nothing culls. report and heal in one press.
                int groups = 0, disabled = 0, inactiveGo = 0, healed = 0;
                foreach (var lg in UnityEngine.Object.FindObjectsOfType<LODGroup>())
                {
                    groups++;
                    if (!lg.gameObject.activeInHierarchy) inactiveGo++;
                    if (!lg.enabled)
                    {
                        disabled++;
                        lg.enabled = true;
                        healed++;
                    }
                }
                Plugin.Log.LogDebug($"[LOD] {groups} LODGroups: {disabled} were DISABLED (now enabled), {inactiveGo} on inactive GOs. " +
                                      (disabled > 0 ? "check your fps NOW." : "all were already enabled — native lod culling should be active; mystery deepens"));
            }

            // F4 = toggle ScreenSpaceReflections. the active profile runs SSR at High/
            // Supersampled/256 iterations — a per-pixel GPU monster whose cost appears
            // exactly when geometry covers the screen (sky early-outs), which matches the
            // F11 behavior as well as draw calls do. cheap decisive toggle.
            if (Input.GetKeyDown(KeyCode.F4))
            {
                try
                {
                    var ppvType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.PostProcessVolume");
                    foreach (var volObj in UnityEngine.Object.FindObjectsOfType(ppvType))
                    {
                        var profile = GetMember(volObj, "sharedProfile") ?? GetMember(volObj, "profile");
                        var settings = GetMember(profile, "settings") as System.Collections.IEnumerable;
                        if (settings == null) continue;
                        foreach (var s in settings)
                        {
                            if (s == null || !s.GetType().Name.Contains("ScreenSpaceReflections")) continue;
                            var activeProp = s.GetType().GetProperty("active");
                            bool cur = (bool)activeProp.GetValue(s);
                            activeProp.SetValue(s, !cur);
                            Plugin.Log.LogDebug($"[RenderEnv] F4: ScreenSpaceReflections active -> {!cur}");
                        }
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[RenderEnv] F4 SSR toggle failed: {e.Message}"); }
            }

            // F12 = census of the UNBAKED renderers (the actual frame cost per the F11
            // split): counts by scene and by top-level root so we can see what the 122k
            // expensive renderers actually are.
            if (Input.GetKeyDown(KeyCode.F12))
            {
                var baked = CollectBakedRenderers();
                var byScene = new Dictionary<string, int>();
                var byRoot = new Dictionary<string, int>();
                int unbaked = 0;
                foreach (var mr in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
                {
                    if (baked.Contains(mr)) continue;
                    unbaked++;
                    var sc = mr.gameObject.scene.name ?? "<none>";
                    byScene.TryGetValue(sc, out var c1); byScene[sc] = c1 + 1;
                    var t = mr.transform; while (t.parent != null) t = t.parent;
                    var key = sc + "/" + t.name;
                    byRoot.TryGetValue(key, out var c2); byRoot[key] = c2 + 1;
                }
                Plugin.Log.LogDebug($"[Census] {unbaked} UNBAKED renderers. by scene:");
                foreach (var kv in byScene.OrderByDescending(k => k.Value))
                    Plugin.Log.LogDebug($"[Census]   {kv.Value,7}  {kv.Key}");
                Plugin.Log.LogDebug("[Census] top roots:");
                foreach (var kv in byRoot.OrderByDescending(k => k.Value).Take(15))
                    Plugin.Log.LogDebug($"[Census]   {kv.Value,7}  {kv.Key}");
            }

            // F1 = bot autopsy: every BotOwner's state, position, and body-renderer health.
            // statue bots fail with zero log output — this makes them talk.
            if (Input.GetKeyDown(KeyCode.F1))
            {
                var bots = UnityEngine.Object.FindObjectsOfType<BotOwner>();
                Plugin.Log.LogDebug($"[BotAutopsy] {bots.Length} BotOwner(s) alive:");
                foreach (var b in bots)
                {
                    try
                    {
                        var role = b.Profile?.Info?.Settings?.Role.ToString() ?? "?";
                        var p = b.Transform?.position ?? Vector3.zero;
                        var rends = b.GetComponentsInChildren<Renderer>(true);
                        int on = 0, skinned = 0, skinnedOn = 0;
                        foreach (var r in rends)
                        {
                            if (r.enabled) on++;
                            if (r is SkinnedMeshRenderer) { skinned++; if (r.enabled && r.gameObject.activeInHierarchy) skinnedOn++; }
                        }
                        // ACTIVE brain layer + node — the "frozen follower" forensics:
                        // Gclass35_0 is the agent's live layer (BigBrain layers show
                        // their custom names here), GetActiveNodeName the action inside
                        string layer = "?";
                        try
                        {
                            var agent = b.Brain?.Agent;
                            var active = agent != null ? agent.Gclass35_0 : null;
                            layer = active != null
                                ? $"{active.Name()}/{agent.GetActiveNodeName()}"
                                : (agent != null ? "NO-ACTIVE-LAYER" : "?");
                        }
                        catch (Exception le) { layer = "err:" + le.Message; }

                        // VANILLA COVER forensics — the runToCover freeze: the lookup is
                        // voxel-driven (CurVoxel neighborhood -> PointsIds -> group filter),
                        // so count every stage to see where a starving bot loses its covers
                        string coverInfo = "?";
                        try
                        {
                            var cov = b.Covers;
                            var vox = b.VoxelesPersonalData?.CurVoxel;
                            int voxPts = vox?.PointsIds?.Count ?? -1;
                            int r1 = -1, close20 = -1;
                            string freeClose = "?";
                            if (cov != null)
                            {
                                try { var vl = cov.GetVoxelesInRadius(1); int s = 0; foreach (var v in vl) s += v?.PointsIds?.Count ?? 0; r1 = s; } catch { }
                                try { close20 = cov.GetClosePoints(p, 20f)?.Count ?? -1; } catch { }
                                try { freeClose = cov.GetFreeClosePoint(p, 0f) != null ? "OK" : "NULL"; } catch (Exception fe) { freeClose = "err:" + fe.Message; }
                            }
                            coverInfo = $"grp={cov?.ConnectionGroupId.ToString() ?? "?"} core={b.StartCorePoint?.Id.ToString() ?? "null"} " +
                                        $"voxel={(vox != null ? vox.Id.ToString() : "NULL")} voxPts={voxPts} r1Pts={r1} close20={close20} " +
                                        $"freeClose={freeClose} haveCover={b.Memory?.BotCurrentCoverInfo?.HaveCover.ToString() ?? "?"} " +
                                        $"curPt={(b.Memory?.CurCustomCoverPoint != null ? "set" : "null")}";
                        }
                        catch (Exception ce) { coverInfo = "err:" + ce.Message; }
                        Plugin.Log.LogDebug($"[BotAutopsy]   covers: {coverInfo}");

                        // VISION/STEERING forensics — the wall-stare freeze: vanilla
                        // sight is a cone around LookDirection, so a bot parked staring
                        // into a wall is blind from every other approach. log where the
                        // bot is looking, whether a wall is centimeters in front of its
                        // face, its current vision distance, and whether it knows an enemy
                        string visInfo = "?";
                        try
                        {
                            var look = b.LookDirection;
                            float wallDist = -1f;
                            var head = b.MyHead != null ? b.MyHead.position : p + Vector3.up * 1.5f;
                            if (Physics.Raycast(head, look, out var hit, 30f, LayerMaskClass.HighPolyWithTerrainMask))
                                wallDist = hit.distance;
                            visInfo = $"visDist={b.LookSensor?.VisibleDist ?? -1f:F1} " +
                                      $"lookHitWallAt={(wallDist < 0 ? "none" : wallDist.ToString("F1"))}m " +
                                      $"goalEnemy={(b.Memory?.GoalEnemy != null ? "SET" : "null")} " +
                                      $"underFire={b.Memory?.IsUnderFire.ToString() ?? "?"}";
                        }
                        catch (Exception ve) { visInfo = "err:" + ve.Message; }
                        Plugin.Log.LogDebug($"[BotAutopsy]   vision: {visInfo}");
                        // render-state forensics for the invisible-body mystery: WHICH
                        // mechanism hides the skinned meshes?
                        var sb = new System.Text.StringBuilder();
                        foreach (var r in rends)
                        {
                            if (!(r is SkinnedMeshRenderer smr)) continue;
                            sb.Append($"[{r.name}: en={r.enabled} act={r.gameObject.activeInHierarchy} " +
                                      $"fro={r.forceRenderingOff} shadow={r.shadowCastingMode} vis={r.isVisible} mat={(r.sharedMaterial != null ? r.sharedMaterial.shader?.name : "NULL")}] ");
                            if (sb.Length > 400) break;
                        }
                        Plugin.Log.LogDebug($"[BotAutopsy]   body: {sb}");
                        Plugin.Log.LogDebug($"[BotAutopsy]  '{b.name}' role={role} state={b.BotState} pos={p} " +
                                              $"renderers={rends.Length}({on} on) skinned={skinned}({skinnedOn} on) brain={layer} " +
                                              $"standby={b.StandBy?.StandByType.ToString() ?? "?"} healthy={(b.GetPlayer != null ? (!b.GetPlayer.HealthController?.IsAlive == false).ToString() : "?")}");
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"[BotAutopsy]  '{b?.name}': dump failed {e.Message}"); }
                }
            }

            // (F9 lamp toggle removed — it collided with the fog tuner's F9 and killing
            // every Light broke things; lamp diagnostics live in the F8 dump instead)
            // F10 = STATIC BATCHING test. ~117k live renderers = draw-call/CPU death that no
            // visibility culling can fix (the F8 numbers show culling works and fps doesn't
            // care). BSG's own "SBG_*" roots are their static-batch-group system — replicate
            // with unity's runtime combiner: merges static meshes under each scene root into
            // batched draw calls. one-shot (can't uncombine without reload).
            if (Input.GetKeyDown(KeyCode.F10) && !_batched)
            {
                _batched = true;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int roots = 0, candidates = 0;
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    if (!sc.isLoaded || sc.name == null || !sc.name.StartsWith("Icebreaker")) continue;
                    foreach (var root in sc.GetRootGameObjects())
                    {
                        // the root overload only combines isStatic children — ripped scenes
                        // lost their static flags, so it no-ops (84ms/118 roots). use the
                        // explicit-array overload where WE pick candidates: any mesh that
                        // isn't a door/interactive (those move — must not be batched).
                        var gos = new List<GameObject>();
                        foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
                        {
                            if (mr.GetComponentInParent<WorldInteractiveObject>() != null) continue;
                            var mf = mr.GetComponent<MeshFilter>();
                            if (mf == null || mf.sharedMesh == null) continue;
                            gos.Add(mr.gameObject);
                        }
                        if (gos.Count == 0) continue;
                        StaticBatchingUtility.Combine(gos.ToArray(), root);
                        candidates += gos.Count;
                        roots++;
                    }
                }
                Plugin.Log.LogDebug($"[RenderEnv] F10: static-batched {candidates} renderers under {roots} roots in {sw.ElapsedMilliseconds}ms — compare fps now");
            }
        }

        // CRITICAL: sharedMaterials, NOT materials. `.materials` instantiates a unique
        // material copy per renderer — 150k unique materials meant no two renderers could
        // ever batch together (setPass == batches == 40k == 20fps). touching each unique
        // SHARED material once fixes every renderer using it AND keeps batching intact.
        // materials already examined across ALL passes — mid-raid passes (F5, streamed-in
        // gear) only pay for what's actually new. a full 2000-material single-frame sweep
        // was a 137ms hitch, and every rebound shader compiles a fresh variant on its next
        // draw (a second hitch the frame after).
        private static readonly HashSet<int> _rebindDone = new HashSet<int>();

        // SDK stand-in shaders -> the game's REAL shaders. the deck vertex-paint
        // materials carry byte-perfect retail values (3-layer snow/trampled/deck with
        // height blending + footstep normals) but were bound to our flat editor
        // stand-in — whose name has no game counterpart, so the same-name rebind
        // below never upgraded them. alias them onto the genuine shader.
        // (VP alias retired: the real Vert Paint Shader Solid never resolved on this
        // map even from the shaders bundle, so the SDK stand-in got a faithful
        // height-blend rewrite instead — it ships in the scene bundle and must NOT
        // be swapped out at runtime.)
        private static readonly Dictionary<string, string> ShaderAliases = new Dictionary<string, string>();

        // NEVER rebind these — keep the bundle's own compiled stand-in (user call 07-30).
        // the game ships a compiled Cloth/ClothShader pair in its own sharedassets5, so
        // the name sweep silently swapped the cutscene cloth onto it — which made four
        // successive stand-in fixes render exactly zero frames while the pre-release
        // client's shader (older than the 1.0 materials we author for) kept tearing.
        // our stand-in is the known quantity: tolerance cutout, Cull Off, seam-safe wind.
        private static readonly HashSet<string> RebindExclude = new HashSet<string>
        {
            "Cloth/ClothShader",
            "Cloth/ClothShader_backface",
        };

        // OWNERSHIP GATE for the global rebind. the pass sweeps every Renderer in the
        // world — ParticleSystemRenderer included — which meant it also grabbed OTHER
        // MODS' materials (HollywoodFX, 2026-08-03: swapping their bundle's compiled
        // particle shaders for the game's variant-stripped same-name copies rendered
        // every particle as an opaque square card). shader-family exclusion is wrong —
        // OUR rip uses particle shaders too. the correct discriminator is ORIGIN:
        // materials embedded in the Icebreaker scenes are captured at scene load,
        // before any mod instantiates anything, and the global pass touches ONLY those.
        // our own runtime spawns (hovercraft, cutscene rig) use the scoped
        // RebindShadersUnder, which is explicitly rooted and needs no gate.
        private static readonly HashSet<int> _ourMaterials = new HashSet<int>();

        internal static void CaptureSceneMaterials(UnityEngine.SceneManagement.Scene scene)
        {
            try
            {
                int added = 0;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                        foreach (var m in r.sharedMaterials)
                            if (m != null && _ourMaterials.Add(m.GetInstanceID())) added++;
                Plugin.Log.LogDebug($"[RebindShaders] captured {added} scene-owned material(s) from '{scene.name}' — the global rebind is fenced to these");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[RebindShaders] material capture failed for '{scene.name}': {e.Message}"); }
        }

        private static bool _aliasRetryNeeded;

        // late loaders (the cutscene scene arrives mid-raid) call this after their
        // content exists — _rebindDone dedupes, so re-runs only touch new materials
        internal static void RebindNow() => RebindShadersToGame();

        // SCOPED rebind for props WE spawn, usable on any map. the ripped p0/* shaders
        // render white in deferred, and the map-wide sweep above is icebreaker-only, so
        // anything we drop into a vanilla scene (the hovercraft on Shoreline) has to fix
        // its own materials. matches by shader NAME against the game's loaded shaders,
        // exactly like the map-wide pass, just confined to one hierarchy.
        internal static int RebindShadersUnder(Transform root)
        {
            if (root == null) return 0;
            int rebound = 0;
            var seen = new HashSet<Material>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null || !seen.Add(m)) continue;
                    var name = m.shader.name;
                    if (RebindExclude.Contains(name)) continue;   // bundle stand-in stays
                    var gameShader = Shader.Find(name) ?? GClass872.Find(name);
                    if (gameShader != null && gameShader != m.shader)
                    {
                        m.shader = gameShader;
                        rebound++;
                    }
                }
            }
            if (rebound > 0)
                Plugin.Log.LogInfo($"[RebindShaders] scoped: {rebound} material(s) under '{root.name}' bound to game shaders");
            return rebound;
        }

        private static void RebindShadersToGame()
        {
            _aliasRetryNeeded = false;
            int rebound = 0, sameOrMissing = 0;
            var seen = new HashSet<Material>();
            var byName = new System.Collections.Generic.Dictionary<string, int>();
            // include INACTIVE (true): the exfil heli and other armed-later objects sat
            // disabled through both rebind passes and kept the broken ripped smap copy —
            // the "white model" bug. (rip blob's forward pass works, deferred is broken:
            // fine in editor scene view, white in-game.)
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null || !seen.Add(m)) continue;
                    if (!_rebindDone.Add(m.GetInstanceID())) continue; // handled in a prior pass
                    var name = m.shader.name;
                    if (!_ourMaterials.Contains(m.GetInstanceID())) { sameOrMissing++; continue; } // not scene-owned: another mod's material — never ours to touch
                    if (RebindExclude.Contains(name)) { sameOrMissing++; continue; }   // bundle stand-in stays
                    bool aliased = ShaderAliases.TryGetValue(name, out var alias);
                    if (aliased) name = alias;
                    var gameShader = Shader.Find(name) ?? GClass872.Find(name);
                    if (gameShader != null && gameShader != m.shader)
                    {
                        m.shader = gameShader;
                        rebound++;
                        byName.TryGetValue(name, out var c);
                        byName[name] = c + 1;
                    }
                    else
                    {
                        sameOrMissing++;
                        // aliased materials MUST eventually land on the real shader —
                        // the target lives in the global shaders bundle which may not
                        // be loaded yet on the early pass. un-mark so pass 2 retries
                        // (the old code marked them done on FAILURE, so the VP deck
                        // stayed on the flat stand-in forever).
                        if (aliased)
                        {
                            _rebindDone.Remove(m.GetInstanceID());
                            _aliasRetryNeeded = true;
                            // remembered by NAME so the retry loop touches only these
                            // materials — the old retry re-ran this entire scene sweep
                            // every 300 frames until the last alias resolved
                            if (!_aliasPending.Contains(m)) _aliasPending.Add(m);
                            Plugin.Log.LogDebug($"[RebindShaders] alias target '{name}' not resolvable yet for '{m.name}' — will retry");
                        }
                    }
                }
            }
            Plugin.Log.LogDebug($"[RebindShaders] unique materials={seen.Count} rebound={rebound} sameOrMissing={sameOrMissing}");
            foreach (var kv in byName)
                Plugin.Log.LogDebug($"[RebindShaders]   {kv.Value}x  {kv.Key}");
        }

        // aliased materials whose real shader wasn't loaded yet — the retry works this
        // short list instead of re-sweeping the whole scene (the old retry paid a full
        // FindObjectsOfType<Renderer> scan every 300 frames until the last one bound)
        private static readonly System.Collections.Generic.List<Material> _aliasPending = new System.Collections.Generic.List<Material>();
        private static bool _rebindRunning;

        private static void RetryAliasPending()
        {
            int bound = 0;
            for (int i = _aliasPending.Count - 1; i >= 0; i--)
            {
                var m = _aliasPending[i];
                if (m == null || m.shader == null) { _aliasPending.RemoveAt(i); continue; }
                if (!ShaderAliases.TryGetValue(m.shader.name, out var alias)) { _aliasPending.RemoveAt(i); continue; } // already rebound
                var gameShader = Shader.Find(alias) ?? GClass872.Find(alias);
                if (gameShader != null && gameShader != m.shader)
                {
                    m.shader = gameShader;
                    _rebindDone.Add(m.GetInstanceID());
                    _aliasPending.RemoveAt(i);
                    bound++;
                }
            }
            if (bound > 0) Plugin.Log.LogDebug($"[RebindShaders] alias retry bound {bound}, {_aliasPending.Count} still pending");
            if (_aliasPending.Count == 0) _aliasRetryNeeded = false;
        }

        // the auto passes run THIS instead of the sync sweep: identical per-material work,
        // but the scene walk yields every few thousand renderers. the sync version stacked
        // with the other builders was the 14.3s load-in frame in the 07-27 telemetry.
        private System.Collections.IEnumerator RebindShadersSliced()
        {
            if (_rebindRunning) yield break;
            _rebindRunning = true;
            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
            int slice = 3000;
            int rebound = 0;
            var seen = new HashSet<Material>();
            foreach (var r in renderers)
            {
                if (r != null)
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null || m.shader == null || !seen.Add(m)) continue;
                        if (!_ourMaterials.Contains(m.GetInstanceID())) continue; // ownership gate — see RebindShadersToGame
                        if (!_rebindDone.Add(m.GetInstanceID())) continue;
                        var name = m.shader.name;
                        if (RebindExclude.Contains(name)) continue;   // bundle stand-in stays
                        bool aliased = ShaderAliases.TryGetValue(name, out var alias);
                        if (aliased) name = alias;
                        var gameShader = Shader.Find(name) ?? GClass872.Find(name);
                        if (gameShader != null && gameShader != m.shader) { m.shader = gameShader; rebound++; }
                        else if (aliased)
                        {
                            _rebindDone.Remove(m.GetInstanceID());
                            _aliasRetryNeeded = true;
                            if (!_aliasPending.Contains(m)) _aliasPending.Add(m);
                        }
                    }
                if (--slice <= 0) { slice = 3000; yield return null; }
            }
            Plugin.Log.LogDebug($"[RebindShaders] sliced pass: {seen.Count} materials, {rebound} rebound, {_aliasPending.Count} alias-pending");
            _rebindRunning = false;
        }

        // stage 2 used to run every builder in ONE frame — rebind sweep, lamp discovery,
        // both cullers — measured at 14,292ms on 07-27. one system per frame instead; the
        // tickers already no-op on empty lists, so arming the stage flag first is safe.
        private System.Collections.IEnumerator StageTwoInit()
        {
            yield return RebindShadersSliced();
            // retail's ambient audio subsystem, rebuilt from the sidecar. early in stage
            // two: the sound scene is live by now and the sooner the loops start the less
            // of the raid opens in silence
            IcebreakerAmbientAudio.TryRestore(); yield return null;
            FreeHovercraftLights(); yield return null;
            DiscoverLamps(); yield return null;
            ApplyLamps(); yield return null;
            // after ApplyLamps on purpose: VolumetricLight.CheckIntensity refuses to
            // register a beam whose light sits under 0.001, and most of the 49 are
            // authored at intensity 0 and only get lit by the lamp pass above
            IcebreakerVolumetricLights.Restore(); yield return null;
            AttachCullingCamera(); yield return null;
            BuildDistanceCuller(); yield return null;
            BuildLightCuller(); yield return null;
            EnsureCullingManager();
            HealDoorRegistry(); yield return null; // fika door sync resolves by this registry
            // ALWAYS, fog on or off — a fog-off player collides with the markers' editor
            // colliders otherwise (invisible walls at doorways/hatch, 07-29 coop test)
            try { IcebreakerVolFog.StripMarkerColliders(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[VolFog] marker strip failed: {e.Message}"); }
            // fika CLIENTS never run BotsController.Init, where the host's scene-repair
            // block lives — so sealed doors sat in their unparseable authored state 64
            // (no prompt at all), keycard proxies stayed broken, flares degraded and the
            // shadow split never ran (07-29 client test). run the player-facing subset
            // here for clients only; every host flavor already ran the full block.
            if (!FikaBridge.BotsAuthority)
            {
                try { IcebreakerSealedDoors.Setup(); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Sealed] client setup failed: {e.Message}"); }
                try { IcebreakerFlares.TryBuild(); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Flares] client build failed: {e.Message}"); }
                try { Patch_EnsureStationaryController.HealKeycardProxies(); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Keycard] client heal failed: {e.Message}"); }
                try { Patch_EnsureStationaryController.EnforceShadowProxies(); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Perf] client shadow split failed: {e.Message}"); }
                yield return null;
            }
            // host-side only: bots exist on the authority, so a client watchdog would
            // be watching observed puppets that never move under their own power anyway
            if (FikaBridge.BotsAuthority && GetComponent<IcebreakerStuckBots>() == null)
                gameObject.AddComponent<IcebreakerStuckBots>();
            if (GetComponent<IcebreakerCrew>() == null) gameObject.AddComponent<IcebreakerCrew>();
            if (GetComponent<IcebreakerHeliExfil>() == null) gameObject.AddComponent<IcebreakerHeliExfil>();
            IcebreakerSnowGusts.Spawn();
            // (runtime StaticBatch retired 08-09: the bundle ships 91% pre-batched and
            // the runtime pass only ever caught ~2.2k leftovers with no measured win)
            // cull-height floor so the sub-2 lod bias doesnt make furniture vanish in
            // plain sight — sliced walk over ~40k+ LODGroups
            StartCoroutine(IcebreakerLodCullFloor.Apply());
            ApplySsrClamp(); yield return null;
        }

        // fika resolves every cross-peer door/keycard interaction via World.FindDoor's
        // id dictionary, and on this backported map the world build misses the doors
        // entirely (built from LocationScene registration our ripped scenes never
        // perform) — every interaction packet arrived 'component exists, id
        // unresolvable' on BOTH peers (07-28 coop), so no door state ever synced.
        // register everything the dictionary lacks through the public API. sorted so
        // every machine registers identical instances if an id is ever duplicated.
        // runs from StageTwoInit (proven to run on fika clients) — idempotent.
        internal static void HealDoorRegistry()
        {
            try
            {
                if (!IceGate.On) return;
                var gw = Comfort.Common.Singleton<GameWorld>.Instance;
                var world = gw != null ? gw.World_0 : null;
                if (world == null) { Plugin.Log.LogWarning("[DoorHeal] no World_0 yet — registry heal skipped"); return; }
                var dict = AccessTools.Field(typeof(World), "dictionary_1")?.GetValue(world)
                    as System.Collections.Generic.Dictionary<string, EFT.Interactive.WorldInteractiveObject>;
                if (dict == null) { Plugin.Log.LogWarning("[DoorHeal] registry dictionary not found — heal skipped"); return; }

                var all = UnityEngine.Object.FindObjectsOfType<EFT.Interactive.WorldInteractiveObject>();
                System.Array.Sort(all, (a, b) =>
                {
                    int c = string.CompareOrdinal(a.Id, b.Id);
                    if (c != 0) return c;
                    var pa = a.transform.position; var pb = b.transform.position;
                    c = pa.x.CompareTo(pb.x); if (c != 0) return c;
                    c = pa.y.CompareTo(pb.y); if (c != 0) return c;
                    return pa.z.CompareTo(pb.z);
                });
                int had = dict.Count, healed = 0;
                foreach (var w in all)
                {
                    if (w == null || string.IsNullOrEmpty(w.Id) || dict.ContainsKey(w.Id)) continue;
                    world.RegisterWorldInteractionObject(w);
                    healed++;
                }
                Plugin.Log.LogWarning($"[DoorHeal] registry had {had} of {all.Length} scene interactables — registered {healed} missing");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[DoorHeal] failed: {e.Message}"); }
        }

        private static bool _batched;
        private static int _geoMode;
        private static int _volProbeIdx;
        private static int _iceFrames;

        // all renderers currently referenced by any perfect-culling bake group — i.e. the
        // set the culling system actually manages. everything else renders unconditionally.
        private static HashSet<Renderer> CollectBakedRenderers()
        {
            var set = new HashSet<Renderer>();
            try
            {
                var volType = System.Type.GetType("Koenigz.PerfectCulling.PerfectCullingVolume, PerfectCullingRuntime");
                if (volType == null) return set;
                var fGroups = volType.GetField("bakeGroups", BindingFlags.Public | BindingFlags.Instance);
                var groupType = volType.Assembly.GetType("Koenigz.PerfectCulling.PerfectCullingBakeGroup");
                var fRenderers = groupType.GetField("renderers");
                foreach (var v in UnityEngine.Object.FindObjectsOfType(volType))
                {
                    var groups = fGroups?.GetValue(v) as System.Array;
                    if (groups == null) continue;
                    foreach (var g in groups)
                    {
                        var rs = fRenderers?.GetValue(g) as Renderer[];
                        if (rs == null) continue;
                        foreach (var r in rs) if (r != null) set.Add(r);
                    }
                }
            }
            catch { }
            return set;
        }
        private static int _autoRebindStage;
        private static float _lastLamp = -1f;
        private static float _lastAmbient = -1f;
        private static bool _lastShadows;
        // the discovered dead lamps, cached so the slider can re-drive them repeatedly (once
        // we've set them to a real intensity they no longer look "dead", so we can't re-detect).
        private static readonly System.Collections.Generic.List<Light> _lamps = new System.Collections.Generic.List<Light>();

        // retail lamp lights serialize at intensity 0 (brightness was baked into lightmaps we
        // don't have). a near-zero non-directional light is a dead lamp — remember it so the
        // slider can drive it. additive: safe to call again for streamed-in stragglers.
        private static void DiscoverLamps()
        {
            // lights owned by a HEALED CullingLightObject are driven by BSG's native system
            // (intensity from _maxLightIntensity + fade curves) — ours would fight it.
            var nativeOwned = new HashSet<Light>();
            foreach (var clo in UnityEngine.Object.FindObjectsOfType<CullingLightObject>())
            {
                var l = clo.GetLight();
                if (l != null) nativeOwned.Add(l);
            }
            int skipped = 0;
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (l == null || l.type == LightType.Directional || l.intensity > 0.1f || _lamps.Contains(l)) continue;
                // MAP lights only — an ungated sweep once revived the Cam2 camera's dormant
                // hideout flashlight to intensity 3 and it rode the player around like a halo
                var sc = l.gameObject.scene.name;
                if (sc == null || !sc.StartsWith("Icebreaker")) continue;
                if (nativeOwned.Contains(l)) { skipped++; continue; }
                _lamps.Add(l);
            }
            if (skipped > 0)
                Plugin.Log.LogWarning($"[LightLamps] {skipped} lamps native-owned (CullingLightObject) — LampIntensity slider drives only the {_lamps.Count} unowned");
        }

        // native BSG culling suppression, now UNCONDITIONAL on this map (the NativeCulling
        // toggle is gone with the Adaptive_grid scene object): the retail packed bake cost
        // fps AND hard-reads StreamingAssets\Culling_Data at scene activation — a file no
        // player install has, which was the infinite-load-with-ambient-audio crash
        // (2026-08-02 field logs). the scene object is deleted in the rebake; this prefix
        // stays as the backstop for OLD bundles still carrying it.
        //
        // KNOWN WEAKNESS in that backstop: TypeByName can resolve the OTHER twin — this
        // class exists in both our self-hosted PerfectCullingRuntime.dll and bsg's fork in
        // Assembly-CSharp, and the scene component binds bsg's. a wrong-twin patch lands
        // silently and gates nothing (the probable reason a player crashed with this gate
        // present). the scene deletion is the real fix; this is best-effort.
        [HarmonyPatch]
        internal static class Patch_NativeCullingGate
        {
            private static MethodBase TargetMethod() =>
                HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("Koenigz.PerfectCulling.EFT.PerfectCullingAdaptiveGrid"), "Awake");

            private static bool Prefix(MonoBehaviour __instance)
            {
                // CRITICAL gate: vanilla maps run their own adaptive grids — suppressing
                // those would strip occlusion culling from every real map (audit follow-up;
                // this prefix was global for one day)
                //
                // gate on the grid's OWN scene, not IceGate: SPT's transit PRELOADS the
                // destination scenes before the new raid's creation, so during a
                // shoreline->icebreaker transit IceGate still answers 'Shoreline' at the
                // exact moment this Awake fires — proven by a player log (2026-08-02,
                // capture at L1488 'Shoreline', grid crash at L1559, no Suburbs capture in
                // between). the component's scene name is true on every entry path, and a
                // vanilla map's grid can never live in an Icebreaker* scene.
                bool ours;
                try { ours = __instance != null && __instance.gameObject.scene.name != null
                              && __instance.gameObject.scene.name.StartsWith("Icebreaker", StringComparison.OrdinalIgnoreCase); }
                catch { ours = IceGate.On; }
                if (!ours && !IceGate.On) return true;
                // NativeCulling re-trial (08-08): with the config on the retail grid is
                // wanted alive again — the runtime graft (Patch_GraftNativeGrid) adds the
                // component and this same Awake must run to set Instance
                if (Plugin.NativeCulling.Value) return true;
                Plugin.Log.LogDebug("[Culling] native BSG grid suppressed — sidecar bakes are the only culling driver on this map");
                return false;
            }
        }

        // NativeCulling runtime graft (08-08): the current bundle ships the Adaptive_grid GO
        // with the PerfectCullingAdaptiveGrid component STRIPPED (its Awake-time hard-read of
        // the 231MB packed bake infinite-loaded every install without the file). the component
        // itself serializes just two fields (_gridData null + _gridHash), so instead of a
        // bundle rebuild we re-add it here, right before the game's own
        // InitializeAutoCulling — which is the code that actually initializes the grid
        // (packed-file load, guid->group resolve, BVH) whenever Instance is non-null.
        // File.Exists gate keeps it ship-safe: config on without the bake = sidecars as usual.
        [HarmonyPatch]
        internal static class Patch_GraftNativeGrid
        {
            private const string RetailGridHash = "065281ec5449481391979c8269072a13";

            private static MethodBase TargetMethod() =>
                HarmonyLib.AccessTools.Method(
                    // assembly-qualified on purpose — TypeByName can land on the Asset Store
                    // twin assembly (the Patch_NativeCullingGate lesson); the EFT-layer
                    // sampler only exists in bsg's fork
                    System.Type.GetType("Koenigz.PerfectCulling.EFT.PerfectCullingCrossSceneSampler, Assembly-CSharp"),
                    "InitializeAutoCulling");

            private static void Prefix()
            {
                try
                {
                    if (!Plugin.NativeCulling.Value) return;
                    var gridT = System.Type.GetType("Koenigz.PerfectCulling.EFT.PerfectCullingAdaptiveGrid, Assembly-CSharp");
                    if (gridT == null) return;
                    var inst = gridT.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    if (inst != null && !inst.Equals(null)) return; // vanilla map, or an old bundle still carrying the component

                    // icebreaker detection by the GO itself: only our culling scene ships a
                    // bare Adaptive_grid root (transit-safe — no IceGate dependence)
                    GameObject host = null;
                    for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount && host == null; i++)
                    {
                        var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                        if (!s.isLoaded || s.name == null || !s.name.StartsWith("Icebreaker", StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (var root in s.GetRootGameObjects())
                            if (root.name == "Adaptive_grid") { host = root; break; }
                    }
                    if (host == null) return;

                    var packed = SysIoPath.Combine(Application.streamingAssetsPath, "Culling_Data", RetailGridHash + "_packed_cull.bytes");
                    if (!System.IO.File.Exists(packed))
                    {
                        Plugin.Log.LogWarning($"[Culling] NativeCulling is ON but the retail packed bake is missing ({packed}) — sidecar volumes stay in charge");
                        return;
                    }

                    // AddComponent fires Awake immediately (sets Instance); the Awake gate
                    // lets it through because the config is on. hash set right after —
                    // nothing reads it until method_0 runs inside InitializeAutoCulling.
                    var comp = host.AddComponent(gridT);
                    HarmonyLib.AccessTools.Field(gridT, "_gridHash")?.SetValue(comp, RetailGridHash);
                    Plugin.Log.LogWarning("[Culling] grafted the retail PerfectCullingAdaptiveGrid — InitializeAutoCulling loads the 84k-cell packed bake next; sidecar driver will stand down");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[Culling] native grid graft failed — sidecar volumes stay in charge: {e}");
                }
            }
        }

        // occlusion culling: the scene bundle carries a PerfectCullingVolume + baked data
        // authored with the Asset Store PC (its OWN PerfectCullingRuntime assembly — the
        // game's built-in PC is BSG's fork missing the vanilla volume class, so we self-host;
        // both coexist). Plugin.Awake preloads PerfectCullingRuntime.dll so the volume binds
        // at scene load; here we give the FPS camera the PerfectCullingCamera driver that
        // queries the bake each frame (OnPreCull) and toggles renderers.
        private static void AttachCullingCamera()
        {
            try
            {
                // NATIVE culling stand-down: if the restored Icebreaker_Culling scene shipped,
                // BSG's own PerfectCullingAdaptiveGrid awakes and loads the packed retail bake
                // from StreamingAssets/Culling_Data. running our sidecar driver on top would
                // double-toggle the same renderers — so when the native system is alive we
                // stand down entirely. PackedData.IsValid distinguishes "component present but
                // bake failed to load" (fall back to our driver) from "actually culling".
                var nativeGridT = System.Type.GetType("Koenigz.PerfectCulling.EFT.PerfectCullingAdaptiveGrid, Assembly-CSharp");
                var nativeGrid = nativeGridT?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (nativeGrid != null && !nativeGrid.Equals(null))
                {
                    bool packedOk;
                    try
                    {
                        var packed = nativeGridT.GetProperty("PackedData")?.GetValue(nativeGrid);
                        packedOk = packed != null && Equals(packed.GetType().GetProperty("IsValid")?.GetValue(packed), true);
                    }
                    catch { packedOk = true; } // reflection drift — component alive, assume native owns culling
                    if (packedOk)
                    {
                        Plugin.Log.LogDebug("[Culling] NATIVE BSG culling grid alive with valid packed data — sidecar driver standing down");
                        return;
                    }
                    Plugin.Log.LogWarning("[Culling] native grid present but packed bake NOT loaded — falling back to sidecar driver");
                }

                var camType = System.Type.GetType("Koenigz.PerfectCulling.PerfectCullingCamera, PerfectCullingRuntime");
                if (camType == null) { Plugin.Log.LogDebug("[Culling] PerfectCullingRuntime not loaded — no culling"); return; }
                var volType = System.Type.GetType("Koenigz.PerfectCulling.PerfectCullingVolume, PerfectCullingRuntime");
                if (volType == null) { Plugin.Log.LogWarning("[Culling] volume type missing"); return; }

                // volumes are built ENTIRELY from sidecars (PCBK2 carries the volume's
                // transform/size) — the bundle needs no culling objects, so rebakes are
                // sidecar-export + client restart, never a 645MB bundle rebuild.
                var dir = SysIoPath.Combine(SysIoPath.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "culling");
                if (System.IO.Directory.Exists(dir))
                    foreach (var f in System.IO.Directory.GetFiles(dir, "*.pcbake"))
                        RehydrateVolumeFromSidecar(f, volType);

                var vols = UnityEngine.Object.FindObjectsOfType(volType);
                if (vols == null || vols.Length == 0)
                {
                    Plugin.Log.LogWarning("[Culling] no live volumes (no sidecars?) — no culling");
                    return;
                }

                // per-volume ground truth: OnEnable only registers a volume into AllVolumes
                // (the list culling iterates) when volumeBakeData is non-null with data.
                // 0/0 culled means every volume failed that gate — log which and why.
                var bakeDataField = volType.GetField("volumeBakeData");
                var allVolumes = volType.GetField("AllVolumes", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as System.Collections.ICollection;
                foreach (var v in vols)
                {
                    var comp = v as Component;
                    var bd = bakeDataField?.GetValue(v);
                    string state;
                    if (bd == null || bd.Equals(null)) state = "volumeBakeData=NULL (asset missing from bundle or unbound)";
                    else
                    {
                        // WHICH class did the ScriptableObject bind to? both our shipped
                        // PerfectCullingRuntime AND the game's Assembly-CSharp contain
                        // Koenigz.PerfectCulling.PerfectCullingVolumeBakeData (BSG fork).
                        // if it bound to BSG's, the layout differs and fields read null.
                        var bt = bd.GetType();
                        var dataF = bt.GetField("data");
                        var dataArr = dataF?.GetValue(bd) as System.Array;
                        var rawArr = bt.GetField("rawData")?.GetValue(bd) as System.Array;
                        var groups = bt.GetField("numberOfGroups")?.GetValue(bd);
                        var cellCount = bt.GetField("cellCount")?.GetValue(bd);
                        var ver = bt.GetField("bakeDataVersion")?.GetValue(bd);
                        var volGroups = volType.GetField("bakeGroups", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(v) as System.Array;
                        state = $"asm={bt.Assembly.GetName().Name} dataField={(dataF != null ? "present" : "MISSING")} data={(dataArr == null ? "null" : dataArr.Length.ToString())} " +
                                $"rawData={(rawArr == null ? "null" : rawArr.Length.ToString())} groups={groups} cellCount={cellCount} ver={ver} volume.bakeGroups={(volGroups == null ? "null" : volGroups.Length.ToString())}";
                    }
                    Plugin.Log.LogDebug($"[Culling] volume '{comp?.name}': {state}");
                }
                Plugin.Log.LogDebug($"[Culling] AllVolumes registered: {allVolumes?.Count ?? -1} of {vols.Length}");

                var cam = CameraRef != null ? CameraRef : Camera.main;
                if (cam == null) { Plugin.Log.LogDebug("[Culling] no camera to attach to"); return; }
                // NO PerfectCullingCamera: its OnPreCull re-executes ALL volumes in one
                // frame on ANY cell crossing — with the fine indoor bake (4m cells) that
                // meant a 90-105ms hitch every few steps. NeuterEditorOnlyCallbacks still
                // runs for the BakeGroup.Toggle -> budgeted-queue patch; the volume
                // execution itself moves to TickPcDriver: one volume per frame,
                // round-robin, each gated by ITS OWN cell index.
                NeuterEditorOnlyCallbacks(camType);
                // own try: a driver-build failure must not also kill the cross-cull arm below
                try
                {
                    BuildPcDriver(vols);
                    Plugin.Log.LogDebug($"[Culling] sliced PC driver armed — {vols.Length} volume(s), 1 volume/frame, per-volume cell gating");
                }
                catch (Exception de) { Plugin.Log.LogWarning($"[Culling] driver build failed: {de.Message}"); }

                // interior volumes only cull for cameras inside themselves — from anywhere
                // else they render wholesale (the ship-center fps hole). arm the distance
                // rule for out-of-volume cameras.
                BuildCrossCull(volType);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Culling] attach failed: {e.Message}"); }
        }

        // rebuild a volume's bake payload + groups from the editor-exported .pcbake sidecar
        // (see IcebreakerCulling.ExportSidecars). renderer identity = name + world position.
        private static System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Renderer>> _rendererPosMap;

        // quantize to 5cm so tiny float drift between editor and runtime doesn't miss
        private static string PosKey(string name, Vector3 p)
            => $"{name}|{Mathf.RoundToInt(p.x * 20)}|{Mathf.RoundToInt(p.y * 20)}|{Mathf.RoundToInt(p.z * 20)}";

        private static void RehydrateVolumeFromSidecar(string file, System.Type volType)
        {
            var volName = SysIoPath.GetFileNameWithoutExtension(file);
            try
            {
                var asm = volType.Assembly;
                var bdType = asm.GetType("Koenigz.PerfectCulling.PerfectCullingVolumeBakeData");
                var visType = bdType.GetNestedType("VisibilitySet");
                var groupType = asm.GetType("Koenigz.PerfectCulling.PerfectCullingBakeGroup");

                using var r = new System.IO.BinaryReader(System.IO.File.OpenRead(file));
                if (r.ReadString() != "PCBK3") { Plugin.Log.LogDebug($"[Culling] {volName}: old/bad sidecar format — re-export from the editor"); return; }
                r.ReadString(); // volume name (== file name)
                var volPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var volRot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var volSize = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var bakeCell = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var cellCount = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

                // find-or-create the volume: bundle may carry an (empty) one by this name;
                // otherwise build it fresh — disabled until fully populated, so OnEnable's
                // registration check runs against real data.
                Component vol = null;
                foreach (var existing in UnityEngine.Object.FindObjectsOfType(volType))
                    if ((existing as Component)?.name == volName) { vol = existing as Component; break; }
                if (vol == null)
                {
                    var go = new GameObject(volName);
                    go.SetActive(false);
                    vol = go.AddComponent(volType) as Component;
                }
                vol.transform.SetPositionAndRotation(volPos, volRot);
                volType.GetField("volumeSize").SetValue(vol, volSize);
                volType.GetField("bakeCellSize").SetValue(vol, bakeCell);
                var cellSize = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var orientation = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                int numberOfGroups = r.ReadInt32();

                int cells = r.ReadInt32();
                var dataArr = System.Array.CreateInstance(visType, cells);
                var fCompressed = visType.GetField("compressed");
                var fLen = visType.GetField("len");
                for (int i = 0; i < cells; i++)
                {
                    ushort len = r.ReadUInt16();
                    var bytes = r.ReadBytes(r.ReadInt32());
                    var cell = Activator.CreateInstance(visType);
                    fLen.SetValue(cell, len);
                    fCompressed.SetValue(cell, bytes);
                    dataArr.SetValue(cell, i);
                }

                // groups: resolve renderers by NAME + quantized WORLD POSITION — geometry
                // doesn't move, so this survives the hierarchy churn that broke path-based
                // identity across bundle rebuilds. duplicates at the same spot pop from a list.
                if (_rendererPosMap == null)
                {
                    _rendererPosMap = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Renderer>>();
                    foreach (var rend in Resources.FindObjectsOfTypeAll<Renderer>())
                    {
                        if (!rend.gameObject.scene.isLoaded) continue;
                        var k = PosKey(rend.name, rend.transform.position);
                        if (!_rendererPosMap.TryGetValue(k, out var lst)) _rendererPosMap[k] = lst = new System.Collections.Generic.List<Renderer>(1);
                        lst.Add(rend);
                    }
                }
                int groups = r.ReadInt32(), missing = 0;
                var groupArr = System.Array.CreateInstance(groupType, groups);
                var fRenderers = groupType.GetField("renderers");
                var fGroupType = groupType.GetField("groupType");
                var userEnum = System.Enum.Parse(fGroupType.FieldType, "User");
                for (int gi = 0; gi < groups; gi++)
                {
                    int rc = r.ReadInt32();
                    var list = new System.Collections.Generic.List<Renderer>(rc);
                    for (int ri = 0; ri < rc; ri++)
                    {
                        var rname = r.ReadString();
                        var rpos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        if (_rendererPosMap.TryGetValue(PosKey(rname, rpos), out var candidates) && candidates.Count > 0)
                        {
                            // pop so identical duplicates each bind a distinct instance
                            var rr = candidates[candidates.Count - 1];
                            candidates.RemoveAt(candidates.Count - 1);
                            if (rr != null) list.Add(rr); else missing++;
                        }
                        else missing++;
                    }
                    var g = Activator.CreateInstance(groupType);
                    fRenderers.SetValue(g, list.ToArray());
                    fGroupType.SetValue(g, userEnum);
                    groupArr.SetValue(g, gi);
                }

                // build the bake data instance and wire everything up
                var bd = ScriptableObject.CreateInstance(bdType);
                bdType.GetField("cellCount").SetValue(bd, cellCount);
                bdType.GetField("cellSize").SetValue(bd, cellSize);
                bdType.GetField("orientation").SetValue(bd, orientation);
                bdType.GetField("numberOfGroups").SetValue(bd, numberOfGroups);
                bdType.GetField("data").SetValue(bd, dataArr);
                volType.GetField("volumeBakeData").SetValue(vol, bd);
                volType.GetField("bakeGroups", BindingFlags.Public | BindingFlags.Instance)?.SetValue(vol, groupArr);

                // re-run Start (rebuilds internal renderer-state array from the new groups),
                // then make sure OnEnable runs against the populated data so the volume
                // registers into AllVolumes (fresh GOs were created inactive).
                volType.GetMethod("Start", BindingFlags.Public | BindingFlags.Instance)?.Invoke(vol, null);
                if (!vol.gameObject.activeSelf) vol.gameObject.SetActive(true);
                else { var beh = vol as Behaviour; if (beh != null) { beh.enabled = false; beh.enabled = true; } }

                Plugin.Log.LogWarning($"[Culling] rehydrated {volName}: {cells} cells, {groups} groups ({missing} renderer paths unresolved)");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Culling] rehydrate {volName} failed: {e}"); }
        }

        // sibling-index-qualified — MUST match the editor exporter's convention exactly.
        // plain name paths collide massively (thousands of same-named siblings) and
        // silently collapse the managed renderer set.
        private static string HierarchyPath(Transform t)
        {
            var sb = new System.Text.StringBuilder($"{t.name}[{t.GetSiblingIndex()}]");
            while (t.parent != null) { t = t.parent; sb.Insert(0, $"{t.name}[{t.GetSiblingIndex()}]/"); }
            return sb.ToString();
        }

        // we ship the EDITOR-compiled PerfectCullingRuntime.dll (Library/ScriptAssemblies),
        // so its #if UNITY_EDITOR code is baked in — LateUpdate/OnGUI are pure editor
        // visualization (UnityEditor.Selection etc) and NRE every frame in the player.
        // the real culling path (OnPreCull -> PerformCameraCulling) is clean runtime code.
        // skip the editor-only callbacks. TODO: compile a proper player DLL instead.
        private static bool _cullingNeutered;
        private static void NeuterEditorOnlyCallbacks(System.Type camType)
        {
            if (_cullingNeutered) return;
            _cullingNeutered = true;
            var h = new Harmony("com.manimal.aidatadumper.culling");
            foreach (var name in new[] { "LateUpdate", "OnGUI" })
            {
                var m = AccessTools.Method(camType, name);
                if (m != null)
                    h.Patch(m, prefix: new HarmonyMethod(typeof(RenderEnvProbe), nameof(SkipOriginal)));
            }

            // PC hides culled renderers via forceRenderingOff — which does NOT affect
            // statically-batched renderers (renderer.enabled is what batching respects).
            // patch BakeGroup.Toggle ITSELF (replacing the body — patching the tiny static
            // ToggleRenderer inside it does nothing because the JIT inlines it) to flip
            // renderer.enabled on the group's renderers.
            var groupT = camType.Assembly.GetType("Koenigz.PerfectCulling.PerfectCullingBakeGroup");
            var toggle = groupT != null ? AccessTools.Method(groupT, "Toggle") : null;
            if (toggle != null)
            {
                _groupRenderersField = groupT.GetField("renderers");
                h.Patch(toggle, prefix: new HarmonyMethod(typeof(RenderEnvProbe), nameof(ToggleGroupViaEnabled)));
                Plugin.Log.LogDebug("[Culling] BakeGroup.Toggle replaced with renderer.enabled path (static-batching compatible)");
            }
            Plugin.Log.LogDebug("[Culling] editor-only callbacks neutered on PerfectCullingCamera");
        }

        private static bool SkipOriginal() => false;

        private static FieldInfo _groupRenderersField;

        // PC toggle amortization — the stutter forensics caught cell transitions flipping
        // 65-84k renderer.enabled in ONE frame (65-78ms hitches, THE noticeable stutter).
        // conditional writes alone don't help when the visible set genuinely changes that
        // much. so: toggles land in a pending map (last-writer-wins per renderer) and a
        // fixed budget applies per frame — a big transition settles over ~10 frames of
        // slight pop-in instead of one giant hitch.
        // 8000 was itself a 112ms frame when a big transition filled it — and 1500 was
        // STILL the engine-room freeze (07-09 log: every repeatable 100-250ms spike had
        // pcToggles pinned at ~1500; entering the fine-cell indoor volume queues a
        // near-total set swap). 350 spreads a full volume entry over ~15 frames: a
        // quarter second of gentle pop-in instead of a hitch.
        private const int PcApplyBudget = 350;
        private static readonly Dictionary<Renderer, bool> _pcPending = new Dictionary<Renderer, bool>();
        private static readonly List<Renderer> _pcDrainScratch = new List<Renderer>(PcApplyBudget);

        // thin wall deco (curtains, picture frames) lives in slivered doorway sightlines
        // the bake's visibility sampling chronically misses at ANY resolution — pops in
        // plain view. exempt those groups from occlusion culling entirely; the distance
        // culler still handles them, so the render cost is a rounding error. deliberately
        // specific patterns: 'frame_plastic' not 'frame' (door frames must stay culled).
        // 'combination_lock' (passcode keypads) and 'cpu_panel' (the wall monitors beside
        // them) are the same failure as the curtains, one step worse: flush wall mounts in
        // a doorway sliver, so the bake calls them hidden from a cell you're STANDING in.
        // the mesh vanishes while the collider stays — a keypad you can use but not see.
        // proven by the 07-30 A/B: both reappear the instant PcDriverEnabled goes off.
        // gameplay-critical, so they get the loot-container treatment: occlusion never
        // touches them, the distance culler still owns them past its own range.
        private static readonly string[] PcNeverCull = { "curtains_", "frame_plastic", "arctic_picture", "combination_lock", "cpu_panel" };
        private static readonly Dictionary<object, bool> _pcWhitelistCache = new Dictionary<object, bool>();
        private static HashSet<Transform> _pcLcRoots; // lazy per raid

        private static bool IsNeverCullGroup(object group, Renderer[] rs)
        {
            if (_pcWhitelistCache.TryGetValue(group, out var hit)) return hit;
            hit = false;
            if (rs != null)
                foreach (var r in rs)
                {
                    if (r == null) continue;
                    var n = r.name.ToLowerInvariant();
                    foreach (var pat in PcNeverCull)
                        if (n.Contains(pat)) { hit = true; break; }
                    // loot containers (PC blocks, medbags, toolboxes...) go PERMANENTLY
                    // invisible when the bake's sightline data for them is stale — they
                    // must never be occlusion-culled. distance culler still owns them.
                    // NOTE: sibling-aware root check — GetComponentInParent NEVER matches
                    // container meshes (the LootableContainer is a sibling, not a parent)
                    if (!hit)
                    {
                        if (_pcLcRoots == null) _pcLcRoots = BuildLootContainerRoots();
                        for (var w = r.transform; w != null && !hit; w = w.parent)
                            if (_pcLcRoots.Contains(w)) hit = true;
                    }
                    if (hit) break;
                }
            _pcWhitelistCache[group] = hit;
            return hit;
        }

        private static bool ToggleGroupViaEnabled(object __instance, bool isVisible)
        {
            // cross-volume interior cull: a PC volume only culls for cameras INSIDE it —
            // from outside, it shows every group it owns (the ship-center fps hole: both
            // indoor volumes render wholesale through the hull). groups force-culled by
            // TickCrossCull stay dark no matter what the volume decides.
            if (isVisible && _crossForced.Contains(__instance)) isVisible = false;
            var rs = _groupRenderersField?.GetValue(__instance) as Renderer[];
            if (!isVisible && IsNeverCullGroup(__instance, rs)) isVisible = true;
            if (rs != null)
                foreach (var r in rs)
                    if (r != null && r.enabled != isVisible)
                        _pcPending[r] = isVisible;
                    else if (r != null)
                        _pcPending.Remove(r); // re-decided back to current state — drop stale queue entry
            return false; // skip original entirely
        }

        // ---- sliced PC volume driver (replaces PerfectCullingCamera's monolithic pass) ----
        private class PcVol
        {
            public object Vol;
            public int LastCell = int.MinValue;
            public System.Reflection.MethodInfo GetIndex, GetIndices, QueueAll, QueueOne, Execute;
            public object[] Groups;            // bakeGroups cached — direct toggles, no per-index reflection
            public HashSet<int> VisSet;        // previous cell's visible group set (null until first apply)
        }
        private static readonly List<PcVol> _pcVols = new List<PcVol>();
        private static readonly List<ushort> _pcIndices = new List<ushort>(2048);
        private static int _pcvCursor;
        private static int _pcDriverRuns; // cumulative cell-crossing executions — proves the driver is alive in [Stutter] lines

        private static void BuildPcDriver(UnityEngine.Object[] vols)
        {
            _pcVols.Clear();
            _pcGroupPending.Clear();
            _pcWhitelistCache.Clear(); // stale group keys from the previous raid
            _pcLcRoots = null;         // containers respawn per raid — rebuild lazily
            foreach (var v in vols)
            {
                var t = v.GetType();
                var fGroups = t.GetField("bakeGroups", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                var groupsArr = fGroups?.GetValue(v) as System.Array;
                var groups = new object[groupsArr?.Length ?? 0];
                if (groupsArr != null) groupsArr.CopyTo(groups, 0);
                _pcVols.Add(new PcVol
                {
                    Vol = v,
                    // three overloads — the bare name throws AmbiguousMatch, which aborted
                    // the whole culling attach (no driver, no cross-cull) on 07-07's raid
                    GetIndex = AccessTools.Method(t, "GetIndexForWorldPos", new[] { typeof(Vector3), typeof(bool).MakeByRefType() }),
                    GetIndices = AccessTools.Method(t, "GetIndicesForWorldPos"),
                    QueueAll = AccessTools.Method(t, "QueueToggleAllRenderers"),
                    QueueOne = AccessTools.Method(t, "QueueToggleRenderer"),
                    Execute = AccessTools.Method(t, "ExecuteQueue"),
                    Groups = groups,
                });
            }
        }

        private static bool _pcDriverWasOff;
        private static void TickPcDriver()
        {
            // live kill switch — the curtain-pop isolation tool: flip PcDriverEnabled off,
            // everything occlusion-culled gets restored, and if the pops stop it's the
            // BAKE's sightline data (no slider fixes that; a rebake does)
            if (!Plugin.PcDriverEnabled.Value)
            {
                if (!_pcDriverWasOff)
                {
                    _pcDriverWasOff = true;
                    foreach (var vol in _pcVols)
                    {
                        if (vol.Vol == null || vol.QueueAll == null || vol.Execute == null) continue;
                        try
                        {
                            vol.QueueAll.Invoke(vol.Vol, new object[] { true });
                            vol.Execute.Invoke(vol.Vol, new object[] { true });
                            vol.LastCell = int.MinValue; // re-enable reapplies from scratch
                            vol.VisSet = null;
                        }
                        catch { }
                    }
                    Plugin.Log.LogDebug("[Culling] PC driver DISABLED (live) — occlusion-culled renderers restored");
                }
                return;
            }
            if (_pcDriverWasOff) { _pcDriverWasOff = false; Plugin.Log.LogDebug("[Culling] PC driver re-enabled (live)"); }

            if (_pcVols.Count == 0 || CameraRef == null) return;
            _pcvCursor = (_pcvCursor + 1) % _pcVols.Count;
            var pv = _pcVols[_pcvCursor];
            if (pv.Vol == null || pv.GetIndex == null) return;
            try
            {
                var camPos = CameraRef.transform.position;
                var args = new object[] { camPos, false };
                int cell = (int)pv.GetIndex.Invoke(pv.Vol, args);
                if (cell == pv.LastCell) return; // camera still in this volume's same cell
                pv.LastCell = cell;
                _pcDriverRuns++;

                // SET DIFF, not full reapply: the old path swept EVERY bake group (plus a
                // reflection Invoke per visible index) in one frame on each cell change —
                // THE engine-room-entry hitch, un-fixable by any renderer-write budget.
                // adjacent cells differ by a handful of groups; only those get touched.
                // group toggles land in a pending map drained N-per-frame, so even the
                // full first-entry apply is sliced instead of a spike.
                _pcIndices.Clear();
                pv.GetIndices.Invoke(pv.Vol, new object[] { camPos, _pcIndices });
                var newSet = new HashSet<int>();
                foreach (var idx in _pcIndices) newSet.Add(idx);
                bool cullNothing = newSet.Count == 0; // empty/unbaked cell: show all (asset's CullNothing behaviour)

                if (pv.VisSet == null)
                {
                    // first apply for this volume: full pass, sliced by the group drain
                    for (int i = 0; i < pv.Groups.Length; i++)
                        _pcGroupPending[pv.Groups[i]] = cullNothing || newSet.Contains(i);
                }
                else
                {
                    for (int i = 0; i < pv.Groups.Length; i++)
                    {
                        bool was = pv.VisSet.Count == 0 || pv.VisSet.Contains(i);
                        bool now = cullNothing || newSet.Contains(i);
                        if (was != now) _pcGroupPending[pv.Groups[i]] = now;
                    }
                }
                pv.VisSet = newSet;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Culling] driver failed on volume: {e.Message}");
                pv.Vol = null; // don't retry a broken volume every cycle
            }
        }

        // group-level pending map (last-writer-wins) drained under its own budget —
        // each drained group expands into renderer-level pendings for DrainPcToggles
        private static readonly Dictionary<object, bool> _pcGroupPending = new Dictionary<object, bool>();
        private static readonly List<object> _pcGroupScratch = new List<object>(1024);
        // each drained group costs a reflection field read plus a renderer loop — 700 in
        // one frame was a hidden burst on every big cell change (07-27 stutter pass)
        private const int PcGroupBudget = 200;

        private static void DrainPcGroupToggles()
        {
            if (_pcGroupPending.Count == 0) return;
            _pcGroupScratch.Clear();
            int budget = PcGroupBudget;
            foreach (var kv in _pcGroupPending)
            {
                if (budget-- <= 0) break;
                ToggleGroupViaEnabled(kv.Key, kv.Value);
                _pcGroupScratch.Add(kv.Key);
            }
            foreach (var g in _pcGroupScratch) _pcGroupPending.Remove(g);
        }

        // ---- cross-volume interior culling ----
        private class XVol
        {
            public string Name;
            public Bounds B;          // union of group bounds (the volume's real footprint)
            public object[] Groups;
            public Bounds[] GB;
            public bool[] Forced;
        }
        private static readonly List<XVol> _xvols = new List<XVol>();
        private static readonly HashSet<object> _crossForced = new HashSet<object>();
        private static int _xcullTick;

        // build once, lazily: interior volumes (Indoor_*) with per-group world bounds
        private static void BuildCrossCull(System.Type volType)
        {
            _xvols.Clear();
            _crossForced.Clear();
            if (_groupRenderersField == null) return;
            var fGroups = volType.GetField("bakeGroups", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var v in UnityEngine.Object.FindObjectsOfType(volType))
            {
                var comp = v as Component;
                if (comp == null || !comp.name.Contains("Indoor")) continue;
                var groups = fGroups?.GetValue(v) as System.Array;
                if (groups == null || groups.Length == 0) continue;
                var xv = new XVol { Name = comp.name, Groups = new object[groups.Length], GB = new Bounds[groups.Length], Forced = new bool[groups.Length] };
                bool haveB = false;
                for (int i = 0; i < groups.Length; i++)
                {
                    var g = groups.GetValue(i);
                    xv.Groups[i] = g;
                    var rs = _groupRenderersField.GetValue(g) as Renderer[];
                    Bounds gb = default;
                    bool haveG = false;
                    if (rs != null)
                        foreach (var r in rs)
                            if (r != null)
                            {
                                if (!haveG) { gb = r.bounds; haveG = true; }
                                else gb.Encapsulate(r.bounds);
                            }
                    xv.GB[i] = gb;
                    if (haveG)
                    {
                        if (!haveB) { xv.B = gb; haveB = true; }
                        else xv.B.Encapsulate(gb);
                    }
                }
                xv.B.Expand(3f); // doorway grace at the seams
                _xvols.Add(xv);
                Plugin.Log.LogDebug($"[XCull] interior volume '{xv.Name}': {xv.Groups.Length} groups, bounds {xv.B.size}");
            }
        }

        // interior containment for the volumetric fog fade — same bounds the cross-cull
        // uses (union of each Indoor_* volume's renderer bounds, +3m doorway grace)
        internal static bool CameraInsideInterior(Vector3 p)
        {
            foreach (var xv in _xvols)
                if (xv.B.Contains(p)) return true;
            return false;
        }

        // every ~15 frames: camera outside an interior volume -> its far groups go dark.
        // near groups stay (stairwell/doorway/window sightlines). all toggles flow through
        // the pending queue so big flips settle under the same frame budget as PC itself.
        private static void TickCrossCull()
        {
            if (!Plugin.InteriorCrossCull.Value || _xvols.Count == 0 || CameraRef == null) return;
            if ((++_xcullTick % 15) != 0) return;
            var cp = CameraRef.transform.position;
            float d2 = Plugin.CrossCullDistance.Value * Plugin.CrossCullDistance.Value;
            foreach (var xv in _xvols)
            {
                bool inside = xv.B.Contains(cp);
                for (int i = 0; i < xv.Groups.Length; i++)
                {
                    bool wantForce = !inside && xv.GB[i].SqrDistance(cp) > d2;
                    if (wantForce == xv.Forced[i]) continue;
                    xv.Forced[i] = wantForce;
                    if (wantForce) _crossForced.Add(xv.Groups[i]);
                    else _crossForced.Remove(xv.Groups[i]);
                    // queue the change now; PC refines un-forced groups on its next pass
                    ToggleGroupViaEnabled(xv.Groups[i], !wantForce);
                }
            }
        }

        // shows get a fat priority budget: a delayed HIDE is invisible to the player,
        // a delayed SHOW is the turn-a-corner-and-the-wall-is-missing pop. typical
        // corner-turn shows are a few hundred renderers = same frame now; the rare
        // full volume-entry swap settles in 2-3 frames instead of ~15.
        // 07-27 telemetry: pcRuns present at 92% of stutter spikes. 5000 shows in one
        // frame was seconds of renderer writes on a big cell reveal — the project's own
        // measured line is ~350 smooth / 1500 frozen. shows keep priority over hides,
        // big reveals just take a few frames to finish streaming in.
        private const int PcShowBudget = 600;

        private static void DrainPcToggles()
        {
            if (_pcPending.Count == 0) return;
            _pcDrainScratch.Clear();
            int showBudget = PcShowBudget;
            foreach (var kv in _pcPending)
            {
                if (!kv.Value) continue;
                if (showBudget-- <= 0) break;
                var r = kv.Key;
                if (r != null && !r.enabled) { r.enabled = true; _pcWrites++; }
                _pcDrainScratch.Add(r);
            }
            int budget = PcApplyBudget;
            foreach (var kv in _pcPending)
            {
                if (kv.Value) continue; // shows handled above
                if (budget-- <= 0) break;
                var r = kv.Key;
                if (r != null && r.enabled) { r.enabled = false; _pcWrites++; }
                _pcDrainScratch.Add(r);
            }
            foreach (var r in _pcDrainScratch) _pcPending.Remove(r);
        }

        // ---- black-screen forensics: what is actually ON the camera, once, at ~5s ----
        // burn-in means the color target is never written, so the answer is always in the
        // render chain: clearFlags, a disabled camera, or an OnRenderImage owner that
        // blits nothing. image-effect failures are SILENT (no exception in either log —
        // proven twice now), so the only way to tell which is to enumerate the chain while
        // the screen is wrong. IMAGE EFFECTS RUN IN COMPONENT ORDER, so the order below is
        // the actual execution order, and the first broken one eats every later one.
        private static bool _camAutopsyDone;

        // ---- dead-effect guard: the Cam2 fallback cannot host UltimateBloom ----
        // every RETAIL camera prefab ships an UltimateBloom, configured by BSG. ours is the
        // Cam2 fallback (LevelSettings has no prefab for this map), which predates it and
        // has none — so a graphics mod that expects one and CONSTRUCTS it here gets a bare
        // component it then fails to configure. measured 2026-08-03: HollywoodGraphics'
        // Bloom ctor NREs in ResetIntensities during GraphicsController.Start, leaving an
        // unconfigured UltimateBloom enabled LAST in the chain — last = owner of the final
        // blit to the backbuffer, so nothing is ever written, the fade-from-black frame
        // stands and the HUD accumulates on it. peeler-confirmed: disabling it alone
        // restores the picture instantly.
        // mod-agnostic by construction: the rule is about OUR camera lacking retail wiring,
        // not about any one mod. only effects WE didnt add are touched, only on our map,
        // and disabled (not destroyed) so the owner's own references stay alive.
        private static readonly HashSet<string> CannotHostOnCam2 = new HashSet<string> { "UltimateBloom" };
        private static readonly HashSet<Behaviour> _strippedEffects = new HashSet<Behaviour>(); // log dedup only
        private static readonly List<Component> _guardScratch = new List<Component>(80); // zero-alloc sweeps

        private void TickDeadEffectGuard()
        {
            // CONTINUOUS, once a second, for the whole raid. the FPS Camera SURVIVES
            // across raids in one game session (component indices creep raid to raid in
            // the autopsies), so any one-window guard dies with its static budget:
            // 08-07 sonic, raid 1 parked the bloom fine, raids 2-5 burned in because HG
            // re-enables its bloom every raid start and the old 30-sweep counter was
            // spent — peeler had to kill it by hand each raid. a GetComponents once a
            // second costs nothing; no sweep cap, no cross-raid state to reset.
            if (_iceFrames < 120 || _iceFrames % 60 != 0) return;
            try
            {
                var cam = CameraRef != null ? CameraRef : Camera.main;
                if (cam == null) return;
                cam.GetComponents(_guardScratch);
                foreach (var c in _guardScratch)
                {
                    if (c == null || !(c is Behaviour b) || !b.enabled) continue;
                    if (!CannotHostOnCam2.Contains(c.GetType().Name)) continue;
                    // the HG provision is NOT exempt: even donor-serialized and HG-configured
                    // it still doesnt blit on some installs (third strike of the lottery,
                    // 2026-08-03 sonic retest — burn-in with zero exceptions). the provision's
                    // job is only to keep HG's GraphicsController.Start alive (AO + motion
                    // blur tuning); the component itself gets parked here and HG writes its
                    // settings into a disabled renderer, harmlessly. no visible bloom on this
                    // map — the one Hollywood feature the Cam2 chassis genuinely cant host.
                    b.enabled = false;
                    if (_strippedEffects.Add(b))
                        Plugin.Log.LogWarning($"[RaidFix] disabled {c.GetType().Name} on the icebreaker camera — the Cam2 "
                            + "fallback has no retail configuration for it, and a mod that adds one here leaves it "
                            + "unconfigured at the END of the effect chain, where it swallows the whole frame "
                            + "(black screen + HUD burn-in). that mod's bloom is off on this map only.");
                    else
                        Plugin.Log.LogInfo($"[RaidFix] re-parked {c.GetType().Name} — its owner re-enabled it (new raid on the persistent camera)");
                }
            }
            catch (Exception e) { Plugin.Log.LogDebug($"[RaidFix] dead-effect guard: {e.Message}"); }
        }

        // ---- AmandsGraphics stuck-motion-blur reconcile ----
        // Amands INJECTS a MotionBlur settings block into the camera's PostProcessVolume
        // profile if none exists, then keeps it off with an Override(false) on the
        // instance it CAPTURED at camera activation. our Cam2 fallback is still being
        // operated on for seconds after that (PPLayer resources heal, donor graft), so
        // the live profile can end up carrying the injected MotionBlur WITHOUT the
        // disable override — motion blur on, default settings, from raid start (08-07
        // sonic). a one-shot re-apply through their own code proved insufficient (their
        // reset/update writes to the same stale capture), so we reconcile the LIVE
        // profile ourselves: once a second, if their MotionBlur config is Off but the
        // live profile's MotionBlur is enabled, we override it off. continuous on
        // purpose — persistent-camera lesson, and the profile can swap mid-raid.
        private bool _amandsStateLogged;

        // ALL reflection cached (08-08 stutter hunt: the uncached version cost ~31ms
        // EVERY second — AccessTools.TypeByName scans every loaded assembly, and it ran
        // per tick. it WAS the rhythmic stutter). resolved once per session; only the
        // per-profile pieces re-resolve, and only when the profile instance changes.
        private static bool _amResolved;
        private static bool _amPresent;
        private static BepInEx.Configuration.ConfigEntryBase _amMbCfg;
        private static Type _amVolType;
        private static System.Reflection.PropertyInfo _amProfileProp;
        private static object _amCachedProfile;
        private static object _amEnabledParam;      // the MotionBlur.enabled BoolParameter of the cached profile
        private static System.Reflection.FieldInfo _amValueField;
        private static System.Reflection.MethodInfo _amOverride;

        private void TickAmandsReapply()
        {
            if (_iceFrames < 300 || _iceFrames % 120 != 0) return; // from ~5s, every ~2s
            try
            {
                if (!_amResolved)
                {
                    _amResolved = true;
                    _amPresent = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.Amanda.Graphics");
                    if (_amPresent)
                    {
                        var pluginType = Type.GetType("AmandsGraphics.AmandsGraphicsPlugin, AmandsGraphics");
                        _amMbCfg = pluginType?.GetProperty("MotionBlur",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                            ?.GetValue(null) as BepInEx.Configuration.ConfigEntryBase;
                        _amVolType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.PostProcessVolume");
                        if (_amVolType != null) _amProfileProp = AccessTools.Property(_amVolType, "profile");
                        _amPresent = _amMbCfg != null && _amProfileProp != null;
                    }
                }
                if (!_amPresent) return;

                bool wantOn = _amMbCfg.BoxedValue?.ToString() == "On";
                var cam = CameraRef != null ? CameraRef : Camera.main;
                if (cam == null) return;
                var vol = cam.GetComponent(_amVolType);
                if (vol == null) return;
                var profile = _amProfileProp.GetValue(vol);
                if (profile == null) return;

                // re-walk the settings list ONLY when the profile instance changed
                // (that swap is the very bug being reconciled) — steady state is two
                // cached field reads
                if (!ReferenceEquals(profile, _amCachedProfile))
                {
                    _amCachedProfile = profile;
                    _amEnabledParam = null;
                    var settings = AccessTools.Field(profile.GetType(), "settings")?.GetValue(profile) as System.Collections.IEnumerable;
                    if (settings != null)
                        foreach (var s in settings)
                            if (s != null && s.GetType().Name == "MotionBlur")
                            {
                                _amEnabledParam = AccessTools.Field(s.GetType(), "enabled")?.GetValue(s);
                                if (_amEnabledParam != null)
                                {
                                    _amValueField = AccessTools.Field(_amEnabledParam.GetType(), "value");
                                    _amOverride = AccessTools.Method(_amEnabledParam.GetType(), "Override");
                                }
                                break;
                            }
                    if (!_amandsStateLogged)
                    {
                        _amandsStateLogged = true;
                        bool liveNow = _amEnabledParam != null && _amValueField != null && (bool)_amValueField.GetValue(_amEnabledParam);
                        Plugin.Log.LogInfo($"[Amands] reconcile armed — config MotionBlur={_amMbCfg.BoxedValue}, live profile MotionBlur "
                            + $"{(_amEnabledParam == null ? "ABSENT" : liveNow ? "enabled" : "disabled")}");
                    }
                }

                if (_amEnabledParam == null || _amValueField == null) return;
                if ((bool)_amValueField.GetValue(_amEnabledParam) && !wantOn)
                {
                    _amOverride?.Invoke(_amEnabledParam, new object[] { false });
                    Plugin.Log.LogWarning("[Amands] live profile MotionBlur was ON with their config Off — overridden off "
                        + "(their disable landed on a stale capture while our camera surgery swapped the profile)");
                }
            }
            catch (Exception e)
            {
                if (!_amandsStateLogged) { _amandsStateLogged = true; Plugin.Log.LogWarning($"[Amands] reconcile failed (cosmetic only): {e.Message}"); }
            }
        }

        // ---- InteractableExfils: OFF on this map (user call 08-07) ----
        // IEAPI wraps every exfil in its own prompt trigger (vanilla collider off, the
        // prompt switches it back on). our exits carry their own machinery — the heli's
        // flare lock + paid till, the gate's chain lock — so on this map we retire the
        // mod outright instead of compat-patching around it (the old manual-activation
        // claim + trigger-exile pile is deleted; this sweep is the ONE mechanism, so a
        // failure here must be LOUD, not degraded-silently). their API and every other
        // map stay untouched. continuous sweep — they build on GameStarted and timing
        // is theirs, not ours.
        //
        // type resolution survives their refactors: exact name first, then any
        // MonoBehaviour named *ExfilTrigger* in any loaded InteractableExfils assembly.
        // assembly present but nothing resolvable = Warning naming the failure, so it
        // gets reported and fixed rather than shipping a silently-broken retirement.
        private static bool _ieapiResolved;
        private static readonly List<Type> _ieapiTriggerTypes = new List<Type>();
        private bool _ieapiLogged;
        private int _ieapiEmptyStreak;
        private float _ieapiNextSweep;

        private void TickIeapiDisable()
        {
            // FindObjectsOfTypeAll costs 50-110ms on this scene (08-08 stutter hunt),
            // and the cost is paid even when the sweep finds nothing — so the sweep
            // must END, not decay: their triggers only ever build at GameStarted.
            // TIME-based cadence (frame-counted "%3600" ran every ~25s at high fps —
            // second time that trap bit today): 5s beats while their build could
            // still land, and after three consecutive empties we stop for the raid.
            // a kill resets the streak, so a late build gets a few more fast sweeps.
            if (_ieapiEmptyStreak >= 3) return; // done this raid
            if (_iceFrames < 300 || Time.unscaledTime < _ieapiNextSweep) return;
            _ieapiNextSweep = Time.unscaledTime + 5f;
            try
            {
                if (!_ieapiResolved)
                {
                    _ieapiResolved = true;
                    var exact = AccessTools.TypeByName("InteractableExfilsAPI.Components.CustomExfilTrigger");
                    if (exact != null) _ieapiTriggerTypes.Add(exact);
                    else
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            if (asm.GetName().Name.IndexOf("InteractableExfils", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            try
                            {
                                foreach (var t in asm.GetTypes())
                                    if (typeof(MonoBehaviour).IsAssignableFrom(t)
                                        && t.Name.IndexOf("ExfilTrigger", StringComparison.OrdinalIgnoreCase) >= 0)
                                        _ieapiTriggerTypes.Add(t);
                            }
                            catch { }
                            if (_ieapiTriggerTypes.Count == 0)
                                Plugin.Log.LogWarning($"[IEAPI] '{asm.GetName().Name}' is loaded but no *ExfilTrigger* type resolves — "
                                    + "their layout changed and the icebreaker retirement is NOT active. their prompts will run on this map; report this.");
                            else
                                Plugin.Log.LogWarning($"[IEAPI] exact type name drifted — resolved by scan instead: "
                                    + string.Join(", ", _ieapiTriggerTypes.Select(t => t.FullName)));
                        }
                }
                if (_ieapiTriggerTypes.Count == 0) return; // not installed (or loudly unresolvable, above)

                int killed = 0;
                foreach (var triggerType in _ieapiTriggerTypes)
                    foreach (var comp in Resources.FindObjectsOfTypeAll(triggerType))
                    {
                        var c = comp as Component;
                        if (c == null || !c.gameObject.scene.IsValid()) continue; // assets/prefabs stay
                        Destroy(c.gameObject);
                        killed++;
                    }
                if (killed > 0)
                {
                    _ieapiEmptyStreak = 0; // they built late once — stay on the fast cadence a while
                    // their triggers switched the vanilla exfil colliders off — restore them.
                    // (the heli's flare lock is positional, not collider-based, so this
                    // cannot fight it: an enabled collider a kilometer under the ship is inert)
                    foreach (var ep in UnityEngine.Object.FindObjectsOfType<EFT.Interactive.ExfiltrationPoint>())
                    {
                        var col = ep.GetComponent<BoxCollider>();
                        if (col != null && !col.enabled) col.enabled = true;
                    }
                    Plugin.Log.LogWarning($"[IEAPI] retired {killed} InteractableExfils trigger(s) on the icebreaker — "
                        + "vanilla exfil flow (with our own gates on it) is the only path on this map; other maps untouched");
                }
                else
                {
                    _ieapiEmptyStreak++;
                    if (_ieapiEmptyStreak == 3)
                        Plugin.Log.LogInfo("[IEAPI] three empty sweeps — their GameStarted build isn't coming, sweep retired for this raid");
                    if (!_ieapiLogged)
                    {
                        _ieapiLogged = true;
                        Plugin.Log.LogDebug("[IEAPI] installed but no scene triggers found on the icebreaker");
                    }
                }
            }
            catch (Exception e)
            {
                if (!_ieapiLogged) { _ieapiLogged = true; Plugin.Log.LogWarning($"[IEAPI] disable sweep FAILED — their prompts stay active on this map, report this: {e.Message}"); }
            }
        }

        // ---- render-chain PEELER (Home/End, DiagHotkeys) ----
        // when the screen is stale-with-burn-in the world IS rendering (the pre-spawn map
        // preview proves it) but something in the image-effect chain never blits to the
        // backbuffer. effects run in COMPONENT ORDER and the LAST one owns the final write,
        // so peeling from the end finds the culprit in one raid: HOME disables the last
        // enabled image effect and logs it; keep tapping until the picture returns — the
        // last name logged is the one eating the frame. END restores everything.
        // NOT on F-keys: every F1-F12 is already taken here (F9 opens the fog tuner and
        // grabs input, F10 is the torch pose probe, F12 is BepInEx's own config manager).
        private static readonly List<Behaviour> _peeled = new List<Behaviour>();

        private void TickChainPeeler()
        {
            if (!Plugin.DiagHotkeys.Value) return;

            if (Input.GetKeyDown(KeyCode.Home))
            {
                var cam = CameraRef != null ? CameraRef : Camera.main;
                if (cam == null) return;
                var comps = cam.GetComponents<Component>();
                for (int i = comps.Length - 1; i >= 0; i--)
                {
                    var c = comps[i];
                    if (c == null || !(c is Behaviour b) || !b.enabled) continue;
                    if (c.GetType().GetMethod("OnRenderImage",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) == null) continue;
                    b.enabled = false;
                    _peeled.Add(b);
                    Plugin.Log.LogWarning($"[Peeler] disabled #{i} {c.GetType().Name} — if the picture just came back, THAT is the culprit "
                        + $"({_peeled.Count} peeled so far, END restores)");
                    return;
                }
                Plugin.Log.LogWarning("[Peeler] no enabled image effects left on the camera — the frame is dying somewhere else "
                    + "(camera/present level, not the effect chain)");
            }

            if (Input.GetKeyDown(KeyCode.End))
            {
                foreach (var b in _peeled) if (b != null) b.enabled = true;
                Plugin.Log.LogWarning($"[Peeler] restored {_peeled.Count} effect(s)");
                _peeled.Clear();
            }
        }

        private void TickCameraAutopsy()
        {
            if (_camAutopsyDone || _iceFrames < 300) return;
            try
            {
                var cam = CameraRef != null ? CameraRef : Camera.main;
                if (cam == null) { if (_iceFrames > 18000) _camAutopsyDone = true; return; }
                // dont burn the one-shot while the camera GO is still inactive — slow
                // loaders hit frame 300 mid-load and the dump reads activeGO=False,
                // which is the LOADING state, not the bug (player log 08-04). wait for
                // the live camera, give up only after ~5 minutes
                if (!cam.gameObject.activeInHierarchy && _iceFrames < 18000) return;
                _camAutopsyDone = true;
                Plugin.Log.LogWarning($"[CamAutopsy] '{cam.name}' enabled={cam.enabled} activeGO={cam.gameObject.activeInHierarchy} "
                    + $"clearFlags={cam.clearFlags} bg={cam.backgroundColor} cullingMask=0x{cam.cullingMask:X} "
                    + $"depth={cam.depth} rect={cam.rect} target={(cam.targetTexture == null ? "screen" : cam.targetTexture.name)} "
                    + $"allowHDR={cam.allowHDR} allowMSAA={cam.allowMSAA} near={cam.nearClipPlane} far={cam.farClipPlane}");
                int i = 0;
                foreach (var c in cam.GetComponents<Component>())
                {
                    if (c == null) { Plugin.Log.LogWarning($"[CamAutopsy] #{i++} <MISSING SCRIPT>"); continue; }
                    string state = c is Behaviour b ? (b.enabled ? "on" : "off") : "-";
                    // an image effect is anything overriding OnRenderImage — those are the
                    // only components that can silently swallow the frame
                    bool ire = c.GetType().GetMethod("OnRenderImage",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly) != null;
                    Plugin.Log.LogWarning($"[CamAutopsy] #{i++} {c.GetType().Name} [{state}]{(ire ? " <IMAGE-EFFECT>" : "")}");
                }
                // other cameras can also own the final present (UI/overlay stacks)
                foreach (var other in Camera.allCameras)
                    if (other != cam)
                        Plugin.Log.LogWarning($"[CamAutopsy] other camera '{other.name}' enabled={other.enabled} "
                            + $"depth={other.depth} clearFlags={other.clearFlags} mask=0x{other.cullingMask:X}");

                Plugin.Log.LogWarning($"[CamAutopsy] screen={Screen.width}x{Screen.height} "
                    + $"camPixels={cam.pixelWidth}x{cam.pixelHeight} vsync={QualitySettings.vSyncCount}");

                // the upscale suspects, field by field — under DLSS the final present runs
                // through this machinery, and its failure mode is silent. dumping both the
                // menu-load (broken) and transit (working) raids and diffing these lines is
                // the whole point of this block.
                foreach (var c in cam.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var tn = c.GetType().Name;
                    if (tn != "SSAA" && tn != "SSAAImpl" && tn != "SSAAPropagator" && tn != "SSAAPropagatorOpaque"
                        && tn != "PostProcessLayer" && tn != "EffectsController") continue;
                    DumpComponentFields(c);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[CamAutopsy] failed: {e.Message}"); }
        }

        private static void DumpComponentFields(Component c)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                var t = c.GetType();
                // walk the hierarchy too — BSG subclasses hide state in bases
                for (var cur = t; cur != null && cur != typeof(MonoBehaviour) && cur != typeof(Behaviour); cur = cur.BaseType)
                    foreach (var f in cur.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        object v;
                        try { v = f.GetValue(c); } catch { continue; }
                        string s;
                        if (v == null) s = "null";
                        else if (v is UnityEngine.Object uo) s = uo == null ? "FAKE-NULL" : $"'{uo.name}'";
                        else if (f.FieldType.IsPrimitive || f.FieldType.IsEnum || v is string || v is Vector2 || v is Vector3) s = v.ToString();
                        else if (v is RenderTexture rt) s = $"RT {rt.width}x{rt.height}";
                        else continue; // complex refs: only interesting when null, handled above
                        sb.Append(f.Name).Append('=').Append(s).Append(' ');
                        if (sb.Length > 700) { Plugin.Log.LogWarning($"[CamAutopsy] {t.Name} fields: {sb}"); sb.Clear(); }
                    }
                if (sb.Length > 0) Plugin.Log.LogWarning($"[CamAutopsy] {t.Name} fields: {sb}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[CamAutopsy] field dump failed for {c.GetType().Name}: {e.Message}"); }
        }

        // ---- stutter forensics: when a frame spikes, log what actually ran in it ----
        // counters incremented by the three toggle paths; PC toggles fire in OnPreCull
        // (after Update) so at Update time the accumulated counts belong to the frame
        // whose deltaTime we're looking at.
        private static int _pcWrites, _distWrites, _lightWrites;
        internal static int NavCalls;
        internal static float NavMs;
        private static float _ftAvg = 1f / 60f;
        private static float _lastSpikeLog;
        private static int _gc0Prev, _gc1Prev;

        // per-tick attribution (08-08 stutter hunt): every one of OUR Update callees is
        // stopwatched by name, so a spike either names its eater or proves the frame
        // was lost OUTSIDE this component ("untracked" = engine, other mods, coroutines,
        // FixedUpdate, rendering). the old counters only ever exonerated four systems.
        private static readonly string[] _tickNames =
        {
            "autopsy", "peeler", "deadFx", "amands", "ieapi", "pcGrpDrain", "pcDrain",
            "distCull", "lightCull", "crossCull", "pcDriver", "alias", "ambKeep",
            "envDrive", "ambBlend", "blizzard", "camPar", "volFog",
        };
        private static readonly double[] _tickMs = new double[18];
        private static readonly System.Diagnostics.Stopwatch _tickSw = new System.Diagnostics.Stopwatch();
        private static void TickBegin() => _tickSw.Restart();
        private static void TickEnd(int i) => _tickMs[i] += _tickSw.Elapsed.TotalMilliseconds;

        // last-8-frames ring (ms) — a spike log that also shows the run-up separates
        // "one giant frame" from "every frame creeping up" (audit suggestion 08-08)
        private static readonly float[] _dtRing = new float[8];
        private static int _dtRingIdx;
        private static int _pcRunsPrev;

        private void TickSpikeProbe()
        {
            float dt = Time.unscaledDeltaTime;
            int gc0 = System.GC.CollectionCount(0);
            int gc1 = System.GC.CollectionCount(1);
            int pcRunsDelta = _pcDriverRuns - _pcRunsPrev; // per-frame, not cumulative
            if (dt > 0.025f && dt > _ftAvg * 2.2f && Time.unscaledTime - _lastSpikeLog > 1f)
            {
                _lastSpikeLog = Time.unscaledTime;
                // _tickMs still holds LAST frame's costs (cleared below, filled after
                // this probe ran last frame) — the same frame dt measures. attribution
                // lines up exactly.
                double ours = 0;
                var parts = new System.Text.StringBuilder();
                for (int i = 0; i < _tickMs.Length; i++)
                {
                    ours += _tickMs[i];
                    if (_tickMs[i] >= 0.3)
                        parts.Append(_tickNames[i]).Append('=').Append(_tickMs[i].ToString("F1")).Append("ms ");
                }
                var ring = new System.Text.StringBuilder();
                for (int i = 1; i <= _dtRing.Length; i++)
                    ring.Append((_dtRing[(_dtRingIdx + i) % _dtRing.Length] * 1000f).ToString("F0")).Append(' ');
                Plugin.Log.LogDebug($"[Stutter] f={Time.frameCount} {dt * 1000f:F0}ms frame (avg {_ftAvg * 1000f:F1}ms, prev8: {ring.ToString().TrimEnd()}): "
                    + $"OURS={ours:F1}ms UNTRACKED={dt * 1000f - ours:F0}ms {parts}| "
                    + $"pcToggles={_pcWrites} pcRunsΔ={pcRunsDelta} distCull={_distWrites} lightCull={_lightWrites} "
                    + $"nav={NavCalls}x/{NavMs:F1}ms gc0={gc0 - _gc0Prev} gc1={gc1 - _gc1Prev} t={Time.timeSinceLevelLoad:F0}s");
            }
            _dtRing[_dtRingIdx] = dt;
            _dtRingIdx = (_dtRingIdx + 1) % _dtRing.Length;
            _pcRunsPrev = _pcDriverRuns;
            _gc0Prev = gc0; _gc1Prev = gc1;
            _ftAvg = Mathf.Lerp(_ftAvg, dt, 0.05f);
            _pcWrites = _distWrites = _lightWrites = 0;
            NavCalls = 0; NavMs = 0f;
            Array.Clear(_tickMs, 0, _tickMs.Length);
        }

        // FRAME SPLIT (08-08): steady-state fps triage. the in-game overlay shows ~30 fps
        // with GPU ~30-50% and CPU ~35-45% — nothing saturated, so the wall lives on ONE
        // thread and the spike probe cant see engine time. FrameTimingManager came back
        // EMPTY (bsg's build lacks the frame-timing-stats flag), so this is the
        // hand-rolled version: Camera.onPreCull..onPostRender brackets the main-thread
        // camera window (view culling + draw submission — batching/culling territory),
        // a WaitForEndOfFrame marker catches the post-render tail (present/vsync/
        // render-thread wait), and whats left of dt is the script phase (AI, animation,
        // physics, every mod's Update). the three-way split names the bottleneck; bot
        // count rides along for correlation (script-phase cost usually scales with it).
        private static readonly System.Diagnostics.Stopwatch _fsClock = System.Diagnostics.Stopwatch.StartNew();
        private static bool _fsHooked;
        private static int _fsCamFrame = -1;
        private static double _fsCamStart, _fsCamEnd, _fsEofMark;
        private static double _fsDtSum, _fsDtMax, _fsCamSum, _fsCamMax, _fsTailSum;
        private static int _fsN, _fsFixed;
        private static float _fsNextLog;
        private bool _eofStarted;

        private void FixedUpdate() => _fsFixed++;

        // raid teardown with a clamp active would leak it into the menus/next map —
        // QualitySettings is global state, hand it back
        private void OnDestroy()
        {
            if (_lodBiasOrig >= 0f) { QualitySettings.lodBias = _lodBiasOrig; _lodBiasOrig = -1f; }
            if (_maxLodOrig >= 0) { QualitySettings.maximumLODLevel = _maxLodOrig; _maxLodOrig = -1; }
            _qsLogged = false;
        }

        private System.Collections.IEnumerator EofMarker()
        {
            var eof = new WaitForEndOfFrame();
            while (true) { yield return eof; _fsEofMark = _fsClock.Elapsed.TotalMilliseconds; }
        }

        // VANILLA-MAP PERF CENSUS (08-09, user: "spt's normal maps run better than our
        // icebreaker — they're doing something we aren't"). one log line per VANILLA
        // raid, ~60s in, inventorying which native perf systems are actually ALIVE
        // there — measured on a live customs/streets raid instead of read out of a
        // decompiler (which mis-called the LODGroup toggle once already). the money
        // field is autocullCells: populated on a vanilla map = the cell autocull DOES
        // run in this engine and the icebreaker resurrection is back on the table.
        private static bool _vanCensusDone;
        private static float _vanCensusAt;

        private void TickVanillaPerfCensus()
        {
            var gw = Comfort.Common.Singleton<GameWorld>.Instance;
            if (gw == null || gw.MainPlayer == null) { _vanCensusDone = false; _vanCensusAt = 0f; return; }
            if (_vanCensusDone) return;
            if (_vanCensusAt == 0f) { _vanCensusAt = Time.unscaledTime + 60f; return; }
            if (Time.unscaledTime < _vanCensusAt) return;
            _vanCensusDone = true;
            try
            {
                var gridT = System.Type.GetType("Koenigz.PerfectCulling.EFT.PerfectCullingAdaptiveGrid, Assembly-CSharp");
                var grid = gridT?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                bool gridAlive = grid != null && !grid.Equals(null);
                bool packedOk = false;
                if (gridAlive)
                {
                    try
                    {
                        var packed = gridT.GetProperty("PackedData")?.GetValue(grid);
                        packedOk = packed != null && Equals(packed.GetType().GetProperty("IsValid")?.GetValue(packed), true);
                    }
                    catch { }
                }
                int autocullCells = GClass1237.List_0 != null ? GClass1237.List_0.Count : -1;
                bool sampler = GClass1238.Instance != null;
                int crossVols = -1;
                try { crossVols = Koenigz.PerfectCulling.EFT.PerfectCullingCrossSceneVolume.AllRuntimeCrossGroupVolumes.Count; } catch { }
                int gridContent = 0, populatedCells = 0;
                foreach (var cgc in UnityEngine.Object.FindObjectsOfType<Koenigz.PerfectCulling.EFT.CullingGridContent>())
                {
                    gridContent++;
                    var cells = cgc.CellContent;
                    if (cells == null) continue;
                    foreach (var c in cells)
                        if (c != null && c.LodGroupCell != null && c.LodGroupCell._lodGroups != null && c.LodGroupCell._lodGroups.Count > 0)
                            populatedCells++;
                }
                int lodGroups = 0, oneLod = 0;
                foreach (var g in UnityEngine.Object.FindObjectsOfType<LODGroup>())
                {
                    lodGroups++;
                    if (g.lodCount <= 1) oneLod++;
                }
                Plugin.Log.LogWarning($"[PerfCensus] map={gw.LocationId} grid={gridAlive} packed={packedOk} sampler1238={sampler} "
                    + $"autocullCells={autocullCells} crossVolumes={crossVols} gridContentComps={gridContent} populatedLodCells={populatedCells} "
                    + $"lodGroups={lodGroups} oneLod={oneLod}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[PerfCensus] failed: {e.Message}"); }
        }

        // MIXER ROUTING (08-08, "wind ignores the volume slider"): every resurrected
        // ambient source — our wind/room beds, door foley, and the retail sound-scene
        // sources whose serialized mixer refs died in the rip — plays with
        // outputAudioMixerGroup null, which BYPASSES bsg's mixer and therefore the
        // in-game volume sliders (max volume forever; the game applies OverallVolume
        // as a mixer channel, never AudioListener.volume). adopt any null-group
        // source into MasterMixerGroup. 10s cadence, idempotent, catches
        // late-created sources; a deliberately-null-routed source doesn't exist in
        // EFT, so adopt-all-null is safe by construction.
        private static float _mixNextSweep;
        private static int _mixAdopted;

        private void TickMixerRoute()
        {
            if (Time.unscaledTime < _mixNextSweep) return;
            _mixNextSweep = Time.unscaledTime + 10f;
            try
            {
                var ba = Comfort.Common.Singleton<BetterAudio>.Instance;
                var master = ba != null ? ba.MasterMixerGroup : null;
                if (master == null) return;
                int adopted = 0;
                foreach (var src in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                    if (src != null && src.outputAudioMixerGroup == null)
                    {
                        src.outputAudioMixerGroup = master;
                        adopted++;
                    }
                if (adopted > 0)
                {
                    _mixAdopted += adopted;
                    Plugin.Log.LogDebug($"[AudioRoute] {adopted} unrouted AudioSource(s) adopted into the master mixer "
                        + $"(total {_mixAdopted}) — game volume sliders now apply to them");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[AudioRoute] sweep failed: {e.Message}"); }
        }

        // live QualitySettings clamps (08-08): global state the game re-asserts on
        // settings-apply, so we re-assert every frame (a float compare — free).
        // originals captured on first touch, restored the moment the config goes back
        // to -1, so mid-raid A/B is safe in both directions. (the shadow clamps that
        // pioneered this pattern retired 08-09 — both no-ops on this shadow-poor map)
        private static float _lodBiasOrig = -1f;
        private static int _maxLodOrig = -1;
        private static bool _qsLogged;
        private static bool _lodProbeOff;

        private void TickQualityClamps()
        {
            float wantBias = Plugin.LodBiasClamp.Value;
            if (wantBias >= 0f)
            {
                if (_lodBiasOrig < 0f) { _lodBiasOrig = QualitySettings.lodBias; Plugin.Log.LogDebug($"[LOD] bias clamp on (game was {_lodBiasOrig:F2})"); }
                if (QualitySettings.lodBias != wantBias) QualitySettings.lodBias = wantBias;
            }
            else if (_lodBiasOrig >= 0f)
            {
                QualitySettings.lodBias = _lodBiasOrig;
                Plugin.Log.LogDebug($"[LOD] bias clamp off (restored {_lodBiasOrig:F2})");
                _lodBiasOrig = -1f;
            }

            int wantMaxLod = Plugin.MaxLodClamp.Value;
            if (wantMaxLod >= 0)
            {
                if (_maxLodOrig < 0) { _maxLodOrig = QualitySettings.maximumLODLevel; Plugin.Log.LogDebug($"[LOD] maxLOD clamp on (game was {_maxLodOrig})"); }
                if (QualitySettings.maximumLODLevel != wantMaxLod) QualitySettings.maximumLODLevel = wantMaxLod;
            }
            else if (_maxLodOrig >= 0)
            {
                QualitySettings.maximumLODLevel = _maxLodOrig;
                Plugin.Log.LogDebug($"[LOD] maxLOD clamp off (restored {_maxLodOrig})");
                _maxLodOrig = -1;
            }

            // one-shot truth line so no more A/Bing values the game already had: the
            // full QualitySettings picture at raid start
            if (!_qsLogged)
            {
                _qsLogged = true;
                Plugin.Log.LogWarning($"[QualityCensus] shadows={QualitySettings.shadows} shadowDist={QualitySettings.shadowDistance:F0} "
                    + $"cascades={QualitySettings.shadowCascades} lodBias={QualitySettings.lodBias:F2} maxLOD={QualitySettings.maximumLODLevel} "
                    + $"pixelLights={QualitySettings.pixelLightCount} vsync={QualitySettings.vSyncCount} targetFps={Application.targetFrameRate} "
                    + $"aniso={QualitySettings.anisotropicFiltering} softParticles={QualitySettings.softParticles}");
            }
        }

        private void TickFrameSplit()
        {
            double now = _fsClock.Elapsed.TotalMilliseconds;
            if (!_fsHooked)
            {
                // static guard — the camera delegates survive across raids
                _fsHooked = true;
                Camera.onPreCull += c =>
                {
                    if (Time.frameCount != _fsCamFrame) { _fsCamFrame = Time.frameCount; _fsCamStart = _fsClock.Elapsed.TotalMilliseconds; }
                };
                Camera.onPostRender += c => _fsCamEnd = _fsClock.Elapsed.TotalMilliseconds;
            }
            if (!_eofStarted)
            {
                // instance guard — the coroutine dies with this component every raid
                // end and must restart on the next raid's probe
                _eofStarted = true;
                StartCoroutine(EofMarker());
                return;
            }

            // all three marks are LAST frame's (cameras + eof run after Update) — same
            // frame unscaledDeltaTime measures, so the split lines up exactly.
            double dt = Time.unscaledDeltaTime * 1000.0;
            double cam = (_fsCamEnd > _fsCamStart && _fsCamEnd - _fsCamStart < dt * 2) ? _fsCamEnd - _fsCamStart : 0;
            // eof-to-this-Update gap: present wait + next-frame engine preamble
            double tail = (_fsEofMark > 0 && now > _fsEofMark && now - _fsEofMark < dt * 2) ? now - _fsEofMark : 0;
            _fsDtSum += dt; if (dt > _fsDtMax) _fsDtMax = dt;
            _fsCamSum += cam; if (cam > _fsCamMax) _fsCamMax = cam;
            _fsTailSum += tail;
            _fsN++;

            if (Time.unscaledTime < _fsNextLog) return;
            _fsNextLog = Time.unscaledTime + 10f;
            if (_fsN == 0) return;
            int alive = 0;
            try { alive = Comfort.Common.Singleton<EFT.GameWorld>.Instance?.AllAlivePlayersList?.Count ?? 0; } catch { }
            double avgDt = _fsDtSum / _fsN, avgCam = _fsCamSum / _fsN, avgTail = _fsTailSum / _fsN;
            Plugin.Log.LogDebug($"[FrameSplit] {_fsN}f/10s: frame {avgDt:F1}ms (max {_fsDtMax:F0}) = "
                + $"scripts {avgDt - avgCam - avgTail:F1} + camMain {avgCam:F1} (max {_fsCamMax:F0}) + presentTail {avgTail:F1} | "
                + $"fixedUpd {(float)_fsFixed / _fsN:F1}/f | alive={alive}");
            _fsDtSum = _fsDtMax = _fsCamSum = _fsCamMax = _fsTailSum = 0;
            _fsN = 0; _fsFixed = 0;
        }

        // DISTANCE CULLING — the missing BSG system (their CullingLightObject/"GameObjects
        // To Turn Off" grid died in the rip). the F8 counters proved the frame cost is
        // 41.6k draw calls toward the ship center; most are tiny distant props contributing
        // zero pixels. cull by size class: small objects vanish near, medium further, big
        // stuff (hull/decks) never. round-robin a slice each frame — no per-frame spikes.
        // container prop roots, shared by the distance culler AND the occlusion
        // whitelist: the LootableContainer is a '_lootable' SIBLING of the visual mesh,
        // never an ancestor — GetComponentInParent checks silently miss every container
        internal static HashSet<Transform> BuildLootContainerRoots()
        {
            var lcRoots = new HashSet<Transform>();
            foreach (var lc in UnityEngine.Object.FindObjectsOfType<EFT.Interactive.LootableContainer>(true))
            {
                var root = lc.transform.parent != null ? lc.transform.parent : lc.transform;
                var walk = lc.transform;
                for (int hop = 0; walk != null && hop < 4; hop++, walk = walk.parent)
                    if (walk.GetComponent<LODGroup>() != null) { root = walk; break; }
                lcRoots.Add(root);
            }
            return lcRoots;
        }

        private struct DistEntry { public Renderer R; public Vector3 Pos; public float CullDist; public bool WeDisabled; }
        private static readonly List<DistEntry> _distEntries = new List<DistEntry>();
        private static int _distCursor;

        // ---- SSR cost clamp (08-08, the center-view fps hunt) ----
        // the retail post volumes bake SSR at SUPERSAMPLED resolution + 256 iterations,
        // and nothing binds it to the player's graphics settings on this map. its cost
        // is per-pixel-with-geometry (sky early-outs), so it is heaviest EXACTLY in the
        // dense center view — the F4 archaeology called it a "per-pixel GPU monster".
        // hardcoded to full-size since 08-09 (SsrQuality config retired): kills the
        // supersampling only — near-identical look, the whole GPU win, no knob.
        private const int SsrMode = 2;
        private static void ApplySsrClamp()
        {
            int mode = SsrMode;
            try
            {
                // make the IN-GAME SSR setting actually work here: vanilla maps apply
                // it via CameraClass.SetSSR, which only touches the CAMERA's volume
                // (and NREs when that profile lacks an SSR block — our Cam2 case). the
                // supersampled monster lives in the RETAIL SCENE volumes the setting
                // never reaches — so every player was paying the retail preset no
                // matter what they selected. mirror SetSSR's own mapping onto the
                // scene volumes: Off kills SSR, Low/Medium keep their presets,
                // High/Ultra map to High (exactly what SetSSR does on vanilla maps).
                string gamePreset = null;
                try
                {
                    var gfx = Comfort.Common.Singleton<SharedGameSettingsClass>.Instance?.Graphics?.Settings;
                    if (gfx != null)
                    {
                        switch ((int)gfx.SSR.Value)
                        {
                            case 0:
                                Plugin.Log.LogInfo("[SSR] in-game SSR setting is OFF — disabling SSR everywhere (the vanilla setting never reached the scene volumes on this map)");
                                mode = 0;
                                break;
                            case 1: gamePreset = "Low"; break;
                            case 2: gamePreset = "Medium"; break;
                            default: gamePreset = "High"; break;
                        }
                    }
                }
                catch { }

                int touched = 0;
                var ppvType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.PostProcessVolume");
                if (ppvType == null) return;
                foreach (var volObj in UnityEngine.Object.FindObjectsOfType(ppvType))
                {
                    var profile = GetMember(volObj, "sharedProfile") ?? GetMember(volObj, "profile");
                    var settings = GetMember(profile, "settings") as System.Collections.IEnumerable;
                    if (settings == null) continue;
                    foreach (var s in settings)
                    {
                        if (s == null || !s.GetType().Name.Contains("ScreenSpaceReflections")) continue;
                        if (mode == 0)
                        {
                            s.GetType().GetProperty("active")?.SetValue(s, false);
                            touched++;
                            continue;
                        }
                        // honor the game setting's PRESET on the scene volumes (what
                        // SetSSR would have done if it could reach them)
                        if (gamePreset != null)
                        {
                            var preParam = AccessTools.Field(s.GetType(), "preset")?.GetValue(s);
                            var preVal = preParam == null ? null : AccessTools.Field(preParam.GetType(), "value");
                            if (preVal != null)
                            {
                                object curPre = preVal.GetValue(preParam);
                                object wantPre = Enum.Parse(preVal.FieldType, gamePreset);
                                if (!Equals(curPre, wantPre))
                                {
                                    preVal.SetValue(preParam, wantPre);
                                    AccessTools.Field(preParam.GetType(), "overrideState")?.SetValue(preParam, true);
                                    touched++;
                                    Plugin.Log.LogInfo($"[SSR] preset {curPre} -> {gamePreset} (your in-game SSR setting, finally applied to the scene volumes)");
                                }
                            }
                        }

                        // resolution is a ParameterOverride<ScreenSpaceReflectionResolution>:
                        // Downsampled=0, FullSize=1, Supersampled=2
                        var resParam = AccessTools.Field(s.GetType(), "resolution")?.GetValue(s);
                        if (resParam == null) continue;
                        var valField = AccessTools.Field(resParam.GetType(), "value");
                        var stateField = AccessTools.Field(resParam.GetType(), "overrideState");
                        if (valField == null) continue;
                        object cur = valField.GetValue(resParam);
                        int want = mode == 1 ? 0 : 1;
                        if (Convert.ToInt32(cur) > want)
                        {
                            valField.SetValue(resParam, Enum.ToObject(valField.FieldType, want));
                            stateField?.SetValue(resParam, true);
                            touched++;
                            Plugin.Log.LogInfo($"[SSR] clamped {cur} -> {(want == 0 ? "Downsampled" : "FullSize")} on a post volume");
                        }
                    }
                }
                if (touched > 0)
                    Plugin.Log.LogWarning($"[SSR] cost clamp applied to {touched} SSR block(s) — "
                        + "the retail profile's supersampled SSR was the per-pixel cost that peaks in the dense center view");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[SSR] clamp failed (retail SSR stays): {e.Message}"); }
        }

        private static void BuildDistanceCuller()
        {
            _distEntries.Clear();

            // container PROP roots: the GetComponentInParent<WorldInteractiveObject>
            // exclusion below never fires for container MESHES — the LootableContainer
            // sits on a '_lootable' SIBLING, not an ancestor, so PC blocks culled at
            // 40m and duffles at 80m ("cant see the loot until im close"). collect the
            // prop roots and skip everything under them: containers are gameplay, not
            // decoration.
            var lcRoots = BuildLootContainerRoots();

            foreach (var mr in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
            {
                // ONLY map-scene geometry — it's static so cached positions are valid.
                // anything else (weapons/viewmodels/loot/pools live in other scenes or
                // DontDestroyOnLoad) MOVES, and a cached-position culler will eat it —
                // it culled the player's melee out of his hands. never again.
                var sc = mr.gameObject.scene.name;
                if (sc == null || !sc.StartsWith("Icebreaker")) continue;
                if (mr.GetComponentInParent<Player>() != null) continue;
                if (mr.GetComponentInParent<WorldInteractiveObject>() != null) continue;
                bool containerProp = false;
                for (var w = mr.transform; w != null && !containerProp; w = w.parent)
                    if (lcRoots.Contains(w)) containerProp = true;
                if (containerProp) continue;
                var size = mr.bounds.size.magnitude;
                if (size > 12f) continue; // structural — always rendered
                // do NOT skip isPartOfStaticBatch here — tried 08-08 on the theory
                // that batched draws are cheap and enabled-toggles on batched
                // renderers are costly. reality: releasing the 148k batched props
                // from distance culling COST fps (they render at all ranges) and the
                // microstutter stayed (its probe counters were zero all along —
                // distCull was never the culprit). the culling earns its keep.
                float cullDist = size < 0.75f ? 40f
                               : size < 2f ? 80f
                               : size < 5f ? 150f
                               : 250f;
                // UNSCALED — CullDistanceScale is applied live in the tick so the F12
                // slider actually works mid-raid (baked-in scale made it a dead knob)
                _distEntries.Add(new DistEntry { R = mr, Pos = mr.bounds.center, CullDist = cullDist });
            }
            Plugin.Log.LogDebug($"[DistCull] tracking {_distEntries.Count} renderers (size-classed cull distances x{Plugin.CullDistanceScale.Value:F2})");
        }

        // called by the static batcher after a combine: toggling enabled on a
        // statically-batched renderer is drastically more expensive than on a loose
        // one (08-08: steady ~1s-cadence 40-100ms stutters, every spike at the
        // distCull=250 budget cap, started the raid the combine first ran). batched
        // renderers draw cheap anyway — they no longer need distance culling at all.
        internal static int PruneDistCullStaticBatched()
        {
            int before = _distEntries.Count;
            _distEntries.RemoveAll(e => e.R == null || e.R.isPartOfStaticBatch);
            int removed = before - _distEntries.Count;
            if (removed > 0)
                Plugin.Log.LogInfo($"[DistCull] released {removed} statically-batched renderer(s) from distance culling ({_distEntries.Count} still tracked)");
            return removed;
        }

        // retail maps ship a scene-resident CullingManager (jobified distance culler that
        // drives every CullingLightObject); ours died in the rip (no SDK class -> missing-
        // script strip). the class needs zero scene data — recreate it and BSG's light
        // culling runs natively once the scene's CullingLightObjects have their _light refs
        // (editor pass "Lighting 4"). CullingLightObject.Awake subscribes OnInstanceCreated,
        // so late creation here is handled by BSG's own code.
        private static void EnsureCullingManager()
        {
            try
            {
                if (CullingManager.Instance != null) { Plugin.Log.LogDebug("[Culling] CullingManager already present"); return; }
                new GameObject("Icebreaker_CullingManager_Fix").AddComponent<CullingManager>();
                Plugin.Log.LogDebug("[Culling] created CullingManager — native light culling live (if _light refs are baked)");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Culling] CullingManager create failed: {e.Message}"); }
        }

        // LIGHT distance culling — same idea for the 1776 realtime lights whose
        // CullingLightObject cullers are dead shells (BSG authored 80m cull distance;
        // the primitives survived, the refs didn't). map lights are static -> cached pos.
        private struct LightEntry { public Light L; public Vector3 Pos; }
        private static readonly List<LightEntry> _lightEntries = new List<LightEntry>();

        private static void BuildLightCuller()
        {
            _lightEntries.Clear();

            // if the scene ships HEALED CullingLightObjects (editor pass rebound their
            // _light refs), BSG's native CullingManager system drives the lights — our
            // crude 80m toggler would fight it. stand down.
            foreach (var clo in UnityEngine.Object.FindObjectsOfType<CullingLightObject>())
            {
                if (clo.GetLight() != null)
                {
                    Plugin.Log.LogDebug("[LightCull] native CullingLightObjects detected — runtime light culler standing down");
                    return;
                }
            }

            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                var sc = l.gameObject.scene.name;
                if (sc == null || !sc.StartsWith("Icebreaker")) continue;
                if (l.type == LightType.Directional) continue;
                _lightEntries.Add(new LightEntry { L = l, Pos = l.transform.position });
            }
            Plugin.Log.LogDebug($"[LightCull] tracking {_lightEntries.Count} static map lights (80m x{Plugin.CullDistanceScale.Value:F2})");
        }

        // cutscene hold: the cullers key off CAMERA position, and the cutscene flies it
        // hundreds of meters out — wide shots culled every deck lamp (>80m) and popped
        // props. driver sets this for the duration; cullers resume + re-cull on release.
        internal static bool CutsceneHold;

        internal static void CutsceneShowAll()
        {
            // the NATIVE CullingManager also rides Camera.onPreCull — registered before
            // the cutscene driver's stomp, so it culled from the PLAYER's stale position
            // all cutscene (dark ship in wide shots, wedged lights). LockState(true)
            // pauses evaluation WITHOUT unhooking anything (do NOT toggle cm.enabled —
            // OnDisable unhooks onPreCull and only Awake re-registers: raid-long
            // lobotomy). BSG's ForceEnable(true) would restore everything but its loop
            // has no per-object guard and NREs on culling objects with null internals —
            // so the restore sweep below is OURS, guarded per object.
            try
            {
                var cm = CullingManager.Instance;
                if (cm != null) cm.LockState(true);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[CutsceneHold] native manager lock failed: {e.Message}"); }

            int lights = 0, rends = 0;
            foreach (var e in _lightEntries)
                if (e.L != null && !e.L.enabled) { e.L.enabled = true; lights++; }
            int native = ForceNativeLightsOn();
            for (int i = 0; i < _distEntries.Count; i++)
            {
                var e = _distEntries[i];
                if (e.R != null && e.WeDisabled)
                {
                    e.R.enabled = true;
                    e.WeDisabled = false;
                    _distEntries[i] = e;
                    rends++;
                }
            }
            Plugin.Log.LogDebug($"[CutsceneHold] forced on: {lights} tracked + {native} native lights, {rends} renderers");
        }

        // the parked hovercraft's two spots (G_spot_1/G_spot_2 in Design_Main, the only
        // underscore-named G_spots in the map — the ship rigs use "G_spot (1)" style, so
        // exact-name matching cant catch strays). their CullingLightObjects hold them at
        // intensity 0 (dark, 07-28 coop test); free them like the exfil heli's lights.
        // runs BEFORE DiscoverLamps: freed lights sit at the authored ceiling (5), so the
        // dead-lamp sweep skips them and the LampIntensity slider never drags them down.
        private static void FreeHovercraftLights()
        {
            int freed = 0;
            foreach (var name in new[] { "G_spot_1", "G_spot_2" })
            {
                var go = GameObject.Find(name);
                if (go != null) freed += FreeNativeLights(go.transform);
            }
            if (freed > 0)
                Plugin.Log.LogDebug($"[LightLamps] hovercraft spots freed from native culling ({freed})");
        }

        // free a rig's lights from native culling FOR GOOD: force them lit at authored
        // intensity and destroy their CullingLightObject components (the game class
        // unregisters itself on destroy). for dynamic actors like the exfil heli, whose
        // lights must shine mid-flight from 300m out — a distance-culled nav strobe is
        // nonsense, and any one-shot heal gets re-faded by the manager within seconds.
        internal static int FreeNativeLights(Transform root)
        {
            if (root == null) return 0;
            if (_cloMaxIntensity == null)
                _cloMaxIntensity = HarmonyLib.AccessTools.Field(typeof(CullingLightObject), "_maxLightIntensity");
            int n = 0;
            foreach (var clo in root.GetComponentsInChildren<CullingLightObject>(true))
            {
                var l = clo.GetLight();
                if (l != null)
                {
                    l.enabled = true;
                    try
                    {
                        float mi = (float)_cloMaxIntensity.GetValue(clo);
                        if (mi > 0f) l.intensity = mi;
                    }
                    catch { }
                    n++;
                }
                try { UnityEngine.Object.Destroy(clo); } catch { }
            }
            return n;
        }

        // the native system darkens lights by fading INTENSITY (float_2 multiplier), not
        // Light.enabled — an enabled-only sweep "fixes 0" while the ship sits black.
        // restore state + intensity ourselves: SetVisibility(true) flips the manager's
        // internal visible flag (per-object try — flicker/volumetric refs can be null on
        // rebaked components), then hard-write intensity back to the authored/driven
        // ceiling (_maxLightIntensity — ApplyLamps keeps it at the LampIntensity slider).
        private static System.Reflection.FieldInfo _cloMaxIntensity;
        internal static int ForceNativeLightsOn()
        {
            if (_cloMaxIntensity == null)
                _cloMaxIntensity = HarmonyLib.AccessTools.Field(typeof(CullingLightObject), "_maxLightIntensity");
            int n = 0;
            foreach (var clo in UnityEngine.Object.FindObjectsOfType<CullingLightObject>())
            {
                try { clo.SetVisibility(true); } catch { }
                var l = clo.GetLight();
                if (l == null) continue;
                if (!l.enabled) l.enabled = true;
                try
                {
                    float mi = (float)_cloMaxIntensity.GetValue(clo);
                    if (mi > 0f && l.intensity < mi) { l.intensity = mi; n++; }
                }
                catch { }
            }
            return n;
        }

        // post-cutscene: heal every native light once more, then unlock the manager so
        // it resumes with fresh distances from the real camera and re-culls cleanly
        internal static void CutsceneRelease()
        {
            CutsceneHold = false;
            int native = 0;
            try { native = ForceNativeLightsOn(); } catch { }
            try
            {
                var cm = CullingManager.Instance;
                if (cm != null) cm.LockState(false);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[CutsceneHold] release failed: {e.Message}"); }
            Plugin.Log.LogDebug($"[CutsceneHold] released — healed {native} native lights, culling unlocked");
        }

        // all lights every ~15 frames — 1.8k distance checks is nothing
        private static void TickLightCuller()
        {
            if (CutsceneHold) return;
            if (_lightEntries.Count == 0 || CameraRef == null || Time.frameCount % 15 != 0) return;
            var camPos = CameraRef.transform.position;
            float d = 80f * Plugin.CullDistanceScale.Value;
            float d2 = d * d;
            foreach (var e in _lightEntries)
            {
                if (e.L == null) continue;
                bool near = (e.Pos - camPos).sqrMagnitude < d2;
                if (e.L.enabled != near) { e.L.enabled = near; _lightWrites++; }
            }
        }

        // ~10k checks/frame => full sweep every ~1s at 150k entries. cheap. only ever
        // re-enables what IT disabled — never overrides the occlusion culling's hides.
        private static void TickDistanceCuller()
        {
            if (CutsceneHold) return;
            if (_distEntries.Count == 0 || CameraRef == null) return;
            var camPos = CameraRef.transform.position;
            float dcScale = Plugin.CullDistanceScale.Value; // live — tune the pop-in in F12 mid-raid
            int n = Mathf.Min(10000, _distEntries.Count);
            // distance CHECKS are cheap; renderer.enabled WRITES are not — a fast camera
            // move once flipped 4412 in one frame (356ms). cap writes; the sweep cursor
            // catches the rest over the following frames. 600 was still ~50ms worth and
            // showed up as dist=600 on two of the worst 07-27 spike frames.
            int writes = 0;
            for (int i = 0; i < n && writes < 250; i++)
            {
                _distCursor = (_distCursor + 1) % _distEntries.Count;
                var e = _distEntries[_distCursor];
                if (e.R == null) continue;
                float cd = e.CullDist * dcScale;
                bool near = (e.Pos - camPos).sqrMagnitude < cd * cd;
                if (!near && e.R.enabled)
                {
                    e.R.enabled = false;
                    e.WeDisabled = true;
                    _distEntries[_distCursor] = e;
                    _distWrites++; writes++;
                }
                else if (near && e.WeDisabled)
                {
                    e.R.enabled = true;
                    e.WeDisabled = false;
                    _distEntries[_distCursor] = e;
                    _distWrites++; writes++;
                }
            }
        }

        // drive every discovered lamp from the config slider (live). native-owned lights
        // (CullingLightObject) get their brightness ceiling set instead: the authored
        // _maxLightIntensity (5) is too hot — the slider caps it, and float_1 (the cached
        // "on" value CullingManager actually drives) must follow or nothing changes.
        private static void ApplyLamps()
        {
            float v = Plugin.LampIntensity.Value;
            var shadows = Plugin.LampShadows.Value ? LightShadows.Hard : LightShadows.None;
            int n = 0;
            foreach (var l in _lamps)
                if (l != null) { l.intensity = v; l.shadows = shadows; n++; }

            int natives = 0;
            var fMax = AccessTools.Field(typeof(CullingLightObject), "_maxLightIntensity");
            var fCached = AccessTools.Field(typeof(CullingLightObject), "float_1");
            foreach (var clo in UnityEngine.Object.FindObjectsOfType<CullingLightObject>())
            {
                if (clo.GetLight() == null) continue;
                fMax?.SetValue(clo, v);
                fCached?.SetValue(clo, v);
                natives++;
            }
            _lastLamp = v;
            Plugin.Log.LogDebug($"[LightLamps] drove {n} plain lamps + {natives} native culling lights to intensity {v:F2} (shadows={Plugin.LampShadows.Value})");
        }

        internal static void Dump(string tag)
        {
            try { DumpInner(tag); }
            catch (Exception e) { Plugin.Log.LogWarning($"[RenderEnv:{tag}] dump failed: {e.Message}"); }
        }

        // engine-level frame counters — which metric tracks the fps is the ground truth the
        // subset-hiding tests can't give. ProfilerRecorder render counters work in players.
        private static Unity.Profiling.ProfilerRecorder _recBatches, _recSetPass, _recTris, _recVerts, _recShadowCasters;
        private static bool _recStarted;

        private static void EnsureRecorders()
        {
            if (_recStarted) return;
            _recStarted = true;
            _recBatches = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Batches Count");
            _recSetPass = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "SetPass Calls Count");
            _recTris = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Triangles Count");
            _recVerts = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Vertices Count");
            _recShadowCasters = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Shadow Casters Count");
        }

        private static string Rec(Unity.Profiling.ProfilerRecorder r)
            => r.Valid && r.LastValue > 0 ? (r.LastValue >= 1000000 ? $"{r.LastValue / 1000000f:F1}M" : r.LastValue >= 1000 ? $"{r.LastValue / 1000f:F1}k" : r.LastValue.ToString()) : "?";

        private static void DumpInner(string tag)
        {
            var loc = "<no world>";
            try { var w = Comfort.Common.Singleton<GameWorld>.Instance; if (w != null) loc = w.LocationId; } catch { }
            var L = Plugin.Log;
            L.LogWarning($"===== [RenderEnv:{tag}] loc={loc} =====");

            var cam = CameraRef != null ? CameraRef : Camera.main;
            if (cam != null)
                L.LogWarning($"[RenderEnv] camera '{cam.name}': HDR={cam.allowHDR} MSAA={cam.allowMSAA} clear={cam.clearFlags} bg={cam.backgroundColor} fov={cam.fieldOfView:F1} cullMask=0x{cam.cullingMask:X}");

            // the numbers that matter: what is the engine actually doing per frame
            EnsureRecorders();
            L.LogWarning($"[RenderEnv] FRAME: fps={1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f):F0} " +
                         $"batches={Rec(_recBatches)} setPass={Rec(_recSetPass)} tris={Rec(_recTris)} verts={Rec(_recVerts)} shadowCasters={Rec(_recShadowCasters)}");

            // scene lighting — the prime suspect for blowout: bad ambient / missing bake
            L.LogWarning($"[RenderEnv] ambient: mode={RenderSettings.ambientMode} intensity={RenderSettings.ambientIntensity:F2} " +
                         $"light={RenderSettings.ambientLight} sky={RenderSettings.ambientSkyColor} eq={RenderSettings.ambientEquatorColor} gnd={RenderSettings.ambientGroundColor}");
            L.LogWarning($"[RenderEnv] skybox={(RenderSettings.skybox != null ? RenderSettings.skybox.name + "/" + (RenderSettings.skybox.shader != null ? RenderSettings.skybox.shader.name : "?") : "<null>")} " +
                         $"reflMode={RenderSettings.defaultReflectionMode} reflIntensity={RenderSettings.reflectionIntensity:F2} fog={RenderSettings.fog} fogColor={RenderSettings.fogColor} fogMode={RenderSettings.fogMode}");

            // brightest lights (a scene with a 50-intensity sun would blow everything)
            try
            {
                var lights = UnityEngine.Object.FindObjectsOfType<Light>();
                Array.Sort(lights, (a, b) => b.intensity.CompareTo(a.intensity));
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < lights.Length && i < 8; i++)
                    sb.Append($"{lights[i].name}({lights[i].type},i={lights[i].intensity:F1},{lights[i].color}) ");
                L.LogWarning($"[RenderEnv] {lights.Length} lights, brightest: {sb}");
            }
            catch (Exception e) { L.LogWarning($"[RenderEnv] lights failed: {e.Message}"); }

            // occlusion culling live stats (proof the perfect-culling camera is working)
            try
            {
                var camType = System.Type.GetType("Koenigz.PerfectCulling.PerfectCullingCamera, PerfectCullingRuntime");
                var pcc = camType != null && cam != null ? cam.GetComponent(camType) : null;
                if (pcc != null)
                {
                    int total = (int)(camType.GetProperty("LastTotal")?.GetValue(pcc) ?? -1);
                    int culled = (int)(camType.GetProperty("LastCulled")?.GetValue(pcc) ?? -1);
                    // GROUND TRUTH: how many culling-managed renderers are actually switched
                    // off right now. PC's ToggleRenderer uses r.enabled OR forceRenderingOff
                    // depending on compile-time defines — count both.
                    var managed = CollectBakedRenderers();
                    int off = 0;
                    foreach (var mr in managed) if (mr != null && (!mr.enabled || mr.forceRenderingOff)) off++;
                    // did build-time static batching actually engage? isPartOfStaticBatch is
                    // the engine's own truth per renderer.
                    int batchedN = 0, totalN = 0;
                    foreach (var mr in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
                    { totalN++; if (mr.isPartOfStaticBatch) batchedN++; }
                    L.LogWarning($"[RenderEnv] culling: {culled}/{total} groups culled | managed={managed.Count}, off={off} | staticBatched={batchedN}/{totalN}");
                }
                else L.LogWarning("[RenderEnv] culling: no PerfectCullingCamera on camera");
            }
            catch (Exception e) { L.LogWarning($"[RenderEnv] culling stats failed: {e.Message}"); }

            // post-processing volumes + their profiles (tonemapping/exposure/grading live here)
            DumpPostVolumes(L);

            L.LogWarning($"===== [RenderEnv:{tag}] end =====");
        }

        private static void DumpPostVolumes(BepInEx.Logging.ManualLogSource L)
        {
            var ppvType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.PostProcessVolume");
            if (ppvType == null) { L.LogWarning("[RenderEnv] PostProcessVolume type not found"); return; }
            var vols = UnityEngine.Object.FindObjectsOfType(ppvType);
            L.LogWarning($"[RenderEnv] {vols.Length} PostProcessVolume(s)");
            foreach (var vol in vols)
            {
                try
                {
                    bool isGlobal = (bool)(GetMember(vol, "isGlobal") ?? false);
                    float priority = ToF(GetMember(vol, "priority"));
                    float weight = ToF(GetMember(vol, "weight"));
                    var profile = GetMember(vol, "sharedProfile") ?? GetMember(vol, "profile");
                    var profName = profile != null ? (profile as UnityEngine.Object)?.name : "<null>";
                    L.LogWarning($"[RenderEnv]  vol '{(vol as UnityEngine.Object)?.name}' global={isGlobal} prio={priority:F0} weight={weight:F2} enabled={(vol as Behaviour)?.enabled} profile={profName}");
                    if (profile == null) continue;
                    var settings = GetMember(profile, "settings") as System.Collections.IEnumerable;
                    if (settings == null) continue;
                    foreach (var s in settings)
                    {
                        if (s == null) continue;
                        bool active = (bool)(GetMember(s, "active") ?? true);
                        var overridden = DumpOverriddenParams(s);
                        L.LogWarning($"[RenderEnv]    {s.GetType().Name} active={active} {overridden}");
                    }
                }
                catch (Exception e) { L.LogWarning($"[RenderEnv]  vol dump failed: {e.Message}"); }
            }
        }

        // read every ParameterOverride<T> field that is actually overridden, as name=value
        private static string DumpOverriddenParams(object effect)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in effect.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var val = f.GetValue(effect);
                if (val == null) continue;
                var ovField = f.FieldType.GetField("overrideState");
                var valField = f.FieldType.GetField("value");
                if (ovField == null || valField == null) continue; // not a ParameterOverride
                try
                {
                    if (!(bool)ovField.GetValue(val)) continue;
                    sb.Append($"{f.Name}={valField.GetValue(val)} ");
                }
                catch { }
            }
            return sb.ToString();
        }

        private static object GetMember(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanRead) return p.GetValue(o);
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f?.GetValue(o);
        }

        private static float ToF(object o) => o is float f ? f : 0f;
    }

    // player-camera setup (PlayerCameraController -> SetCameraFromSettings -> CreateBindings)
    // applies each graphics setting by invoking CameraClass.SetXxx with the user's current
    // value. our camera arrives missing SSAA/SSAAImpl/VolumetricLightRenderer (GetComponent
    // returned null in method_2 — reason unconfirmed), so the whole upscaler/AA setter family
    // NREs. all of them are cosmetic (upscaling, AA, super-sampling, aspect) — swallow the
    // entire family in one multi-target finalizer so the bind pass completes at native res.
    // one finalizer covers every setter; no more discovering them one restart at a time.
    [HarmonyPatch]
    internal static class Patch_CameraGraphicsSetters
    {
        private static readonly string[] Setters =
        {
            "SetFSR2", "SetFSR3", "SetDLSSPreset", "SetAntiAliasing", "SetSuperSampling", "SetAspectRatio",
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var name in Setters)
            {
                var m = AccessTools.Method(typeof(CameraClass), name);
                if (m != null) yield return m;
            }
        }

        private static Exception Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception != null && !IceGate.On) return __exception;
            if (__exception != null)
                Plugin.Log.LogWarning($"[RaidFix] swallowed CameraClass.{__originalMethod.Name}: {__exception.Message}");
            return null;
        }
    }

    // BotsController.Init runs even with bots off and calls AICoversData.RestoreData to
    // rebuild the baked cover/voxel graph. our scene's AICoversData is a dead ripped shell
    // (fields nulled by metadata encryption), so AIVoxelesData.RestoreData throws on a LINQ
    // over a null collection and aborts the raid. with no bots, covers are pure dead weight —
    // swallow and leave the graph empty. milestone 2 replaces this data outright via
    // InjectGeneratedCovers (navmesh-generated), so this patch is walkable-only scaffolding.
    [HarmonyPatch(typeof(AICoversData), "RestoreData")]
    internal static class Patch_CoversRestore
    {
        // RESURRECTION #5: fill the holders with the RETAIL bake before RestoreData runs —
        // when the fill lands, RestoreData executes clean like on a real map (builds its
        // own cache, resolves ids) and the exception path below never fires. fill failure
        // = the synthesized-generation fallback below takes over unchanged.
        private static void Prefix(AICoversData __instance)
        {
            if (!IceGate.On) return; // vanilla covers restore untouched
            if (Plugin.RetailAIBake.Value)
                IcebreakerAIBake.TryFill(__instance);

            // heal null holder collections on the SUCCESS path too — this used to live only
            // in the exception finalizer, so the first clean retail restore shipped an
            // AIPlaceInfoHolder with null Places straight into every exUsec brain (silent
            // activation death: invisible statue bots). only touches fields that are null.
            HealAiHolders();
        }

        private static Exception Finalizer(Exception __exception, AICoversData __instance)
        {
            if (__exception != null && !IceGate.On) return __exception;
            if (__exception != null)
            {
                Plugin.Log.LogWarning($"[RaidFix] swallowed AICoversData.RestoreData: {__exception.Message}");
                HealAiHolders();

                // if the RETAIL bake was loaded but RestoreData still died, the holders
                // are stuck half-restored — clear them so the synthesized fallback below
                // starts from a clean slate (last run it kept 1885 retail points but
                // rebuilt an EMPTY voxel grid over them: covers with no cell linkage =
                // silent statue bots)
                if (IcebreakerAIBake.Loaded && __instance != null)
                {
                    Plugin.Log.LogWarning("[RaidFix] retail bake failed mid-restore — clearing for full synth fallback");
                    __instance.Points = new List<GroupPoint>();
                    __instance.Ways = new List<GroupPointWay>();
                    __instance.Pathes = new List<GroupPointPath>();
                    IcebreakerAIBake.Loaded = false;
                }

                // finish RestoreData's job manually: its LAST line builds the cover cache,
                // and our swallow kills it microseconds earlier. every bot's activation
                // (method_10 line 1: VoxelesPersonalData.Activate -> GetCovers -> _cache)
                // dies on the null cache with an INTERNAL catch -> silent ActiveFail statues.
                // empty-but-valid collections + a real cache = bots activate coverless.
                try
                {
                    if (__instance != null)
                    {
                        if (__instance.Points == null) __instance.Points = new List<GroupPoint>();
                        if (__instance.Ways == null) __instance.Ways = new List<GroupPointWay>();
                        if (__instance.Pathes == null) __instance.Pathes = new List<GroupPointPath>();

                        // order matters: grid first (generation buckets into it), then the
                        // full factory-style generation (covers/cores/doors/loot/exfils),
                        // then the cache LAST so it caches the real points.
                        BuildVoxelGrid(__instance);
                        if (Plugin.InjectCovers.Value)
                            CoverScanner.TryGenerateOnEmpty(__instance);
                        // patrols too, or RetailAIBake=false leaves bots with covers and
                        // nowhere to walk (zero PatrolPoints, zero PatrolWays). only when the
                        // retail bake did NOT land — it brings its own 647/20 and wires the
                        // zones itself, and two sets of ways on one zone is not a thing.
                        if (!IcebreakerAIBake.Loaded)
                        {
                            try { PatrolScanner.GenerateForZones(__instance); }
                            catch (Exception e) { Plugin.Log.LogWarning($"[PatrolGen] failed: {e.Message}"); }
                        }
                        AccessTools.Field(typeof(AICoversData), "_cache").SetValue(__instance, new GClass411(__instance));
                        Plugin.Log.LogDebug($"[RaidFix] AI skeleton ready: {__instance.Points.Count} covers, " +
                                              $"{__instance.AICorePointsHolder?.CorePoints?.Count ?? 0} cores, cache built");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[RaidFix] AI skeleton build failed: {e}"); }
            }
            else if (IceGate.On && IcebreakerAIBake.Loaded)
            {
                // SUCCESS path: the retail bake restored cleanly. if the user named zones to
                // rebuild with our own data, carve and refill NOW rather than in the prefix —
                // RestoreData is what resolves point ids, buckets the voxel grid and builds
                // the cover cache, and the hybrid has to edit the finished article.
                try { IcebreakerHybridBake.Apply(__instance); }
                catch (Exception e) { Plugin.Log.LogError($"[Hybrid] failed — full retail bake stands: {e}"); }
            }
            return null;
        }

        // VOXEL GRID SYNTHESIS: bots index AICoversData.VoxelesArray by position on every
        // BotMover.Activate (GetVoxelSafe: cell = (pos-min)/10x5x10) — a null/empty grid =
        // IndexOutOfRange = ActiveFail statues. build the grid over the navmesh bounds with
        // BSG's cell sizes (verified against our factory dumps). cells start empty — the
        // cover generator populates PointsIds later; empty cells are valid (_closetsPointId
        // 0 skips the cover-linking paths).
        private static void BuildVoxelGrid(AICoversData covers)
        {
            var vox = covers.Voxels;
            if (vox == null) { Plugin.Log.LogWarning("[RaidFix] no AIVoxelesData component — voxel grid not built"); return; }
            if (vox.VoxelesArray != null && vox.VoxelsList != null && vox.VoxelsList.Count > 0) return; // real data present

            var tri = UnityEngine.AI.NavMesh.CalculateTriangulation();
            if (tri.vertices == null || tri.vertices.Length == 0)
            {
                Plugin.Log.LogWarning("[RaidFix] no navmesh loaded — voxel grid not built (bots will ActiveFail)");
                return;
            }
            Vector3 min = tri.vertices[0], max = tri.vertices[0];
            foreach (var v in tri.vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
            min -= new Vector3(10f, 5f, 10f);
            max += new Vector3(10f, 5f, 10f);
            int nx = Mathf.Max(1, Mathf.CeilToInt((max.x - min.x) / 10f));
            int ny = Mathf.Max(1, Mathf.CeilToInt((max.y - min.y) / 5f));
            int nz = Mathf.Max(1, Mathf.CeilToInt((max.z - min.z) / 10f));
            if (nx * ny * nz > 60000)
            {
                Plugin.Log.LogWarning($"[RaidFix] voxel grid too large ({nx}x{ny}x{nz}) — ushort ids would overflow; not built");
                return;
            }
            vox.MinVoxelesValues = min;
            vox.MaxVoxelesValues = max;
            vox.MaxX = nx; vox.MaxY = ny; vox.MaxZ = nz;
            vox.VoxelesArray = new NavGraphVoxelSimple[nx, ny, nz];
            vox.VoxelsList = new List<NavGraphVoxelSimple>(nx * ny * nz);
            ushort id = 1;
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                    for (int z = 0; z < nz; z++)
                    {
                        var pos = min + new Vector3(x * 10f, y * 5f, z * 10f); // cell origin (baked convention: centre = pos + (5, 2.5, 5))
                        var cell = new NavGraphVoxelSimple(pos, x, y, z, id++);
                        cell.DoorLinks = new List<NavMeshDoorLink>(); // only field without an initializer
                        vox.VoxelesArray[x, y, z] = cell;
                        vox.VoxelsList.Add(cell);
                    }
            Plugin.Log.LogDebug($"[RaidFix] synthesized voxel grid {nx}x{ny}x{nz} ({vox.VoxelsList.Count} cells) over navmesh bounds {min}..{max}");
        }

        // CreateOrFind only ADDs holders when the scene has none — our ripped scene HAS
        // them as dead shells (serialized list fields nulled by metadata encryption), so
        // every consumer LINQing over CorePoints/Places/etc. throws. sweep the whole
        // holder family once and empty-init any null List<>/array field via reflection,
        // so GetCorePoint & friends become clean no-hits instead of a per-caller whack-a-mole.
        private static void HealAiHolders()
        {
            var holderTypes = new[]
            {
                typeof(AICorePointHolder), typeof(AIPlaceInfoHolder), typeof(AIManualPointsHolder),
                typeof(AIMinesPositionsHolder), typeof(AIDangerPlacesHolder), typeof(AIDoorsHolder),
            };
            foreach (var t in holderTypes)
            {
                var holder = UnityEngine.Object.FindObjectOfType(t);
                if (holder == null) continue;
                int healed = 0;
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.GetValue(holder) != null) continue;
                    if (f.FieldType.IsArray)
                    {
                        f.SetValue(holder, Array.CreateInstance(f.FieldType.GetElementType(), 0));
                        healed++;
                    }
                    else if (f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(List<>))
                    {
                        f.SetValue(holder, Activator.CreateInstance(f.FieldType));
                        healed++;
                    }
                }
                if (healed > 0)
                    Plugin.Log.LogDebug($"[RaidFix] healed {healed} null collection(s) on {t.Name}");
            }
        }
    }

    // CachePoints runs right after RestoreData and pre-warms the cover-point cache; if
    // RestoreData aborted early the cache internals may be null. cold cache is fine.
    [HarmonyPatch(typeof(AICoversData), "CachePoints")]
    internal static class Patch_CoversCache
    {
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!IceGate.On) return __exception;
            Plugin.Log.LogWarning($"[RaidFix] swallowed AICoversData.CachePoints: {__exception.Message}");
            return null;
        }
    }

    // BotsController.Init does FindUnityObjectOfType<AIStationaryController>() then calls
    // .Init on it with NO null check — a scene without one (ours) NREs inside Init itself,
    // where no leaf finalizer can catch it. create an empty controller up front (the
    // EnvironmentManager pattern). its Init iterates a Weapons array that may be null on a
    // fresh component, so the finalizer below covers that too.
    [HarmonyPatch(typeof(BotsController), "Init")]
    internal static class Patch_EnsureStationaryController
    {
        // after Init: CoversData + AIPlaceInfoHolder exist, bots haven't spawned —
        // rebuild BSG's spawn-trigger layer (tier events + group-size BD triggers)
        private static void Postfix(BotsController __instance)
        {
            // EVERYTHING below is icebreaker-only scene repair (flares, shadow split,
            // sealed doors, keycard heal, spawn triggers). it used to run on every map:
            // a vanilla raid paid a 236ms shadow sweep over 224k renderers and logged
            // flare/door diagnostics for a scene that has none of our data.
            if (!IceGate.On) return;

            // unconditional now: the retail trigger layer IS the spawn system since the
            // legacy watchers were deleted (08-01). CrewBlackDiv is the on/off switch, and
            // with it off the events simply fire with nothing subscribed.
            IcebreakerAIPlaces.TryBuild(__instance);

            // sealed doors: register authored DoorState=64 doors + carve their navmesh
            // shut (runs here because doors AND their links are live post-RefreshData)
            try { IcebreakerSealedDoors.Setup(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sealed] setup failed: {e.Message}"); }

            // retail lens flares (1300 lamp flares via the game's own MultiFlare stack)
            try { IcebreakerFlares.TryBuild(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[Flares] build failed: {e}"); }

            // perf: enforce the retail shadow split. the old lodBias clamp is GONE —
            // it overrode the player's Object LOD quality setting and halved loose
            // loot visibility distance (loot LODGroups cull on lodBias); vanilla
            // settings stay the player's own.
            try { EnforceShadowProxies(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[Perf] perf pass failed: {e.Message}"); }

            // keycard self-heal: bundles built before the 1R proximity-wiring fix ship
            // KeycardDoors with empty Proxies (GetHandle indexes Proxies[0] -> IOOR crash
            // the moment the swipe starts) and proxies with null Link (swiper prompt dead).
            // same nearest-within-2m pairing as the editor tool — retail data says the true
            // pair is always <=1.3m and the next door >=2.6m away.
            try { HealKeycardProxies(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[Keycard] heal failed: {e.Message}"); }

            // door-chain autopsy (runs AFTER RestoreData + BotDoorsController.RefreshData):
            // bots ignoring doors means one of three stages died silently — cell ids not
            // filled, id->link reconnect empty, or links without matched Door. name it.
            // pure diagnostic, so keep it behind the diag switch.
            if (!Plugin.DiagHotkeys.Value) return;
            try
            {
                var covers = __instance.CoversData;
                int idCells = 0, linkedCells = 0;
                if (covers != null && covers.Voxels != null && covers.Voxels.VoxelsList != null)
                    foreach (var v in covers.Voxels.VoxelsList)
                    {
                        if (v.DoorLinksIds != null && v.DoorLinksIds.Count > 0) idCells++;
                        if (v.DoorLinks != null && v.DoorLinks.Count > 0) linkedCells++;
                    }
                var links = UnityEngine.Object.FindObjectsOfType<NavMeshDoorLink>();
                int withDoor = 0, carved = 0;
                var ids = new HashSet<int>();
                foreach (var l in links)
                {
                    if (l.Door != null) withDoor++;
                    if (l.Carver_Opened != null) carved++;
                    ids.Add(l.Id);
                }
                var bdc = UnityEngine.Object.FindObjectOfType<BotDoorsController>();
                var listField = AccessTools.Field(typeof(BotDoorsController), "_navMeshDoorLinks")?.GetValue(bdc) as List<NavMeshDoorLink>;
                Plugin.Log.LogDebug($"[DoorDiag] cells: {idCells} with ids -> {linkedCells} reconnected | links: {links.Length} in scene, {ids.Count} distinct ids, {withDoor} door-matched, {carved} carved | controller list: {(listField != null ? listField.Count.ToString() : "null")}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[DoorDiag] failed: {e.Message}"); }
        }

        // the retail shadow split: maps ship dedicated low-poly _SHADOW_ proxy meshes;
        // visual meshes cast NOTHING, proxies cast ONLY. if the rip left both casting,
        // every shadowed light draws the shadow geometry twice. one sweep at load
        // restores the division and reports what it changed — zero-count = rip was fine.
        internal static void EnforceShadowProxies()
        {
            if (!Plugin.ShadowProxyFix.Value) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int proxiesFixed = 0, visualsFixed = 0;
            var shadowParents = new HashSet<Transform>();
            var all = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
            foreach (var r in all)
            {
                if (r == null || !r.name.Contains("SHADOW")) continue;
                // NEVER players: character bodies carry their own shadow meshes, and in
                // fika the observed copies of already-spawned bots EXIST at sweep time —
                // flipping their body renderers cast-only made them invisible until a
                // hit rebuilt the part (07-28 coop: floating-gear rogues, knight fine
                // because he spawned after the sweep)
                if (r.GetComponentInParent<Player>() != null) continue;
                if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
                {
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                    proxiesFixed++;
                }
                if (r.transform.parent != null) shadowParents.Add(r.transform.parent);
            }
            foreach (var r in all)
            {
                if (r == null || r.name.Contains("SHADOW")) continue;
                if (r.GetComponentInParent<Player>() != null) continue;
                if (r.transform.parent == null || !shadowParents.Contains(r.transform.parent)) continue;
                if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
                {
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    visualsFixed++;
                }
            }
            Plugin.Log.LogDebug($"[Perf] shadow split: {proxiesFixed} proxies set cast-only, {visualsFixed} visuals set no-cast ({sw.ElapsedMilliseconds}ms over {all.Length} renderers)");
        }

        private static readonly System.Reflection.FieldInfo _gripsField =
            AccessTools.Field(typeof(EFT.Interactive.WorldInteractiveObject), "Grips");
        private static readonly System.Reflection.FieldInfo _proxyStandField =
            AccessTools.Field(typeof(InteractiveProxy), "_interactionPosition");
        private static readonly System.Reflection.FieldInfo _proxyViewField =
            AccessTools.Field(typeof(InteractiveProxy), "_viewTarget");

        // retail WORLD-SPACE swipe stand/view points per swiper (extract_proxy_standpoints.py,
        // full retail TRS chain). the character's stand spot is proxy.TransformPoint(
        // _interactionPosition) — the rip's proxy ROTATIONS are untrustworthy (same export
        // pass mangled the lightplanes) so a mirrored basis slides the player to the wrong
        // side of the door. positions verified <0.5m, so re-deriving the LOCAL offset from
        // retail world truth self-corrects any rotation error. [0]=stand, [1]=view target.
        private static readonly Dictionary<string, Vector3[]> _proxyStandWorld = new Dictionary<string, Vector3[]>
        {
            { "security_pass_card_captain", new[] { new Vector3(-6.295f, 34.499f, 28.319f), new Vector3(-5.504f, 35.899f, 28.299f) } },
            { "security_pass_card_lab", new[] { new Vector3(11.604f, 18.377f, 40.545f), new Vector3(11.634f, 19.777f, 41.336f) } },
            { "security_pass_card_living_01", new[] { new Vector3(-6.617f, 23.68f, 31.971f), new Vector3(-7.408f, 25.08f, 32.011f) } },
            { "security_pass_card_living_02", new[] { new Vector3(6.622f, 23.68f, 33.369f), new Vector3(7.413f, 25.08f, 33.329f) } },
            { "security_pass_card_living_03", new[] { new Vector3(6.622f, 23.68f, 25.211f), new Vector3(7.413f, 25.08f, 25.171f) } },
            { "security_pass_card_living_04", new[] { new Vector3(-6.617f, 26.342f, 31.499f), new Vector3(-7.408f, 27.742f, 31.539f) } },
            { "security_pass_card_living_05", new[] { new Vector3(-6.617f, 26.342f, 41.282f), new Vector3(-7.408f, 27.742f, 41.322f) } },
            { "security_pass_card_living_06", new[] { new Vector3(6.622f, 26.342f, 24.742f), new Vector3(7.413f, 27.742f, 24.701f) } },
            { "security_pass_card_living_07", new[] { new Vector3(-6.619f, 29.06f, 24.843f), new Vector3(-7.41f, 30.46f, 24.883f) } },
            { "security_pass_card_living_08", new[] { new Vector3(6.622f, 29.06f, 38.964f), new Vector3(7.413f, 30.46f, 38.924f) } },
            { "security_pass_card_tech", new[] { new Vector3(11.298f, 15.552f, 26.862f), new Vector3(10.507f, 16.862f, 26.892f) } },
        };

        internal static void HealKeycardProxies()
        {
            var doors = UnityEngine.Object.FindObjectsOfType<EFT.Interactive.KeycardDoor>(true);
            var proxies = UnityEngine.Object.FindObjectsOfType<InteractiveProxy>(true);
            if (doors.Length == 0) return;
            int linked = 0, wired = 0, orphans = 0, standFixed = 0;
            foreach (var p in proxies)
            {
                // stand/view correction from retail world truth (see table above). logs how
                // far the rip-basis point was off — >0.5m means the rip rotation really was
                // mirrored/mangled for that swiper.
                var swiper = p.transform.parent != null ? p.transform.parent.name : "";
                if (_proxyStandWorld.TryGetValue(swiper, out var world) && _proxyStandField != null)
                {
                    var oldLocal = (Vector3)_proxyStandField.GetValue(p);
                    float drift = Vector3.Distance(p.transform.TransformPoint(oldLocal), world[0]);
                    _proxyStandField.SetValue(p, p.transform.InverseTransformPoint(world[0]));
                    _proxyViewField?.SetValue(p, p.transform.InverseTransformPoint(world[1]));
                    standFixed++;
                    if (drift > 0.25f)
                        Plugin.Log.LogDebug($"[Keycard] '{swiper}' stand point was {drift:F2}m off retail — rip transform confirmed bad, corrected");
                }

                if (p.Link != null) continue;
                EFT.Interactive.KeycardDoor best = null; float bestD = 2f;
                foreach (var d in doors)
                {
                    float dist = Vector3.Distance(p.transform.position, d.transform.position);
                    if (dist < bestD) { bestD = dist; best = d; }
                }
                if (best != null) { p.Link = best; linked++; }
            }
            int gripped = 0;
            foreach (var d in doors)
            {
                if (d.Proxies == null || d.Proxies.Length == 0)
                {
                    var mine = new List<InteractiveProxy>();
                    foreach (var p in proxies)
                        if (ReferenceEquals(p.Link, d)) mine.Add(p);
                    if (mine.Count > 0) d.Proxies = mine.ToArray();
                    else { orphans++; continue; } // GetHandle would still IOOR here — but at least we named it
                }
                wired++;
                // the swipe stand point only engages when door.Grips contains the swiper's
                // KeyGrip (DoorState=Locked): GetClosestGrip picks it, method_12 matches the
                // proxy, and the interaction uses the proxy stand point. without it the code
                // falls back to the BASE door stand (interactPosition1) — the slid-to-the-
                // door's-edge swipe. KeycardDoor.OnEnable concats proxy.Grips into door.Grips
                // natively, but only if proxy.Grips survived the bundle — verify containment
                // and append what's missing. base OnEnable pre-seeds the door's OWN child
                // grips, so 'non-empty' proves nothing.
                if (_gripsField != null)
                {
                    var cur = new List<GripPose>((_gripsField.GetValue(d) as GripPose[]) ?? new GripPose[0]);
                    bool changed = false;
                    foreach (var p in d.Proxies)
                    {
                        if (p == null) continue;
                        if (p.Grips == null || p.Grips.Length == 0)
                            p.Grips = p.GetComponentsInChildren<GripPose>(true); // bundle lost them — they live on proxy/Lock/KeyGrip
                        foreach (var g in p.Grips)
                            if (g != null && !cur.Contains(g)) { cur.Add(g); changed = true; }
                    }
                    if (changed) { _gripsField.SetValue(d, cur.ToArray()); gripped++; }
                }
            }
            Plugin.Log.LogDebug($"[Keycard] {doors.Length} doors: {wired} with proxies ({linked} Link(s) healed by proximity), {gripped} given swiper grips, {standFixed} stand points set from retail, {orphans} orphan(s){(orphans > 0 ? " — swipe on those will crash" : "")}");
        }

        private static void Prefix()
        {
            if (!IceGate.On) return; // audit P0: these repairs must never touch vanilla maps
            LateWaypointsPatch.Apply(); // by now every plugin assembly is loaded
            if (UnityEngine.Object.FindObjectOfType<AIStationaryController>() == null)
            {
                new GameObject("Icebreaker_AIStationary_Fix").AddComponent<AIStationaryController>();
                Plugin.Log.LogWarning("[RaidFix] created missing AIStationaryController (no stationary weapons on map)");
            }

            // ObservedCullingManager is a scene component on retail maps (missing here) that
            // drives bot BODY visibility: bots register a culling sphere with it; without it
            // the visibility event never fires, Auto mode resolves invisible, and every bot
            // body is forceRenderingOff until damage forces a refresh (the floating-gear
            // ghosts). must exist BEFORE any bot spawns — hence this prefix, not stage 2.
            if (!Comfort.Common.Singleton<ObservedCullingManager>.Instantiated)
            {
                new GameObject("Icebreaker_ObservedCullingManager_Fix").AddComponent<ObservedCullingManager>();
                Plugin.Log.LogWarning("[RaidFix] created missing ObservedCullingManager (bot body visibility)");
            }
        }
    }

    [HarmonyPatch(typeof(AIStationaryController), "Init")]
    internal static class Patch_StationaryInit
    {
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!IceGate.On) return __exception; // vanilla maps keep their real exceptions
            Plugin.Log.LogWarning($"[RaidFix] swallowed AIStationaryController.Init: {__exception.Message}");
            return null;
        }
    }

    // the server spawn merge Destroy()s scene markers it doesn't recognize, but BotZone's
    // authored SpawnPointMarkers list keeps the stale refs — BotZone.get_SpawnPoints then
    // NREs mapping marker.SpawnPoint (and it's a computed property, so swallowing Init
    // wouldn't save later callers). prune dead/null-SpawnPoint entries once, before Init
    // uses the list; every downstream consumer (SpawnPoints, CenterOfSpawnPoints, patrol
    // checks) sees a clean list. try/catch per-entry because destroyed unity objects can
    // throw from property getters.
    [HarmonyPatch(typeof(BotZone), "Init")]
    internal static class Patch_BotZonePruneMarkers
    {
        private static void Prefix(BotZone __instance)
        {
            if (!IceGate.On) return; // vanilla zones are healthy — dont touch their lists
            var list = __instance.SpawnPointMarkers;
            if (list == null)
            {
                __instance.SpawnPointMarkers = new List<SpawnPointMarker>();
                return;
            }
            int removed = list.RemoveAll(m =>
            {
                if (m == null) return true; // unity fake-null catches destroyed markers
                try { return m.SpawnPoint == null; }
                catch { return true; }
            });
            if (removed > 0)
                Plugin.Log.LogDebug($"[RaidFix] pruned {removed} dead spawn markers from BotZone '{__instance.name}' ({list.Count} left)");
        }
    }

    // bot door graph refresh — walks doors against the (empty) covers graph. bots-off, so
    // a failed refresh costs nothing.
    [HarmonyPatch(typeof(BotDoorsController), "RefreshData")]
    internal static class Patch_BotDoorsRefresh
    {
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!IceGate.On) return __exception;
            Plugin.Log.LogWarning($"[RaidFix] swallowed BotDoorsController.RefreshData: {__exception.Message}");
            return null;
        }
    }

    // DrakiaXYZ Waypoints postfixes BotsController.Init to link doors into its navmesh
    // graph, and NREs on our map's door/nav state — killing the raid AFTER all of BSG's
    // own init succeeded. swallow their postfix. can't use [HarmonyPatch]+Prepare for this:
    // our plugin loads BEFORE Waypoints (chainloader order), so TypeByName finds nothing at
    // startup. instead this is applied lazily from the BotsController.Init prefix above,
    // when every plugin assembly is guaranteed loaded. bots-off makes door links moot, and
    // BOT-INIT FIREWALL — the client half of the choke-point defense. BotsController.Init
    // sits on the raid-start critical path, and OTHER MODS' postfixes on it run inside the
    // same call: ORBIT's waypoint table threw a KeyNotFoundException here and aborted raid
    // creation outright (error screen, no spawn). the per-mod patch for that was removed
    // by policy; this is the mod-agnostic replacement: on OUR map only, any exception
    // escaping Init is swallowed WITH THE CULPRIT NAMED, so an unknown mod's missing-map
    // table degrades that mod's feature instead of killing the raid. vanilla maps keep
    // their exceptions — masking another map's failures is not this mod's business.
    [HarmonyPatch(typeof(BotsController), "Init")]
    internal static class Patch_BotsInitFirewall
    {
        private static void Prefix() => RaidFirewall.WrapForeignPostfixes(
            AccessTools.Method(typeof(BotsController), "Init"));

        private static Exception Finalizer(Exception __exception)
            => RaidFirewall.Swallow(__exception, "BotsController.Init");
    }

    // GAME-START FIREWALL — same defense, second choke point. GameWorld.OnGameStarted is
    // the other raid-start critical section mods postfix; unlike BotsController.Init an
    // exception here climbs the TarkovApplication async chain, faults it with "Local game
    // matching failed" and pops the error dialog MID-RAID while EFT half-tears the game
    // down under you (repro 2026-08-03: SkillsExtended's lockpicking table had no
    // 'Suburbs' entry — KeyNotFound after spawn, then SAIN/DynamicMaps died in the
    // shrapnel). BSG's own body runs before the postfixes, so swallowing only costs the
    // throwing mod (and any postfix queued after it) their game-start hook on our map.
    [HarmonyPatch(typeof(GameWorld), "OnGameStarted")]
    internal static class Patch_GameStartFirewall
    {
        private static void Prefix() => RaidFirewall.WrapForeignPostfixes(
            AccessTools.Method(typeof(GameWorld), "OnGameStarted"));

        private static Exception Finalizer(Exception __exception)
            => RaidFirewall.Swallow(__exception, "GameWorld.OnGameStarted");
    }

    // SOUND-PIPELINE AIRBAG — BotEventHandler.PlaySound runs SYNCHRONOUSLY inside
    // whatever made the sound: the player's footstep code, the keycard-swipe
    // interaction, a weapon routine. any dangling reference in the bot hearing graph
    // (a destroyed bot someone forgot to unhook — our old raw-despawn trim did exactly
    // that; other mods can too) makes EVERY player sound throw, and the exception
    // tears the CALLING player system mid-frame: controller flip-outs, interactions
    // stuck in slow stutterstep (08-04 raid, 415 exceptions). the sound is lost;
    // the player's action must not be.
    [HarmonyPatch(typeof(BotEventHandler), nameof(BotEventHandler.PlaySound))]
    internal static class Patch_PlaySoundAirbag
    {
        private static float _lastLog;

        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!IceGate.On) return __exception;
            if (Time.unscaledTime - _lastLog > 30f)
            {
                _lastLog = Time.unscaledTime;
                Plugin.Log.LogWarning($"[RaidFix] swallowed a bot-hearing exception (dangling despawned-bot ref in the sound graph) — player actions protected. inner: {__exception.Message}");
            }
            return null;
        }
    }

    // shared machinery for the raid-start choke points above
    internal static class RaidFirewall
    {
        private const string OwnPrefix = "com.manimal.icebreaker";
        private static readonly HashSet<System.Reflection.MethodBase> Wrapped = new();

        // PER-POSTFIX AIRBAG. a postfix that throws aborts every postfix queued after it
        // in the same chain — so one mod's missing-map table would cost innocent mods
        // their hook too. instead of letting the chain break, each foreign postfix method
        // on the choke point gets its OWN finalizer: the throw is swallowed at that
        // postfix's level and the chain continues. no argument re-binding, __state-safe
        // (we never invoke anything ourselves — harmony's own call just gets an airbag).
        // runs from the choke point's prefix so every plugin has patched by then; cheap
        // set-diff per raid catches late patchers. inert off-map (finalizer checks
        // IceGate). the outer Swallow stays as backstop for prefix throws and any postfix
        // the jit inlined past our detour.
        internal static void WrapForeignPostfixes(System.Reflection.MethodBase chokePoint)
        {
            // sweep only on our map — vanilla raids never touch other mods' methods.
            // wraps persist into later raids, but the airbag is IceGate-inert there.
            if (chokePoint == null || !IceGate.On) return;
            try
            {
                var info = Harmony.GetPatchInfo(chokePoint);
                if (info?.Postfixes == null) return;
                var h = new Harmony(OwnPrefix + ".postfixairbag");
                var airbag = new HarmonyMethod(AccessTools.Method(typeof(RaidFirewall), nameof(PostfixAirbag)));
                foreach (var p in info.Postfixes)
                {
                    if (p.owner.StartsWith(OwnPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Wrapped.Add(p.PatchMethod)) continue;
                    try
                    {
                        h.Patch(p.PatchMethod, finalizer: airbag);
                        Plugin.Log.LogDebug($"[Firewall] airbag on {p.PatchMethod.DeclaringType?.FullName}.{p.PatchMethod.Name} (owner {p.owner})");
                    }
                    catch (Exception e)
                    {
                        // unwrappable postfix (generic/inlined/weird) — outer backstop still covers it
                        Plugin.Log.LogDebug($"[Firewall] couldnt wrap {p.PatchMethod.Name} of {p.owner}: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Firewall] postfix sweep failed on {chokePoint.Name}: {e.Message}");
            }
        }

        private static Exception PostfixAirbag(Exception __exception, System.Reflection.MethodBase __originalMethod)
        {
            if (__exception == null) return null;
            if (!IceGate.On) return __exception;
            var asm = __originalMethod?.DeclaringType?.Assembly.GetName().Name ?? "unknown";
            Plugin.Log.LogError($"[Firewall] {asm}'s hook {__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name} "
                + $"threw on the icebreaker — swallowed; only THAT mod's hook is skipped, later mods' hooks still run "
                + $"(likely a per-map table with no 'Suburbs' entry — report it to {asm}'s author). inner: {__exception.Message}");
            return null;
        }

        // choke-point backstop: swallow-and-name-the-culprit for anything the airbags missed
        internal static Exception Swallow(Exception ex, string site)
        {
            if (ex == null) return null;
            if (!IceGate.On) return ex;
            string culprit = "unknown";
            foreach (var line in (ex.StackTrace ?? "").Split('\n'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line,
                    @"at (?!EFT\.|System\.|UnityEngine\.|Manimal\.|DMD|\(wrapper)([A-Za-z_][\w]*)[\w.]*\.");
                if (m.Success) { culprit = m.Groups[1].Value; break; }
            }
            Plugin.Log.LogError($"[Firewall] a mod threw inside {site} on the icebreaker — "
                + $"swallowed so the raid can start; that mod is INACTIVE this raid. culprit: {culprit} "
                + $"(likely a per-map table with no 'Suburbs' entry — report it to that mod's author). "
                + $"inner: {ex.Message}");
            return null;
        }
    }

    // Waypoints stays fully functional on real maps. no-op if Waypoints isn't installed.
    internal static class LateWaypointsPatch
    {
        private static bool _done;

        internal static void Apply()
        {
            if (_done) return;
            _done = true;
            var t = AccessTools.TypeByName("DrakiaXYZ.Waypoints.Patches.DoorLinkPatch");
            if (t == null) return; // waypoints not installed
            var target = AccessTools.Method(t, "PatchPostfix");
            if (target == null)
            {
                Plugin.Log.LogWarning("[RaidFix] Waypoints DoorLinkPatch found but PatchPostfix missing — layout changed?");
                return;
            }
            new Harmony("com.manimal.aidatadumper.raidfix-late").Patch(target,
                finalizer: new HarmonyMethod(typeof(LateWaypointsPatch), nameof(SwallowFinalizer)));
            Plugin.Log.LogDebug("[RaidFix] late-patched Waypoints DoorLinkPatch with finalizer");
        }

        private static bool _stackLogged;
        private static Exception SwallowFinalizer(Exception __exception)
        {
            // once installed this finalizer lives on Waypoints' patch for the whole
            // session — vanilla raids after an icebreaker raid keep their exceptions
            if (__exception != null && !IceGate.On) return __exception;
            if (__exception != null)
            {
                // Waypoints' per-Door link generation is now our PRIMARY bot-door path
                // (retail links parse clean but 0.16.9 bots ghost through them) — if it
                // fails we need the exact line, not just the message
                if (!_stackLogged)
                {
                    _stackLogged = true;
                    Plugin.Log.LogWarning($"[RaidFix] Waypoints DoorLinkPatch threw (full stack, once): {__exception}");
                }
                else
                    Plugin.Log.LogWarning($"[RaidFix] swallowed Waypoints DoorLinkPatch: {__exception.Message}");
            }
            return null;
        }
    }

    // opening a door emits its state-change triggers via GClass3592.Instance.Emit — a
    // quest/event singleton that's a dead shell on our backported map, so every door
    // interaction NREs in WorldInteractiveObject.method_3. the door still opens (the NRE is
    // after the swing); swallow the trigger emit — our map has no quest triggers to fire.
    [HarmonyPatch(typeof(WorldInteractiveObject), "method_3")]
    internal static class Patch_DoorTriggerEmit
    {
        // vanilla maps have LIVE quest/event trigger singletons — masking their emit
        // exceptions would silently break quests (audit P0 family)
        private static Exception Finalizer(Exception __exception)
            => __exception == null || IceGate.On ? null : __exception;
    }

    // bot activation (BotOwner.method_10) swallows its exception INTERNALLY (try/catch ->
    // silent ActiveFail statues), so a finalizer on method_10 itself sees nothing. instead
    // witness every SUBSYSTEM the chain calls: finalizer-patch the Activate method(s) of
    // every BotOwner property type — the throwing step logs itself before the internal
    // catch eats it. inert on success, exception passes through unchanged.
    [HarmonyPatch]
    internal static class Patch_BotActivationWitness
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            // cover EVERYTHING method_10 calls: Activate() overloads AND the Init/
            // InitPoints steps, on PROPERTY- and FIELD-typed members alike (the old
            // property-Activate-only net provably missed Bot6's ActiveFail step), plus
            // BotOwner's own method_2/method_11 sub-steps.
            var stepNames = new HashSet<string> { "Activate", "Init", "InitPoints" };
            var seen = new HashSet<MethodBase>();
            var memberTypes = new List<System.Type>();
            foreach (var prop in typeof(BotOwner).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                memberTypes.Add(prop.PropertyType);
            foreach (var fld in typeof(BotOwner).GetFields(BindingFlags.Public | BindingFlags.Instance))
                memberTypes.Add(fld.FieldType);
            // enumerate CONCRETE implementations too: several members (Brain, Mover...)
            // are typed as abstract bases whose Activate is abstract — enumerating the
            // declared type finds only the (unpatchable) abstract slot while the real
            // override lives on a subclass. sweep the assembly for every type assignable
            // to any member type and take its DECLARED step methods.
            var baseTypes = new List<System.Type>();
            foreach (var t in memberTypes)
                if (t.IsClass && t != typeof(string)) baseTypes.Add(t);
            System.Type[] all;
            try { all = typeof(BotOwner).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { all = System.Array.FindAll(e.Types, x => x != null); }
            foreach (var t in all)
            {
                if (!t.IsClass || t.IsGenericTypeDefinition) continue;
                bool relevant = false;
                foreach (var bt in baseTypes)
                    if (bt.IsAssignableFrom(t)) { relevant = true; break; }
                if (!relevant) continue;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!stepNames.Contains(m.Name) || m.IsAbstract || m.IsGenericMethod) continue;
                    if (m.GetMethodBody() == null) continue;
                    if (seen.Add(m)) yield return m;
                }
            }
            foreach (var name in new[] { "method_2", "method_11" })
            {
                var m = AccessTools.Method(typeof(BotOwner), name);
                if (m != null && !m.IsGenericMethod && seen.Add(m)) yield return m;
            }
        }

        private static Exception Finalizer(Exception __exception, object __instance, MethodBase __originalMethod)
        {
            // diagnostic only, and only ours: a vanilla-map bot throwing here is BSG's
            // business, not something this mod should be shouting about
            if (__exception != null && IceGate.On && Plugin.DiagHotkeys.Value)
                Plugin.Log.LogError($"[BotWitness] {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} FAILED: {__exception}");
            return __exception;
        }
    }

    // THE activation fix + diagnosis: BSG's method_10 aborts the ENTIRE activation on
    // the first throw (internal catch -> silent ActiveFail statue), and the throwing
    // step evaded every witness net (not a patchable call — likely a lazy property
    // getter or an unlisted call). so on the icebreaker we run the mirrored step list
    // ourselves: each step individually guarded, failures logged BY NAME with stacks,
    // and activation continues — one broken subsystem no longer statues the bot.
    // mirrors 0.16.9 method_10 exactly (order matters: BotState=Active lands mid-list
    // where BSG set it).
    [HarmonyPatch(typeof(BotOwner), "method_10")]
    internal static class Patch_BotActivationStepwise
    {
        private static readonly MethodInfo _m2 = AccessTools.Method(typeof(BotOwner), "method_2");
        private static readonly MethodInfo _m11 = AccessTools.Method(typeof(BotOwner), "method_11");
        private static readonly FieldInfo _activateTime = AccessTools.Field(typeof(BotOwner), "_activateTime");

        private static bool Prefix(BotOwner __instance)
        {
            if (!IceGate.On) return true; // vanilla maps: BSG's original behavior
            var b = __instance;
            int failed = 0;
            bool lastStepFailed = false;
            void Step(string name, Action a)
            {
                lastStepFailed = false;
                try { a(); }
                catch (Exception e)
                {
                    failed++;
                    lastStepFailed = true;
                    Plugin.Log.LogError($"[BotWitness] '{b.name}' step {name} FAILED: {e}");
                }
            }

            Step("VoxelesPersonalData", () => b.VoxelesPersonalData.Activate(b.BotsGroup.BotGame.BotsController.CoversData));
            Step("LookSensor", () => b.LookSensor.Activate());
            Step("Settings", () => b.Settings.Activate());
            Step("ExternalItemsController", () => b.ExternalItemsController.Activate());
            Step("ItemTaker", () => b.ItemTaker.Activate());
            Step("BewarePlantedMine", () => b.BewarePlantedMine.Activate());
            Step("EnemyChooser", () => b.EnemyChooser.Activate());
            Step("PlanDropItem", () => b.PlanDropItem.Activate());
            Step("MinesData", () => b.MinesData.Activate());
            Step("ItemDropper", () => b.ItemDropper.Activate());
            Step("SuppressStationary", () => b.SuppressStationary.Activate());
            Step("NavMeshCutterController", () => b.NavMeshCutterController.Activate());
            Step("BotFollower", () => b.BotFollower.Activate());
            Step("FriendlyTilt", () => b.FriendlyTilt.Activate());
            Step("RandomPlanItemDropper", () => b.RandomPlanItemDropper.Activate());
            Step("Tactic", () => b.Tactic.Activate());
            Step("EnemiesController", () => b.EnemiesController.Activate(b.BotsGroup.BotGame.BotsController.OnlineDependenceSettings.CanPersueAxeman));
            Step("HearingSensor", () => b.HearingSensor.Init());
            Step("LeaveData", () => b.LeaveData.Activate(b.BotsGroup.BotZone.Modifier.LeaveDist));
            Step("Receiver", () => b.Receiver.Init());
            Step("Mover", () => b.Mover.Activate());
            Step("BotTalk", () => b.BotTalk.Activate());
            Step("LoyaltyData", () => b.LoyaltyData.Activate());
            Step("AssaultDangerArea", () => b.AssaultDangerArea.Activate());
            Step("DangerArea", () => b.DangerArea.Activate());
            Step("BotPersonalStats", () => b.BotPersonalStats.Init(b, b.BotsGroup.BotZone.name));
            Step("StandBy.InitPoints", () => b.StandBy.InitPoints(b.BotsGroup.BotZone.Modifier.DistToActivate, b.BotsGroup.BotZone.Modifier.DistToSleep));
            Step("method_2", () => _m2.Invoke(b, null));
            Step("FlashGrenade", () => b.FlashGrenade.Activate());
            Step("PeaceHardAim", () => b.PeaceHardAim.Activate());
            Step("ShootData", () => b.ShootData.Activate());
            Step("PeaceLook", () => b.PeaceLook.Activate());
            Step("NearDoorData", () => b.NearDoorData.Activate());
            Step("AIData", () => b.AIData.Activate());
            Step("UnityEditorRunChecker", () => b.UnityEditorRunChecker.Activate());
            Step("NightVision", () => b.NightVision.Activate());
            Step("SearchData", () => b.SearchData.Activate());
            Step("Medecine", () => b.Medecine.Activate());
            b.BotState = EBotState.Active;
            Step("Memory", () => b.Memory.Activate());
            Step("SuppressShoot", () => b.SuppressShoot.Activate());
            Step("EatDrinkData", () => b.EatDrinkData.Activate());
            Step("SecondWeaponData", () => b.SecondWeaponData.Activate());
            Step("BotLay", () => b.BotLay.Activate());
            Step("SuppressGrenade", () => b.SuppressGrenade.Activate());
            Step("method_11", () => _m11.Invoke(b, null));
            Step("Brain", () => b.Brain.Activate());
            Step("PatrollingData", () => b.PatrollingData.Activate());
            Step("WeaponManager", () => b.WeaponManager.Activate());
            Step("BotFollower.TryFindBoss", () => b.BotFollower.TryFindBoss());
            try { _activateTime?.SetValue(b, Time.time); } catch { }

            if (failed > 0)
                Plugin.Log.LogError($"[BotWitness] '{b.name}' activated WITH {failed} failed step(s) — see above (BSG would have statued it)");
            return false; // original skipped — we ran the whole sequence
        }
    }
    // the raid-settings "amount of bots" slider rescales every wave's slots (Medium:
    // 0.5+(max-min)/2 — our tight 2..3 waves collapse to exactly 1 bot each). icebreaker's
    // rogue count is retail-authored, not a preference — skip the rescale for our waves
    // (gated by the suffixed BotZone* zone names only our map uses), keep the difficulty
    // and tagged&cursed behavior identical to the original.
    [HarmonyPatch(typeof(LocalGame), "smethod_7")]
    internal static class Patch_WaveSlotsAuthored
    {
        private static bool Prefix(WavesSettings wavesSettings, WildSpawnWave[] waves, ref WildSpawnWave[] __result)
        {
            // audit P0: the old gate was a naming-convention heuristic (every wave named
            // BotZone*) — identity now comes from the location id captured at smethod_6;
            // the wave-shape check remains only as secondary validation
            if (!IceGate.On) return true;
            bool ours = waves != null && waves.Length > 0;
            if (ours)
                foreach (var w in waves)
                    if (w.SpawnPoints == null || !w.SpawnPoints.StartsWith("BotZone") || w.SpawnPoints.Length <= "BotZone".Length)
                    { ours = false; break; }
            if (!ours) return true; // icebreaker but waves dont look authored — leave alone

            foreach (var w in waves)
            {
                if (wavesSettings.IsTaggedAndCursed && w.WildSpawnType == WildSpawnType.assault)
                    w.WildSpawnType = WildSpawnType.cursedAssault;
                // difficulty follows the player's raid-settings pick, same as any map
                w.BotDifficulty = wavesSettings.BotDifficulty.ToBotDifficulty();
            }
            Plugin.Log.LogDebug($"[RaidFix] wave slots kept as authored ({waves.Length} waves, bot-amount slider ignored)");
            __result = waves;
            return false;
        }
    }

    // the spawner resolves each bot's StartCorePoint from the spawn param's CorePointId —
    // our server params say 0 (no baked core ids exist on a backported map) so it arrives
    // null and every path request NREs (GoToPosition derefs StartCorePoint.ConnectionGroupId).
    // safety net: assign the nearest generated core at creation.
    [HarmonyPatch(typeof(BotOwner), nameof(BotOwner.Create))]
    internal static class Patch_BotStartCorePoint
    {
        private static void Postfix(BotOwner __result)
        {
            try
            {
                if (!IceGate.On) return; // vanilla maps have real baked core ids
                if (__result == null || __result.StartCorePoint != null) return;
                var covers = UnityEngine.Object.FindObjectOfType<AICoversData>();
                var cores = covers != null && covers.AICorePointsHolder != null ? covers.AICorePointsHolder.CorePoints : null;
                if (cores == null || cores.Count == 0) return;
                AICorePoint best = null;
                float bestD = float.MaxValue;
                var pos = __result.Transform != null ? __result.Transform.position : __result.GetPlayer.Transform.position;
                foreach (var c in cores)
                {
                    if (c == null) continue;
                    float d = (c.Position - pos).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = c; }
                }
                __result.StartCorePoint = best;
                Plugin.Log.LogDebug($"[RaidFix] assigned StartCorePoint {best?.Id} to '{__result.name}' ({Mathf.Sqrt(bestD):F1}m away)");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[RaidFix] StartCorePoint assign failed: {e.Message}"); }
        }
    }

    // spawned-player weapon procedural animation ticks every LateUpdate; MotionEffector
    // (one of the ProceduralWeaponAnimation effectors) NREs each frame on our map. that's
    // per-frame and non-fatal — Unity logs and continues — but it spams the log. swallow it;
    // losing one weapon-sway effector is invisible for a walk-around. (finalizer runs every
    // frame but is free when nothing throws.)
    [HarmonyPatch(typeof(MotionEffector), "FixedTracking")]
    internal static class Patch_MotionEffectorTick
    {
        // silent swallow ONLY on the icebreaker (per-frame, logging would flood);
        // vanilla maps keep their exceptions — this was masking every FixedTracking
        // throw everywhere (audit P0)
        private static Exception Finalizer(Exception __exception)
            => __exception == null || IceGate.On ? null : __exception;
    }
}
