using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.Quests;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // the smugglers' hovercraft on the Shoreline coast, and the transit it offers to the
    // icebreaker. the CRAFT is always there so the coast reads the same whatever your
    // progress; only the transit point is gated on finishing the Boreas chain, so before
    // then you can walk up to it and find nobody willing to take you anywhere.
    //
    // how transits work (verified against the assembly): TransitPoint MonoBehaviours are
    // BAKED INTO each scene, and at raid start TransitControllerAbstractClass.method_0
    // pairs them with the server's transits[] entries BY ID, dropping any marked inactive
    // and registering the rest. two consequences drive the design here:
    //   1. a server transit whose id has no point in the scene does nothing, so we supply
    //      the point ourselves.
    //   2. LocationScene.GetAllObjects reads lists cached at scene Awake, so a runtime
    //      point is invisible to that pairing pass. we therefore build ours AFTER the
    //      controller exists and register it by hand, which also lets us own its
    //      parameters outright instead of needing a server entry at all.
    // the transfer itself is not gated engine-side: summonedTransits is populated for
    // every player at raid start, so an active registered point just works.
    internal static class IcebreakerTransit
    {
        // GameWorld.LocationId is the SHORT id ("Shoreline", "Suburbs"), not the mongo
        // _Id — same string IceGate matches on. the transit TARGET below is the opposite:
        // vanilla transits address their destination by mongo _Id.
        private const string ShorelineLocation = "Shoreline";
        private const string IcebreakerTarget = "5714dc342459777137212e0b"; // the Suburbs slot
        // the DESTINATION as parameters.location, which is not a display name: on transit
        // the engine does TryGetLocationById(point.parameters.location), and that compares
        // against LocationSettings loc.Id. vanilla transits carry exactly this ("Lighthouse",
        // "bigmap"), and our slot's Id is "Suburbs". a name it can't resolve leaves
        // transitionStatus pointing at nothing and the raid just ends to the main menu.
        private const string IcebreakerLocationId = "Suburbs";
        private const string BoreasPart3 = "9d5e3f7d6320a7fd139a2772";     // chain capstone
        internal const int PointId = 900;                                    // ours, no vanilla clash

        private const string BundleKey = "manimal/hovercraft.bundle";
        private static readonly string[] AssetNames =
        {
            "assets/manimal/hovercraft/hovercraft.prefab",
            "hovercraft.prefab",
            "Hovercraft",
        };

        private static GameObject _prefab;
        private static Task<GameObject> _loadTask;

        // PROMPT ARRIVAL TAP (08-05 coop hunt): method_14 is the single funnel every
        // prompt goes through on every peer — on fika clients it runs when the host's
        // TransitInteractionEvent.Show arrives. logging it for OUR point only turns
        // "the prompt didn't come up" into "the Show did/didn't reach the client",
        // which splits the fault between host detection and client display.
        [HarmonyPatch(typeof(TransitInteractionControllerAbstractClass), "method_14")]
        internal static class Patch_PromptTap
        {
            private static void Postfix(int pointId, Player player)
            {
                if (pointId != PointId) return;
                TransitZoneExitWatch.Trace($"PROMPT_OFFERED to {player?.ProfileId} local={player?.IsYourPlayer}");
                Plugin.Log.LogDebug($"[Transit] PROMPT OFFERED for point {pointId} to '{player?.Profile?.Nickname}' (local={player?.IsYourPlayer})");
            }
        }

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_SpawnTransit
        {
            [HarmonyPostfix]
            private static void Postfix(GameWorld __instance)
            {
                try
                {
                    if (!Plugin.HovercraftTransit.Value) return;
                    if (__instance == null ||
                        !string.Equals(__instance.LocationId, ShorelineLocation, StringComparison.OrdinalIgnoreCase))
                        return;
                    __instance.gameObject.AddComponent<HovercraftSpawner>();
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Transit] arm failed: {e.Message}"); }
            }
        }

        // the bundle load is async, so the placement rides a coroutine rather than
        // blocking the raid-start postfix
        private class HovercraftSpawner : MonoBehaviour
        {
            private IEnumerator Start()
            {
                var world = Singleton<GameWorld>.Instance;
                var pos = new Vector3(Plugin.TransitX.Value, Plugin.TransitY.Value, Plugin.TransitZ.Value);
                var rot = Quaternion.Euler(Plugin.TransitRotX.Value, Plugin.TransitRotY.Value, Plugin.TransitRotZ.Value);

                var task = EnsureLoaded();
                while (!task.IsCompleted) yield return null;
                var prefab = task.Result;

                if (prefab != null)
                {
                    var craft = UnityEngine.Object.Instantiate(prefab, pos, rot);
                    craft.name = "Icebreaker_Hovercraft";
                    craft.SetActive(true);
                    // vanilla map, so the icebreaker-wide shader rebind never runs here.
                    // the ripped p0/* shaders render white in deferred, so this prop has
                    // to fix its own materials.
                    try { RenderEnvProbe.RebindShadersUnder(craft.transform); } catch { }
                    // report the scene + renderer count: a prop that instantiates into no
                    // scene, or with nothing to draw, is invisible for very different reasons
                    var scene = craft.scene.IsValid() ? craft.scene.name : "<none>";
                    int rends = craft.GetComponentsInChildren<Renderer>(true).Length;
                    Plugin.Log.LogDebug($"[Transit] hovercraft placed at {pos} rot {rot.eulerAngles} " +
                                          $"scene='{scene}' renderers={rends} active={craft.activeInHierarchy}");
                }
                else
                {
                    Plugin.Log.LogDebug("[Transit] hovercraft prefab unavailable, placing the transit without it");
                }

                BuildTransitPoint(world);
                Destroy(this);
            }
        }

        private static void BuildTransitPoint(GameWorld world)
        {
            try
            {
                // the point is ALWAYS built, even with the chain unfinished, so the zone
                // can answer for itself: walking in without the quests gets an access
                // denied toast and no prompt, which reads far better than an inert patch
                // of beach. see IcebreakerTransitFare for the gate.
                var controller = world != null ? world.TransitController : null;
                if (controller == null) { Plugin.Log.LogDebug("[Transit] no TransitController on Shoreline"); return; }

                // which controller we got decides whether any of this works. BaseLocalGame
                // only builds LocalGameTransitControllerClass when transitSettings.active is
                // set, and ONLY that constructor assigns OnPlayerEnter/OnPlayerExit. get a
                // different one and TransitPoint.OnTriggerEnter reaches a null delegate and
                // returns without a word, which looks exactly like the trigger not firing.
                Plugin.Log.LogDebug($"[Transit] controller={controller.GetType().Name} " +
                                      $"interaction={(controller is TransitInteractionControllerAbstractClass)} " +
                                      $"onEnter={(controller.OnPlayerEnter != null)} " +
                                      $"onExit={(controller.OnPlayerExit != null)} " +
                                      $"vanillaPoints={controller.Dictionary_0?.Count}");
                if (controller.Dictionary_0 != null && controller.Dictionary_0.ContainsKey(PointId)) return;

                var zonePos = new Vector3(Plugin.TransitZoneX.Value, Plugin.TransitZoneY.Value, Plugin.TransitZoneZ.Value);
                var go = new GameObject("Icebreaker_HovercraftTransit");
                go.transform.position = zonePos;
                go.transform.rotation = Quaternion.Euler(0f, Plugin.TransitZoneRotY.Value, 0f);

                var box = go.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.size = new Vector3(Plugin.TransitZoneSizeX.Value,
                                       Plugin.TransitZoneSizeY.Value,
                                       Plugin.TransitZoneSizeZ.Value);

                var point = go.AddComponent<TransitPoint>();
                // the panel runs all three through .Localized(), which returns the key
                // itself when there's no locale entry, so plain strings render verbatim.
                // vanilla uses keys (SHO_TRANSIT_24 -> "TRANSIT01"); ours are literal.
                point.parameters = new LocationSettingsClass.Location.TransitParameters
                {
                    id = PointId,
                    active = true,
                    name = "TRANSIT",
                    description = "Transit to Icebreaker",
                    conditions = $"You need to pay {Plugin.TransitCost.Value:N0} roubles",
                    activateAfterSec = Plugin.TransitActivateAfterSec.Value,
                    time = 20,
                    target = IcebreakerTarget,
                    location = IcebreakerLocationId,
                    events = false,
                };
                point.Enabled = true;
                // the smugglers aren't in position the second the raid starts. IsActive
                // false leaves it listed red with a countdown, and TransitPoint.Update
                // ticks activateAfterSec down each second, flips IsActive itself, then
                // replays OnPlayerEnter for anyone already stood in the zone. walking in
                // early gets the engine's own "not active yet" notice.
                point.IsActive = Plugin.TransitActivateAfterSec.Value <= 0;
                point.Controller = controller;
                controller.Dictionary_0[PointId] = point;

                // FIKA CLIENT ARMING (2026-08-04, headless repro: zone silent, no prompt,
                // no toast): the HOST arms every point in its dictionary at init, but a
                // fika CLIENT only arms points named in the host's TransitInitEvent —
                // which fika builds from Location.transitParameters, where this runtime
                // point never appears. armed state is per-player (timers = fika's
                // SetTimers = method_8; event/exit wiring = HandleExits = method_2), and
                // an unarmed point processes the trigger enter against missing state and
                // produces NOTHING. arm ours for the local player on every peer — both
                // calls are idempotent, and solo's already-armed flow is unchanged.
                try
                {
                    var me = world.MainPlayer;
                    if (me != null && controller is TransitInteractionControllerAbstractClass tic)
                    {
                        var one = new List<TransitPoint> { point };
                        tic.method_8(one, me, false);
                        tic.method_2(one, me);
                        TransitZoneExitWatch.Trace($"BUILD+ARM ctrl={controller.GetType().Name}#{controller.GetHashCode():X}");
                        Plugin.Log.LogDebug($"[Transit] point armed for the local player (controller={controller.GetType().Name})");
                    }
                    else
                        TransitZoneExitWatch.Trace($"BUILD unarmed (me={(me != null)}, ctrl={controller.GetType().Name})");
                }
                catch (Exception e)
                {
                    TransitZoneExitWatch.Trace($"BUILD arm FAILED: {e.Message}");
                    Plugin.Log.LogWarning($"[Transit] arming failed — the prompt may not show on fika clients: {e.Message}");
                }

                // leaving the zone has to cancel the interaction, and on this runtime-built
                // point Unity's OnTriggerExit was not arriving (the prompt survived walking
                // right away from an 8x3x3 box). rather than depend on it, watch the
                // distance ourselves and drive the same OnPlayerExit the engine would.
                go.AddComponent<TransitZoneExitWatch>().Bind(point, box);

                // the extract/transit list (double O) is built during raid init from the
                // points registered at that moment, so ours, registered afterwards, was
                // functional but unlisted. push it into the panel by hand. the row label
                // is TransitPoint.Description, which returns parameters.name.
                try
                {
                    var ui = MonoBehaviourSingleton<GameUI>.Instance;
                    if (ui != null && ui.TimerPanel != null)
                    {
                        ui.TimerPanel.SetTransits(new[] { point }, false);
                        Plugin.Log.LogDebug("[Transit] listed in the extract panel");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Transit] panel listing failed: {e.Message}"); }

                // log what the engine will actually act on, not a friendly name: the
                // destination being wrong is invisible until the raid ends in the menu
                Plugin.Log.LogDebug($"[Transit] transit live at {zonePos} ({box.size.x}x{box.size.y}x{box.size.z}m, " +
                                      $"yaw {Plugin.TransitZoneRotY.Value}) -> location='{point.parameters.location}' " +
                                      $"target='{point.parameters.target}' opens in {point.parameters.activateAfterSec}s");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Transit] point build failed: {e.Message}"); }
        }

        // OnTriggerExit is the engine's only way out of a transit interaction: TransitPoint
        // forwards it to TransitControllerAbstractClass.OnPlayerExit, which reaches
        // LocalGameTransitControllerClass.method_21 -> GroupExit + method_18, and method_18
        // is what nulls AvailableInteractionState and closes the timer panel. if that event
        // never lands the prompt and the countdown both stay up anywhere on the map. this
        // watcher supplies the same call from a distance test, so it does not matter why
        // the physics callback went missing. invoking it twice is harmless: GroupExit
        // early-returns for a profile it no longer holds and method_18 is idempotent.
        private class TransitZoneExitWatch : MonoBehaviour
        {
            private TransitPoint _point;
            private BoxCollider _box;

            internal void Bind(TransitPoint point, BoxCollider box)
            {
                _point = point;
                _box = box;
            }

            // a plain inside/outside test oscillates: the body transform sits near the box
            // edge and every frame it flips, and each flip fired an exit that wiped the
            // prompt method_14 had just put up. so arm only when properly inside the box,
            // and release only once clear of it by this much. no boundary can chatter
            // across a 4m gap.
            private const float ExitMargin = 4f;

            // per-player visit state — EVERY human, not just the local one. on a fika
            // host (headless included) the client's PROMPT only appears when the HOST
            // detects that client's observed body in the zone and sends
            // TransitInteractionEvent.Show (client method_22 -> method_14; the client
            // NEVER builds its own prompt — verified in GClass1906: no OnPlayerEnter ->
            // prompt wiring exists there). the old local-player-only watch meant the
            // headless never noticed anyone entering, so no Show was ever sent — zone
            // silent for every client (08-05 repro).
            private sealed class Visit { public bool Inside; public int DrivesLeft; public float NextDriveAt; public string LastGate; }

            private void TraceGate(Visit v, Player p, string gate)
            {
                if (v.LastGate == gate) return; // once per state change, not per frame
                v.LastGate = gate;
                Trace($"GATE-BLOCKED {Who(p)}: {gate}");
            }
            private readonly System.Collections.Generic.Dictionary<string, Visit> _visits
                = new System.Collections.Generic.Dictionary<string, Visit>();
            private readonly System.Collections.Generic.List<Player> _humans
                = new System.Collections.Generic.List<Player>();

            private void Update()
            {
                try
                {
                    if (_point == null || _box == null) return;
                    _humans.Clear();
                    FikaBridge.CollectHumans(_humans);
                    if (_humans.Count == 0)
                    {
                        var mp = Singleton<GameWorld>.Instance?.MainPlayer;
                        if (mp == null) return;
                        _humans.Add(mp);
                    }
                    foreach (var player in _humans)
                    {
                        if (player == null || string.IsNullOrEmpty(player.ProfileId)) continue;
                        WatchOne(player);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Transit] exit watch failed: {e.Message}");
                    enabled = false;
                }
            }

            // RESTART-PROOF TRACE (08-04): the headless auto-restarts and wipes its
            // BepInEx log before anyone can copy it — every transit-pipeline event also
            // appends to icebreaker_transit_trace.log next to the plugin dll, which
            // nothing deletes. tiny volume (zone events only), timestamped per line.
            private static string _tracePath;

            internal static void Trace(string line)
            {
                try
                {
                    _tracePath = _tracePath ?? System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                        "icebreaker_transit_trace.log");
                    System.IO.File.AppendAllText(_tracePath,
                        $"{DateTime.Now:MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
                }
                catch { }
            }

            private static string Who(Player p)
                => $"{p.ProfileId}{(p.IsYourPlayer ? "(local)" : $"('{p.Profile?.Nickname}')")}";

            private void WatchOne(Player player)
            {
                if (!_visits.TryGetValue(player.ProfileId, out var v))
                    _visits[player.ProfileId] = v = new Visit();

                // local space, so the zone's yaw is respected the way the collider is.
                // Position is the body transform (synced on observed remote bodies).
                var local = transform.InverseTransformPoint(player.Position) - _box.center;
                var half = _box.size * 0.5f;
                var outside = new Vector3(
                    Mathf.Max(0f, Mathf.Abs(local.x) - half.x),
                    Mathf.Max(0f, Mathf.Abs(local.y) - half.y),
                    Mathf.Max(0f, Mathf.Abs(local.z) - half.z));
                float gap = outside.magnitude;

                var controller = _point.Controller;
                // SELF-HEAL controller drift: fika constructs its transit controllers on
                // its own schedule — if the world now holds a DIFFERENT instance than the
                // one this point bound at build time, every drive lands on a dead object.
                // follow the world: rebind, re-register, re-arm.
                var worldCtrl = Singleton<GameWorld>.Instance?.TransitController;
                if (worldCtrl != null && !ReferenceEquals(controller, worldCtrl))
                {
                    Trace($"REBIND {(controller == null ? "NULL" : controller.GetType().Name)} -> {worldCtrl.GetType().Name}#{worldCtrl.GetHashCode():X}");
                    _point.Controller = worldCtrl;
                    if (!worldCtrl.Dictionary_0.ContainsKey(_point.parameters.id))
                        worldCtrl.Dictionary_0[_point.parameters.id] = _point;
                    try
                    {
                        var me = Singleton<GameWorld>.Instance?.MainPlayer;
                        if (me != null && worldCtrl is TransitInteractionControllerAbstractClass t2)
                        {
                            var one = new System.Collections.Generic.List<TransitPoint> { _point };
                            t2.method_8(one, me, false);
                            t2.method_2(one, me);
                        }
                    }
                    catch (Exception e) { Trace($"REBIND arm failed: {e.Message}"); }
                    controller = worldCtrl;
                }
                var interaction = controller as TransitInteractionControllerAbstractClass;

                if (!v.Inside)
                {
                    if (gap > 0f) return;
                    v.Inside = true;
                    // RETRYING drive (08-04, headless once-per-visit hunt): a single Show
                    // can get lost or eaten with no observable trace — retry a few times
                    // per visit. safe to repeat: method_14 just re-offers, the client
                    // toast is a SINGLETON notification (self-replacing, no stacking).
                    v.DrivesLeft = 4;
                    v.NextDriveAt = Time.time + 0.5f;
                    v.LastGate = null;
                    // worldCtrl from the self-heal above — after it, same=True is the norm
                    Trace($"ENTER {Who(player)} active={_point.IsActive} summoned={controller?.summonedTransits?.ContainsKey(player.ProfileId)} "
                        + $"ptCtrl={(controller == null ? "NULL" : controller.GetType().Name)}#{controller?.GetHashCode():X} "
                        + $"worldCtrl={(worldCtrl == null ? "NULL" : worldCtrl.GetType().Name)}#{worldCtrl?.GetHashCode():X} same={ReferenceEquals(controller, worldCtrl)}");
                    Plugin.Log.LogDebug(
                        $"[Transit] {(player.IsYourPlayer ? "player" : $"'{player.Profile?.Nickname}'")} inside the zone " +
                        $"(active={_point.IsActive}, opensIn={_point.parameters.activateAfterSec}s, " +
                        $"summoned={controller?.summonedTransits?.ContainsKey(player.ProfileId)})");
                    return;
                }

                if (gap > ExitMargin)
                {
                    v.Inside = false;
                    v.DrivesLeft = 0;
                    Trace($"EXIT {Who(player)} gap={gap:F1}");
                    // report what actually happened, not that we tried. OnPlayerExit is
                    // only wired up by the LocalGameTransitControllerClass constructor,
                    // so on a controller built any other way it is null and the earlier
                    // "interaction cleared" line was reporting a no-op as a success.
                    if (controller?.OnPlayerExit != null)
                    {
                        controller.OnPlayerExit(_point, player);
                        Plugin.Log.LogDebug($"[Transit] {player.ProfileId} {gap:F1}m clear, interaction cleared");
                    }
                    else
                    {
                        interaction?.method_18(player);
                        Plugin.Log.LogDebug($"[Transit] {player.ProfileId} {gap:F1}m clear, cleared directly (no OnPlayerExit)");
                    }
                    // 08-05 coop trace: fika 2.3.9's client exit chain does NOT clear
                    // AvailableInteractionState, and its enter chain won't re-offer while
                    // it's stale — the prompt showed exactly once per raid. method_18 IS
                    // the clear (state null + ForceInteractionsChanged + panel close) and
                    // it's idempotent, so force it for the local player on every exit.
                    if (player.IsYourPlayer && interaction != null)
                        interaction.method_18(player);
                    return;
                }

                // still inside. the engine's OnTriggerEnter is what normally reaches
                // method_14 / the host's Show-sender, and on this runtime point it does
                // not always arrive. drive OnPlayerEnter ourselves, retrying every 2.5s
                // up to the per-visit budget. the AvailableInteractionState no-double-up
                // gate only makes sense for the LOCAL player — it is the host's OWN
                // prompt UI state, and gating a REMOTE player's enter on it would starve
                // every client the moment the host player stands near a transit.
                // gate order matters — trace-solved 08-05: FikaHeadlessTransitController
                // (the fika-headless plugin's own class) derives from the BASE transit
                // controller, NOT the interaction subclass, and the old hard interaction
                // cast silently ate every drive on the headless. its OnPlayerEnter
                // delegate carries everything needed (access check + Show packet), so the
                // DELEGATE is the requirement — the interaction cast is only needed for
                // the direct-method_14 fallback and the local prompt-state gate.
                if (v.DrivesLeft <= 0) return;
                if (!_point.IsActive) { TraceGate(v, player, "point-inactive"); return; }
                if (Time.time < v.NextDriveAt) return;
                if (controller?.OnPlayerEnter == null && interaction == null)
                {
                    TraceGate(v, player, $"no-delegate-no-interaction (Controller={( _point.Controller == null ? "NULL" : _point.Controller.GetType().Name)})");
                    return;
                }
                if (player.IsYourPlayer && interaction != null && interaction.AvailableInteractionState != null)
                {
                    TraceGate(v, player, "local-state-already-set");
                    return;
                }

                v.DrivesLeft--;
                v.NextDriveAt = Time.time + 2.5f;
                Trace($"DRIVE {Who(player)} left={v.DrivesLeft} ctrl={controller.GetType().Name}");
                if (controller.OnPlayerEnter != null)
                {
                    controller.OnPlayerEnter(_point, player);
                    // demoted to Debug 08-05 once the headless-controller gate fix proved
                    // out — the trace file still records every drive
                    Plugin.Log.LogDebug($"[Transit] drove OnPlayerEnter for {player.ProfileId} "
                        + $"(local={player.IsYourPlayer}, ctrl={controller.GetType().Name}, "
                        + $"transitPlayer={controller.transitPlayers?.ContainsKey(player.ProfileId)})");
                }
                else
                {
                    // no delegate to go through, so build the offer ourselves. method_14
                    // is the same call OnPlayerEnter would land on, and it is what puts
                    // the prompt up and wires the interaction action.
                    interaction.method_14(_point.parameters.id, player, interaction.method_17());
                    Plugin.Log.LogDebug($"[Transit] no OnPlayerEnter delegate, offered the interaction directly for {player.ProfileId}");
                }
            }
        }

        private static Task<GameObject> EnsureLoaded()
        {
            if (_prefab != null) return Task.FromResult(_prefab);
            if (_loadTask != null) return _loadTask;
            _loadTask = LoadAsync();
            return _loadTask;
        }

        private static async Task<GameObject> LoadAsync()
        {
            try
            {
                if (!Singleton<IEasyAssets>.Instantiated)
                {
                    Plugin.Log.LogError("[Transit] IEasyAssets not up, cannot load the hovercraft bundle");
                    _loadTask = null;
                    return null;
                }

                var ea = Singleton<IEasyAssets>.Instance;
                await GClass1857.LoadBundles(ea.Retain(new[] { BundleKey }));

                if (!ea.IsAssetLoaded(BundleKey))
                {
                    Plugin.Log.LogError($"[Transit] bundle '{BundleKey}' failed to load");
                    _loadTask = null;
                    return null;
                }

                foreach (var name in AssetNames)
                {
                    var p = ea.GetAsset<GameObject>(BundleKey, name);
                    if (p != null)
                    {
                        Plugin.Log.LogInfo($"[Transit] hovercraft prefab loaded (asset='{name}')");
                        return _prefab = p;
                    }
                }

                // name drifted: dump the inventory so the fix is one log line away
                foreach (var ab in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (ab.name == null || ab.name.IndexOf("hovercraft", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Plugin.Log.LogDebug($"[Transit] === bundle '{ab.name}' inventory ===");
                    foreach (var n in ab.GetAllAssetNames()) Plugin.Log.LogDebug($"   {n}");
                    var gos = ab.LoadAllAssets<GameObject>();
                    if (gos.Length > 0)
                    {
                        Plugin.Log.LogWarning($"[Transit] fallback picked '{gos[0].name}'");
                        return _prefab = gos[0];
                    }
                }

                Plugin.Log.LogError("[Transit] no prefab found in the hovercraft bundle");
                _loadTask = null;
                return null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Transit] bundle load threw: {e.GetType().Name}: {e.Message}");
                _loadTask = null;
                return null;
            }
        }

        // the chain capstone is the gate: once transport is arranged the smugglers will
        // actually run the crossing. reads the raid profile rather than the server, so it
        // costs nothing and can't disagree with the player's own quest log.
        internal static bool ChainComplete()
            => ChainComplete(Singleton<GameWorld>.Instance);

        private static bool ChainComplete(GameWorld world)
        {
            try
            {
                var quests = world?.MainPlayer?.Profile?.QuestsData;
                if (quests == null) return false;
                return quests.Any(q => q != null && q.Id == BoreasPart3 && q.Status == EQuestStatus.Success);
            }
            catch { return false; }
        }
    }
}
