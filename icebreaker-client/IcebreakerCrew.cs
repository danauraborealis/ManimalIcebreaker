using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // deterministic crew spawner for the icebreaker (speed-cola pattern): the game's wave
    // scenario under-delivers through a stack of silent gates (bot-amount slider rescale,
    // per-zone type blocks, spawn-point saturation) — instead of fighting each gate, count
    // what actually spawned and FORCE the remainder via BotSpawner.TryToSpawnInZoneInner
    // with a pre-picked point (bypasses the whole scenario/limit machinery). retail comp:
    // party-scaled rogues (config min=solo, +1/extra player to max) + solo knight,
    // distributed over the confirmed rogue zones.
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
            ResetCutsceneGate();
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

            // fika client: bots are the host's job and arrive as network replicas —
            // running the crew here would grow a second, local-only ghost crew. the
            // cutscene watcher stays: the video is a local presentation beat, each
            // player sees it when THEY cross the box (bot side effects gated inside).
            if (!FikaBridge.BotsAuthority)
            {
                Plugin.Log.LogDebug("[Crew] fika client — crew is host-authoritative, arming local cutscene watcher only");
                if (Plugin.CrewBlackDiv.Value)
                    StartCoroutine(EngineAdvanceWatch());
                yield break;
            }

            // POLL, don't check once: under fika the IBotGame singleton lands on its own
            // schedule and a single 2s-mark check lost the race (07-28 raid: "no
            // BotSpawner — giving up" and with it the cutscene watcher, the event
            // bridge, and the knight all died together, events buffering forever)
            float spawnerDeadline = Time.time + 60f;
            while (_spawner == null && Time.time < spawnerDeadline)
            {
                var botGame = Singleton<IBotGame>.Instance;
                _controller = botGame?.BotsController;
                _spawner = _controller?.BotSpawner;
                if (_spawner == null) yield return new WaitForSeconds(0.5f);
            }
            if (_spawner == null)
            {
                Plugin.Log.LogDebug("[Crew] no BotSpawner after 60s — crew spawner giving up");
                yield break;
            }

            // silent per-(zone,type) spawn blocks reject forced spawns too — off for this raid
            try { if (_controller.ZonesLeaveController != null) _controller.ZonesLeaveController.NoZoneBlocks = true; }
            catch { }

            var zones = CollectRogueZones();
            if (zones.Count == 0)
            {
                Plugin.Log.LogDebug("[Crew] no rogue zones found — crew spawner giving up");
                yield break;
            }

            // ALL the watchers/caches arm FIRST, before the top-up loop: last raid the
            // player rushed the cutscene box before the top-up finished and the watcher
            // wasn't running yet — the story beat just didn't fire. the cutscene flips
            // BdPhase which the top-up loop already respects mid-flight.
            if (Plugin.CrewBlackDiv.Value)
            {
                // premake moved BELOW the rogue fill — 18 cached BD profiles were
                // competing with the rogue fill for the server's bot-gen queue and the
                // crew landed minutes late, audibly around the player inside the ship
                SubscribeEventSpawns();
                StartCoroutine(EngineAdvanceWatch());
            }

            // DETERMINISTIC crew (user call 08-05, replacing the fill + culler): the
            // count-and-correct machinery raced the wave spawner every raid — the fill
            // measured mid-landing, saw the not-yet-activated bots as missing, over-
            // spawned, and the culler had to claw it back (which it cant do to anything
            // a player can see, so a flood in your face just stayed). the authored
            // waves reliably deliver the 6-man base crew and the T1 knight detail adds
            // 2 escorts — theres nothing to count. coop just adds flat extras, one
            // fire-and-forget batch, no measuring before or after.
            yield return new WaitForSeconds(4f);

            // solo gets the base crew, every extra coop player adds one rogue up to
            // the cap — same roster source as the BD trigger brackets. 6/10 not 8/12:
            // the knight detail adds 2 escorts, so TOTALS land at 8 solo .. 12 full
            const int baseRogues = 6, capRogues = 10;
            int party = GroupSizeEventLogic.GroupSize();
            int extra = Mathf.Clamp(baseRogues + (party - 1), baseRogues, capRogues) - baseRogues;
            Plugin.Log.LogInfo($"[Crew] waves bring {baseRogues}; party of {party} adds {extra}; knight={Plugin.CrewKnight.Value}");
            if (extra > 0 && !BdPhase)
            {
                var shuffled = zones.OrderBy(_ => UnityEngine.Random.value).ToList();
                var t = ForceSpawnBatch(WildSpawnType.exUsec, shuffled[0], extra);
                while (!t.IsCompleted) yield return null;
            }

            // boss-group spawns stack leader+escorts on one marker (frozen conjoined
            // rogues) — and the wave lands STAGGERED, so a one-shot pass missed everyone
            // who arrived after it. patrol for the first few minutes instead.
            StartCoroutine(UnstackPatrol());

            // knight raid-start force REMOVED: retail base.json says he arrives via the
            // T1 trigger with 2 rogue escorts (SpawnKnightDetail) — never at raid start

            // NOW prepare the trigger squads — after the rogues are on deck, so the
            // build has the bot-gen queue to itself. pool mode goes one further than
            // the profile premake: the bots are fully SPAWNED into an off-map pen and
            // events teleport them in, so the instantiation + gear-bundle cost lands
            // here in the quiet early raid (the ice climb) instead of on a trigger frame
            // native-waves mode: the POOL would double-spawn (pen bots + native rows),
            // but the profile PREMAKE stays valuable — it warms the server's bot-gen
            // cache so the native trigger spawns draw prebuilt profiles (less hitch)
            if (Plugin.CrewBlackDiv.Value)
                StartCoroutine(Plugin.CrewPreSpawnPool.Value && !Plugin.NativeBdWaves.Value
                    ? PoolSpawnTriggerSquads() : PreMakeTriggerSquads());

            // the resurrected retail trigger layer raises the events (hides0/stern0/wedges1
            // + the group-size ladder) and OUR force-spawner delivers them — the server's
            // BossLocationSpawn pipeline silently refused the custom blackdiv roles, so
            // triggers fired and zero bots arrived. everything is armed up top (bridge +
            // cutscene watcher + premake); nothing starts here any more.
            //
            // the old EventSpawns=false fallback (BlackDivisionWatch + EngineRoomWatch) was
            // deleted 08-01: unused since the switch, and a silently-drifting second copy of
            // the spawn logic — the blackDivIb retype and the bossWedge restore both landed
            // in it first and did nothing, because it never ran.
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
            Plugin.Log.LogDebug("[Crew] event-spawn bridge armed (buffered events drained)");
        }

        private void OnDestroy()
        {
            _unsubEvents?.Invoke();
            // statics must not leak bots into the next raid's pool
            _penPending.Clear(); _penIntake.Clear(); _pool.Clear(); _penSlot = 0;
        }

        // BD AI modifications (temperament rolls, mind rewires, forced rush) REMOVED per
        // user call 07-11 — the layered brain pokes kept fighting each other ("bugging
        // out"). black division runs vanilla brains; we control WHERE and WHEN they
        // spawn, plus ONE staging behavior brought back 07-17: the engine squad's
        // hold-until-trigger (patrol pause only, no brain pokes).

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

        // trim/shaver machinery deleted 08-05 (user call): counting live rogues to
        // decide corrections was a race against the staggered wave spawner, and every
        // flood traced back to it. the deterministic spawn plan above replaced it.
        // if a cull is ever needed again: LeaveData.RemoveFromMap, NEVER raw
        // BotDespawn — raw despawns leave dangling transforms in BotEventHandler and
        // every later player sound NREs in PlaySound (08-04: controller flip-out).

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
                        if (!placed) Plugin.Log.LogDebug($"[Crew] couldnt find navmesh spot to unstack '{b.name}'");
                    }
                if (moved > 0) Plugin.Log.LogDebug($"[Crew] unstacked {moved} rogue(s) — group spawns piled them on one marker");
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
                Plugin.Log.LogDebug("[Crew] no Icebreaker_StartCutsceneTrigger in scene — cutscene/BD-phase watcher off");
                yield break;
            }
            Bounds bounds;
            var col = triggerGo.GetComponent<Collider>() ?? triggerGo.GetComponentInChildren<Collider>(true);
            if (col != null) bounds = col.bounds;
            else bounds = new Bounds(triggerGo.transform.position, new Vector3(8f, 6f, 8f));
            // DOWNWARD pad only, nothing else (user call 07-30): the box floats at chest
            // height and we test the player's FEET, so it needs floor reach — but any
            // horizontal/upward growth trips the cutscene through walls and decks
            // (raw box confirmed unreachable by feet; +1m xz fired through walls)
            bounds = PadDown(bounds, 0f, 1.5f);
            Plugin.Log.LogWarning($"[Crew] cutscene box armed — centre {bounds.center} size {bounds.size} ({(col != null ? "authored collider" : "8x6x8 fallback")})");

            // three half-lives in coop: the CUTSCENE is per-player (plays when YOU
            // cross the box), the STORY BEAT (BD phase, engine squad) is
            // host-authoritative and fires when ANY human crosses first, and the
            // PROGRESS DOOR waits on the cutscene gate (all living players crossed).
            // solo all three trip on the same tick.
            var world = Singleton<GameWorld>.Instance;
            bool videoPlayed = false, beatFired = false;
            if (FikaBridge.BotsAuthority) StartCoroutine(CutsceneGateWatch());
            while (true)
            {
                var p = world?.MainPlayer;
                if (!videoPlayed && p != null && bounds.Contains(p.Position))
                {
                    videoPlayed = true;
                    Plugin.Log.LogDebug($"[Crew] CUTSCENE TRIGGER — cutscene, rogue spawns stopped, black division phase (player at {p.Position})");
                    BdPhase = true;                // A+B: no more rogue top-ups from here on
                    IcebreakerCutscene.TryPlay();  // BD infiltration cutscene while the real ones deploy below
                    // the door does NOT unlock here anymore — it waits on the gate
                    // below until every living player has triggered their own cutscene
                    MarkLocalCutsceneActivated();
                }
                if (!beatFired && FikaBridge.BotsAuthority && FikaBridge.AnyHumanIn(bounds))
                {
                    beatFired = true;
                    if (!videoPlayed) Plugin.Log.LogDebug("[Crew] cutscene box tripped by a teammate — BD phase begins (your cutscene still plays when you cross)");
                    BdPhase = true;
                    OnSpawnEvent("hides0");        // no-op if the BSG box already queued them
                }
                if (videoPlayed && (beatFired || !FikaBridge.BotsAuthority)) yield break;
                yield return new WaitForSeconds(0.5f);
            }
        }

        // horizontal pad both ways, vertical pad DOWN only — the boxes are authored at
        // chest height and we compare against the player's feet
        private static Bounds PadDown(Bounds b, float xz, float down)
        {
            var min = b.min; var max = b.max;
            min.x -= xz; max.x += xz;
            min.z -= xz; max.z += xz;
            min.y -= down;
            b.SetMinMax(min, max);
            return b;
        }

        internal const string ProgressDoorId = "door_Icebreaker_Indoor_02_00073";

        // fika sync hooks for the cutscene progress door (see EngineAdvanceWatch)
        internal static event Action ProgressDoorUnlocked;
        internal static void ApplyRemoteProgressDoor() => UnlockDoorById(ProgressDoorId);

        // ---- CUTSCENE GATE (user call 07-30): the progress door stays locked until
        // EVERY LIVING player has triggered their own (unsynced, per-player) cutscene —
        // the squad regroups at the engine-section exit. dead/extracted/disconnected
        // players drop out of the requirement via the alive-roster check, so a corpse
        // can't deadlock the raid; a LIVING deck camper CAN hold the door hostage —
        // accepted (friends-only fika, and team damage settles arguments).
        // tracked by ProfileId so the mechanism is identical with or without fika;
        // only the bot AUTHORITY evaluates the gate, everyone else gets the unlock
        // via the addon's kind-4 broadcast.
        private static readonly HashSet<string> _cutsceneSeen = new HashSet<string>();
        private static bool _doorGateOpen;
        internal static event Action<string> CutsceneActivated; // -> fika addon (kind 9)

        internal static void ResetCutsceneGate()
        {
            _cutsceneSeen.Clear();
            _doorGateOpen = false;
        }

        private static void MarkLocalCutsceneActivated()
        {
            var pid = Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId;
            if (pid == null) return;
            _cutsceneSeen.Add(pid);
            try { CutsceneActivated?.Invoke(pid); }
            catch (Exception e) { Plugin.Log.LogWarning($"[Crew] cutscene-activation hook failed: {e.Message}"); }
            CheckCutsceneGate();
        }

        internal static void ApplyRemoteCutsceneActivation(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return;
            _cutsceneSeen.Add(profileId);
            CheckCutsceneGate();
        }

        private static void CheckCutsceneGate()
        {
            if (_doorGateOpen || !FikaBridge.BotsAuthority) return;
            if (_cutsceneSeen.Count == 0) return; // nobody has crossed yet
            var humans = new List<Player>();
            FikaBridge.CollectHumans(humans);
            foreach (var h in humans)
                if (h != null && h.ProfileId != null && !_cutsceneSeen.Contains(h.ProfileId)) return;
            _doorGateOpen = true;
            Plugin.Log.LogDebug($"[Crew] cutscene gate OPEN — all {humans.Count} living player(s) triggered the cutscene, progress door unlocking");
            UnlockDoorById(ProgressDoorId);
            try { ProgressDoorUnlocked?.Invoke(); } // kind 4 -> every peer unlocks
            catch (Exception e) { Plugin.Log.LogWarning($"[Crew] progress-door hook failed: {e.Message}"); }
        }

        // deaths must release the gate WITHOUT a fresh activation (the last straggler
        // dying should open the door for the survivors) — poll on the authority
        private IEnumerator CutsceneGateWatch()
        {
            while (!_doorGateOpen)
            {
                CheckCutsceneGate();
                yield return new WaitForSeconds(3f);
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
                        Plugin.Log.LogDebug($"[Crew] unlocked progress door '{id}'");
                    }
                    return;
                }
            Plugin.Log.LogWarning($"[Crew] progress door '{id}' not found — check the Id in the bundle");
        }

        private void OnSpawnEvent(string eventId)
        {
            if (string.IsNullOrEmpty(eventId) || !_firedEvents.Add(eventId)) return; // one-shot per event

            // NATIVE WAVES MODE: our trigger ids ARE the retail TriggerIds, so the
            // whole delivery reduces to raising BSG's own bot event — the appended
            // BossLocationSpawn rows do the spawning. the staging extras that aren't
            // spawns stay ours: the engine hold watcher (scans arrivals regardless of
            // who spawned them), the SZ-1 charge sweeps, and the progress doors.
            if (Plugin.NativeBdWaves.Value)
            {
                try
                {
                    Singleton<BotEventHandler>.Instance?.AnyEvent(eventId);
                    Plugin.Log.LogWarning($"[Crew] native wave trigger raised: '{eventId}' (BSG pipeline delivers the squad)");
                }
                catch (Exception e) { Plugin.Log.LogError($"[Crew] native trigger raise failed for '{eventId}': {e.Message}"); }

                if (eventId.StartsWith("hides"))
                {
                    StartCoroutine(HoldEngineSquad(0, 4, 0)); // watcher — holds whoever arrives at the hide markers
                    StartCoroutine(PlaceChargeSweep("BotZoneEngineHide"));
                }
                else if (eventId.StartsWith("stern"))
                    StartCoroutine(PlaceChargeSweep("BotZoneSternTop", "BotZoneStern"));
                else if (eventId == "T3")
                    StartCoroutine(PlaceChargeSweep("BotZoneOutside_t3"));
                return;
            }
            // suffix = extras over the base squad (decoded retail group-size ladder)
            int extras = eventId.Length > 0 && char.IsDigit(eventId[eventId.Length - 1])
                ? eventId[eventId.Length - 1] - '0' : 0;
            if (eventId.StartsWith("hides"))
            {
                // hold/release STAGING (07-17), split (07-28), re-split (08-03), back to
                // VANILLA (08-07 user call): all 4 hold the ambush at the hide markers
                // until the release trigger blows — retail's behavior, no early
                // patrollers. the extras still push in from the pen on release.
                StartCoroutine(SpawnSquad("engine room", new[] { "BotZoneEngineHide" }, 4, null));
                StartCoroutine(HoldEngineSquad(0, 4, extras));
                StartCoroutine(PlaceChargeSweep("BotZoneEngineHide"));
            }
            else if (eventId.StartsWith("stern"))
                StartCoroutine(SpawnSternDeployment(extras));
            // bossWedge leads this detail again (user, 08-01). SpawnSquad already takes a
            // bossRole — it was simply being passed null. assaults drop by one so the detail
            // stays the same size: boss + 3 instead of 4 escorts.
            // the AIPlaces trigger fires 'wedges1' and lands here — this is the only wedge
            // path now that the legacy watcher is gone.
            else if (eventId.StartsWith("wedges"))
                StartCoroutine(SpawnSquad("wedge detail", WedgeZones, 3 + (extras - 1), (WildSpawnType)BdWedge));
            // retail base.json truth (recovered 07-09): T1 = the knight + 2 rogue escorts
            // at Mash_t1 (he was never a raid-start spawn); T3/T4 = BD deployments at the
            // outside/inside zones we previously never delivered
            else if (eventId == "T1")
                StartCoroutine(SpawnKnightDetail());
            else if (eventId == "T3")
            {
                StartCoroutine(SpawnSquad("outside t3", new[] { "BotZoneOutside_t3" }, 3, null));
                StartCoroutine(PlaceChargeSweep("BotZoneOutside_t3"));
            }
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
                            Plugin.Log.LogDebug($"[DoorProbe] '{b.name}' {Mathf.Sqrt(sq):0.0}m from link {l.Id} (door {(l.Door != null ? l.Door.DoorState.ToString() : "NULL")}): curVoxel={(vox != null)} cellLinks={cellLinks} thisLinkInCell={inCell} mover={b.Mover?.CurrentState} shallInteract={l.ShallInteract()} botY={b.Position.y:0.0}");
                            _probeLogs++;
                        }
                        catch (Exception e) { Plugin.Log.LogDebug($"[DoorProbe] {e.Message}"); _probeLogs++; }
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
            Plugin.Log.LogDebug($"[Crew] EVENT SPAWN: {label} — {(bossRole != null ? "boss + " : "")}{assaults}x assault");
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
                var t = ForceSpawnBatch((WildSpawnType)BdIb, zones[z], perZone[z]);
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
            StartCoroutine(PlaceChargeSweep("BotZoneSternTop", "BotZoneStern"));
        }

        // retail T1: the knight arrives mid-raid at Mash_t1 with two rogue escorts —
        // never a raid-start spawn. escorts ride the exUsec pipeline (BdPhase-gated;
        // if T1 fires post-cutscene the knight comes alone, which fits the fiction)
        private IEnumerator SpawnKnightDetail()
        {
            Plugin.Log.LogDebug("[Crew] T1 — the knight arrives (Mash_t1 + 2 rogue escorts)");
            var zone = UnityEngine.Object.FindObjectsOfType<BotZone>()
                .FirstOrDefault(z => z.name == "BotZoneMash_t1" && z.SpawnPointMarkers != null && z.SpawnPointMarkers.Count > 0);
            if (zone == null) { Plugin.Log.LogWarning("[Crew] no BotZoneMash_t1 — knight detail skipped"); yield break; }
            _squadSpawnBusy = true;
            var t = ForceSpawn(WildSpawnType.bossKnight, zone);
            while (!t.IsCompleted) yield return null;
            var te = ForceSpawnBatch(WildSpawnType.exUsec, zone, 2);
            while (!te.IsCompleted) yield return null;
            _squadSpawnBusy = false;

            // the escorts bring the TOTAL to 8 solo (6 wave crew + these 2)
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

        // active-layer diagnostic: names which brain layer owns each crew bot a few
        // seconds after a job assignment — the difference between "SAIN outranked us",
        // "our layer isn't attached to this brain at all" and "working as intended"
        // is unguessable from behavior alone
        private IEnumerator LogCrewBrains(List<BotOwner> bots, string label)
        {
            yield return new WaitForSeconds(8f);
            foreach (var b in bots)
            {
                if (b == null || b.GetPlayer == null || b.GetPlayer.HealthController == null
                    || !b.GetPlayer.HealthController.IsAlive) continue;
                string layer = "?";
                try { layer = DrakiaXYZ.BigBrain.Brains.BrainManager.GetActiveLayerName(b) ?? "(none)"; }
                catch (Exception e) { layer = $"(query failed: {e.Message})"; }
                string brain = "?";
                try { brain = b.Brain?.BaseBrain?.ShortName() ?? "?"; } catch { }
                Plugin.Log.LogDebug($"[CrewLayer] {label}: '{b.name}' brain='{brain}' activeLayer='{layer}'");
            }
        }

        // SZ-1 GUARANTEE (user call 08-03; replaced the knight-pocket server inject that
        // died on pocket size): exactly ONE chain-door charge per raid, carried by the
        // black division. the first qualifying deployment to spawn gets it — stern,
        // stern top, engine hide, or the outside T3 squad — preferring a guard who
        // rolled a BACKPACK (rare here) with pockets as the fallback. client-side and
        // host-only by construction (the crew spawner runs with bots authority; fika
        // corpse searches read the host's inventory live, so the loot replicates).
        private bool _chargePlaced;

        private IEnumerator PlaceChargeSweep(params string[] zoneNames)
        {
            if (_chargePlaced) yield break;
            // batches create asynchronously (server round trips) — poll until the squad
            // is actually standing there rather than sweeping a fixed delay too early.
            // timeout leaves _chargePlaced false so a LATER qualifying squad still takes it.
            var anchors = new List<Vector3>();
            foreach (var z in UnityEngine.Object.FindObjectsOfType<BotZone>())
                if (zoneNames.Contains(z.name) && z.SpawnPointMarkers != null)
                    foreach (var m in z.SpawnPointMarkers)
                        if (m != null) anchors.Add(m.transform.position);
            if (anchors.Count == 0) yield break;

            float giveUp = Time.time + 60f;
            var cands = new List<BotOwner>();
            while (Time.time < giveUp && !_chargePlaced)
            {
                cands.Clear();
                foreach (var b in UnityEngine.Object.FindObjectsOfType<BotOwner>())
                {
                    if (b == null || b.Profile?.Info?.Settings?.Role != (WildSpawnType)BdIb || IsPenBot(b)) continue;
                    var p = b.GetPlayer;
                    if (p == null || p.HealthController == null || !p.HealthController.IsAlive) continue;
                    foreach (var a in anchors)
                        if ((b.Position - a).sqrMagnitude < 35f * 35f) { cands.Add(b); break; }
                }
                if (cands.Count > 0) break;
                yield return new WaitForSeconds(1f);
            }
            if (_chargePlaced) yield break;
            if (cands.Count == 0)
            {
                Plugin.Log.LogDebug($"[Crew] SZ-1 placement: nobody near {string.Join("/", zoneNames)} in time — leaving it for the next squad");
                yield break;
            }

            // shuffle, then stable-sort backpack carriers to the front — random pick
            // within each tier, bag carriers always tried first
            foreach (var b in cands.OrderBy(_ => UnityEngine.Random.value).OrderByDescending(b => BackpackOf(b) != null).ToList())
            {
                if (StuffCharge(b))
                {
                    _chargePlaced = true;
                    yield break;
                }
            }
            Plugin.Log.LogWarning($"[Crew] SZ-1 placement: no room on anyone near {string.Join("/", zoneNames)} — leaving it for the next squad");
        }

        private static EFT.InventoryLogic.CompoundItem BackpackOf(BotOwner b)
        {
            try
            {
                return b.Profile?.Inventory?.Equipment?.GetSlot(EFT.InventoryLogic.EquipmentSlot.Backpack)?.ContainedItem
                    as EFT.InventoryLogic.CompoundItem;
            }
            catch { return null; }
        }

        private static bool StuffCharge(BotOwner b)
        {
            try
            {
                var factory = Singleton<ItemFactoryClass>.Instance;
                if (factory == null) return false;
                var item = factory.CreateItem(factory.MongoID_0, IcebreakerChainDoor.ChargeTpls[0], null);
                if (item == null) return false;

                var grids = new List<StashGridClass>();
                var bag = BackpackOf(b);
                int bagGrids = 0;
                if (bag != null && bag.Grids != null) { grids.AddRange(bag.Grids); bagGrids = bag.Grids.Length; }
                var pockets = b.Profile?.Inventory?.Equipment?.GetSlot(EFT.InventoryLogic.EquipmentSlot.Pockets)?.ContainedItem
                    as EFT.InventoryLogic.CompoundItem;
                if (pockets != null && pockets.Grids != null) grids.AddRange(pockets.Grids);

                for (int i = 0; i < grids.Count; i++)
                {
                    var loc = grids[i].FindFreeSpace(item);
                    if (loc == null) continue;
                    // WithoutRestrictions: skip container FILTERS (geometry still applies) —
                    // a pocket excluded-filter must not veto the guaranteed charge
                    if (!grids[i].AddItemWithoutRestrictions(item, loc).Succeeded) continue;
                    Plugin.Log.LogDebug($"[Crew] SZ-1 charge placed in '{b.name}' {(i < bagGrids ? "BACKPACK" : "pockets")} — one per raid, go find him");
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Crew] SZ-1 stuff failed on '{b?.name}': {e.Message}");
                return false;
            }
        }

        // ENGINE SQUAD HOLD — the hides squad spawns at the cutscene but retail's ambush
        // beat is that they're WAITING when you descend. the first `freePatrol` of them
        // skip the hold entirely and Guard-roam the room (visible presence before the
        // trigger); `holdCount` crouch at the hide markers via the bigbrain Hold job and
        // release into Hunt when the player passes the engine-room trigger. spawn
        // staging only: no aggro/mind pokes, combat still overrides everything by design.
        private bool _holdCombatLogged;

        private IEnumerator HoldEngineSquad(int freePatrol, int holdCount, int reinforcements = 0)
        {
            int expected = freePatrol + holdCount;
            // release box = the authored engine trigger; fallback = glowstick box (same
            // as the old engine-room watcher did). the authored box floats at chest height (y 10.3-12.4,
            // floor ~8.5) — fine for a physics trigger vs the player CAPSULE, but we test
            // Player.Position which is the FEET: pad vertically so a floor-level point
            // registers.
            Bounds bounds;
            var trigGo = GameObject.Find(EngineTrigger);
            var trigCol = trigGo != null ? (trigGo.GetComponent<Collider>() ?? trigGo.GetComponentInChildren<Collider>(true)) : null;
            if (trigCol != null) bounds = trigCol.bounds;
            else
            {
                Vector3 center = EngineLandmarkFallback;
                var lm = GameObject.Find(EngineLandmark);
                if (lm != null) center = lm.transform.position;
                bounds = new Bounds(center, new Vector3(12f, 8f, 12f));
            }
            bounds = PadDown(bounds, 0f, 2.5f); // feet vs chest-height box — floor reach only, never up
            Plugin.Log.LogWarning($"[Crew] engine squad hold armed — release box {bounds.center} size {bounds.size} ({(trigCol != null ? "bundle trigger" : "glowstick fallback")})");

            var hideZone = UnityEngine.Object.FindObjectsOfType<BotZone>()
                .FirstOrDefault(z => z.name == "BotZoneEngineHide" && z.SpawnPointMarkers != null && z.SpawnPointMarkers.Count > 0);
            var anchor = hideZone != null ? hideZone.SpawnPointMarkers[0].transform.position : EngineLandmarkFallback;


            var held = new HashSet<BotOwner>();      // ambushers — Hold job, released by the trigger
            var free = new List<BotOwner>();         // room patrollers — Guard job, never held
            // patrol box spans the hide markers AND the release-trigger area so the free
            // pair visibly roams the engine room proper, not just the hide corners
            var roamBox = new Bounds(bounds.center, new Vector3(12f, 5f, 12f));
            roamBox.Encapsulate(anchor);
            roamBox.Expand(new Vector3(4f, 2f, 4f));
            var world = Singleton<GameWorld>.Instance;
            float giveUp = Time.time + 900f; // failsafe: never hold a squad forever
            float nextHeavy = 0f;
            while (Time.time < giveUp)
            {
                // heavy work (bot scan + re-pause) at 0.5s cadence; the box check runs
                // per frame below
                // any human trips the release, not just the host (coop)
                if (Time.time < nextHeavy) { if (held.Count > 0 && FikaBridge.AnyHumanIn(bounds)) break; yield return null; continue; }
                nextHeavy = Time.time + 0.5f;
                if (free.Count + held.Count < expected)
                    foreach (var b in UnityEngine.Object.FindObjectsOfType<BotOwner>())
                    {
                        if (free.Count + held.Count >= expected) break; // one sweep used to add 5/4
                        // IsPenBot: a pool bot in pen transit stands at its birth marker
                        // for a frame or two — the 07-28 raid held one, and it spent the
                        // rest of the raid paused under the ice
                        if (b != null && b.Profile?.Info?.Settings?.Role == (WildSpawnType)BdIb
                            && (b.Position - anchor).sqrMagnitude < 30f * 30f
                            && !IsPenBot(b) && !free.Contains(b) && !held.Contains(b))
                        {
                            // first `freePatrol` roam the room (Guard); the rest are the
                            // AMBUSH — Hold crouches them at the hide markers, WAITING when
                            // you descend. the layer replaces the patrol-pause under SAIN;
                            // the pokes below stay as vanilla fallback for the held only
                            if (free.Count < freePatrol)
                            {
                                free.Add(b);
                                // 10s rush: walk OUT of the hide room to the first roam
                                // point no matter what SAIN thinks of the ambient noise
                                IceCrewJobs.Assign(b, IceCrewJobs.Job.Guard, roamBox, rushSeconds: 10f);
                                Plugin.Log.LogDebug($"[Crew] engine squad patroller loose in the room ({free.Count}/{freePatrol})");
                            }
                            else
                            {
                                held.Add(b);
                                IceCrewJobs.Assign(b, IceCrewJobs.Job.Hold);
                                Plugin.Log.LogDebug($"[Crew] engine squad member held ({held.Count}/{holdCount})");
                            }
                        }
                    }

                // RE-pause every poll: activation and goal changes silently reset patrol
                // status. pause only stops the patrol layer — walk in on them early and
                // combat still runs. NOTE that means gunfire/noise alerts CAN move them
                // (search/combat layers aren't gated) — log it once so a "spread out"
                // sighting can be told apart from a broken hold.
                foreach (var b in held)
                {
                    if (b == null || b.PatrollingData == null || b.GetPlayer == null
                        || b.GetPlayer.HealthController == null || !b.GetPlayer.HealthController.IsAlive) continue;
                    b.PatrollingData.Pause();
                    if (!_holdCombatLogged)
                    {
                        try
                        {
                            if (b.Memory != null && (b.Memory.GoalEnemy != null || b.Memory.IsUnderFire))
                            {
                                _holdCombatLogged = true;
                                Plugin.Log.LogDebug("[Crew] held engine squad ENGAGED early (enemy/underfire) — combat overrides the hold by design, expect repositioning");
                            }
                        }
                        catch { _holdCombatLogged = true; } // Memory API drift — don't spam retries
                    }
                }

                if (held.Count > 0 && FikaBridge.AnyHumanIn(bounds)) break;
                // PER-FRAME, not 0.5s: the authored box is only 2.4m deep — a sprinting
                // player crosses it in ~0.35s and slipped between polls
                yield return null;
            }

            // Unpause alone restores PrevStatus — which was captured at the FIRST Pause,
            // before the patrol ever reached 'go': the squad released into a dead 'stay'
            // with no target point and never moved. force a fresh cycle: go + new point.
            int released = 0, deadOrGone = 0;
            foreach (var b in held)
            {
                if (b == null || b.PatrollingData == null
                    || b.GetPlayer == null || b.GetPlayer.HealthController == null
                    || !b.GetPlayer.HealthController.IsAlive)
                {
                    deadOrGone++;
                    continue;
                }
                try
                {
                    // phantom combat is the emergence-killer: they HEAR the descent
                    // gunfight while paused (Pause only stops the patrol layer), flip to
                    // combat/search, and a combat brain ignores patrol commands. clearing
                    // the remembered enemy hands control back to patrol; REAL contact
                    // re-acquires through vision instantly, so no combat ability is lost.
                    if (b.Memory != null && b.Memory.GoalEnemy != null) b.Memory.GoalEnemy = null;
                    // 15s RUSH: the deploy must not lose a priority fight to SAIN's
                    // hold-position combat — they move NOW, combat AI gets them after
                    IceCrewJobs.Assign(b, IceCrewJobs.Job.Hunt, rushSeconds: 15f);
                    b.PatrollingData.Unpause();
                    b.PatrollingData.RefreshStatus();            // status = go, unconditionally
                    b.PatrollingData.FindNextPoint(true, false); // pick a point + set course
                    b.Sprint(true, true); // deploy at a run — walk-pace emergence reads as "late"
                    released++;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Crew] release failed on '{b.name}': {e.Message}");
                }
            }
            // the loose patrollers join the push too — they're already in the room,
            // Hunt just stops them wandering back to the far corner of the roam box
            foreach (var b in free)
                if (b != null && b.GetPlayer != null && b.GetPlayer.HealthController != null && b.GetPlayer.HealthController.IsAlive)
                    IceCrewJobs.Assign(b, IceCrewJobs.Job.Hunt, rushSeconds: 15f);
            Plugin.Log.LogDebug($"[Crew] ENGINE SQUAD RELEASED — {released} black division moving out"
                + (deadOrGone > 0 ? $" ({deadOrGone} of the held were dead/despawned by release time)" : ""));

            // who actually owns each bot 8s in — the tell for a priority fight (SAIN
            // layer name = we lost) or an uncovered brain (vanilla layer name = the
            // ExUsec/Pmc* registration missed this bot's brain entirely)
            StartCoroutine(LogCrewBrains(new List<BotOwner>(held.Concat(free)), "post-release"));

            // reinforcement wave (user call 07-28): the other half of the squad stayed in
            // the pen and pushes in the moment the trigger blows. 12m hard player
            // exclusion keeps the teleport off-screen (they appear behind machinery/doors
            // and run in — if every lower-deck marker is inside 12m PickPoints keeps the
            // full set, so a visible materialize is possible but unlikely). pen shortfall
            // falls back to a classic spawn: pays the old hitch, but only if the pen ran dry.
            if (reinforcements > 0 && hideZone != null)
            {
                var reinf = new List<BotOwner>();
                int got = DeliverFromPool((WildSpawnType)BdIb, hideZone, reinforcements, 12f, reinf);
                foreach (var b in reinf)
                {
                    try
                    {
                        IceCrewJobs.Assign(b, IceCrewJobs.Job.Hunt, rushSeconds: 15f); // bigbrain: it's a push
                        // same dead-'stay' fix as the release above — Unpause alone
                        // restores a pre-'go' status and they'd stand at the drop point
                        b.PatrollingData.RefreshStatus();
                        b.PatrollingData.FindNextPoint(true, false);
                        b.Sprint(true, true); // it's a push, not a patrol
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"[Crew] reinforcement cycle failed on '{b.name}': {e.Message}"); }
                }
                for (int i = got; i < reinforcements; i++)
                {
                    // same 12m off-screen rule as the pool path — these spawn while the
                    // player is standing in the release box
                    var t = ForceSpawn((WildSpawnType)BdIb, hideZone, 12f);
                    while (!t.IsCompleted) yield return null;
                }
                Plugin.Log.LogDebug($"[Crew] ENGINE REINFORCEMENTS — {reinforcements} pushing in ({got} from the pen)");
            }
            else if (reinforcements > 0)
                Plugin.Log.LogWarning("[Crew] reinforcements skipped — BotZoneEngineHide not found");
        }

        // BLACK DIVISION — trigger-gated (retail: they arrive after the start cutscene).
        // watch the player against the Icebreaker_StartCutsceneTrigger volume; on first
        // overlap, force-spawn the squads. type ids from the BlackDiv mod's prepatch
        // (mod must be installed): 848420 blackDivLead, 848421 blackDivAssault,
        // 848424 bossWedge, 848426 blackDivIb.
        // EVERY black division bot on this map is blackDivIb (848426) — it is the
        // icebreaker-specific type. blackDivAssault is the generic one and was only ever a
        // stand-in; a first pass on 08-01 changed the force-spawn path alone and missed the
        // batch spawner AND the 25-bot premake cache, so a raid still came out 1949
        // blackDivAssault to 1 blackDivIb. all twelve sites are converted now.
        private static readonly string[] BlackDivZones =
        {
            "BotZoneSternTop", "BotZoneOutside_t3", "BotZoneStern", "BotZoneBack",
        };
        // BdLead (848420) intentionally unused — its server profile always generates naked
        internal const int BdLead = 848420;
        // kept ONLY so BdRogueRelations still recognises a generic BD if something else
        // spawns one — nothing in this file spawns it any more (08-01)
        internal const int BdAssault = 848421;
        // the icebreaker-specific black division type (user, 08-01). every BD that spawns
        // on this map should be this one — blackDivAssault is the generic mod type and was
        // only ever a stand-in here.
        internal const int BdIb = 848426;        // blackDivIb
        internal const int BdWedge = 848424;     // bossWedge — the black division boss
        private static readonly string[] WedgeZones = { "BotZoneRoomsThird", "BotZoneRoomsThirdKitchen" };

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
        // "why" separates two entirely different diseases the old bool collapsed
        // (08-05 rental naked-storm hunt): an EMPTY response is the server refusing to
        // produce bots at all (per-raid cap/state — the transit leg is the suspect);
        // unarmed profiles are the generator failing on equipment. the fix differs.
        internal static string NakedWhy(BotCreationDataClass data)
        {
            try
            {
                if (data == null) return "null data";
                var profiles = data.Profiles;
                if (profiles == null) return "EMPTY RESPONSE (null profile list)";
                if (profiles.Count == 0) return "EMPTY RESPONSE (0 profiles)";
                return IsNakedProfile(data) ? $"UNARMED profiles ({profiles.Count} returned, all weapon slots empty)" : null;
            }
            catch (Exception e) { return $"vet failed: {e.Message}"; }
        }

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
            var profileData = new BotProfileDataClass(EPlayerSide.Savage, role, BotDifficulty.normal, 5f, spawnParams, false);
            var data = await BotCreationDataClass.Create(profileData, _spawner.BotCreator, count, _spawner);
            if (data == null) { Plugin.Log.LogWarning($"[Crew] profile creation failed for {role}"); return null; }

            // naked roll — give the generator a breather and re-request ONCE; if it
            // fails again, skip this batch entirely (a missing bot beats a mannequin)
            var why = NakedWhy(data);
            if (why != null)
            {
                Plugin.Log.LogWarning($"[Crew] {role} profile arrived NAKED [{why}] — re-requesting in 3s");
                await Task.Delay(3000);
                data = await BotCreationDataClass.Create(profileData, _spawner.BotCreator, count, _spawner);
                if (data == null || IsNakedProfile(data))
                {
                    Plugin.Log.LogWarning($"[Crew] {role} re-request also bad [{NakedWhy(data) ?? "ok??"}] — skipping this spawn");
                    return null;
                }
            }
            return data;
        }

        // pick N spawn points, preferring ones the player won't watch materialize:
        // anything beyond 25m of the player first (shuffled), close points only as a
        // last resort. minPlayerDist > 0 makes the exclusion HARD (rogue fill) — a
        // soft preference is useless once the player stands among the markers.
        // wraps if the zone has fewer markers than N.
        private static List<EFT.Game.Spawning.ISpawnPoint> PickPoints(BotZone zone, int count, float minPlayerDist = 0f)
        {
            var pts = zone.SpawnPoints;
            if (pts == null || pts.Length == 0) return null;
            var pool = new List<EFT.Game.Spawning.ISpawnPoint>();
            foreach (var p in pts) if (p != null) pool.Add(p);
            // markers with a living bot already on them are out — back-to-back squad
            // deliveries into the same zone (the two stern teams) reshuffled the same
            // marker set and teleported bots into each other (07-28 raid)
            var living = UnityEngine.Object.FindObjectsOfType<BotOwner>();
            var free = pool.FindAll(p =>
            {
                foreach (var b in living)
                    if (b != null && !b.IsDead && (b.Position - p.Position).sqrMagnitude < 4f)
                        return false;
                return true;
            });
            if (free.Count > 0) pool = free; // all occupied: keep the full set, wrap-offset handles it
            if (minPlayerDist > 0f)
            {
                {
                    // nearest HUMAN, not MainPlayer — in coop a bot materializing in
                    // front of a remote teammate is just as ugly as in front of the host
                    var farOnly = pool.FindAll(p => FikaBridge.NearestHumanSqr(p.Position) > minPlayerDist * minPlayerDist);
                    if (farOnly.Count > 0) pool = farOnly;
                    // zone entirely inside the bubble: keep the pool (spawning beats not
                    // spawning) — the soft far-sort below still does its best
                }
            }
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
            // soft preference: points no human is near (>25m) rank first. humans are
            // collected once — the comparator runs O(n log n) times
            var humans = new List<Player>();
            FikaBridge.CollectHumans(humans);
            bool Far(EFT.Game.Spawning.ISpawnPoint sp)
            {
                foreach (var h in humans)
                    if ((sp.Position - h.Position).sqrMagnitude <= 625f) return false;
                return true;
            }
            if (humans.Count > 0)
                pool.Sort((a, b) => (Far(b) ? 1 : 0) - (Far(a) ? 1 : 0));
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
                // pen bots first — a teleport costs nothing and the squad appears
                // instantly; anything beyond the pool runs the classic pipeline below
                int fromPen = DeliverFromPool(role, zone, count);
                count -= fromPen;
                if (count <= 0) return;

                var ready = new List<BotCreationDataClass>(count);
                if (_preMade.TryGetValue((int)role, out var pq))
                    while (ready.Count < count && pq.Count > 0)
                        ready.Add(pq.Dequeue());
                int fromCache = ready.Count;
                int need = count - ready.Count;
                if (need > 0)
                {
                    // ALL profile requests concurrently — sequential awaits made a
                    // 4-bot batch cost 4x the server round-trip (plus 3s naked retries)
                    var creates = new List<Task<BotCreationDataClass>>(need);
                    for (int i = 0; i < need; i++) creates.Add(CreateAndPrewarm(role));
                    foreach (var d in await Task.WhenAll(creates))
                        if (d != null) ready.Add(d); // naked twice — skip that bot, keep the squad
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
                // rogues get a HARD 35m player-exclusion (the old far-sort was only a
                // preference — useless once the player stands among the markers); the
                // engine/stern trigger squads keep close spawns by design
                var pts = PickPoints(zone, ready.Count, role == WildSpawnType.exUsec ? 35f : 0f);
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
        // create + prewarm as one awaitable unit so batches can run them all in parallel
        private async Task<BotCreationDataClass> CreateAndPrewarm(WildSpawnType role)
        {
            var d = await CreateData(role);
            if (d == null) return null;
            await Prewarm(d);
            return d;
        }

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
            // full event roster: engine 2+2 + stern 3+3+3 + wedge 4 + T3 3 + T4 5
            // = 25 assaults (the wedge boss slot is a plain assault now, user call
            // 07-28). extras beyond the cache fall back to on-demand creation.
            var wants = new List<WildSpawnType>();
            for (int i = 0; i < 25; i++) wants.Add((WildSpawnType)BdIb);
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
            Plugin.Log.LogDebug($"[Crew] trigger squads pre-made: {total} bots cached + bundle-warm");
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

        // ---- PRE-SPAWN POOL (user call 07-27) ----
        // the profile premake killed the server round-trip, but the trigger frame still
        // paid instantiation + cold gear bundles (the 6.4s frame in the 07-25 log). pool
        // mode spawns the whole roster during the quiet early raid, whisks each bot to a
        // pen far off the map, and events TELEPORT them in — the same delivery points,
        // the same hold/release staging, none of the cost.
        //
        // each squad pen-spawns INTO ITS DESTINATION ZONE first (so BotsGroup/patrol data
        // point at the right part of the ship) and only then moves to the pen — the pen
        // itself is a bare collider slab below the ice, out of earshot and sight, where
        // paused patrol + distance keep the bots dormant.
        private struct PenEntry { public BotOwner Bot; public string ZoneName; }
        private static readonly Dictionary<string, (int role, string zone)> _penPending = new Dictionary<string, (int, string)>();
        private static readonly List<BotOwner> _penIntake = new List<BotOwner>();
        private static readonly Dictionary<int, List<PenEntry>> _pool = new Dictionary<int, List<PenEntry>>();
        private static readonly Vector3 PenBase = new Vector3(350f, -58f, 0f);
        private static int _penSlot;

        private IEnumerator PoolSpawnTriggerSquads()
        {
            _penPending.Clear(); _penIntake.Clear(); _pool.Clear(); _penSlot = 0;

            var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            slab.name = "Icebreaker_BotPen";
            slab.transform.position = PenBase + Vector3.down;
            slab.transform.localScale = new Vector3(30f, 1f, 30f);
            var mr = slab.GetComponent<MeshRenderer>();
            if (mr != null) mr.enabled = false;

            // destination-true plan, one entry per bot, counts EXACTLY mirroring the
            // events — headroom bots turned out to be poison: a spare EngineHide bot
            // delivered cross-zone by the old any-role fallback kept its birth-zone
            // patrol data and trekked home across the ship (07-28 raid: piles of black
            // division wandering the stern). shortfalls fall back to the classic
            // premake/create path inside ForceSpawnBatch, which spawns INTO the zone.
            // T4 (5x Inside_t4) is deliberately not pooled: late-raid + rare, and the
            // pen already flirts with the alive-bot budget next to the rogue waves.
            var plan = new List<(WildSpawnType role, string zone)>();
            void Add(int n, WildSpawnType r, string z) { for (int i = 0; i < n; i++) plan.Add((r, z)); }
            Add(4, (WildSpawnType)BdIb, "BotZoneEngineHide");  // hides0 squad
            Add(3, (WildSpawnType)BdIb, "BotZoneSternTop");    // stern first team
            Add(6, (WildSpawnType)BdIb, "BotZoneStern");       // stern second + third teams
            // wedge detail = bossWedge + 3 escorts. premade to MATCH what actually spawns
            // (boss at zone[0], escorts alternating from zone[1]) — a role the plan does not
            // reserve gets built cold at spawn time, which is exactly when profiles come
            // back naked
            Add(1, (WildSpawnType)BdWedge, WedgeZones[0]);     // the boss
            Add(1, (WildSpawnType)BdIb, WedgeZones[0]);
            Add(2, (WildSpawnType)BdIb, WedgeZones[1]);
            Add(3, (WildSpawnType)BdIb, "BotZoneOutside_t3");  // T3 deployment

            var zonesByName = UnityEngine.Object.FindObjectsOfType<BotZone>()
                .Where(z => z.SpawnPointMarkers != null && z.SpawnPointMarkers.Count > 0)
                .GroupBy(z => z.name).ToDictionary(g => g.Key, g => g.First());

            foreach (var (role, zoneName) in plan)
            {
                if (!zonesByName.TryGetValue(zoneName, out var zone))
                {
                    Plugin.Log.LogWarning($"[Crew] pool: no zone '{zoneName}' — {role} skipped");
                    continue;
                }
                // live event spawns keep absolute priority on the generator queue
                while (_squadSpawnBusy) yield return new WaitForSeconds(0.5f);
                var t = PoolMakeOne(role, zone);
                while (!t.IsCompleted) yield return null;
                yield return new WaitForSeconds(2f); // pace the queue — the naked-profile lesson
            }
            int total = 0; foreach (var l in _pool.Values) total += l.Count;
            Plugin.Log.LogDebug($"[Crew] pen pool built: {total} bots spawned + parked ({_penIntake.Count} still settling)");
        }

        private async Task PoolMakeOne(WildSpawnType role, BotZone zone)
        {
            try
            {
                var data = await CreateData(role);
                if (data == null) return;
                await Prewarm(data);
                // tag the profiles so Patch_PenIntake recognizes the bots at Create
                foreach (var pr in data.Profiles) _penPending[pr.Id] = ((int)role, zone.name);
                _spawner.TryToSpawnInZoneInner(zone, data, 1, false, true, PickPoints(zone, 1, 35f), true);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Crew] pool make {role} failed: {e.Message}"); }
        }

        // intake runs a frame late on purpose: teleporting inside the Create postfix
        // races the activation chain's own placement, and PatrollingData may not exist
        // yet. the bot spawns at a far marker (35m player exclusion), stands there for
        // a frame or two, then vanishes into the pen.
        private void Update()
        {
            if (_penIntake.Count == 0) return;
            for (int i = _penIntake.Count - 1; i >= 0; i--)
            {
                var b = _penIntake[i];
                if (b == null) { _penIntake.RemoveAt(i); continue; }
                if (b.GetPlayer == null || b.PatrollingData == null) continue; // not settled yet
                _penIntake.RemoveAt(i);
                if (!_penPending.TryGetValue(b.ProfileId, out var info)) continue;
                _penPending.Remove(b.ProfileId);
                var slot = PenBase + new Vector3((_penSlot % 5) * 2.5f, 0f, (_penSlot / 5) * 2.5f);
                _penSlot++;
                try { b.GetPlayer.Teleport(slot); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Crew] pen teleport failed for '{b.name}': {e.Message}"); }
                try { b.PatrollingData.Pause(); } catch { }
                if (!_pool.TryGetValue(info.role, out var list)) _pool[info.role] = list = new List<PenEntry>();
                list.Add(new PenEntry { Bot = b, ZoneName = info.zone });
                Plugin.Log.LogInfo($"[Crew] penned '{b.name}' ({(WildSpawnType)info.role}) for {info.zone}");
            }
        }

        [HarmonyPatch(typeof(BotOwner), nameof(BotOwner.Create))]
        internal static class Patch_PenIntake
        {
            [HarmonyPostfix]
            private static void Postfix(BotOwner __result)
            {
                try
                {
                    if (__result == null || _penPending.Count == 0) return;
                    var pid = __result.ProfileId;
                    if (pid != null && _penPending.ContainsKey(pid)) _penIntake.Add(__result);
                }
                catch { }
            }
        }

        // events call this through ForceSpawnBatch/ForceSpawn: exact zone-matched pen
        // bots first, wedge-FAMILY matches second (the wedge event splits its squad
        // across both RoomsThird zones nondeterministically, so a bot tagged for one
        // is at home in the other). NO any-zone pass: a cross-zone teleport keeps the
        // bot's birth-zone patrol data and it walks home across the ship — the 07-28
        // wandering piles. shortfalls go to the classic spawn path, which is correct.
        private static readonly HashSet<string> WedgeFamily = new HashSet<string>(WedgeZones);

        private int DeliverFromPool(WildSpawnType role, BotZone zone, int count,
            float minPlayerDist = 0f, List<BotOwner> deliveredOut = null)
        {
            if (!Plugin.CrewPreSpawnPool.Value) return 0;
            if (!_pool.TryGetValue((int)role, out var list) || list.Count == 0) return 0;
            bool zoneIsWedge = WedgeFamily.Contains(zone.name);
            var picks = new List<BotOwner>();
            for (int pass = 0; pass < 2 && picks.Count < count; pass++)
                for (int i = list.Count - 1; i >= 0 && picks.Count < count; i--)
                {
                    var e = list[i];
                    if (e.Bot == null || e.Bot.IsDead) { list.RemoveAt(i); continue; }
                    if (pass == 0 && e.ZoneName != zone.name) continue;
                    if (pass == 1 && !(zoneIsWedge && WedgeFamily.Contains(e.ZoneName))) continue;
                    picks.Add(e.Bot);
                    list.RemoveAt(i);
                }
            if (picks.Count == 0) return 0;

            var pts = PickPoints(zone, picks.Count, minPlayerDist); // trigger squads spawn close by default
            for (int i = 0; i < picks.Count; i++)
            {
                var b = picks[i];
                var pos = pts != null ? pts[i % pts.Count].Position : zone.transform.position;
                // wrapped onto an already-used point (zone has fewer free markers than
                // bots): ring-offset so nobody teleports inside a squadmate
                if (pts == null || i >= pts.Count)
                {
                    float ang = i * 2.4f; // golden-angle-ish, no two offsets align
                    pos += new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * 1.6f;
                }
                try { b.GetPlayer.Teleport(pos); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Crew] pool delivery teleport failed: {e.Message}"); }
                ReanchorCore(b, pos);
                try { b.PatrollingData.Unpause(); } catch { }
                deliveredOut?.Add(b);
            }
            Plugin.Log.LogDebug($"[Crew] delivered {picks.Count}/{count}x {role} from the pen into {zone.name}");
            return picks.Count;
        }

        // the hold scan and other role-based sweeps must never claim a bot that is
        // pen-parked or still in transit to the pen — it would be held (and released)
        // 60m under the ice
        internal static bool IsPenBot(BotOwner b)
        {
            if (b == null) return false;
            var pid = b.ProfileId;
            if (pid != null && _penPending.ContainsKey(pid)) return true;
            foreach (var l in _pool.Values)
                foreach (var e in l)
                    if (ReferenceEquals(e.Bot, b)) return true;
            return false;
        }

        // the pen anchored StartCorePoint near the pen-spawn marker; after the delivery
        // teleport the anchor must follow, or path requests reference the wrong graph
        // neighborhood (same nearest-core logic as RaidFix's spawn safety net)
        private static void ReanchorCore(BotOwner bot, Vector3 pos)
        {
            try
            {
                var covers = UnityEngine.Object.FindObjectOfType<AICoversData>();
                var cores = covers != null && covers.AICorePointsHolder != null ? covers.AICorePointsHolder.CorePoints : null;
                if (cores == null || cores.Count == 0) return;
                AICorePoint best = null;
                float bestD = float.MaxValue;
                foreach (var c in cores)
                {
                    if (c == null) continue;
                    float d = (c.Position - pos).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = c; }
                }
                if (best != null) bot.StartCorePoint = best;
            }
            catch { }
        }

        private async Task ForceSpawn(WildSpawnType role, BotZone zone, float minPlayerDist = 0f)
        {
            if (DeliverFromPool(role, zone, 1, minPlayerDist) > 0) return;
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
                _spawner.TryToSpawnInZoneInner(zone, data, 1, false, true, PickPoints(zone, 1, minPlayerDist), true);
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

    // HOSTILITY REWRITE (user calls 07-17) — two map-specific rules the vanilla matrix
    // gets wrong here:
    //  1) rogues are the ship's crew defending it: the human player is ALWAYS the enemy.
    //     vanilla exUsec neutrality (BotsGroup.method_1: usec-kill-counter / mixed-group
    //     checks) means a clean-record usec walks the deck unchallenged.
    //  2) black division and the rogues are on the same side (BSG runs them friendly) —
    //     without this the held engine squad shreds the engine-room crew before the
    //     player ever gets below deck (last raid: ENGAGED early + empty engine room).
    internal static class BdRogueRelations
    {
        internal static bool IsBd(WildSpawnType r)
        {
            int i = (int)r;
            return i == IcebreakerCrew.BdLead || i == IcebreakerCrew.BdAssault
                || i == IcebreakerCrew.BdIb || i == IcebreakerCrew.BdWedge;
        }

        internal static bool IsFriendlyPair(WildSpawnType a, WildSpawnType b)
            => (IsBd(a) && b == WildSpawnType.exUsec) || (a == WildSpawnType.exUsec && IsBd(b));
    }

    [HarmonyPatch(typeof(BotsGroup), nameof(BotsGroup.IsPlayerEnemy))]
    internal static class Patch_BotsGroupInitialHostility
    {
        [HarmonyPostfix]
        private static void Postfix(BotsGroup __instance, IPlayer player, ref bool __result)
        {
            try
            {
                if (!IceGate.On) return; // audit P0: rogue/BD relations are icebreaker lore, not lighthouse's
                var self = __instance.InitialBotType;
                bool selfRogue = self == WildSpawnType.exUsec;
                if (!selfRogue && !BdRogueRelations.IsBd(self)) return;
                if (player.AIData != null && player.AIData.IsAI)
                {
                    var other = player.AIData.BotOwner?.Profile?.Info?.Settings?.Role;
                    if (other != null && BdRogueRelations.IsFriendlyPair(self, other.Value))
                        __result = false;
                }
                else if (selfRogue)
                {
                    __result = true; // human player — no usec-neutrality on this map
                }
            }
            catch { } // hostility fallback = vanilla verdict, never break group creation
        }
    }

    // the initial matrix isn't the only entry: provocation paths (friendly fire, revenge
    // logic) go through CheckAndAddEnemy — skip it entirely for the BD/rogue pair so one
    // stray hit can't escalate into a ship-wide civil war.
    [HarmonyPatch(typeof(BotsGroup), nameof(BotsGroup.CheckAndAddEnemy))]
    internal static class Patch_BotsGroupProvokedHostility
    {
        [HarmonyPrefix]
        private static bool Prefix(BotsGroup __instance, IPlayer player, ref bool __result)
        {
            try
            {
                if (!IceGate.On) return true;
                if (player.AIData != null && player.AIData.IsAI)
                {
                    var other = player.AIData.BotOwner?.Profile?.Info?.Settings?.Role;
                    if (other != null && BdRogueRelations.IsFriendlyPair(__instance.InitialBotType, other.Value))
                    {
                        __result = false;
                        return false;
                    }
                }
            }
            catch { }
            return true;
        }
    }
}
