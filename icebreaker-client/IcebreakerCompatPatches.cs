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

    // ROGUE FORGIVENESS (user call 08-03 — replaces the removed damage-voider with
    // something better-aimed). rogue-on-rogue accidents must not START fights: with
    // SAIN the ship turned into a rogue deathmatch. BotsGroup.AddEnemy is the single
    // gate — vanilla combat reads BotsGroup.Enemies, and SAIN seeds its ENTIRE enemy
    // list from BotsGroup.OnEnemyAdd (verified in its EnemyListController), so one
    // blocked add starves both retaliation paths. bonus, also verified in SAIN
    // source: its fire-line check (SAINFriendlyFireClass) FriendlyBlocks any shot
    // through a player who is NOT an enemy — keeping allies out of the enemy dict is
    // exactly what makes SAIN stop aiming through them in the first place, which
    // covers the "avoid friendly crossfire" ask with zero extra machinery.
    // damage stays real (sprays can still down a squadmate) — only the GRUDGE is
    // voided. the player is never protected (IsAI gate), and BD-vs-rogue stays a
    // real fight (different faction, not allied).
    [HarmonyPatch(typeof(BotsGroup), nameof(BotsGroup.AddEnemy))]
    internal static class Patch_RogueForgiveness
    {
        private static bool _logged;

        private static bool Allied(WildSpawnType a, WildSpawnType b)
            => (a == b && (a == WildSpawnType.exUsec || (int)a == IcebreakerCrew.BdIb))
               || (a == WildSpawnType.exUsec && b == WildSpawnType.bossKnight)
               || (a == WildSpawnType.bossKnight && b == WildSpawnType.exUsec);

        [HarmonyPrefix]
        private static bool Prefix(BotsGroup __instance, IPlayer person, ref bool __result)
        {
            if (!IceGate.On) return true;
            try
            {
                if (person == null || !person.IsAI) return true;
                var attackerRole = person.Profile?.Info?.Settings?.Role;
                if (attackerRole == null) return true;
                // the group's faction = its first live member's role
                WildSpawnType? groupRole = null;
                for (int i = 0; i < __instance.MembersCount; i++)
                {
                    var m = __instance.Member(i);
                    if (m != null) { groupRole = m.Profile?.Info?.Settings?.Role; break; }
                }
                if (groupRole == null || !Allied(attackerRole.Value, groupRole.Value)) return true;
                if (!_logged)
                {
                    _logged = true;
                    Plugin.Log.LogDebug($"[Forgive] blocked ally enemy-add ({attackerRole} vs {groupRole} group) — logged once, applies all raid");
                }
                __result = false;
                return false;
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
                Plugin.Log.LogDebug("[Vision] day-vision lift is live (200 applications this raid)");
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
                Plugin.Log.LogDebug("[Vision] weather seen-debuff lifted map-wide (200 applications this raid)");
            rainK = 1f; fogK = 1f; __result = 1f;
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
                    Plugin.Log.LogDebug($"[Torch] stripped {torches.Count} blowtorch(es) on raid end ({exitStatus}) — map kit stays on the map");
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
}
