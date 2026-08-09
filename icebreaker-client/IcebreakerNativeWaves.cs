using System;
using System.Collections.Generic;
using HarmonyLib;

namespace Manimal.Icebreaker
{
    // NATIVE BSG TRIGGER WAVES (08-08 experiment, config NativeBdWaves): the retail
    // base.json drives every BD/knight/wedge squad through BossLocationSpawn rows with
    // TriggerName "botEvent" + a TriggerId the scene raises. the original attempt died
    // because nothing could parse the custom role names — since fixed by MoreBots'
    // prepatcher Cecil-injecting real enum members (verified 08-08), and the server
    // generates blackDivIb through the same wave endpoint daily (the premake proves it).
    //
    // wiring: rows appended in a prefix on BossSpawnScenario.smethod_0 — the single
    // funnel both LocalGame.smethod_6 AND fika's CoopGame creation feed, and it sits
    // AFTER LocalGame.smethod_8's pve/waves filtering so nothing strips our rows.
    // Init() (chance roll, enum parses, escort ladder) runs inside the scenario's own
    // method_0 like any retail row. our resurrected trigger layer already raises the
    // exact retail TriggerIds — with the flag on, IcebreakerCrew.OnSpawnEvent forwards
    // them to BotEventHandler.AnyEvent instead of force-spawning.
    //
    // the role mapping is the whole trick: retail's bossBullyBlackDiv /
    // followerBullyBlackDiv / pmcBotBlackDiv all collapse onto our blackDivIb;
    // bossWedge and bossKnight parse under their own (injected/vanilla) names.
    internal static class IcebreakerNativeWaves
    {
        private const string Bd = "blackDivIb";
        private const string Wedge = "bossWedge";
        private const string WedgeRooms = "BotZoneRoomsFour,BotZoneRoomsThird,BotZoneRoomsThirdKitchen";

        private static BossLocationSpawn Row(string boss, string escort, int escorts,
            string zone, string triggerId)
        {
            return new BossLocationSpawn
            {
                BossName = boss,
                BossEscortType = escort,
                BossEscortAmount = escorts.ToString(),
                BossZone = zone,
                BossChance = 100,
                BossDifficult = "normal",
                BossEscortDifficult = "normal",
                TriggerName = "botEvent",
                TriggerId = triggerId,
                Time = 9999f,          // retail semantics: trigger-only, never by timer
                Delay = 0f,
                ForceSpawn = true,
                IgnoreMaxBots = true,
                BossPlayer = false,
                DependKarma = false,
                Supports = null,
            };
        }

        // the retail 1.0 table (base(1).json ground truth), roles mapped to ours.
        // per-trigger totals: hides0/1/2/3 = 4/6/8/10, stern0/1/2/3 = 9/12/15/15,
        // T1 = knight+2, T3 = 3, T4 = 5, wedges1/2/3/4 = 4/5/7/7 with exact
        // per-zone splits. the ladder digit is chosen by OUR trigger layer (it
        // raises exactly one id), so the unfired rows simply never activate.
        internal static BossLocationSpawn[] BuildRetailRows()
        {
            var rows = new List<BossLocationSpawn>
            {
                // T1 — the knight arrives with two rogue escorts
                Row("bossKnight", "exUsec", 2, "BotZoneMash_t1", "T1"),

                // engine hides (retail: bossBully+followers at EngineHide)
                Row(Bd, Bd, 3, "BotZoneEngineHide", "hides0"),
                Row(Bd, Bd, 2, "BotZoneEngineHide", "hides1"),
                Row(Bd, Bd, 2, "BotZoneEngineHide", "hides1"),
                Row(Bd, Bd, 3, "BotZoneEngineHide", "hides2"),
                Row(Bd, Bd, 3, "BotZoneEngineHide", "hides2"),
                Row(Bd, Bd, 4, "BotZoneEngineHide", "hides3"),
                Row(Bd, Bd, 4, "BotZoneEngineHide", "hides3"),

                // stern deployment (retail: SternTop squad + two Stern squads, all scale)
                Row(Bd, Bd, 2, "BotZoneSternTop", "stern0"),
                Row(Bd, Bd, 2, "BotZoneStern", "stern0"),
                Row(Bd, Bd, 2, "BotZoneStern", "stern0"),
                Row(Bd, Bd, 3, "BotZoneSternTop", "stern1"),
                Row(Bd, Bd, 3, "BotZoneStern", "stern1"),
                Row(Bd, Bd, 3, "BotZoneStern", "stern1"),
                Row(Bd, Bd, 4, "BotZoneSternTop", "stern2"),
                Row(Bd, Bd, 4, "BotZoneStern", "stern2"),
                Row(Bd, Bd, 4, "BotZoneStern", "stern2"),
                Row(Bd, Bd, 4, "BotZoneSternTop", "stern3"),
                Row(Bd, Bd, 4, "BotZoneStern", "stern3"),
                Row(Bd, Bd, 4, "BotZoneStern", "stern3"),

                // tier gates
                Row(Bd, Bd, 2, "BotZoneOutside_t3", "T3"),
                Row(Bd, Bd, 4, "BotZoneInside_t4", "T4"),

                // wedge detail — NOT retail-literal on purpose (08-08 test raid:
                // FOUR Wedges). retail's bossWedge is a generic squad-member role, so
                // their per-zone boss rows are fine there — but the BlackDiv mod's
                // bossWedge is THE named boss with unique gear, so every extra row
                // cloned him. one row per trigger: the boss + blackDivIb escorts at
                // retail totals (4/5/7/7); the comma zone list makes the engine pick
                // one of retail's three rooms at random per raid (BornZone semantics).
                Row(Wedge, Bd, 3, WedgeRooms, "wedges1"),
                Row(Wedge, Bd, 4, WedgeRooms, "wedges2"),
                Row(Wedge, Bd, 6, WedgeRooms, "wedges3"),
                Row(Wedge, Bd, 6, WedgeRooms, "wedges4"),
            };
            return rows.ToArray();
        }

        // every botEvent raise, from EVERY source (08-08: player saw stern/helipad
        // squads spawn while standing in the engine room — the AIPlaces logic raises
        // AnyEvent directly with no log line, so trigger history was unprovable).
        // one line per raise makes "who fired and when" a grep instead of a theory.
        [HarmonyPatch(typeof(BotEventHandler), nameof(BotEventHandler.AnyEvent), new[] { typeof(string) })]
        internal static class Patch_LogAnyEvent
        {
            [HarmonyPostfix]
            private static void Postfix(string eventId)
            {
                if (IceGate.On)
                    Plugin.Log.LogWarning($"[NativeWaves] botEvent '{eventId}' raised t={UnityEngine.Time.timeSinceLevelLoad:F0}s");
            }
        }

        [HarmonyPatch(typeof(BossSpawnScenario), nameof(BossSpawnScenario.smethod_0))]
        internal static class Patch_AppendNativeWaves
        {
            [HarmonyPrefix]
            private static void Prefix(ref BossLocationSpawn[] bossWaves)
            {
                try
                {
                    if (!Plugin.NativeBdWaves.Value) return;
                    if (!string.Equals(IceGate.PendingLocationId, "Suburbs", StringComparison.OrdinalIgnoreCase)) return;

                    var add = BuildRetailRows();
                    var merged = new List<BossLocationSpawn>(bossWaves ?? Array.Empty<BossLocationSpawn>());
                    merged.AddRange(add);
                    bossWaves = merged.ToArray();
                    Plugin.Log.LogWarning($"[NativeWaves] appended {add.Length} retail trigger rows to the boss scenario "
                        + "(BD/knight/wedge squads now spawn through BSG's own botEvent pipeline)");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[NativeWaves] append failed — falling back to the force-spawner this raid: {e.Message}");
                }
            }
        }
    }
}
