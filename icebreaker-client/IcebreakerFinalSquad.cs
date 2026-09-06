using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Game.Spawning;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace Manimal.Icebreaker
{
    // T4 is five identical BD soldiers, not a distinct boss plus follower roles.
    // BSG's boss path activates the leader, THEN generates/queues the four escorts
    // with forceSpawn=false and four copies of the leader's point. Prepare this
    // particular squad together and submit five single-bot activations instead.
    [HarmonyPatch(typeof(BotBossSpawn), nameof(BotBossSpawn.SpawnBossAndFollowers))]
    internal static class IcebreakerFinalSquad
    {
        [HarmonyPrefix]
        private static bool Prefix(BotBossSpawn __instance, BotCreationData creationData,
            BossLocationSpawn wave, BotSpawnParams spawnParams, int followersCount,
            BotZone botZone, ref Task __result)
        {
            if (!IceGate.On || !FikaBridge.BotsAuthority || wave == null ||
                wave.TriggerId != "T4" || !wave.ForceSpawn || followersCount != 4 ||
                (int)wave.BossType != IcebreakerCrew.BdIb || wave.EscortType != wave.BossType ||
                botZone == null || botZone.name != "BotZoneInside_t4") return true;
            __result = Spawn(__instance, creationData, wave, spawnParams, botZone);
            return false;
        }

        private static async Task Spawn(BotBossSpawn boss, BotCreationData leader,
            BossLocationSpawn wave, BotSpawnParams spawnParams, BotZone zone)
        {
            var ready = new List<BotCreationData> { leader };
            var token = boss._spawner.GetCancelToken();
            bool submitted = false;
            try
            {
                if (leader == null || leader.Count != 1 || leader.Profiles[0] == null)
                    throw new InvalidOperationException("T4 leader must contain one valid profile");
                spawnParams.ShallBeGroup = new ShallBeGroupParams(true, true, 5);
                // SPT's profile-client path may ignore Create(count), so make four
                // explicit requests and await all of them BEFORE activating the leader.
                var requests = new Task<BotCreationData>[4];
                for (int i = 0; i < requests.Length; i++)
                    requests[i] = BotCreationData.Create(new GetProfileDataParams(EPlayerSide.Savage,
                        wave.EscortType, wave.EscortDif, wave.Time, spawnParams, false),
                        boss._botCreator, 1, boss._spawner);
                try { await Task.WhenAll(requests); }
                finally
                {
                    foreach (var request in requests)
                        if (request.Status == TaskStatus.RanToCompletion && request.Result != null)
                            ready.Add(request.Result);
                }
                token.ThrowIfCancellationRequested();
                if (ready.Count != 5 || ready.Any(d => d.Count != 1 || d.Profiles[0] == null || d.SpawnStopped))
                    throw new InvalidOperationException("T4 profile preparation did not produce five ready bots");
                if (ready.Select(d => d.Profiles[0].Id).Distinct().Count() != 5)
                    throw new InvalidOperationException("T4 profile preparation returned duplicate IDs");

                var paths = ready.SelectMany(d => d.Profiles[0].GetAllPrefabPaths(false)).ToArray();
                await Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(
                    ObjectsFactory.PoolsCategory.Raid, ObjectsFactory.AssemblyType.Local,
                    paths, Diz.Jobs.JobYieldPriority.Low, null, token);
                token.ThrowIfCancellationRequested();
                if (!IceGate.On || !FikaBridge.BotsAuthority || ready.Any(d => d.SpawnStopped)) return;

                // Resolve immediately before activation: the player can move upstairs
                // while the backend is preparing profiles. Never release a partial team.
                var points = PickPositions(zone, 5);
                if (points.Count != 5)
                    throw new InvalidOperationException($"T4 has only {points.Count}/5 safe, separated spawn positions");
                int activated = 0;
                float started = Time.time;
                submitted = true;
                for (int i = 0; i < ready.Count; i++)
                {
                    // This is the same low-level activation used by SpawnBossMain.
                    // No min/max or proximity-delay queue can split the prepared squad.
                    boss._spawner.SpawnBotsInZoneOnPositions(new List<ISpawnPoint> { points[i] },
                        zone, ready[i], bot =>
                        {
                            if (!spawnParams.ShallBeGroup.IsBossSetted)
                            {
                                spawnParams.ShallBeGroup.IsBossSetted = true;
                                bot.Boss.SetBoss(4);
                            }
                            activated++;
                            Plugin.Log.LogInfo($"[T4Squad] active {activated}/5 at {bot.Position}, +{Time.time - started:0.00}s");
                        });
                }
                Plugin.Log.LogInfo("[T4Squad] all five profiles ready; five separated activations submitted together");
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Plugin.Log.LogError("[T4Squad] " + (submitted ? "activation failed: " : "whole squad deferred: ") + e);
                if (!submitted && leader != null && !leader.SpawnStopped && !token.IsCancellationRequested && IceGate.On)
                    boss.DelayBossSpawn(wave, spawnParams, leader);
            }
            finally
            {
                if (!submitted)
                    foreach (var data in ready.Skip(1)) data.StopSpawn();
            }
        }

        private static List<ISpawnPoint> PickPositions(BotZone zone, int count)
        {
            var result = new List<ISpawnPoint>();
            if (zone.SpawnPoints == null) return result;
            var seeds = zone.SpawnPoints.Where(p => p != null)
                .OrderByDescending(p => FikaBridge.NearestHumanSqr(p.Position)).ToArray();
            var humans = new List<Player>();
            FikaBridge.CollectHumans(humans);
            var living = UnityEngine.Object.FindObjectsOfType<BotOwner>();
            int sightMask = LayerMask.GetMask("HighPolyCollider", "Terrain");
            // The authored T4 zone has only TWO markers. Add nearby positions on
            // the same deck, reachable in a straight line on the baked NavMesh.
            for (int ring = 0; ring <= 3; ring++)
            foreach (var seed in seeds)
            for (int direction = 0; direction < (ring == 0 ? 1 : 12); direction++)
            {
                if (!NavMesh.SamplePosition(seed.Position, out var anchor, 0.75f, NavMesh.AllAreas)) continue;
                float angle = direction * Mathf.PI / 6f;
                var candidate = anchor.position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * (ring * 1.8f);
                if (!NavMesh.SamplePosition(candidate, out var hit, 0.75f, NavMesh.AllAreas)) continue;
                var position = hit.position;
                if (Mathf.Abs(position.y - seed.Position.y) > 0.75f ||
                    NavMesh.Raycast(anchor.position, position, out _, NavMesh.AllAreas)) continue;
                if (result.Any(p => (p.Position - position).sqrMagnitude < 2.25f)) continue;
                if (living.Any(b => b != null && !b.IsDead && (b.Position - position).sqrMagnitude < 2.25f)) continue;
                bool exposed = humans.Any(h => (h.Position - position).sqrMagnitude < 9f ||
                    !Physics.Linecast(h.Position + Vector3.up * 1.5f, position + Vector3.up * 1.5f,
                        sightMask, QueryTriggerInteraction.Ignore));
                if (exposed) continue;
                result.Add(new SpawnPoint
                {
                    Id = seed.Id + "-t4-" + result.Count, Name = "Icebreaker T4 squad",
                    Position = position, Rotation = seed.Rotation, CorePointId = seed.CorePointId,
                    BotZone = zone, Sides = seed.Sides, Categories = seed.Categories
                });
                if (result.Count == count) return result;
            }
            return result;
        }
    }
}
