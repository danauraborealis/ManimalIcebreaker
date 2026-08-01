using System;
using EFT;
using EFT.SynchronizableObjects;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // THE STATUE-BOT ROOT CAUSE (caught live by the stepwise activation witness):
    // follower joins a boss -> formation slot -> PatrolPoint.GetSubPoint(index) —
    // and our GENERATED patrol points ship with ZERO sub-points (retail bakes ~6
    // formation offsets per point; our AI generator never did). empty list makes
    // GetSubPoint's own clamp produce index -1 -> ArgumentOutOfRange -> BSG's
    // activation try{} swallows it -> statue. two-part fix: build the missing
    // sub-points at raid start via the game's OWN generator (navmesh-sampled,
    // the non-SubManual path of CreateSubPoints), plus a hard guard on GetSubPoint.
    [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
    internal static class Patch_BuildMissingSubPoints
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            if (!IceGate.On) return;
            int ways = 0, built = 0, failed = 0;
            try
            {
                foreach (var zone in UnityEngine.Object.FindObjectsOfType<BotZone>(true))
                {
                    if (zone.PatrolWays == null) continue;
                    foreach (var way in zone.PatrolWays)
                    {
                        if (way == null || way.Points == null) continue;
                        ways++;
                        foreach (var p in way.Points)
                        {
                            if (p == null || p.SubPointsCount > 0) continue;
                            try { p.CreateSubPoints(way); built++; }
                            catch { failed++; }
                        }
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[SubPoints] sweep failed: {e.Message}"); }
            Plugin.Log.LogWarning($"[SubPoints] generated formation sub-points on {built} patrol points ({ways} ways, {failed} failed)");
        }
    }

    // SAIN corridor fights: with a dozen rogues in tight ship hallways, sprays meant
    // for the player rake through squadmates — the FF hit flips allies into revenge
    // aggro and whole squads eat each other. rule: an AI hit by an AI that is NOT
    // currently an enemy of its group takes no damage at all (ally accident, voided
    // before the being-hit reaction chain even starts). genuine bot-vs-bot fights
    // (attacker already in the group's enemy dict) stay fully lethal, and the player
    // is never protected.
    [HarmonyPatch(typeof(Player), nameof(Player.ApplyDamageInfo))]
    internal static class Patch_NoAllyBotFriendlyFire
    {
        [HarmonyPrefix]
        private static bool Prefix(Player __instance, DamageInfoStruct damageInfo)
        {
            if (!IceGate.On || Plugin.BotFriendlyFire.Value) return true;
            try
            {
                if (__instance == null || !__instance.IsAI) return true;
                var attacker = damageInfo.Player != null ? damageInfo.Player.iPlayer : null;
                if (attacker == null || !attacker.IsAI) return true;
                if (ReferenceEquals(attacker, __instance)) return true; // self-damage (own grenade) stays real
                var group = __instance.AIData?.BotOwner?.BotsGroup;
                if (group == null) return true;
                if (group.Enemies.ContainsKey(attacker)) return true;   // real fight — lethal
                return false; // ally accident — voided
            }
            catch { return true; }
        }
    }

    // the ship is a frozen-NIGHT map but lit everywhere — vanilla vision multiplies
    // sight distance by the time-of-day curve, leaving bots nearly blind under bright
    // lamps ("staring at a wall until touched"). fix ONLY the perception range: the
    // original method has already run its full night pipeline (ClearVisibleDist stays
    // at the night value, so NVGs flip down and flashlights click on naturally, exactly
    // like vanilla night) — the postfix then lifts the FINAL VisibleDist to day level,
    // weather debuff still applied outdoors. bots act night, see day.
    [HarmonyPatch(typeof(LookSensor), "method_2")]
    internal static class Patch_DayVisionOnLitShip
    {
        // proof-of-life: fika bots felt blind (07-28 coop) and the first question is
        // whether this postfix even runs there — one line per raid answers it
        private static int _applications;

        [HarmonyPostfix]
        private static void Postfix(LookSensor __instance)
        {
            if (!IceGate.On) return;
            if (++_applications == 200)
                Plugin.Log.LogWarning("[Vision] day-vision lift is live (200 applications this raid)");
            try
            {
                var bo = __instance.BotOwner;
                if (bo == null || bo.Settings == null) return;
                float baseDist = bo.Settings.Current.CurrentVisibleDistance;
                var look = bo.Settings.FileSettings.Look;
                // the weather multiplier is GONE too (user call 07-30). it used to be
                // applied unless the bot was inside — but AIData.IsInside is false for
                // every bot on this map (all 26 retail AIPlaceInfos ship IsInside 0), so
                // that guard never fired and the blizzard was quietly cutting the day-
                // vision lift indoors as well. permanent weather shouldn't be a permanent
                // blindfold; Patch_NoWeatherSeenDebuff removes the matching penalty on how
                // fast they notice.
                float dayDist = Mathf.Clamp(baseDist, look.MINIMUM_VISIBLE_DIST, 9999f);
                if (__instance.VisibleDist < dayDist) __instance.VisibleDist = dayDist;
            }
            catch { }
        }
    }

    // THE OTHER HALF OF THE VISION FIX. Patch_DayVisionOnLitShip above lifts how FAR a bot
    // can see; this is how FAST it registers what it sees, which is a separate chain.
    //
    // EnemyInfo.method_9 multiplies a stack of coefficients and turns the product into a
    // time-to-notice (1 / (VISIBILITY_CHANGE_SPEED * k) seconds). one of those terms is
    // method_11, the weather debuff — and it only skips the debuff when the bot AND its
    // target are both flagged inside. neither ever is here: retail authored all 26 of
    // icebreaker's AIPlaceInfos with IsInside 0, and the player's IAIData hardcodes the
    // property to false on every map. so with our permanent blizzard (BlizzardFog 0.015,
    // well up the NoFog->Continuous curve) every sighting on the ship is permanently
    // slowed — a bot stares down a lit corridor and only reacts once you're on top of it.
    //
    // killed map-wide (user call 07-30), not just indoors. the blizzard is PERMANENT here,
    // so this isn't weather the player can wait out — it's a flat, unending tax on every
    // sighting, and the ship is lit end to end. bots act night, see day; that now covers
    // how fast they notice as well as how far they see.
    [HarmonyPatch(typeof(EnemyInfo), "method_11")]
    internal static class Patch_NoWeatherSeenDebuff
    {
        private static int _applications;

        [HarmonyPostfix]
        private static void Postfix(ref float __result, ref float rainK, ref float fogK)
        {
            if (!IceGate.On) return;
            if (__result >= 1f) return;
            if (++_applications == 200)
                Plugin.Log.LogWarning("[Vision] weather seen-debuff lifted map-wide (200 applications this raid)");
            rainK = 1f; fogK = 1f; __result = 1f;
        }
    }

    // VISION FORENSICS. bots noticing the player late has now survived two fixes aimed at
    // the wrong term, so stop guessing: EnemyInfo.method_9 multiplies eight coefficients
    // and turns the product into a time-to-notice, and every one of those terms is a public
    // method. print them for the nearest bot looking at a HUMAN and the small one names
    // itself. mirrors the fields BSG's own trace builder emits.
    // DiagHotkeys-gated, one line every 2s, and it never touches the values.
    // the coefficient chain (method_9) turned out to be FINE — bot11 sat at k=1.3,
    // "notices in 0.2s", at 62m, and still ignored the player. so the speed of gaining
    // visibility was never the problem; what matters is whether visibility ACCUMULATES at
    // all, which happens per body part in CalculatePartVisibility behind a LOS raycast.
    // this logs the state that decides it:
    //   VisLvl stuck at 0      -> line of sight is failing (nothing to accumulate)
    //   VisLvl climbing slowly -> speed problem after all
    //   VisLvl high, Visible 0 -> threshold problem
    // per-BOT throttle, so one bot can be followed across an approach instead of the log
    // hopping between whichever bot happened to tick.
    [HarmonyPatch(typeof(EnemyInfo), nameof(EnemyInfo.CheckLookEnemy))]
    internal static class Patch_VisionBreakdownDiag
    {
        private static readonly System.Collections.Generic.Dictionary<string, float> _next =
            new System.Collections.Generic.Dictionary<string, float>();

        // NavMesh.CalculatePath is not free, but this whole diag is already throttled
        // per bot, so it runs at the same low cadence as the log line itself.
        // first position we ever saw for a bot ~= where it spawned (the diag starts firing
        // the moment it has an EnemyInfo, which is close enough to tell "never moved" from
        // "wandered and came back")
        private static readonly System.Collections.Generic.Dictionary<int, Vector3> _firstSeen =
            new System.Collections.Generic.Dictionary<int, Vector3>();
        private static readonly System.Collections.Generic.Dictionary<int, float> _maxDrift =
            new System.Collections.Generic.Dictionary<int, float>();

        private static readonly Vector3[] _fan =
        {
            new Vector3( 1, 0,  0), new Vector3(-1, 0,  0),
            new Vector3( 0, 0,  1), new Vector3( 0, 0, -1),
            new Vector3( 0.7f, 0, 0.7f), new Vector3(-0.7f, 0, -0.7f),
        };

        // how far can this bot ACTUALLY walk? probe outward on a fan and keep the longest
        // route it can complete. a bot loose on the ship reaches tens of metres; one sealed
        // in a freezer tops out at the size of the room.
        private static string Island(BotOwner owner)
        {
            try
            {
                if (owner == null) return "reach=<no owner>";
                var from = owner.Position;
                var path = new UnityEngine.AI.NavMeshPath();
                float best = 0f;
                int complete = 0;
                foreach (var dir in _fan)
                {
                    var target = from + dir * 60f;
                    if (!UnityEngine.AI.NavMesh.CalculatePath(from, target, UnityEngine.AI.NavMesh.AllAreas, path))
                        continue;
                    var c = path.corners;
                    if (c == null || c.Length < 2) continue;
                    // walk the corners: straight-line distance lies through walls, path length does not
                    float len = 0f;
                    for (int i = 1; i < c.Length; i++) len += Vector3.Distance(c[i - 1], c[i]);
                    if (path.status == UnityEngine.AI.NavMeshPathStatus.PathComplete) complete++;
                    if (len > best) best = len;
                }
                return $"reach={best:F1}m dirs={complete}/{_fan.Length}"
                     + (best < 12f ? "  <-- BOXED IN" : "");
            }
            catch (Exception e) { return "reach=<threw:" + e.GetType().Name + ">"; }
        }

        private static string SpawnInfo(BotOwner owner)
        {
            try
            {
                if (owner == null) return "spawn=<no owner>";
                string zone = "<none>";
                try
                {
                    var z = owner.SpawnBotZone;
                    if (z != null) zone = z.NameZone;
                    else if (owner.BotsGroup != null && owner.BotsGroup.BotZone != null)
                        zone = owner.BotsGroup.BotZone.NameZone + "*";   // * = via group, not spawn
                }
                catch { }

                int id = owner.Id;
                Vector3 first;
                if (!_firstSeen.TryGetValue(id, out first))
                {
                    first = owner.Position;
                    _firstSeen[id] = first;
                }
                float drift = Vector3.Distance(owner.Position, first);
                float peak;
                _maxDrift.TryGetValue(id, out peak);
                if (drift > peak) { peak = drift; _maxDrift[id] = peak; }
                return $"zone='{zone}' drift={drift:F1}m peak={peak:F1}m";
            }
            catch (Exception e) { return "spawn=<threw:" + e.GetType().Name + ">"; }
        }

        internal static string DecisionOf(BotOwner owner) => Decision(owner);

        private static string Decision(BotOwner owner)
        {
            try
            {
                var brain = owner?.Brain;
                if (brain == null) return "<no brain>";
                var d = brain.LastDecision;
                return d == null ? "<none>" : d.Value.ToString();
            }
            catch (Exception e) { return "<threw:" + e.GetType().Name + ">"; }
        }

        private static string PatrolReach(BotOwner owner)
        {
            try
            {
                var pd = owner?.PatrollingData;
                if (pd == null) return "ptrl=<no data>";
                var cur = pd.CurPatrolPoint;
                if (cur == null) return "ptrl=<no point>";
                var dest = cur.Position;
                float straight = Vector3.Distance(owner.Position, dest);

                var path = new UnityEngine.AI.NavMeshPath();
                bool ok = UnityEngine.AI.NavMesh.CalculatePath(
                    owner.Position, dest, UnityEngine.AI.NavMesh.AllAreas, path);
                if (!ok) return $"ptrl=NOPATH straight={straight:F1}m";
                // how far short of the point the route actually stops — a partial path that
                // ends 40m away is a bot that can never arrive and never re-picks
                float shortfall = 0f;
                var c = path.corners;
                if (c != null && c.Length > 0)
                    shortfall = Vector3.Distance(c[c.Length - 1], dest);
                return $"ptrl={path.status} straight={straight:F1}m shortfall={shortfall:F1}m";
            }
            catch (Exception e) { return "ptrl=<threw:" + e.GetType().Name + ":" + (e.Message ?? "").Replace(' ', '_') + ">"; }
        }

        private static string GoalEnemy(BotOwner owner, EnemyInfo forThis)
        {
            try
            {
                var goal = owner?.Memory?.GoalEnemy;
                if (goal == null) return "goal=<none>";
                bool isUs = ReferenceEquals(goal, forThis);
                var p = goal.Person;
                string who = p?.Profile?.Info?.Nickname;
                if (string.IsNullOrEmpty(who)) who = p?.GetType().Name ?? "?";
                var role = p?.Profile?.Info?.Settings?.Role.ToString() ?? "?";
                float d = 0f;
                try { d = Vector3.Distance(owner.Position, goal.Person.Position); } catch { }
                return $"goal={(isUs ? "THIS" : "OTHER")}:'{who}'({role}) @ {d:F1}m";
            }
            catch (Exception e) { return "goal=<threw:" + e.GetType().Name + ">"; }
        }

        private static string MoverState(BotOwner owner)
        {
            try
            {
                var m = owner?.Mover;
                if (m == null) return "mover=<null>";
                // RealDestPoint is where its ACTUALLY headed — not the enemy, which is what
                // the path probe above measures. AdvAssaultTarget advances to an assault
                // POSITION, so these two can disagree completely.
                var d = m.RealDestPoint;
                return $"pause={m.Pause}({m.RemainPause:F1}s) distDest={m.DistDestination:F1} "
                     + $"incomplete={m.HasPathAndNoComplete} dest=({d.x:F1},{d.y:F1},{d.z:F1}) "
                     + $"pathFrom='{m.CallstackPathComeFrom}'";
            }
            catch (Exception e) { return "mover=<threw:" + e.GetType().Name + ">"; }
        }

        private static string PathTo(BotOwner owner, IPlayer target)
        {
            try
            {
                if (owner == null || target == null) return "<no-target>";
                var path = new UnityEngine.AI.NavMeshPath();
                bool ok = UnityEngine.AI.NavMesh.CalculatePath(
                    owner.Position, target.Position, UnityEngine.AI.NavMesh.AllAreas, path);
                if (!ok) return "NOPATH";
                // corners tell us how far it actually gets before giving up
                float reach = 0f;
                var c = path.corners;
                if (c != null && c.Length > 1)
                    reach = Vector3.Distance(c[c.Length - 1], target.Position);
                return $"{path.status}(endGap={reach:F1}m)";
            }
            catch (Exception e) { return "<threw:" + e.GetType().Name + ">"; }
        }

        private static string SafeLayer(BotOwner owner)
        {
            try { return owner?.Brain?.ActiveLayerName() ?? "<null>"; }
            catch (Exception e) { return "<threw:" + e.GetType().Name + ">"; }
        }

        [HarmonyPostfix]
        private static void Postfix(EnemyInfo __instance)
        {
            if (!IceGate.On || !Plugin.DiagHotkeys.Value) return;
            try
            {
                var person = __instance.Person;
                if (person == null || person.IsAI) return;      // only sightings OF a human
                var owner = __instance.Owner;
                if (owner == null) return;
                string key = owner.name ?? "?";
                if (_next.TryGetValue(key, out var t) && Time.time < t) return;
                _next[key] = Time.time + 2f;

                var ev = __instance.EnemyVision;
                // the "stuck rogue" hunt (07-30, post-patrol-restore): a FEW bots still
                // freeze until approached. the columns after the vision block say why:
                //   standby=sleep            -> BotStandBy paused the AI (130m sleep rule)
                //   way=<none>               -> the bot never got a patrol way assigned
                //   patrol=stay + !moving    -> parked on a stayPoint by the point chooser
                //   role                     -> is it really only exUsecs (place-defense brain)
                string extra = "";
                try
                {
                    var pd = owner.PatrollingData;
                    extra = $" | role={owner.Profile?.Info?.Settings?.Role} " +
                            $"standby={owner.StandBy?.StandByType} " +
                            $"patrol={(pd != null ? pd.Status.ToString() : "null")} " +
                            $"way={(pd?.Way != null ? pd.Way.name : "<none>")} " +
                            $"moving={owner.Mover != null && owner.Mover.IsMoving} " +
                            // THE question for the frozen bots: the pump runs and the brain
                            // activated, so what layer is it actually sitting on? a frozen bot
                            // parked on a do-nothing layer (or one that never changes) names
                            // the culprit; a bot cycling layers normally clears the brain and
                            // points at the mover/navmesh instead
                            $"layer='{SafeLayer(owner)}' " +
                            $"state={owner.BotState} " +
                            // bots move fine on PatrolAssault (21.8%) and stop dead the
                            // moment they hold an enemy (AssaultHaveEnemy: 3 of 396). an
                            // assault layer that wont advance is usually one that cannot
                            // PATH — patrol points sit on the mesh (worst drift 0.11m) but
                            // that says nothing about whether the mesh is CONNECTED between
                            // decks. so ask unity directly, bot -> enemy.
                            $"path={PathTo(owner, __instance.Person)} " +
                            // the frozen ones recur in the SAME PLACES (user, 07-31), so the
                            // world position is the lead: paste it into the SDK scene view and
                            // look at what the navmesh is doing there. half of all bot->enemy
                            // paths come back PathPartial, so a handful of bad spots is the
                            // likeliest shape of it.
                            $"at=({owner.Position.x:F1},{owner.Position.y:F1},{owner.Position.z:F1}) " +
                            // the layer wants to advance and the path to the ENEMY is complete,
                            // so ask the mover itself why nothing happens. Pause alone would do
                            // it — a paused mover ignores every decision above it — and
                            // CallstackPathComeFrom names whoever last set the path.
                            $"| {MoverState(owner)} " +
                            // EVERY other field on this line describes the bot's relationship
                            // to US, because the diag hangs off EnemyInfo. that hid the
                            // obvious question: who is the bot's ACTUAL GoalEnemy? the
                            // assault layer holds position when its goal enemy is "toofar"
                            // (GClass39.GetDecision), so a bot locked onto a distant OTHER BOT
                            // would freeze exactly like this and ignore us until we walk into
                            // its cone. exUsec and blackDivAssault are hostile factions.
                            $"| {GoalEnemy(owner, __instance)} " +
                            // THE measurement that matters. every path probe so far aimed at
                            // the ENEMY, which was never where a patrolling bot is trying to
                            // go. 1195 of 1623 samples are bots with no enemy at all, sitting
                            // on PatrolAssault with distDest=0 — no destination. and
                            // PatrollingData sets Status=go WITHOUT checking the
                            // NavMeshPathStatus GoToPoint handed back, so an unreachable
                            // patrol point reads as "go" forever. so: can this bot actually
                            // reach the patrol point it has been assigned?
                            $"| {PatrolReach(owner)} " +
                            // patrol points come back PathComplete (719/719) and distDest is
                            // still 0 — so the layer runs but never issues a move. head turns
                            // and voicelines play, body never rotates, which is look-system
                            // alive + mover never commanded. the layer NAME cannot show that;
                            // the DECISION can. Brain.LastDecision is what BotAimingClass and
                            // BotCurrentCoverInfoClass read, so its the real current action.
                            $"decision={Decision(owner)} " +
                            // user's idea, 07-31: is this about WHERE they spawn? every
                            // recurring stuck coordinate sits 0.02-0.41m from a cover point,
                            // so a bot that spawns on one would pick it for runToCover on its
                            // very first tick and never have a reason to leave. the zone names
                            // the spawn, and drift says whether it has EVER moved since.
                            $"| {SpawnInfo(owner)} " +
                            // ISLAND TEST (user's freezer observation, 07-31). the bot opened a
                            // closed door, walked into a small room, and stopped — so movement
                            // and pathing both work and the ROOM is the trap. if the door's
                            // NavMeshObstacle carves once it shuts, the interior becomes an
                            // isolated navmesh island and every destination the bot can pick is
                            // inside it (distDest 0.1-0.5m, patrol point 2.9m away, arrive in
                            // place, face a wall).
                            // NOTE this cannot use the earlier path probe: CalculatePath snaps
                            // its START onto the nearest mesh, so it escapes straight through
                            // the freezer wall and reports PathComplete for a route the agent
                            // can never walk. probing OUTWARD in several directions and taking
                            // the furthest genuinely-reachable distance does not have that flaw.
                            $"| {Island(owner)}";
                }
                catch { }
                Plugin.Log.LogWarning(
                    $"[Vision] '{key}' @ {__instance.Distance:F1}m | VisLvl={ev.VisibilityLevel:F3} " +
                    $"Visible={ev.Visible} type={ev.VisibleType} canShoot={ev.CanShoot} " +
                    $"| speedK={__instance.VisibilityChangeSpeedK:F4} visDist={owner.LookSensor.VisibleDist:F0}m " +
                    $"lookAngle={Vector3.Angle(owner.LookDirection, __instance.Direction):F0}deg{extra}");
            }
            catch { }
        }
    }

    // the blowtorch is MAP KIT, not loot (user call 07-30): it leaves the raid with
    // whoever grabbed it and piles up in stashes. strip every torch from the LOCAL
    // player's inventory when the game stops with a live exit — the same sanctioned
    // remove-transaction the chain-door charge uses, so fika peers replicate it.
    // Killed is skipped: that inventory is already forfeit, don't race the death path.
    // fika's CoopGame overrides Stop, so IcebreakerFikaCompat re-anchors this there.
    [HarmonyPatch(typeof(LocalGame), nameof(LocalGame.Stop))]
    internal static class Patch_StripTorchOnExtract
    {
        [HarmonyPrefix]
        private static void Prefix(string profileId, ExitStatus exitStatus) => Strip(profileId, exitStatus);

        internal static void Strip(string profileId, ExitStatus exitStatus)
        {
            if (!IceGate.On) return;
            try
            {
                if (exitStatus == ExitStatus.Killed) return;
                var player = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
                if (player == null || player.ProfileId != profileId) return;
                var torches = new System.Collections.Generic.List<EFT.InventoryLogic.Item>();
                foreach (var it in player.Profile?.Inventory?.AllRealPlayerItems
                                   ?? System.Linq.Enumerable.Empty<EFT.InventoryLogic.Item>())
                    if (Blowtorch.BlowtorchIds.IsTorch(it)) torches.Add(it);
                foreach (var it in torches)
                {
                    var op = InteractionsHandlerClass.Remove(it, player.InventoryController, true);
                    if (op.Failed) { Plugin.Log.LogWarning($"[Torch] extract-strip validation failed: {op.Error}"); continue; }
                    player.InventoryController.TryRunNetworkTransaction(op, r =>
                    { if (!r.Succeed) Plugin.Log.LogWarning($"[Torch] extract-strip execution failed: {r.Error}"); });
                }
                if (torches.Count > 0)
                    Plugin.Log.LogWarning($"[Torch] stripped {torches.Count} blowtorch(es) on raid end ({exitStatus}) — map kit stays on the map");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Torch] extract strip failed: {e.Message}"); }
        }
    }

    // HEARING-CHAIN GUARD + FORENSICS. discovered via the CS gas mod (its grenades
    // never popped on icebreaker): a bot with half-initialized AI state NREs in
    // BotMemoryClass.Spotted every time it HEARS anything. two blast radii beyond the
    // NRE spam: Grenade.InvokeBlowUpEvent runs the bot-notification event BEFORE
    // OnExplosion, so one broken bot aborts every grenade on the map — and bot sound
    // notifications fan out through one multicast delegate, so everything subscribed
    // AFTER the broken bot never hears that sound either. deaf bots at random =
    // the "stuck rogue" raids (vision was proven healthy; hearing never arrived).
    //
    // CS gas ships the same guard, but icebreaker must not depend on another mod for
    // its own bots — and the CS gas log line drops the evidence (message only). the
    // prime suspect is OUR premake pipeline ("blackDivAssault profile arrived NAKED"),
    // so this one logs the bot's identity + full stack, throttled: one raid names the
    // broken bot and the null field, then the creation path gets fixed for real.
    [HarmonyPatch(typeof(BotMemoryClass), nameof(BotMemoryClass.Spotted))]
    internal static class Patch_SpottedGuardAndForensics
    {
        private static float _nextLog;
        private static int _swallowed;

        [HarmonyFinalizer]
        private static Exception Finalizer(BotMemoryClass __instance, Exception __exception)
        {
            if (__exception == null) return null;
            _swallowed++;
            if (Time.time >= _nextLog)
            {
                _nextLog = Time.time + 60f;
                string who = "<unknown>";
                try
                {
                    var bo = __instance.BotOwner_0;
                    who = bo != null
                        ? $"{bo.name} role={bo.Profile?.Info?.Settings?.Role} profileNick='{bo.Profile?.Info?.Nickname}'"
                        : "<BotOwner_0 null>";
                }
                catch { }
                Plugin.Log.LogWarning($"[Hearing] Spotted threw for {who} (x{_swallowed} so far — hearing dead for this bot, " +
                                      $"event chain protected): {__exception.GetType().Name}: {__exception.Message}\n{__exception.StackTrace}");
            }
            return null;   // swallow: one broken bot must not mute the map
        }
    }

    // backstop for any point the generator couldn't fix (bad navmesh spot): an empty
    // sub-point list returns the point ITSELF instead of indexing [-1] — the follower
    // stands on the point, formation degenerates gracefully, activation survives
    [HarmonyPatch(typeof(PatrolPoint), nameof(PatrolPoint.GetSubPoint))]
    internal static class Patch_GetSubPointEmptyGuard
    {
        [HarmonyPrefix]
        private static bool Prefix(PatrolPoint __instance, ref PatrolPoint __result)
        {
            if (__instance.SubPointsCount > 0) return true;
            __result = __instance;
            return false;
        }
    }

    // wire-keeping: TripwireSynchronizableObject.method_3 is the INERT timeout — for
    // non-AI owners it fires after TripwiresGlobalSettings.InertSeconds (300s) and
    // deactivates the wire. our authored wires have a synthetic owner (no player), so
    // they all died 5 minutes into the raid. authored wires never go stale.
    [HarmonyPatch(typeof(TripwireSynchronizableObject), "method_3")]
    internal static class Patch_AuthoredTripwireNeverInert
    {
        [HarmonyPrefix]
        private static bool Prefix(TripwireSynchronizableObject __instance)
        {
            if (!IceGate.On) return true;
            return __instance.PlacerPlayerId.ToString() != IcebreakerTripwires.OwnerId;
        }
    }

    // bots roll Mind.CHACE_TO_DEACTIVATE (default 100!) when they spot a wire and then
    // walk over and defuse it — SAIN raids cleared every authored wire before the
    // player arrived. on the ship, bots don't defuse wires at all.
    [HarmonyPatch(typeof(BotBewarePlantedMine), nameof(BotBewarePlantedMine.SetMineToDeactivate))]
    internal static class Patch_NoBotTripwireDefuse
    {
        [HarmonyPrefix]
        private static bool Prefix(PlantedMineAIInfo toDeactivate)
        {
            if (!IceGate.On) return true;
            return toDeactivate == null; // null = clearing state, always allowed
        }
    }

    // 32k NREs/raid: ripped CullingObjects can lose their serialized _transform, and
    // Register() -> UpdateSphere() -> get_Position() NREs on every one at Start.
    // heal the field to the component's own transform before the game touches it —
    // same fix the SDK gizmo needed, now for the runtime class.
    [HarmonyPatch(typeof(CullingObject), "Start")]
    internal static class Patch_CullingObjectNullTransform
    {
        private static readonly System.Reflection.FieldInfo TransformField =
            AccessTools.Field(typeof(CullingObject), "_transform");

        [HarmonyPrefix]
        private static void Prefix(CullingObject __instance)
        {
            try
            {
                if (TransformField != null && TransformField.GetValue(__instance) == null)
                    TransformField.SetValue(__instance, __instance.transform);
            }
            catch { }
        }
    }

    // SAIN log spam: its LocationClass.parseLocation switch has no case for "Suburbs"
    // -> LogError EVERY ManualUpdate forever (the found-flag only latches on a match).
    // soft-dependency patch (reflection only — SAIN may not be installed): on the ship,
    // answer "Labyrinth" — SAIN's tight-interior CQB profile, the closest fit for the
    // ship's corridors, and the latch stops the spam.
    internal static class SainLocationCompat
    {
        private static bool _attempted;

        // called from the tripwire raid-start hook — SAIN's assembly is certainly
        // loaded by then regardless of plugin init order
        internal static void TryPatch(Harmony harmony)
        {
            if (_attempted) return;
            _attempted = true;
            try
            {
                var locClass = AccessTools.TypeByName("SAIN.Components.LocationClass");
                if (locClass == null) return; // SAIN not installed
                var target = AccessTools.Method(locClass, "parseLocation");
                if (target == null) { Plugin.Log.LogWarning("[SainCompat] parseLocation not found — SAIN updated?"); return; }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(SainLocationCompat), nameof(ParseLocationPrefix)));
                Plugin.Log.LogInfo("[SainCompat] SAIN location parse patched (Suburbs -> Labyrinth profile)");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[SainCompat] patch failed: {e.Message}"); }
        }

        private static bool ParseLocationPrefix(object __instance, ref object __result)
        {
            if (!IceGate.On) return true;
            try
            {
                var elocType = __result?.GetType() ?? AccessTools.TypeByName("SAIN.ELocation") ?? AccessTools.TypeByName("ELocation");
                if (elocType == null || !elocType.IsEnum) return true;
                __result = Enum.Parse(elocType, "Labyrinth");
                var found = AccessTools.Field(__instance.GetType(), "_foundLocation");
                if (found != null) found.SetValue(__instance, true);
                return false;
            }
            catch { return true; }
        }
    }

    // THE DISCARDED STATUS (07-31). simplePatrol is decided 181 times and sets no
    // destination in 154 of them; doorOpen 81/95 the same. meanwhile every decision that
    // DOES get a real destination moves (goToCoverPoint 7.7m -> 77%, attackMovingFlank
    // 4.6m -> 100%), so the mover is healthy and the command simply never arrives.
    //
    // BotMover.GoToPoint RETURNS a NavMeshPathStatus and PatrollingData drops it on the
    // floor before pinning Status = go (PatrollingData.cs ~476, the else-branch). that
    // return value is the single piece of evidence nobody keeps. patch the mover itself so
    // every caller is covered, and report anything that is not PathComplete.
    // BotMover has TWO GoToPoint overloads (Vector3... and CustomNavigationPoint...), so
    // patching by name alone is an AmbiguousMatchException that kills PatchAll. spell the
    // signature out. this is the Vector3 one — what PatrollingData uses.
    [HarmonyPatch(typeof(BotMover), nameof(BotMover.GoToPoint),
        new System.Type[] { typeof(Vector3), typeof(bool), typeof(float),
                            typeof(bool), typeof(bool), typeof(bool), typeof(bool) })]
    internal static class Patch_GoToPointStatus
    {
        private static readonly System.Collections.Generic.Dictionary<string, float> _next =
            new System.Collections.Generic.Dictionary<string, float>();
        private static int _ok, _partial, _invalid;
        private static float _summary;

        [HarmonyPostfix]
        private static void Postfix(BotMover __instance, Vector3 pos,
                                    UnityEngine.AI.NavMeshPathStatus __result)
        {
            if (!IceGate.On || !Plugin.DiagHotkeys.Value) return;
            try
            {
                if (__result == UnityEngine.AI.NavMeshPathStatus.PathComplete) _ok++;
                else if (__result == UnityEngine.AI.NavMeshPathStatus.PathPartial) _partial++;
                else _invalid++;

                if (Time.time >= _summary)
                {
                    _summary = Time.time + 15f;
                    Plugin.Log.LogWarning($"[GoTo] BotMover.GoToPoint results: complete={_ok} partial={_partial} invalid={_invalid}"
                        + (_partial + _invalid > 0 ? "  <-- these callers get NO usable path and PatrollingData still reports 'go'" : ""));
                }
                if (__result == UnityEngine.AI.NavMeshPathStatus.PathComplete) return;

                var owner = __instance.BotOwner_0;
                var key = owner != null ? owner.name : "?";
                float t;
                if (_next.TryGetValue(key, out t) && Time.time < t) return;
                _next[key] = Time.time + 5f;
                var from = owner != null ? owner.Position : Vector3.zero;
                Plugin.Log.LogWarning($"[GoTo] '{key}' {__result} to ({pos.x:F1},{pos.y:F1},{pos.z:F1}) "
                    + $"from ({from.x:F1},{from.y:F1},{from.z:F1}) straight={Vector3.Distance(from, pos):F1}m "
                    + $"decision={Patch_VisionBreakdownDiag.DecisionOf(owner)}");
            }
            catch { }
        }
    }

    // AI PUMP PROBE (07-31). the stuck bots are FROZEN — no head turn, no patrol, no
    // idle, but they still aggro the instant they SEE you. everything that stopped lives
    // downstream of one unguarded chain, BotsController.method_0():
    //
    //     ArtilleryZonesController.ManualUpdate();
    //     AICoreController.Update();
    //     BotSmokesVisionSystem.Update();
    //     AiTaskManager.Update();      <-- delayed hearing tasks + LookSensor regular tasks
    //     Bots.UpdateByUnity();        <-- the bot BRAIN: looking, patrol, decisions
    //     EventsController.ManualUpdate();
    //
    // no try/catch anywhere in it, so the FIRST call that throws silently kills every call
    // after it, every frame, for every bot on the map. a throw landing between
    // AiTaskManager and Bots.UpdateByUnity would produce exactly what we see: vision alive
    // (LookSensor is an AiTaskManager regular task) but brains frozen and peaceful-bot
    // hearing dead (it defers through RegisterDelayedTask).
    //
    // this only OBSERVES: is the pump running, and does it throw. no behaviour change.
    [HarmonyPatch]
    internal static class Patch_AiPumpProbe
    {
        private static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(BotsController), "method_0");

        private static int _ticks, _throws;
        private static float _nextLog;

        [HarmonyPrefix]
        private static void Prefix()
        {
            if (!IceGate.On) return;
            _ticks++;
            if (Time.time < _nextLog) return;
            _nextLog = Time.time + 10f;
            Plugin.Log.LogWarning($"[AiPump] BotsController.method_0 ticks={_ticks} throws={_throws}"
                                  + (_throws == 0 ? " — chain is clean" : " — SOMETHING IN THE CHAIN IS THROWING"));
        }

        // finalizer, not a try/catch: it sees the exception WITHOUT swallowing it, so
        // behaviour is identical to an unpatched game and the stack names the culprit
        [HarmonyFinalizer]
        private static void Finalizer(System.Exception __exception)
        {
            if (__exception == null || !IceGate.On) return;
            _throws++;
            if (_throws > 3) return;   // it would be every frame — three is plenty
            Plugin.Log.LogError($"[AiPump] BotsController.method_0 THREW (#{_throws}) — everything after the throwing "
                                + $"call is dead this frame (task pump / bot brains / events): {__exception}");
        }
    }

    // HEARING PROBE (07-31). bot9 ignored footsteps AND a melee hit at 1.2m behind him,
    // then aggroed instantly the moment he turned and SAW us — so vision is healthy and
    // everything routed through hearing is not. that funnel is narrow and worth proving
    // rather than guessing at: BotHearingSensor.Init subscribes to
    // Singleton<BotEventHandler>.Instance.OnSoundPlayed, and SetUnderFire (the entire
    // "something is attacking me" reaction) is raised from BotHearingSensor and NOWHERE
    // else in the assembly. so if PlaySound never fires, or fires on a different
    // BotEventHandler instance than the one the bots subscribed to, a bot is deaf and
    // unshootable-at by design and no amount of vision tuning will touch it.
    //
    // two facts, once each: does PlaySound fire at all, and is the singleton the bots
    // subscribed to still the live one.
    [HarmonyPatch(typeof(BotEventHandler), nameof(BotEventHandler.PlaySound))]
    internal static class Patch_HearingProbe
    {
        private static int _fired;
        private static float _nextLog;

        [HarmonyPostfix]
        private static void Postfix(BotEventHandler __instance, IPlayer person, Vector3 position, float power, AISoundType type)
        {
            if (!IceGate.On) return;
            try
            {
                _fired++;
                if (Time.time < _nextLog) return;
                _nextLog = Time.time + 5f;
                var live = Comfort.Common.Singleton<BotEventHandler>.Instance;
                bool sameInstance = ReferenceEquals(live, __instance);
                Plugin.Log.LogWarning($"[HearProbe] PlaySound x{_fired} | by='{person?.Profile?.Nickname}' power={power:F1} "
                                      + $"type={type} | handler==liveSingleton={sameInstance}"
                                      + (sameInstance ? "" : "  <-- BOTS SUBSCRIBED TO A DEAD HANDLER"));
            }
            catch { }
        }
    }

}
