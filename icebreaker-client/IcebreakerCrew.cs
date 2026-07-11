using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // deterministic crew spawner for the icebreaker (speed-cola pattern): the game's wave
    // scenario under-delivers through a stack of silent gates (bot-amount slider rescale,
    // per-zone type blocks, spawn-point saturation) — instead of fighting each gate, count
    // what actually spawned and FORCE the remainder via BotSpawner.TryToSpawnInZoneInner
    // with a pre-picked point (bypasses the whole scenario/limit machinery). retail comp:
    // 10-15 rogues (config) + solo knight, distributed over the confirmed rogue zones.
    public class IcebreakerCrew : MonoBehaviour
    {
        private static readonly string[] RogueZones =
        {
            "BotZoneKitchen", "BotZoneFront", "BotZoneFront2", "BotZoneMash_t1", "BotZoneKorrDown1",
            "BotZoneRoom_Eng", "BotZoneRoom_Eng2",
        };

        private BotSpawner _spawner;
        private BotsController _controller;

        private void Start()
        {
            IcebreakerCutscene.ResetForRaid();
            IcebreakerChainDoor.ResetForRaid();
            IcebreakerFlares.ResetForRaid();
            StartCoroutine(Run());
            // DoorProbe retired: it nailed the MidOpen/MidClose bug and doors work now —
            // re-arm here if bots ever ghost doors again
            // StartCoroutine(DoorProbe());
        }

        private IEnumerator Run()
        {
            // there IS no wave scenario to defer to — base.json ships zero waves, every
            // bot is ours. the old 12s grace just delayed the crew's arrival.
            yield return new WaitForSeconds(2f);

            var botGame = Singleton<IBotGame>.Instance;
            _controller = botGame?.BotsController;
            _spawner = _controller?.BotSpawner;
            if (_spawner == null)
            {
                Plugin.Log.LogWarning("[Crew] no BotSpawner — crew spawner giving up");
                yield break;
            }

            // silent per-(zone,type) spawn blocks reject forced spawns too — off for this raid
            try { if (_controller.ZonesLeaveController != null) _controller.ZonesLeaveController.NoZoneBlocks = true; }
            catch { }

            var zones = CollectRogueZones();
            if (zones.Count == 0)
            {
                Plugin.Log.LogWarning("[Crew] no rogue zones found — crew spawner giving up");
                yield break;
            }

            // ALL the watchers/caches arm FIRST, before the top-up loop: last raid the
            // player rushed the cutscene box before the top-up finished and the watcher
            // wasn't running yet — the story beat just didn't fire. the cutscene flips
            // BdPhase which the top-up loop already respects mid-flight.
            if (Plugin.CrewBlackDiv.Value && Plugin.EventSpawns.Value)
            {
                StartCoroutine(PreMakeTriggerSquads());
                SubscribeEventSpawns();
                StartCoroutine(EngineAdvanceWatch());
            }

            // HYBRID crew (user call after comparing behaviors): the BSG boss scenario
            // spawns just 2 fireteams + the knight for instant raid-start presence, and
            // OUR spawner fills the rest immediately — force-spawned bots get clean
            // individual patrol brains, while boss-group bots drag follower-brain baggage
            // and camp their marker ("not really patrolling, getting stuck").
            yield return new WaitForSeconds(4f);

            int haveRogues = CountByRole(WildSpawnType.exUsec);
            bool haveKnight = CountByRole(WildSpawnType.bossKnight) > 0;
            // retail rolls a random crew size each raid — pick a target in [min,max]
            int lo = Mathf.Min(Plugin.CrewRoguesMin.Value, Plugin.CrewRoguesMax.Value);
            int hi = Mathf.Max(Plugin.CrewRoguesMin.Value, Plugin.CrewRoguesMax.Value);
            int wantRogues = UnityEngine.Random.Range(lo, hi + 1);
            Plugin.Log.LogWarning($"[Crew] present: {haveRogues} rogues, knight={haveKnight}; target {wantRogues} + knight={Plugin.CrewKnight.Value}");

            if (haveRogues > wantRogues)
                TrimRogues(haveRogues - wantRogues);
            // boss-group spawns stack leader+escorts on one marker (frozen conjoined
            // rogues) — and the wave lands STAGGERED, so a one-shot pass missed everyone
            // who arrived after it. patrol for the first few minutes instead.
            StartCoroutine(UnstackPatrol());

            // knight raid-start force REMOVED: retail base.json says he arrives via the
            // T1 trigger with 2 rogue escorts (SpawnKnightDetail) — never at raid start

            // fill from the wave's ~6 up to the rolled target with OUR spawner — batched
            // singles, burst activation, right at raid start
            int guard = 0;
            while (CountByRole(WildSpawnType.exUsec) < wantRogues && guard++ < 12 && !BdPhase)
            {
                int batch = Mathf.Min(4, wantRogues - CountByRole(WildSpawnType.exUsec));
                var zone = zones[UnityEngine.Random.Range(0, zones.Count)];
                var t = ForceSpawnBatch(WildSpawnType.exUsec, zone, batch);
                while (!t.IsCompleted) yield return null;
                yield return new WaitForSeconds(1.5f);
            }

            Plugin.Log.LogWarning($"[Crew] done: {CountByRole(WildSpawnType.exUsec)} rogues, knight={CountByRole(WildSpawnType.bossKnight) > 0}");

            // event-spawn mode (default): the resurrected retail trigger layer raises the
            // events (hides0/stern0/wedges1 + group-size ladder) — but delivery is OUR
            // force-spawner (the server BossLocationSpawn pipeline silently refused the
            // custom blackdiv roles: triggers fired, zero bots arrived). legacy watchers
            // stay as the EventSpawns=false fallback.
            // event-spawn mode armed everything up top (bridge + cutscene watcher +
            // premake); only the legacy fallback still starts here
            if (Plugin.CrewBlackDiv.Value && !Plugin.EventSpawns.Value)
            {
                StartCoroutine(EngineRoomWatch()); // parallel — its own glowstick-area trigger
                yield return BlackDivisionWatch();
            }
        }

        // ---- retail-event -> force-spawn bridge ----
        private Action _unsubEvents;
        private readonly HashSet<string> _firedEvents = new HashSet<string>();

        private void SubscribeEventSpawns()
        {
            // IcebreakerAIPlaces subscribed at trigger-build time and buffers anything
            // fired before we're ready (the hides0-lost-during-topup race) — attach the
            // bridge and drain the buffer
            IcebreakerAIPlaces.AttachBridge(OnSpawnEvent);
            _unsubEvents = () => IcebreakerAIPlaces.Bridge = null;
            Plugin.Log.LogWarning("[Crew] event-spawn bridge armed (buffered events drained)");
        }

        private void OnDestroy()
        {
            _unsubEvents?.Invoke();
        }

        // BD AI modifications (temperament rolls, mind rewires, hold/release staging,
        // forced rush) all REMOVED per user call 07-11 — the layered brain pokes kept
        // fighting each other ("bugging out"). black division runs vanilla brains now;
        // we only control WHERE and WHEN they spawn.

        // cultist-style pop-out: IBotGame.BotDespawn = BotDied bookkeeping + full AI
        // unregister + ReturnToPool on the GO. no death, no ragdoll, no loot. trims the
        // max-spawned wave crew down to the raid's rolled size. farthest-from-player
        // first, and nobody within 60m — a rogue vanishing in view would look broken.
        private static List<BotOwner> AliveRogues()
        {
            var list = new List<BotOwner>();
            foreach (var b in UnityEngine.Object.FindObjectsOfType<BotOwner>())
                if (b != null && b.Profile?.Info?.Settings?.Role == WildSpawnType.exUsec
                    && b.GetPlayer != null && b.GetPlayer.HealthController != null
                    && b.GetPlayer.HealthController.IsAlive)
                    list.Add(b);
            return list;
        }

        private static bool IsStacked(BotOwner b, List<BotOwner> all)
        {
            foreach (var o in all)
                if (!ReferenceEquals(o, b) && (o.Position - b.Position).sqrMagnitude < 0.5625f) // <0.75m = capsules interpenetrating
                    return true;
            return false;
        }

        private void TrimRogues(int count)
        {
            try
            {
                var game = Singleton<IBotGame>.Instance;
                var player = Singleton<GameWorld>.Instance?.MainPlayer;
                if (game == null || player == null) return;
                var all = AliveRogues();
                var candidates = new List<BotOwner>();
                foreach (var b in all)
                    if ((b.Position - player.Position).sqrMagnitude > 60f * 60f)
                        candidates.Add(b);
                // STACKED bots go first — the boss-group spawner piles leader+escorts on
                // one marker and interpenetrating capsules freeze the movement solver, so
                // the surplus we have to delete anyway should be the broken ones
                candidates.Sort((a, b) =>
                {
                    bool sa = IsStacked(a, all), sb = IsStacked(b, all);
                    if (sa != sb) return sb.CompareTo(sa);
                    return (b.Position - player.Position).sqrMagnitude.CompareTo((a.Position - player.Position).sqrMagnitude);
                });
                int trimmed = 0;
                foreach (var b in candidates)
                {
                    if (trimmed >= count) break;
                    try { game.BotDespawn(b); trimmed++; }
                    catch (Exception e) { Plugin.Log.LogWarning($"[Crew] despawn failed on '{b.name}': {e.Message}"); }
                }
                Plugin.Log.LogWarning($"[Crew] trimmed {trimmed}/{count} wave rogues (stacked first, then farthest) to hit the rolled crew size");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Crew] trim failed: {e.Message}"); }
        }

        private System.Collections.IEnumerator UnstackPatrol()
        {
            for (int i = 0; i < 12; i++) // ~4 minutes of coverage for late wave arrivals
            {
                UnstackRogues();
                yield return new WaitForSeconds(20f);
            }
        }

        // any stack that survives the trim gets physically separated: teleport extras to
        // free navmesh spots in a ring around the pile. interpenetrating capsules freeze
        // the movement solver — separated bots recover on their own.
        private void UnstackRogues()
        {
            try
            {
                var all = AliveRogues();
                int moved = 0;
                for (int i = 0; i < all.Count; i++)
                    for (int j = i + 1; j < all.Count; j++)
                    {
                        if ((all[i].Position - all[j].Position).sqrMagnitude >= 0.5625f) continue;
                        var b = all[j]; // keep i, move j
                        bool placed = false;
                        for (int attempt = 0; attempt < 8 && !placed; attempt++)
                        {
                            var ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                            var probe = b.Position + new Vector3(Mathf.Cos(ang), 0.2f, Mathf.Sin(ang)) * UnityEngine.Random.Range(1.5f, 3f);
                            if (UnityEngine.AI.NavMesh.SamplePosition(probe, out var hit, 1.5f, UnityEngine.AI.NavMesh.AllAreas))
                            {
                                b.GetPlayer.Teleport(hit.position, false);
                                placed = true;
                                moved++;
                            }
                        }
                        if (!placed) Plugin.Log.LogWarning($"[Crew] couldnt find navmesh spot to unstack '{b.name}'");
                    }
                if (moved > 0) Plugin.Log.LogWarning($"[Crew] unstacked {moved} rogue(s) — group spawns piled them on one marker");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Crew] unstack failed: {e.Message}"); }
        }

        // the cutscene box is the STORY BEAT, not just a spawn trigger: video plays, rogue
        // spawning force-stops (the ship's crew phase ends, black division phase begins),
        // and the engine squad is queued if the BSG box hasn't already done it. last raid
        // the watcher exited the moment hides0 fired elsewhere and the video never played —
        // this one always waits for the BOX.
        internal static bool BdPhase; // flips at the cutscene — Run's rogue top-up checks it

        private IEnumerator EngineAdvanceWatch()
        {
            var triggerGo = GameObject.Find("Icebreaker_StartCutsceneTrigger");
            if (triggerGo == null)
            {
                Plugin.Log.LogWarning("[Crew] no Icebreaker_StartCutsceneTrigger in scene — cutscene/BD-phase watcher off");
                yield break;
            }
            Bounds bounds;
            var col = triggerGo.GetComponent<Collider>() ?? triggerGo.GetComponentInChildren<Collider>(true);
            if (col != null) bounds = col.bounds;
            else bounds = new Bounds(triggerGo.transform.position, new Vector3(8f, 6f, 8f));
            bounds.Expand(new Vector3(2f, 3f, 2f)); // feet-point vs authored box, same lesson as the release box

            var world = Singleton<GameWorld>.Instance;
            while (true)
            {
                var p = world?.MainPlayer;
                if (p != null && bounds.Contains(p.Position))
                {
                    Plugin.Log.LogWarning("[Crew] CUTSCENE TRIGGER — video, rogue spawns stopped, black division phase");
                    BdPhase = true;                // A+B: no more rogue top-ups from here on
                    IcebreakerCutscene.TryPlay();  // BD infiltration video while the real ones deploy below
                    OnSpawnEvent("hides0");        // no-op if the BSG box already queued them
                    // the forward-progress door out of the engine section ships Locked —
                    // without this the player is walled in after the cutscene. the DoorState
                    // setter raises OnDoorStateChanged, so nav links/carvers follow along.
                    UnlockDoorById("door_Icebreaker_Indoor_02_00073");
                    yield break;
                }
                yield return new WaitForSeconds(0.5f);
            }
        }

        internal static void UnlockDoorById(string id)
        {
            foreach (var d in UnityEngine.Object.FindObjectsOfType<EFT.Interactive.Door>(true))
                if (d.Id == id)
                {
                    if (d.DoorState == EFT.Interactive.EDoorState.Locked)
                    {
                        d.DoorState = EFT.Interactive.EDoorState.Shut;
                        Plugin.Log.LogWarning($"[Crew] unlocked progress door '{id}'");
                    }
                    return;
                }
            Plugin.Log.LogWarning($"[Crew] progress door '{id}' not found — check the Id in the bundle");
        }

        private void OnSpawnEvent(string eventId)
        {
            if (string.IsNullOrEmpty(eventId) || !_firedEvents.Add(eventId)) return; // one-shot per event
            // suffix = extras over the base squad (decoded retail group-size ladder)
            int extras = eventId.Length > 0 && char.IsDigit(eventId[eventId.Length - 1])
                ? eventId[eventId.Length - 1] - '0' : 0;
            if (eventId.StartsWith("hides"))
            {
                // vanilla brains from spawn (hold/release staging removed with the rest
                // of the BD AI pokes) — BSG's Hide Zone trigger spawns them at the
                // lower-deck hide markers and the bots take it from there
                StartCoroutine(SpawnSquad("engine room", new[] { "BotZoneEngineHide" }, 4 + extras, null));
            }
            else if (eventId.StartsWith("stern"))
                StartCoroutine(SpawnSternDeployment(extras));
            else if (eventId.StartsWith("wedges"))
                StartCoroutine(SpawnSquad("wedge detail", WedgeZones, 3 + (extras - 1), (WildSpawnType)BdWedge));
            // retail base.json truth (recovered 07-09): T1 = the knight + 2 rogue escorts
            // at Mash_t1 (he was never a raid-start spawn); T3/T4 = BD deployments at the
            // outside/inside zones we previously never delivered
            else if (eventId == "T1")
                StartCoroutine(SpawnKnightDetail());
            else if (eventId == "T3")
                StartCoroutine(SpawnSquad("outside t3", new[] { "BotZoneOutside_t3" }, 3, null));
            else if (eventId == "T4")
                StartCoroutine(SpawnSquad("inside t4", new[] { "BotZoneInside_t4" }, 5, null));
            else
                _firedEvents.Remove(eventId); // not ours (T2 etc) — leave re-armable
        }


        // spawn a BD squad across the given zones; if bossRole set, the first spawn is
        // that boss (wedge) and the rest are assaults
        private static bool _squadSpawnBusy;

        // door-gate probe: whenever a bot gets close to an authored door link, log every
        // gate in the BotDoorOpener decision chain ONCE per bot+link pair. tells us
        // whether ghosting = null CurVoxel (grid hole), empty cell links (reconnect),
        // wrong mover state, or an unlinked door (retail never authored one there).
        private readonly HashSet<long> _probed = new HashSet<long>();
        private int _probeLogs;

        private IEnumerator DoorProbe()
        {
            NavMeshDoorLink[] links = null;
            while (_probeLogs < 24)
            {
                yield return new WaitForSeconds(3f);
                if (links == null || links.Length == 0)
                {
                    links = UnityEngine.Object.FindObjectsOfType<NavMeshDoorLink>();
                    if (links.Length == 0) continue;
                }
                foreach (var b in UnityEngine.Object.FindObjectsOfType<BotOwner>())
                {
                    if (b == null || b.GetPlayer == null || b.GetPlayer.HealthController == null
                        || !b.GetPlayer.HealthController.IsAlive) continue;
                    foreach (var l in links)
                    {
                        if (l == null) continue;
                        float sq = (l.transform.position - b.Position).sqrMagnitude;
                        if (sq > 25f) continue; // within 5m
                        long key = ((long)b.Id << 16) | (uint)l.Id;
                        if (!_probed.Add(key)) continue;
                        try
                        {
                            var vox = b.VoxelesPersonalData != null ? b.VoxelesPersonalData.CurVoxel : null;
                            int cellLinks = vox != null && vox.DoorLinks != null ? vox.DoorLinks.Count : -1;
                            bool inCell = vox != null && vox.DoorLinks != null && vox.DoorLinks.Contains(l);
                            Plugin.Log.LogWarning($"[DoorProbe] '{b.name}' {Mathf.Sqrt(sq):0.0}m from link {l.Id} (door {(l.Door != null ? l.Door.DoorState.ToString() : "NULL")}): curVoxel={(vox != null)} cellLinks={cellLinks} thisLinkInCell={inCell} mover={b.Mover?.CurrentState} shallInteract={l.ShallInteract()} botY={b.Position.y:0.0}");
                            _probeLogs++;
                        }
                        catch (Exception e) { Plugin.Log.LogWarning($"[DoorProbe] {e.Message}"); _probeLogs++; }
                    }
                }
            }
        }

        private IEnumerator SpawnSquad(string label, string[] zoneNames, int assaults, WildSpawnType? bossRole)
        {
            var byName = new HashSet<string>(zoneNames);
            var zones = UnityEngine.Object.FindObjectsOfType<BotZone>()
                .Where(z => byName.Contains(z.name) && z.SpawnPointMarkers != null && z.SpawnPointMarkers.Count > 0)
                .ToList();
            if (zones.Count == 0)
            {
                Plugin.Log.LogWarning($"[Crew] {label}: no zones found — skipped");
                yield break;
            }
            Plugin.Log.LogWarning($"[Crew] EVENT SPAWN: {label} — {(bossRole != null ? "boss + " : "")}{assaults}x assault");
            _squadSpawnBusy = true;
            if (bossRole != null)
            {
                var tb = ForceSpawn(bossRole.Value, zones[UnityEngine.Random.Range(0, zones.Count)]);
                while (!tb.IsCompleted) yield return null;
            }
            // whole fireteam per zone in one batched call — a squad trickling in one bot
            // per 2.5s is exactly the "staggered ambush" the player kept noticing
            var perZone = new int[zones.Count];
            for (int i = 0; i < assaults; i++) perZone[i % zones.Count]++;
            for (int z = 0; z < zones.Count; z++)
            {
                if (perZone[z] == 0) continue;
                var t = ForceSpawnBatch((WildSpawnType)BdAssault, zones[z], perZone[z]);
                while (!t.IsCompleted) yield return null;
            }
            _squadSpawnBusy = false;
        }

        // stern deployment, retail composition: SternTop fireteam + TWO Stern fireteams
        // (Outside_t3 moved to its own T3 trigger per the recovered base.json; retail has
        // no 'Back' rung at all)
        private IEnumerator SpawnSternDeployment(int extras)
        {
            yield return SpawnSquad("stern helipad", new[] { "BotZoneSternTop" }, 3 + extras, null);
            yield return SpawnSquad("stern", new[] { "BotZoneStern" }, 3, null);
            yield return SpawnSquad("stern second team", new[] { "BotZoneStern" }, 3, null);
        }

        // retail T1: the knight arrives mid-raid at Mash_t1 with two rogue escorts —
        // never a raid-start spawn. escorts ride the exUsec pipeline (BdPhase-gated;
        // if T1 fires post-cutscene the knight comes alone, which fits the fiction)
        private IEnumerator SpawnKnightDetail()
        {
            Plugin.Log.LogWarning("[Crew] T1 — the knight arrives (Mash_t1 + 2 rogue escorts)");
            var zone = UnityEngine.Object.FindObjectsOfType<BotZone>()
                .FirstOrDefault(z => z.name == "BotZoneMash_t1" && z.SpawnPointMarkers != null && z.SpawnPointMarkers.Count > 0);
            if (zone == null) { Plugin.Log.LogWarning("[Crew] no BotZoneMash_t1 — knight detail skipped"); yield break; }
            _squadSpawnBusy = true;
            var t = ForceSpawn(WildSpawnType.bossKnight, zone);
            while (!t.IsCompleted) yield return null;
            var te = ForceSpawnBatch(WildSpawnType.exUsec, zone, 2);
            while (!te.IsCompleted) yield return null;
            _squadSpawnBusy = false;
        }

        // ENGINE-ROOM SQUAD — retail: a Black Division fireteam pops in the aft engine room
        // as you descend past the red glowstick, spawns at BotZoneEngineHide and immediately
        // patrols the EngineHide/EngineCenter points, which walks them right into the player
        // (our synthesized AI graph gives them the patrol + combat transition for free).
        // this is its OWN trigger, independent of the start-cutscene BD.
        private const int EngineSquadSize = 4;
        private const string EngineTrigger = "Icebreaker_EngineRoomWaveTrigger"; // user-authored box (isTrigger)
        private const string EngineLandmark = "Glowstick_01_red (9)"; // fallback anchor if the trigger's missing
        private static readonly Vector3 EngineLandmarkFallback = new Vector3(0f, 10.3f, -1.8f);

        private IEnumerator EngineRoomWatch()
        {
            // prefer the hand-authored trigger volume (exact bounds, no guessing); fall
            // back to a box around the glowstick if it isn't in the bundle yet
            Bounds bounds;
            var trigGo = GameObject.Find(EngineTrigger);
            var col = trigGo != null ? (trigGo.GetComponent<Collider>() ?? trigGo.GetComponentInChildren<Collider>(true)) : null;
            if (col != null)
            {
                bounds = col.bounds;
                Plugin.Log.LogWarning($"[Crew] engine-room squad armed — authored trigger '{EngineTrigger}'");
            }
            else
            {
                Vector3 center = EngineLandmarkFallback;
                var lm = GameObject.Find(EngineLandmark);
                if (lm != null) center = lm.transform.position;
                bounds = new Bounds(center, new Vector3(12f, 8f, 12f));
                Plugin.Log.LogWarning($"[Crew] engine-room squad armed — '{EngineTrigger}' not found, using glowstick fallback box");
            }

            Plugin.Log.LogWarning($"[Crew] engine trigger at {bounds.center} size {bounds.size}");

            var world = Singleton<GameWorld>.Instance;
            while (true)
            {
                var p = world?.MainPlayer;
                if (p != null && bounds.Contains(p.Position)) break;
                yield return new WaitForSeconds(0.5f);
            }

            Plugin.Log.LogWarning("[Crew] engine room entered — BLACK DIVISION ambush");
            var hide = UnityEngine.Object.FindObjectsOfType<BotZone>()
                .FirstOrDefault(z => z.name == "BotZoneEngineHide" && z.SpawnPointMarkers != null && z.SpawnPointMarkers.Count > 0);
            if (hide == null)
            {
                Plugin.Log.LogWarning("[Crew] BotZoneEngineHide not found — engine squad skipped");
                yield break;
            }
            for (int i = 0; i < EngineSquadSize; i++)
            {
                var t = ForceSpawn((WildSpawnType)BdAssault, hide);
                while (!t.IsCompleted) yield return null;
                yield return new WaitForSeconds(2.5f);
            }
            Plugin.Log.LogWarning($"[Crew] engine room black division deployed ({EngineSquadSize}x at BotZoneEngineHide)");
        }

        // BLACK DIVISION — trigger-gated (retail: they arrive after the start cutscene).
        // watch the player against the Icebreaker_StartCutsceneTrigger volume; on first
        // overlap, force-spawn the squads. type ids from the BlackDiv mod's prepatch:
        // 848420 = blackDivLead, 848421 = blackDivAssault (mod must be installed).
        private static readonly string[] BlackDivZones =
        {
            "BotZoneSternTop", "BotZoneOutside_t3", "BotZoneStern", "BotZoneBack",
        };
        // BdLead (848420) intentionally unused — its server profile always generates naked
        private const int BdAssault = 848421;
        private const int BdWedge = 848424; // bossWedge — the black division boss
        private static readonly string[] WedgeZones = { "BotZoneRoomsThird", "BotZoneRoomsThirdKitchen" };

        private IEnumerator BlackDivisionWatch()
        {
            var triggerGo = GameObject.Find("Icebreaker_StartCutsceneTrigger");
            if (triggerGo == null)
            {
                Plugin.Log.LogWarning("[Crew] no Icebreaker_StartCutsceneTrigger in scene — black division not gated (skipping)");
                yield break;
            }
            Bounds bounds;
            var col = triggerGo.GetComponent<Collider>() ?? triggerGo.GetComponentInChildren<Collider>(true);
            if (col != null) bounds = col.bounds;
            else bounds = new Bounds(triggerGo.transform.position, new Vector3(8f, 6f, 8f)); // shell without collider — approximate
            // the retail trigger is phone-booth sized (4.5x2x3.7) — a light inflate so you
            // cant squeeze past it, but small enough that you have to actually reach the
            // cutscene spot (the old +6m pad tripped it from a corridor away)
            bounds.Expand(new Vector3(2f, 1f, 2f));

            Plugin.Log.LogWarning($"[Crew] black division armed — trigger at {bounds.center} size {bounds.size}");

            var world = Singleton<GameWorld>.Instance;
            while (true)
            {
                var p = world?.MainPlayer;
                if (p != null && bounds.Contains(p.Position)) break;
                yield return new WaitForSeconds(0.5f);
            }

            Plugin.Log.LogWarning("[Crew] cutscene trigger hit — BLACK DIVISION INBOUND");
            var byName = new HashSet<string>(BlackDivZones);
            var bdZones = UnityEngine.Object.FindObjectsOfType<BotZone>()
                .Where(z => byName.Contains(z.name) && z.SpawnPointMarkers != null && z.SpawnPointMarkers.Count > 0)
                .ToList();
            // staggered hard: rapid custom-type profile requests overwhelmed the server
            // generator (some bots arrived NAKED and frozen). one bot per ~2.5s keeps it
            // happy. blackDivLead (848420) specifically ALWAYS came back naked — its server
            // profile is broken — so squads are all blackDivAssault (848421) now; same
            // 3-man size, no lead type.
            foreach (var zone in bdZones)
            {
                for (int i = 0; i < 3; i++)
                {
                    var t = ForceSpawn((WildSpawnType)BdAssault, zone);
                    while (!t.IsCompleted) yield return null;
                    yield return new WaitForSeconds(2.5f);
                }
            }
            // bossWedge — the black division boss — holds the third-deck rooms with a
            // 3-man detail distributed between RoomsThird and RoomsThirdKitchen
            var wedgeNames = new HashSet<string>(WedgeZones);
            var wedgeZones = UnityEngine.Object.FindObjectsOfType<BotZone>()
                .Where(z => wedgeNames.Contains(z.name) && z.SpawnPointMarkers != null && z.SpawnPointMarkers.Count > 0)
                .ToList();
            if (wedgeZones.Count > 0)
            {
                var t2 = ForceSpawn((WildSpawnType)BdWedge, wedgeZones[UnityEngine.Random.Range(0, wedgeZones.Count)]);
                while (!t2.IsCompleted) yield return null;
                yield return new WaitForSeconds(2.5f);
                for (int i = 0; i < 3; i++)
                {
                    t2 = ForceSpawn((WildSpawnType)BdAssault, wedgeZones[i % wedgeZones.Count]);
                    while (!t2.IsCompleted) yield return null;
                    yield return new WaitForSeconds(2.5f);
                }
                Plugin.Log.LogWarning("[Crew] bossWedge deployed with 3-man detail (RoomsThird/RoomsThirdKitchen)");
            }
            else Plugin.Log.LogWarning("[Crew] wedge zones missing — bossWedge skipped");

            Plugin.Log.LogWarning($"[Crew] black division deployed: {bdZones.Count} squads + wedge");
        }

        private List<BotZone> CollectRogueZones()
        {
            var byName = new HashSet<string>(RogueZones);
            return UnityEngine.Object.FindObjectsOfType<BotZone>()
                .Where(z => byName.Contains(z.name) && z.SpawnPointMarkers != null && z.SpawnPointMarkers.Count > 0)
                .ToList();
        }

        private int CountByRole(WildSpawnType role)
        {
            int n = 0;
            foreach (var b in UnityEngine.Object.FindObjectsOfType<BotOwner>())
                if (b != null && b.Profile?.Info?.Settings?.Role == role && b.GetPlayer != null
                    && b.GetPlayer.HealthController != null && b.GetPlayer.HealthController.IsAlive)
                    n++;
            return n;
        }

        // every properly generated bot carries at least a scabbard knife — a profile with
        // ALL weapon slots empty is the server generator failing under burst load on the
        // custom blackdiv types (the naked frozen mannequins). vet before spawning.
        private static bool IsNakedProfile(BotCreationDataClass data)
        {
            try
            {
                var profiles = data.Profiles;
                if (profiles == null || profiles.Count == 0) return true;
                foreach (var pr in profiles)
                {
                    var eq = pr?.Inventory?.Equipment;
                    if (eq == null) return true;
                    bool armed = false;
                    foreach (var slot in new[] { EFT.InventoryLogic.EquipmentSlot.FirstPrimaryWeapon,
                                                 EFT.InventoryLogic.EquipmentSlot.SecondPrimaryWeapon,
                                                 EFT.InventoryLogic.EquipmentSlot.Holster,
                                                 EFT.InventoryLogic.EquipmentSlot.Scabbard })
                    {
                        var s = eq.GetSlot(slot);
                        if (s != null && s.ContainedItem != null) { armed = true; break; }
                    }
                    if (!armed) return true;
                }
                return false;
            }
            catch { return false; } // cant tell — dont block the spawn on a probe failure
        }

        // profile creation + the naked-profile vetting, shared by direct spawns and the
        // trigger-squad pre-maker
        private async Task<BotCreationDataClass> CreateData(WildSpawnType role, int count = 1)
        {
            var spawnParams = new BotSpawnParams { ShallBeGroup = new ShallBeGroupParams(false, false, Math.Max(1, count)) };
            var diff = Plugin.HardBots.Value ? BotDifficulty.hard : BotDifficulty.normal;
            var profileData = new BotProfileDataClass(EPlayerSide.Savage, role, diff, 5f, spawnParams, false);
            var data = await BotCreationDataClass.Create(profileData, _spawner.BotCreator, count, _spawner);
            if (data == null) { Plugin.Log.LogWarning($"[Crew] profile creation failed for {role}"); return null; }

            // naked roll — give the generator a breather and re-request ONCE; if it
            // fails again, skip this batch entirely (a missing bot beats a mannequin)
            if (IsNakedProfile(data))
            {
                Plugin.Log.LogWarning($"[Crew] {role} profile arrived NAKED — re-requesting in 3s");
                await Task.Delay(3000);
                data = await BotCreationDataClass.Create(profileData, _spawner.BotCreator, count, _spawner);
                if (data == null || IsNakedProfile(data))
                {
                    Plugin.Log.LogWarning($"[Crew] {role} re-request also bad — skipping this spawn");
                    return null;
                }
            }
            return data;
        }

        // pick N spawn points, preferring ones the player won't watch materialize:
        // anything beyond 25m of the player first (shuffled), close points only as a
        // last resort. wraps if the zone has fewer markers than N.
        private static List<EFT.Game.Spawning.ISpawnPoint> PickPoints(BotZone zone, int count)
        {
            var pts = zone.SpawnPoints;
            if (pts == null || pts.Length == 0) return null;
            var pool = new List<EFT.Game.Spawning.ISpawnPoint>();
            foreach (var p in pts) if (p != null) pool.Add(p);
            // EngineHide spans two decks and the upper one (y~15.7+) has NO bot-walkable
            // route down to the engine room (ladder only — bots cant ladder): a squad
            // seeded up there jitters in place against an unreachable rush target
            // forever (07-11 log: released at y15.7, 0/4 arrived, <1m moved). ground
            // the squad on the lower deck where the doors are.
            if (zone.name == "BotZoneEngineHide")
            {
                var lower = pool.FindAll(p => p.Position.y < 12.5f);
                if (lower.Count > 0) pool = lower;
            }
            if (pool.Count == 0) return null;
            for (int i = 0; i < pool.Count; i++)
            {
                int j = UnityEngine.Random.Range(i, pool.Count);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }
            var player = Singleton<GameWorld>.Instance?.MainPlayer;
            if (player != null)
                pool.Sort((a, b) =>
                    (((b.Position - player.Position).sqrMagnitude > 625f) ? 1 : 0)
                    - (((a.Position - player.Position).sqrMagnitude > 625f) ? 1 : 0));
            var result = new List<EFT.Game.Spawning.ISpawnPoint>(count);
            for (int i = 0; i < count; i++) result.Add(pool[i % pool.Count]);
            return result;
        }

        // batch spawn: premade cache first (warm), then SINGLE-profile creates for the
        // shortfall — all prepared first, then activated back-to-back in one burst so the
        // squad APPEARS simultaneously. the grouped-request version (one CreateData with
        // count=N) silently yielded ~1 bot per batch (07-08 raid: 12x "4x exUsec" batches
        // -> 7 rogues alive, engine squad 1/4) — the count param doesn't mean what it
        // seems, so back to the proven per-bot pipeline without the old 1.5-2.5s gaps.
        private async Task ForceSpawnBatch(WildSpawnType role, BotZone zone, int count)
        {
            try
            {
                var ready = new List<BotCreationDataClass>(count);
                if (_preMade.TryGetValue((int)role, out var pq))
                    while (ready.Count < count && pq.Count > 0)
                        ready.Add(pq.Dequeue());
                int fromCache = ready.Count;
                for (int i = ready.Count; i < count; i++)
                {
                    var d = await CreateData(role);
                    if (d == null) continue; // naked twice — skip this bot, keep the squad
                    await Prewarm(d);
                    ready.Add(d);
                }
                // the cutscene ends the crew phase MID-FLIGHT too: a rogue batch that was
                // still creating profiles when the trigger hit used to activate afterwards
                // anyway (07-09 log: cutscene at 10667, 3 rogues landed at 10679)
                if (role == WildSpawnType.exUsec && BdPhase)
                {
                    Plugin.Log.LogWarning($"[Crew] rogue batch aborted at activation — black division phase started mid-creation");
                    return;
                }
                // one DISTINCT marker per squad member: independent per-bot picks kept
                // ranking the same far-from-player corner first, piling the whole squad
                // behind one door — EngineHide alone has 10 markers across two floors
                // and both sides of the room, so spread is free when picked as a set
                var pts = PickPoints(zone, ready.Count);
                for (int i = 0; i < ready.Count; i++)
                {
                    var pick = pts != null ? new List<EFT.Game.Spawning.ISpawnPoint> { pts[i % pts.Count] } : null;
                    _spawner.TryToSpawnInZoneInner(zone, ready[i], 1, false, true, pick, true);
                }
                Plugin.Log.LogInfo($"[Crew] batch-spawned {ready.Count}/{count}x {role} into {zone.name} ({fromCache} from cache, {(pts != null ? pts.Count : 0)} spread points)");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Crew] batch spawn {role} failed: {e}"); }
        }

        // the spawn hitch: instantiation force-loads any COLD gear bundle synchronously
        // (30-100ms/bot, worst on the custom blackdiv roles which are never in the raid
        // pool). this is BSG's own pre-pool call — async, spread by the job system — so
        // awaiting it first means the spawn instantiates against warm pools.
        private static async Task Prewarm(BotCreationDataClass data)
        {
            try
            {
                var keys = data.Profiles.SelectMany(p => p.GetAllPrefabPaths(false)).ToArray();
                if (keys.Length > 0)
                    await Singleton<PoolManagerClass>.Instance.LoadBundlesAndCreatePools(
                        PoolManagerClass.PoolsCategory.Raid, PoolManagerClass.AssemblyType.Local,
                        // Low, not General: pool CREATION instantiates templates on the main
                        // thread and General-priority slices burst 176-362ms at premake time
                        keys, JobPriorityClass.Low, null, default(System.Threading.CancellationToken));
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Crew] prewarm failed (spawn will cold-load): {e.Message}"); }
        }

        // pre-made, pre-warmed bots for the trigger squads — created during the quiet
        // early raid so event spawns are instant and burst-free
        private readonly Dictionary<int, Queue<BotCreationDataClass>> _preMade = new Dictionary<int, Queue<BotCreationDataClass>>();

        private IEnumerator PreMakeTriggerSquads()
        {
            // engine 4 + stern 3+3+3+3 + wedge detail 3 = 19 assaults, 1 wedge boss.
            // extras beyond the cache fall back to on-demand creation (still prewarmed).
            var wants = new List<WildSpawnType>();
            for (int i = 0; i < 19; i++) wants.Add((WildSpawnType)BdAssault);
            wants.Add((WildSpawnType)BdWedge);
            foreach (var role in wants)
            {
                // event spawns get absolute priority on the server generator — premake
                // running concurrently starved a live squad spawn (members arrived minutes
                // apart: an ambush team trickling in one bot at a time)
                while (_squadSpawnBusy) yield return new WaitForSeconds(0.5f);
                var t = PreMakeOne(role);
                while (!t.IsCompleted) yield return null;
                yield return new WaitForSeconds(3f); // gentle — the naked-profile lesson
            }
            int total = 0; foreach (var q in _preMade.Values) total += q.Count;
            Plugin.Log.LogWarning($"[Crew] trigger squads pre-made: {total} bots cached + bundle-warm");
        }

        private async Task PreMakeOne(WildSpawnType role)
        {
            try
            {
                var data = await CreateData(role);
                if (data == null) return;
                await Prewarm(data);
                if (!_preMade.TryGetValue((int)role, out var q)) _preMade[(int)role] = q = new Queue<BotCreationDataClass>();
                q.Enqueue(data);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Crew] premake {role} failed: {e.Message}"); }
        }

        private async Task ForceSpawn(WildSpawnType role, BotZone zone)
        {
            try
            {
                BotCreationDataClass data = null;
                if (_preMade.TryGetValue((int)role, out var pq) && pq.Count > 0)
                    data = pq.Dequeue(); // pre-made + already warm
                else
                {
                    data = await CreateData(role);
                    if (data == null) return;
                    await Prewarm(data);
                }

                // pre-pick a point: SelectAISpawnPoints refuses once a zone saturates; a
                // forced explicit point bypasses that gate (speed-cola lesson). PickPoints
                // also prefers markers the player isn't staring at.
                _spawner.TryToSpawnInZoneInner(zone, data, 1, false, true, PickPoints(zone, 1), true);
                Plugin.Log.LogInfo($"[Crew] forced {role} into {zone.name}");
            }
            catch (Exception e)
            {
                // full stack — a remote tester's log is all we get, Message alone can't
                // name the null line (spawner internals vs profile request vs zone data)
                Plugin.Log.LogWarning($"[Crew] ForceSpawn {role} failed: {e}");
            }
        }
    }
}
