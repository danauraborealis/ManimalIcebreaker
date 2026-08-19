using System;
using HarmonyLib;

namespace Manimal.Icebreaker
{
    // QUESTING BOTS, TERMINAL-ONLY MUTE (user call 2026-08-17: QB keeps trying to
    // spawn PMC groups here — 22x 'No valid spawn points' per raid, one floating
    // invisible PMC, and the regular waves starved). the LockableDoors pattern:
    // resolve QB's generator overrides by name and prefix-skip them behind the
    // terminal gate — QB stays fully active on every other map. we gate
    // CanSpawnBots + GetNumberOfBotsAllowedToSpawn (spawning) but NOT
    // GetMaxGeneratedBots: generation must complete normally or QB's
    // delay_game_start_until_bot_gen_finishes wait would hang the raid load.
    internal static class IcebreakerQuestingBotsOff
    {
        internal static void TryPatch(Harmony h)
        {
            var pmc = AccessTools.TypeByName("QuestingBots.Components.Spawning.PMCGenerator");
            var pscav = AccessTools.TypeByName("QuestingBots.Components.Spawning.PScavGenerator");
            if (pmc == null && pscav == null) return; // QB not installed
            int gated = 0;
            foreach (var t in new[] { pmc, pscav })
            {
                if (t == null) continue;
                // DECLARED methods — these are overrides of abstract bases, and a
                // name-only lookup on the base type would patch nothing (the
                // virtual-base harmony lesson)
                var canSpawn = AccessTools.DeclaredMethod(t, "CanSpawnBots");
                if (canSpawn != null)
                {
                    h.Patch(canSpawn, prefix: new HarmonyMethod(typeof(IcebreakerQuestingBotsOff), nameof(SkipFalse)));
                    gated++;
                }
                var allowed = AccessTools.DeclaredMethod(t, "GetNumberOfBotsAllowedToSpawn");
                if (allowed != null)
                {
                    h.Patch(allowed, prefix: new HarmonyMethod(typeof(IcebreakerQuestingBotsOff), nameof(SkipZero)));
                    gated++;
                }
            }
            // the wave killer (2026-08-17 log: 'Suppressing boss wave ... or too many
            // bosses' x31): the map population ships as BossLocationSpawn
            // waves and QB's limit_initial_boss_spawns eats them all. never suppress
            // on icebreaker.
            var bossPatch = AccessTools.TypeByName("QuestingBots.Patches.Spawning.ActivateBossesByWavePatch");
            var suppress = bossPatch == null ? null : AccessTools.DeclaredMethod(bossPatch, "shouldSuppressBossWave");
            if (suppress != null)
            {
                h.Patch(suppress, prefix: new HarmonyMethod(typeof(IcebreakerQuestingBotsOff), nameof(SkipFalse)));
                gated++;
            }

            // QB's game-start delay ALSO captures + replays boss waves — two wave
            // holders (QB's + our SpawnGate) double-process the same waves and the
            // spawner's InSpawnProcess counter ghosts the cap check (2026-08-17:
            // profiles generated server-side, zero spawns, CheckOnMax deferring
            // everything). force the delay OFF here so OUR gate is the only holder.
            var gameStart = AccessTools.TypeByName("QuestingBots.Patches.Spawning.GameStartPatch");
            var delayGetter = gameStart == null ? null : AccessTools.PropertyGetter(gameStart, "IsDelayingGameStart");
            if (delayGetter != null)
            {
                h.Patch(delayGetter, prefix: new HarmonyMethod(typeof(IcebreakerQuestingBotsOff), nameof(SkipFalse)));
                gated++;
            }

            if (gated > 0)
                Plugin.Log.LogInfo($"[QuestingBotsOff] detected — {gated} method(s) gated (spawn generators + boss suppression + start delay); QB muted on icebreaker ONLY");
        }

        private static bool SkipFalse(ref bool __result)
        {
            if (!IceGate.On) return true;
            __result = false;
            return false;
        }

        private static bool SkipZero(ref int __result)
        {
            if (!IceGate.On) return true;
            __result = 0;
            return false;
        }
    }
}
