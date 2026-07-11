using System;
using System.Collections.Generic;
using System.Linq;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace Manimal.Icebreaker
{
    // generates AILootPoints from the live scene's LootableContainers and LootPoint
    // spawns, plus clusters using BSG's own formation constants. runtime id-matching
    // (AIPatrolsData.RestoreLoot) keys on the same Id strings we read here, so points
    // built this way resolve exactly like baked ones.
    //
    // ground truth (factory dump): container points stand ~0.5-1.1m from the loot,
    // simple points nearly on top; lookPoint = the loot object itself. both emerge
    // naturally from "stand at nearest navmesh spot, look at the object".
    internal static class LootScanner
    {
        // observed max stand-off in baked data is 2.45m — anything farther is a
        // container the bots cant actually reach from the navmesh
        private const float MaxStandOff = 2.5f;
        // AIPatrolsData formation constants (LootClusterRadius / MinLootPointsInCluster).
        // the rarity-score filter is skipped: container inventories may not be populated
        // yet at RestoreData time, and cluster VALUE is recalculated at runtime anyway
        // by CollectActualSpawnedLoot.
        private const float ClusterRadius = 20f;
        private const int MinClusterPoints = 10;

        // set when injection swaps a raid's loot data; the RestoreLoot postfix picks these
        // up to add loose-loot points once the actual spawned-loot records are available.
        // simple points cant come from the scene: factory has ZERO loose-loot scene markers
        // (no StaticLoot, and LocationScene never caches LootPoint) — on SPT loose loot
        // exists only as backend records in location.Loot.
        private static AICoversData s_pendingCovers;
        private static List<AICorePoint> s_cores;
        private static int s_nextId;

        public static List<AILootPoint> GenerateContainers(AICoversData covers, List<AICorePoint> cores)
        {
            var containerPoints = new List<AILootPoint>();
            int id = 1;
            int unreachable = 0;

            foreach (var c in LocationScene.GetAllObjects<LootableContainer>(false))
            {
                if (c == null)
                    continue;
                if (!TryStandPos(c.transform.position, out var stay))
                {
                    unreachable++;
                    continue;
                }
                containerPoints.Add(new AILootPoint(id++, stay, c.transform.position, c, Nearest(cores, stay)));
            }

            // arm the RestoreLoot postfix for the loose-loot half
            s_pendingCovers = covers;
            s_cores = cores;
            s_nextId = id;

            Plugin.Log.LogInfo($"loot scan (containers): {containerPoints.Count} points, {unreachable} unreachable skipped; loose loot deferred to RestoreLoot");
            return containerPoints;
        }

        // called from the AIPatrolsData.RestoreLoot postfix — the only moment we can see
        // which loose loot actually spawned this raid (location.Loot records; !IsContainer
        // = loose item at a raw position). points get their spawned item attached directly,
        // voxels get stitched via AddLootPoint (RestoreData's voxel pass already ran), and
        // clusters form over the full container+simple set.
        public static void OnRestoreLoot(AIPatrolsData patrols, List<LootItemPositionClass> lootData)
        {
            var covers = s_pendingCovers;
            if (covers == null || patrols == null || lootData == null)
                return;
            s_pendingCovers = null; // one shot per injected raid

            int unreachable = 0;
            var simple = new List<AILootPoint>();
            foreach (var record in lootData)
            {
                if (record == null || record.IsContainer)
                    continue;
                if (!TryStandPos(record.Position, out var stay))
                {
                    unreachable++;
                    continue;
                }
                var p = MakeSimplePoint(s_nextId++, stay, record.Position, record.Id, Nearest(s_cores, stay));
                p.RestoreLoot(record); // attach the spawned item now — no id matching needed
                simple.Add(p);
                var voxel = covers.GetVoxelSafe(p.Position, false);
                if (voxel != null)
                    voxel.AddLootPoint(p);
            }
            patrols.SimpleLootPoints = simple;

            var all = (patrols.ContainerLootPoints ?? new List<AILootPoint>()).Concat(simple).ToList();
            var clusters = BuildClusters(all);
            foreach (var cluster in clusters)
                cluster.CollectActualSpawnedLoot(lootData);
            patrols.LootPointClusters = clusters;

            Plugin.Log.LogInfo($"loot scan (loose): {simple.Count} simple points from {lootData.Count} records ({unreachable} unreachable), {clusters.Count} clusters over {all.Count} total points");
        }

        // exfil AI points for every exfil in the scene. baked factory only has 3, but
        // over-generating is safe: PatrolExfiltrationPointsData filters by exfil Status
        // and Requirements at runtime, so ineligible exits self-filter. we pre-link the
        // live ExfiltrationPoint (the bot chooser only lazy-links against the PMC array,
        // which would strand scav exits). extra _posAdditional spots keep grouped scavs
        // from stacking on one spot at extract.
        public static List<AIExfiltrationPoint> GenerateExfils(List<AICorePoint> cores)
        {
            var result = new List<AIExfiltrationPoint>();
            int id = 1;
            foreach (var exfil in LocationScene.GetAllObjects<ExfiltrationPoint>(false))
            {
                if (exfil == null || exfil.Settings == null)
                    continue;
                NavMeshHit hit;
                if (!NavMesh.SamplePosition(exfil.transform.position, out hit, 3f, -1))
                    continue;
                var p = new AIExfiltrationPoint(id++, hit.position, Nearest(cores, hit.position), exfil);
                p.LinkExfiltration(exfil);
                for (int i = 0; i < 4 && p._posAdditional.Count < 3; i++)
                {
                    var off = Quaternion.Euler(0f, 90f * i, 0f) * Vector3.forward * 1.5f;
                    NavMeshHit extra;
                    if (NavMesh.SamplePosition(hit.position + off, out extra, 1f, -1)
                        && (extra.position - hit.position).sqrMagnitude > 0.5f)
                        p.AddPositionAdditional(extra.position);
                }
                result.Add(p);
            }
            Plugin.Log.LogInfo($"exfil scan: {result.Count} ai exfil points");
            return result;
        }

        // the LootPoint ctor overload needs a live LootPoint, which runtime scenes dont
        // reliably have — but every AILootPoint field is public, so build one bare. the
        // real ctor does nothing beyond these same assignments.
        private static AILootPoint MakeSimplePoint(int id, Vector3 stay, Vector3 look, string spawnId, AICorePoint core)
        {
            var p = (AILootPoint)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(AILootPoint));
            p.Id = id;
            p._pos = stay;
            p.LookPoint = look;
            p._lootableContainerId = null;
            p._lootSpawnId = spawnId;
            p.Core = core;
            p._coreId = core != null ? core.Id : 0;
            return p;
        }

        private static bool TryStandPos(Vector3 lootPos, out Vector3 stay)
        {
            stay = default;
            NavMeshHit hit;
            if (!NavMesh.SamplePosition(lootPos, out hit, 2f, -1))
                return false;
            float dx = hit.position.x - lootPos.x, dz = hit.position.z - lootPos.z;
            if (dx * dx + dz * dz > MaxStandOff * MaxStandOff)
                return false;
            stay = hit.position;
            return true;
        }

        private static AICorePoint Nearest(List<AICorePoint> cores, Vector3 pos)
        {
            AICorePoint best = null;
            float bestD = float.MaxValue;
            foreach (var c in cores)
            {
                if (c == null)
                    continue;
                float d = (c.Position - pos).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = c;
                }
            }
            return best;
        }

        // greedy formation with BSG's radius/size constants: seed a cluster on each
        // unassigned point, sweep in everything unassigned within radius and the same
        // connection group, keep it if it reaches the minimum size
        private static List<AILootPointsCluster> BuildClusters(List<AILootPoint> all)
        {
            var clusters = new List<AILootPointsCluster>();
            var assigned = new HashSet<int>();
            foreach (var seed in all)
            {
                if (assigned.Contains(seed.Id) || seed.Core == null)
                    continue;
                var members = new List<AILootPoint>();
                foreach (var p in all)
                {
                    if (assigned.Contains(p.Id) || p.Core == null)
                        continue;
                    if (p.Core.ConnectionGroupId != seed.Core.ConnectionGroupId)
                        continue;
                    if ((p.Position - seed.Position).sqrMagnitude <= ClusterRadius * ClusterRadius)
                        members.Add(p);
                }
                if (members.Count < MinClusterPoints)
                    continue;
                var cluster = new AILootPointsCluster(seed, ClusterRadius);
                foreach (var m in members)
                {
                    cluster.AddPoint(m);
                    assigned.Add(m.Id);
                }
                cluster.RebalanceCluster();
                clusters.Add(cluster);
            }
            return clusters;
        }
    }
}
