using System;
using System.Collections.Generic;
using System.Linq;
using SysIoPath = System.IO.Path;
using EFT;
using EFT.Interactive;
using Comfort.Common;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Icebreaker
{
    // resurrection #4: BSG's spawn-trigger layer (the T1-T4 tier triggers + the group-size
    // black division spawn triggers). recovered from retail level706 by
    // analysis/extract_aiplaces.py — see icebreaker_aiplaces.json. the mechanism (verified
    // in the decompile): AIPlaceInfo trigger box -> AIPlaceInfoLogic child raises
    // BotEventHandler.AnyEvent(name) on player entry -> BossSpawnScenario fires any
    // base.json BossLocationSpawn wave with TriggerName=botEvent + TriggerId=name. the
    // waves themselves are authored server-side (vanilla data) — this file only rebuilds
    // the scene trigger side.
    //
    // classes: AIPlaceInfo + AIPlaceLogicEventRaise exist in 0.16.9 -> rehydrated
    // faithfully. AIPlaceLogicEventRaiseByGroupSize is 1.0-only -> reimplemented below
    // (GroupSizeEventLogic) using the hand-decoded threshold tables.
    internal static class IcebreakerAIPlaces
    {
        private static JObject _sidecar;
        private static bool _sidecarTried;
        private static GameObject _marker;

        // events can fire the moment triggers exist (player spawns near the bow box —
        // hides0 fired 14 log-lines before the crew bridge armed and was LOST). so the
        // subscription happens HERE at trigger-build time, and events buffer until the
        // crew bridge attaches.
        internal static Action<string> Bridge;
        internal static readonly List<string> PendingEvents = new List<string>();

        private static void OnAnyEvent(string id)
        {
            var b = Bridge;
            if (b != null) b(id);
            else { PendingEvents.Add(id); Plugin.Log.LogWarning($"[AIPlaces] event '{id}' buffered (bridge not armed yet)"); }
        }

        public static void AttachBridge(Action<string> bridge)
        {
            Bridge = bridge;
            foreach (var id in PendingEvents) bridge(id);
            PendingEvents.Clear();
        }

        private static string DataDir =>
            SysIoPath.Combine(SysIoPath.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "aiplaces");

        private static JObject Sidecar()
        {
            if (_sidecarTried) return _sidecar;
            _sidecarTried = true;
            try
            {
                var path = SysIoPath.Combine(DataDir, "icebreaker_aiplaces.json");
                if (System.IO.File.Exists(path))
                    _sidecar = JObject.Parse(System.IO.File.ReadAllText(path));
                else
                    Plugin.Log.LogWarning($"[AIPlaces] no sidecar at {path} — spawn triggers stay off");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[AIPlaces] sidecar parse failed: {e.Message}"); }
            return _sidecar;
        }

        // called from the BotsController.Init postfix — CoversData + holder exist, bots
        // haven't spawned yet
        public static void TryBuild(BotsController controller)
        {
            if (_marker != null) return;
            var sc = Sidecar();
            if (sc == null || !IcebreakerAcoustics.IcebreakerLoaded()) return;

            try
            {
                var holderGo = GameObject.Find("AIPlaceInfoHolder");
                if (holderGo == null) { Plugin.Log.LogWarning("[AIPlaces] AIPlaceInfoHolder GO not in scene — aborting"); return; }

                // path index under the holder root
                var index = new Dictionary<string, Transform>();
                void Walk(Transform t, string p)
                {
                    index[p] = t;
                    for (int i = 0; i < t.childCount; i++)
                    {
                        var c = t.GetChild(i);
                        Walk(c, p + "/" + c.name);
                    }
                }
                Walk(holderGo.transform, "AIPlaceInfoHolder");

                // pass 1: the EventRaise logic components (children of the trigger GOs).
                // NOT the engine's AIPlaceLogicEventRaise — that one has zero player filter,
                // so bots trip the boxes during their spawn frame and burn the events at
                // raid start. TierEventLogic is the same thing gated on IsYourPlayer.
                var logicByRef = new Dictionary<long, AIPlaceInfoLogic>();
                foreach (var row in (sc["components"]?["AIPlaceLogicEventRaise"] as JArray) ?? new JArray())
                {
                    if (!index.TryGetValue(row.Value<string>("go") ?? "", out var t)) continue;
                    var stale = t.gameObject.GetComponent<AIPlaceLogicEventRaise>();
                    if (stale != null) UnityEngine.Object.Destroy(stale);
                    var lg = t.gameObject.GetComponent<TierEventLogic>() ?? t.gameObject.AddComponent<TierEventLogic>();
                    var f = row["fields"];
                    lg.EventName = f?.Value<string>("EventName") ?? "none";
                    lg.EnterRaise = (f?.Value<int?>("EnterRaise") ?? 1) != 0;
                    lg.ExitRaise = (f?.Value<int?>("ExitRaise") ?? 0) != 0;
                    logicByRef[row.Value<long>("path_id")] = lg;
                }

                // pass 2: the AIPlaceInfo boxes themselves
                var holder = controller.CoversData != null ? controller.CoversData.AIPlaceInfoHolder : null;
                if (holder == null) holder = UnityEngine.Object.FindObjectOfType<AIPlaceInfoHolder>();
                // Places is null on a runtime-created holder — AddPlace NREs on it and, worse,
                // ExUsecBrainClass foreaches it per decision tick (null = silent statue rogues)
                if (holder != null && holder.Places == null) holder.Places = new List<AIPlaceInfo>();
                var doors = UnityEngine.Object.FindObjectsOfType<Door>();
                int built = 0, evTriggers = 0, gsTriggers = 0;

                foreach (var row in (sc["components"]?["AIPlaceInfo"] as JArray) ?? new JArray())
                {
                    var goPath = row.Value<string>("go") ?? "";
                    if (!index.TryGetValue(goPath, out var t)) continue;
                    var go = t.gameObject;
                    if (go.GetComponent<BoxCollider>() == null) continue; // box died in rip — useless as trigger

                    var place = go.GetComponent<AIPlaceInfo>() ?? go.AddComponent<AIPlaceInfo>();
                    var f = row["fields"];
                    place.AreaId = f?.Value<int?>("AreaId") ?? 0;
                    place.Id = f?.Value<string>("Id") ?? "";
                    place.IsOnTrigger = (f?.Value<int?>("IsOnTrigger") ?? 1) != 0;
                    place.BlockGrenade = (f?.Value<int?>("BlockGrenade") ?? 0) != 0;
                    place.IsDark = (f?.Value<int?>("IsDark") ?? 0) != 0;
                    place.IsMute = (f?.Value<int?>("IsMute") ?? 0) != 0;
                    place.IsInside = (f?.Value<int?>("IsInside") ?? 0) != 0;
                    place.AtrZone = (f?.Value<int?>("AtrZone") ?? 0) != 0;
                    place.CoversSpecial = (ECoverPointSpecial)(f?.Value<int?>("CoversSpecial") ?? 0);
                    place.UseAsCoverGroupId = (f?.Value<int?>("UseAsCoverGroupId") ?? 1) != 0;
                    place.GrenadePlaces = Array.Empty<ThrowGrenadePlace>(); // retail's grenade-place class is 1.0-only
                    place.Collider = go.GetComponent<BoxCollider>();

                    // wire the logic: EventRaise if the ref resolved; else the group-size
                    // reimpl for the three BD spawn boxes (name-matched)
                    long logicRef = (f?["InfoLogicAllEnemy"] as JObject)?.Value<long?>("ref") ?? 0;
                    if (logicByRef.TryGetValue(logicRef, out var lg))
                    {
                        place.InfoLogicAllEnemy = lg;
                        evTriggers++;
                    }
                    else
                    {
                        var table = GroupSizeEventLogic.TableFor(goPath);
                        if (table != null)
                        {
                            var gs = go.GetComponent<GroupSizeEventLogic>() ?? go.AddComponent<GroupSizeEventLogic>();
                            gs.SetTable(table);
                            place.InfoLogicAllEnemy = gs;
                            gsTriggers++;
                        }
                    }

                    if (holder != null && (holder.Places == null || !holder.Places.Contains(place)))
                        holder.AddPlace(place);
                    place.Init(doors, controller);
                    built++;
                }

                // subscribe NOW — before any player movement can fire a trigger
                Bridge = null;
                PendingEvents.Clear();
                var handler = Singleton<BotEventHandler>.Instance;
                if (handler != null) handler.OnEvent += OnAnyEvent;
                else Plugin.Log.LogWarning("[AIPlaces] no BotEventHandler at build time — events may be lost");

                _marker = new GameObject("Icebreaker_AIPlacesStaged");
                var scn = SceneManager.GetSceneByName("Icebreaker_AI");
                if (scn.IsValid() && scn.isLoaded) SceneManager.MoveGameObjectToScene(_marker, scn);
                Plugin.Log.LogWarning($"[AIPlaces] SPAWN TRIGGERS REBUILT: {built} places ({evTriggers} tier-event, {gsTriggers} group-size BD) — events buffering until crew bridge attaches");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[AIPlaces] rebuild failed: {e}");
            }
        }
    }

    // the engine's AIPlaceLogicEventRaise minus its blind spot: it raises for ANY player
    // (bots included), ours only for the human. same EventName/EnterRaise/ExitRaise fields.
    public class TierEventLogic : AIPlaceInfoLogic
    {
        public string EventName = "none";
        public bool EnterRaise = true;
        public bool ExitRaise;
        private AIPlaceInfo _place;

        public override void Init(AIPlaceInfo aiPlaceInfo, BotsController botsController)
        {
            base.Init(aiPlaceInfo, botsController);
            _place = aiPlaceInfo;
            _place.OnPlayerEnter += OnEnter;
            _place.OnPlayerExit += OnExit;
        }

        public override void Dispose()
        {
            if (_place != null) { _place.OnPlayerEnter -= OnEnter; _place.OnPlayerExit -= OnExit; }
            base.Dispose();
        }

        private void OnEnter(Player p)
        {
            if (EnterRaise && p != null && p.IsYourPlayer)
                Singleton<BotEventHandler>.Instance?.AnyEvent(EventName);
        }

        private void OnExit(Player p)
        {
            if (ExitRaise && p != null && p.IsYourPlayer)
                Singleton<BotEventHandler>.Instance?.AnyEvent(EventName);
        }
    }

    // reimplements retail 1.0's AIPlaceLogicEventRaiseByGroupSize (class doesn't exist in
    // 0.16.9): on player entry, raise the event whose bracket matches the human group
    // size. tables hand-decoded from the retail bytes — BSG scales the black division
    // response to the party size. singleplayer always lands in the solo bracket.
    public class GroupSizeEventLogic : AIPlaceInfoLogic
    {
        private (int min, int max, string name)[] _table;
        private AIPlaceInfo _place;
        private bool _fired;

        public void SetTable((int, int, string)[] table) => _table = table;

        public static (int, int, string)[] TableFor(string goPath)
        {
            if (goPath.Contains("Sten")) return new[] { (0, 1, "stern0"), (2, 2, "stern1"), (3, 4, "stern2"), (5, 10, "stern3") };
            if (goPath.Contains("Hide")) return new[] { (0, 1, "hides0"), (2, 2, "hides1"), (3, 4, "hides2"), (5, 10, "hides3") };
            if (goPath.Contains("Wedge")) return new[] { (0, 1, "wedges1"), (2, 2, "wedges2"), (3, 4, "wedges3"), (5, 10, "wedges4") };
            return null;
        }

        public override void Init(AIPlaceInfo aiPlaceInfo, BotsController botsController)
        {
            base.Init(aiPlaceInfo, botsController);
            _place = aiPlaceInfo;
            _place.OnPlayerEnter += OnEnter;
        }

        public override void Dispose()
        {
            if (_place != null) _place.OnPlayerEnter -= OnEnter;
            base.Dispose();
        }

        private void OnEnter(Player player)
        {
            if (_fired || _table == null) return; // waves are one-shot (Activated flag) anyway
            // IsYourPlayer, not !IsAI: bots trip the boxes during their spawn frame (IsAI not
            // set yet / staging position) and burned the one-shots at raid start — the engine
            // squad was fully deployed on patrol before the player ever reached the trigger
            if (player == null || !player.IsYourPlayer) return;
            _fired = true;
            int size = GroupSize();
            foreach (var (min, max, name) in _table)
            {
                if (size >= min && size <= max)
                {
                    Plugin.Log.LogWarning($"[AIPlaces] '{gameObject.name}' entered (group={size}) -> event '{name}'");
                    Singleton<BotEventHandler>.Instance?.AnyEvent(name);
                    return;
                }
            }
        }

        private static int GroupSize()
        {
            try
            {
                var world = Singleton<GameWorld>.Instance;
                if (world == null) return 1;
                int n = 0;
                foreach (var p in world.RegisteredPlayers)
                    if (p != null && !p.IsAI && p.HealthController != null && p.HealthController.IsAlive)
                        n++;
                return Mathf.Max(1, n);
            }
            catch { return 1; }
        }
    }
}
