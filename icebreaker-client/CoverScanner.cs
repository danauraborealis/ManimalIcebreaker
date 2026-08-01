using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace Manimal.Icebreaker
{
    // prototype cover-point generator: proposes GroupPoint-shaped data from the live
    // map's navmesh + collision geometry, then dumps it in the same json shape as the
    // real data so analyze_dump.py can diff it against BSG's baked points.
    //
    // parity anchors with BSG's pipeline:
    //  - defence scoring calls the game's own CoverPointDefenceInfo(Vector3) ctor
    //  - raycasts use LayerMaskClass.HighPolyWithTerrainMask like GClass369 does
    //  - dedup uses the leaked CoverPointCreatorPreset cluster constants
    //    (CLUSTER_NEAR_DIST 0.4 / CLUSTER_LARGE_DIST 0.8 / CLUSTER_LARGE_ANGLE 50)
    //  - ground truth from dumps: wallDirection horizontal+unit, firePos = pos+1.272up,
    //    no Lay/altPosition/special, NN spacing median ~1m, way degree mean ~4
    internal static class CoverScanner
    {
        // sampling
        private const float SampleStep = 0.75f;         // grid pitch over the navmesh
        private const int RayDirections = 16;           // horizontal sweep per sample
        private const float DetectDist = 1.3f;          // how far a wall can be from a sample
        private const float DetectHeight = 1.0f;        // must block a crouched torso to count as cover
        private const float StayHeight = 1.5f;          // also blocks standing torso -> Stay
        private const float StandOff = 0.45f;           // body distance off the wall
        // dedup — CoverPointCreatorPreset defaults
        private const float ClusterNearDist = 0.4f;
        private const float ClusterLargeDist = 0.9f; // preset says 0.8 but we land ~1.18x BSG density there
        private const float ClusterLargeAngleCos = 0.6428f; // cos(50°)
        // ways graph — observed: mean degree ~3.9, way dists mostly < 8m
        private const float NeighborRadius = 6f;
        private const int MaxDegree = 4;
        // core skeleton — ground truth: Factory has 5 cores, GZ 31, which works out
        // to roughly one per 55m cell of occupied space
        private const float CoreCellSize = 55f;

        private class GenPoint
        {
            public int Id;
            public Vector3 Pos;
            public Vector3 WallDir;
            public string CoverLevel;
            public string Environment;
            public float DefenceLevel;
            public int Group = -1;
            public int CoreId = -1;
            public List<int> WayIds = new List<int>();
        }

        public static void TryGenerate()
        {
            try
            {
                Generate();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"generation failed: {e}");
            }
        }

        private static void Generate()
        {
            var sw = Stopwatch.StartNew();
            var (points, ways) = GenerateGraph(sw);
            if (points == null)
                return;

            AssignGroupsAndCores(points, ways, out var cores);
            Plugin.Log.LogInfo($"connection groups + {cores.Count} core points ({sw.ElapsedMilliseconds}ms)");

            foreach (var p in points)
                p.DefenceLevel = new CoverPointDefenceInfo(p.Pos).DefenceLevel;

            var voxels = BuildVoxels(points);
            Plugin.Log.LogInfo($"voxel grid: {voxels.cells.Count} cells ({sw.ElapsedMilliseconds}ms)");

            WriteDump(points, ways, cores, voxels);
            Plugin.Log.LogInfo($"generation done in {sw.ElapsedMilliseconds}ms");
        }

        // shared pipeline for the F10 dump and the raid-load injection: scan candidates,
        // link them with navmesh paths, bridge fragments, prune orphans
        private static (List<GenPoint>, List<(int id, int from, int to, float dist)>) GenerateGraph(Stopwatch sw)
        {
            var tri = NavMesh.CalculateTriangulation();
            if (tri.vertices == null || tri.vertices.Length == 0)
            {
                Plugin.Log.LogWarning("no navmesh loaded — nothing to scan");
                return (null, null);
            }

            var min = tri.vertices[0];
            var max = tri.vertices[0];
            foreach (var v in tri.vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            Plugin.Log.LogInfo($"scanning navmesh bounds {min} .. {max}");

            var points = ScanCandidates(min, max);
            Plugin.Log.LogInfo($"candidates after dedup: {points.Count} ({sw.ElapsedMilliseconds}ms)");

            var ways = BuildWays(points);
            Plugin.Log.LogInfo($"ways: {ways.Count} ({sw.ElapsedMilliseconds}ms)");

            BridgeAndPrune(ref points, ways);
            Plugin.Log.LogInfo($"after bridge+prune: {points.Count} points, {ways.Count} ways ({sw.ElapsedMilliseconds}ms)");
            return (points, ways);
        }

        // --- raid-load injection: swap the map's baked cover graph for a generated one ---
        //
        // replaces ONLY Points/Ways/voxel point-lists and keeps BSG's core points, patrols,
        // and voxel door/loot/exfil wiring. runs as a prefix on AICoversData.RestoreData, so
        // the game's own restore chain wires our data exactly like it would baked data.
        // one-variable-at-a-time: any bot behavior change is attributable to our points.
        public static void TryInjectIntoCovers(AICoversData covers)
        {
            try
            {
                InjectIntoCovers(covers);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"cover injection failed (baked data left untouched if this hit before the swap): {e}");
            }
        }

        // GENERATE-ON-EMPTY (backported maps): no baked covers, no baked cores — synthesize
        // the whole graph from the navmesh: cores as a spaced subset of generated points
        // (connection groups = ways-graph components), then the factory-validated builder
        // does the rest. call AFTER the voxel grid exists (bucketing needs it).
        public static void TryGenerateOnEmpty(AICoversData covers)
        {
            try
            {
                if (covers == null || (covers.Points != null && covers.Points.Count > 0))
                    return; // real baked data present — not our job
                if (covers.VoxelesArray == null)
                {
                    Plugin.Log.LogWarning("generate-on-empty skipped: no voxel grid");
                    return;
                }
                var sw = Stopwatch.StartNew();
                var (gen, genWays) = GenerateGraph(sw);
                if (gen == null || gen.Count == 0)
                {
                    Plugin.Log.LogWarning("generate-on-empty skipped: scanner produced nothing");
                    return;
                }
                var cores = SynthesizeCores(gen, genWays);
                if (covers.AICorePointsHolder != null)
                    covers.AICorePointsHolder.CorePoints = cores;
                Plugin.Log.LogInfo($"generate-on-empty: {gen.Count} scanned points, {cores.Count} synthesized cores");
                BuildAndSwapFromGen(covers, cores, gen, genWays, sw, 0);

                // on factory the ORIGINAL RestoreData ran after our swap and InitForGame'd
                // every point (resolves CorePointInGame — the field bots deref in
                // GetLastConnectionId). here the original is dead — do its job.
                foreach (var p in covers.Points)
                    p.InitForGame(covers.AICorePointsHolder);
                Plugin.Log.LogInfo($"generate-on-empty: {covers.Points.Count} points InitForGame'd");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"generate-on-empty failed: {e}");
            }
        }

        // REGION BUILD (hybrid bake). same scan, same GroupPoint construction as the full
        // swap above, but it hands back a SUBSET instead of overwriting covers.Points, and
        // anchors every point to a caller-supplied core list rather than synthesized cores.
        //
        // that anchor argument is the whole trick behind mixing our data with retail's:
        // pass retail's own AICorePointsHolder.CorePoints and each generated point inherits
        // a REAL ConnectionGroup from the surrounding baked graph, so bot group logic and
        // GetLastConnectionId keep working across the seam. synthesizing fresh cores instead
        // would give the region its own island ids and split the map's connectivity in two.
        //
        // ids are offset because retail already owns 0.._lastId and a duplicate id silently
        // wins or loses in RestoreData's dictionary rebuild.
        internal static (List<GroupPoint>, List<GroupPointWay>) BuildForRegion(
            Func<Vector3, bool> inRegion, List<AICorePoint> anchorCores, int idOffset)
        {
            var sw = Stopwatch.StartNew();
            var (gen, genWays) = GenerateGraph(sw);
            if (gen == null || gen.Count == 0) return (null, null);

            var keep = new Dictionary<int, GenPoint>();
            foreach (var g in gen)
                if (inRegion(g.Pos)) keep[g.Id] = g;
            if (keep.Count == 0) return (new List<GroupPoint>(), new List<GroupPointWay>());

            // a way only survives if BOTH ends did — a half-way pointing at a dropped point
            // is a null Target deref during the bot's cover walk
            var keptWays = new List<(int id, int from, int to, float dist)>();
            var keptWayIds = new HashSet<int>();
            foreach (var w in genWays)
                if (keep.ContainsKey(w.from) && keep.ContainsKey(w.to))
                {
                    keptWays.Add(w);
                    keptWayIds.Add(w.id);
                }

            var byId = new Dictionary<int, GroupPoint>(keep.Count);
            var points = new List<GroupPoint>(keep.Count);
            foreach (var g in keep.Values)
            {
                AICorePoint core = null;
                float best = float.MaxValue;
                foreach (var c in anchorCores)
                {
                    if (c == null) continue;
                    float d = (c.Position - g.Pos).sqrMagnitude;
                    if (d < best) { best = d; core = c; }
                }
                var firePos = g.Pos + Vector3.up * GroupPoint.CHECK_CAN_HIDE_STAY;
                var p = new GroupPoint(g.Id + idOffset, null, g.Pos, null, core,
                    g.CoverLevel == "Stay" ? CoverLevel.Stay : CoverLevel.Sit,
                    false, g.WallDir, firePos, PointWithNeighborType.cover);
                p.CoverType = CoverType.Wall;
                p.EnvironmentType = g.Environment == "Indoor" ? EnvironmentType.Indoor : EnvironmentType.Outdoor;
                p.NeighbourhoodsWaysIds = g.WayIds.Where(keptWayIds.Contains).Select(i => i + idOffset).ToList();
                p.CalcDefenceLevel();
                try { p.InitLightBorders(); } catch { }
                try { p.method_0(); } catch { }
                try { p.method_2(); } catch { }
                points.Add(p);
                byId[g.Id] = p;
            }

            var ways = new List<GroupPointWay>(keptWays.Count);
            foreach (var w in keptWays)
            {
                var way = new GroupPointWay(w.id + idOffset, byId[w.to], w.dist);
                way.IdTarget = w.to + idOffset;
                ways.Add(way);
            }
            Plugin.Log.LogInfo($"[Hybrid] region build: {points.Count}/{gen.Count} scanned points kept, "
                               + $"{ways.Count} ways ({sw.ElapsedMilliseconds}ms)");
            return (points, ways);
        }

        // cores = the coarse anchor nodes bots group around. spaced subset (~15m) of the
        // generated points; ConnectionGroupId = connected component of the ways graph
        // (union-find), matching BSG's navmesh-island semantics.
        private static List<AICorePoint> SynthesizeCores(List<GenPoint> gen, List<(int id, int from, int to, float dist)> ways)
        {
            var parent = new Dictionary<int, int>();
            int Find(int x)
            {
                while (parent.TryGetValue(x, out var p) && p != x) { x = p; }
                return x;
            }
            foreach (var g in gen) parent[g.Id] = g.Id;
            foreach (var w in ways)
            {
                int a = Find(w.from), b = Find(w.to);
                if (a != b) parent[a] = b;
            }
            var groupIds = new Dictionary<int, int>();
            int nextGroup = 1;

            var parentGo = new GameObject("GeneratedAICorePoints");
            var cores = new List<AICorePoint>();
            var accepted = new List<Vector3>();
            const float spacing2 = 15f * 15f;
            int coreId = 1;
            foreach (var g in gen)
            {
                bool tooClose = false;
                foreach (var a in accepted)
                    if ((a - g.Pos).sqrMagnitude < spacing2) { tooClose = true; break; }
                if (tooClose) continue;
                accepted.Add(g.Pos);

                int root = Find(g.Id);
                if (!groupIds.TryGetValue(root, out var grp)) groupIds[root] = grp = nextGroup++;

                var go = new GameObject($"GenCore_{coreId}");
                go.transform.SetParent(parentGo.transform);
                go.transform.position = g.Pos;
                var core = go.AddComponent<AICorePoint>();
                core.SetIds(coreId++, grp);
                if (core.ConnectionsAtNet == null) core.ConnectionsAtNet = new List<AICorePoint>();
                cores.Add(core);
            }
            return cores;
        }

        private static void InjectIntoCovers(AICoversData covers)
        {
            if (covers == null || covers.Points == null || covers.Points.Count == 0)
            {
                Plugin.Log.LogInfo("injection skipped: no baked covers here (hideout/menu?)");
                return;
            }
            var cores = covers.AICorePointsHolder != null ? covers.AICorePointsHolder.CorePoints : null;
            if (cores == null || cores.Count == 0)
            {
                Plugin.Log.LogWarning("injection skipped: no AICorePoints to attach generated points to");
                return;
            }

            var sw = Stopwatch.StartNew();
            int bakedCount = covers.Points.Count;
            var (gen, genWays) = GenerateGraph(sw);
            if (gen == null || gen.Count == 0)
            {
                Plugin.Log.LogWarning("injection skipped: scanner produced nothing");
                return;
            }
            BuildAndSwapFromGen(covers, cores, gen, genWays, sw, bakedCount);
        }

        private static void BuildAndSwapFromGen(AICoversData covers, List<AICorePoint> cores,
            List<GenPoint> gen, List<(int id, int from, int to, float dist)> genWays, Stopwatch sw, int bakedCount)
        {
            // build real GroupPoints attached to the nearest ORIGINAL core point — the ctor
            // takes ConnectionGroup from the core, keeping patrols and bot group logic intact
            var byId = new Dictionary<int, GroupPoint>(gen.Count);
            var newPoints = new List<GroupPoint>(gen.Count);
            foreach (var g in gen)
            {
                AICorePoint core = null;
                float best = float.MaxValue;
                foreach (var c in cores)
                {
                    if (c == null)
                        continue;
                    float d = (c.Position - g.Pos).sqrMagnitude;
                    if (d < best)
                    {
                        best = d;
                        core = c;
                    }
                }
                // FIRE POSITION — was Vector3.zero, which is not "unset" as far as the AI is
                // concerned. CustomNavigationPoint guards with (FirePosition != zero), but
                // GClass236 does NOT: it tests SqrDistHorizontal(FirePosition, Position) > 0.1
                // and then walks to Vector3.Lerp(FirePosition, Position, 0.3f). with zero that
                // distance is the point's whole distance from world origin, so every generated
                // cover point told the bot its firing spot was 70% of the way to (0,0,0).
                //
                // retail's own value, measured across all 1885 baked points: a PURELY VERTICAL
                // offset of 1.272m (median == min; horizontal alignment with WallDirection
                // ~0.001). that number is BSG's own GroupPoint.CHECK_CAN_HIDE_STAY constant,
                // and vertical-only is what makes the SqrDistHorizontal test read zero — i.e.
                // the branch retail actually takes.
                var firePos = g.Pos + Vector3.up * GroupPoint.CHECK_CAN_HIDE_STAY;

                var p = new GroupPoint(g.Id, null, g.Pos, null, core,
                    g.CoverLevel == "Stay" ? CoverLevel.Stay : CoverLevel.Sit,
                    false, g.WallDir, firePos, PointWithNeighborType.cover);
                p.CoverType = CoverType.Wall;
                p.EnvironmentType = g.Environment == "Indoor" ? EnvironmentType.Indoor : EnvironmentType.Outdoor;
                p.NeighbourhoodsWaysIds = g.WayIds;
                p.CalcDefenceLevel();

                // let BSG fill the rest rather than approximating it. every one of these
                // fields has a real calculator on GroupPoint, and method_0 in particular does
                // actual raycasts (GClass369.TestDir) — guessing from geometry would be
                // strictly worse than calling the thing that ships with the game.
                //   InitLightBorders -> BordersLightHave + Left/RightBorderLight
                //                       (WallDirection rotated +-57deg = LIGHT_WALL_ANG)
                //   method_0         -> CanLookLeft / CanLookRight  (peeking round cover)
                //   method_2         -> TiltType                    (leaning; Stay cover only)
                // order matters: method_0 and method_2 both read FirePosition, so they have to
                // run after it is set, and method_2 needs CoverLevel too.
                try { p.InitLightBorders(); } catch { }
                try { p.method_0(); } catch { }
                try { p.method_2(); } catch { }
                newPoints.Add(p);
                byId[g.Id] = p;
            }

            var newWays = new List<GroupPointWay>(genWays.Count);
            foreach (var w in genWays)
            {
                var way = new GroupPointWay(w.id, byId[w.to], w.dist);
                way.IdTarget = w.to; // RestoreData rewires Target from this id — dont trust the ctor to set it
                newWays.Add(way);
            }

            covers.Points = newPoints;
            covers.Ways = newWays;
            covers.Pathes = new List<GroupPointPath>();

            // generated door links replace baked ones (own try — a door failure shouldnt
            // sink the cover swap; without links bots path into closed doors and stall)
            List<NavMeshDoorLink> doorLinks = null;
            try
            {
                doorLinks = DoorScanner.GenerateAndSwap();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"door link generation failed: {e}");
            }

            // generated loot points: containers now, loose loot later in the RestoreLoot
            // postfix (spawned-loot records dont exist yet); exfil points stay baked
            int bakedLoot = (covers.Patrols?.ContainerLootPoints?.Count ?? 0) + (covers.Patrols?.SimpleLootPoints?.Count ?? 0);
            List<AILootPoint> allLoot = null;
            List<AIExfiltrationPoint> exfils = null;
            if (covers.Patrols != null)
            {
                var cont = LootScanner.GenerateContainers(covers, cores);
                covers.Patrols.ContainerLootPoints = cont;
                covers.Patrols.SimpleLootPoints = new List<AILootPoint>();
                covers.Patrols.LootPointClusters = new List<AILootPointsCluster>();
                allLoot = cont;
                exfils = LootScanner.GenerateExfils(cores);
                covers.Patrols.ExfiltrationPoints = exfils;
            }

            // re-bucket the ORIGINAL voxel grid's point + loot lists; door/exfil ids stay baked.
            // every cell gets rewritten so no stale _closetsPointId can dangle into the old ids.
            var vox = covers.Voxels;
            var byCell = new Dictionary<(int, int, int), List<GroupPoint>>();
            foreach (var p in newPoints)
            {
                var key = CellIndex(vox, p.Position);
                if (!byCell.TryGetValue(key, out var list))
                    byCell[key] = list = new List<GroupPoint>();
                list.Add(p);
            }
            var lootByCell = new Dictionary<(int, int, int), List<int>>();
            if (allLoot != null)
                foreach (var lp in allLoot)
                {
                    var key = CellIndex(vox, lp.Position);
                    if (!lootByCell.TryGetValue(key, out var list))
                        lootByCell[key] = list = new List<int>();
                    list.Add(lp.Id);
                }
            // baked convention: exfil ids live in their containing cell only
            var exfilByCell = new Dictionary<(int, int, int), List<int>>();
            if (exfils != null)
                foreach (var ex in exfils)
                {
                    var key = CellIndex(vox, ex.Position);
                    if (!exfilByCell.TryGetValue(key, out var list))
                        exfilByCell[key] = list = new List<int>();
                    list.Add(ex.Id);
                }
            // baked convention: each door link id appears in the full 3x3x3 neighborhood
            // around its cell (median 27 cells/link on factory, fewer only at grid edges)
            var doorByCell = new Dictionary<(int, int, int), List<int>>();
            var linkById = new Dictionary<int, NavMeshDoorLink>();
            if (doorLinks != null)
                foreach (var dl in doorLinks)
                {
                    linkById[dl.Id] = dl;
                    var (cx, cy, cz) = CellIndex(vox, dl.MidOpen);
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                int nx = cx + dx, ny = cy + dy, nz = cz + dz;
                                if (nx < 0 || ny < 0 || nz < 0 || nx >= vox.MaxX || ny >= vox.MaxY || nz >= vox.MaxZ)
                                    continue;
                                var key = (nx, ny, nz);
                                if (!doorByCell.TryGetValue(key, out var list))
                                    doorByCell[key] = list = new List<int>();
                                list.Add(dl.Id);
                            }
                }
            foreach (var cell in vox.VoxelsList)
            {
                cell.PointsIds = new List<int>();
                cell.Points = new List<GroupPoint>();
                cell._closetsPointId = 0;
                cell.PointStartSearch = null;
                var idx = ((int)cell.IndexX, (int)cell.IndexY, (int)cell.IndexZ);
                if (byCell.TryGetValue(idx, out var inside))
                {
                    var centre = cell.Position + new Vector3(5f, 2.5f, 5f);
                    cell.PointsIds = inside.Select(p => p.Id).ToList();
                    cell.Points = inside.ToList();
                    var closest = inside.OrderBy(p => (p.Position - centre).sqrMagnitude).First();
                    cell._closetsPointId = closest.Id;
                    // normally voxel.RestoreData resolves this from _closetsPointId — we
                    // set it directly (bots deref CurVoxel.PointStartSearch.CorePointInGame
                    // in GetLastConnectionId; a null here = ActiveFail)
                    cell.PointStartSearch = closest;
                }
                if (allLoot != null)
                    cell.LootPointsIds = lootByCell.TryGetValue(idx, out var lootIds) ? lootIds : new List<int>();
                if (doorLinks != null)
                {
                    cell.DoorLinksIds = doorByCell.TryGetValue(idx, out var doorIds) ? doorIds : new List<int>();
                    // AND the RESOLVED list. ids alone are worthless here: the id->object
                    // resolve lives in AIVoxelesData.RestoreData, which on this path already
                    // ran (or died) long before these links existed, and nothing runs it
                    // again. every bot reads the OBJECT list —
                    //   BotNearDoorData.CurrentDoorLinks() => CurVoxel.DoorLinks
                    // so an empty DoorLinks means GetNearestDoor() returns null, BotDoorOpener
                    // never gets a CurrentDoorLink, and no bot ever interacts with a door.
                    // it fails SILENTLY rather than throwing because BuildVoxelGrid seeds the
                    // list empty — which is exactly what "bots walk straight through doors"
                    // looked like. a shut door carves nothing (BotDoorsController.method_0
                    // only enables Carver_Opened, and only while the door is OPEN), so the
                    // doorway stays walkable navmesh and they just stroll through it.
                    cell.DoorLinks = cell.DoorLinksIds
                        .Select(i => linkById.TryGetValue(i, out var l) ? l : null)
                        .Where(l => l != null).ToList();
                }
                if (exfils != null)
                    cell.ExfiltrationPointsIds = exfilByCell.TryGetValue(idx, out var exfilIds) ? exfilIds : new List<int>();
            }

            // dead-cell fill: cells with no points still get a PointStartSearch (nearest
            // point overall) — bots standing in an empty cell deref it via
            // GetLastConnectionId and NRE otherwise. keep PointsIds empty (no cover THERE),
            // only the search seed is borrowed.
            int filled = 0;
            foreach (var cell in vox.VoxelsList)
            {
                if (cell.PointStartSearch != null || newPoints.Count == 0) continue;
                var centre = cell.Position + new Vector3(5f, 2.5f, 5f);
                GroupPoint nearest = null;
                float best = float.MaxValue;
                foreach (var p in newPoints)
                {
                    float d = (p.Position - centre).sqrMagnitude;
                    if (d < best) { best = d; nearest = p; }
                }
                cell.PointStartSearch = nearest;
                cell._closetsPointId = nearest.Id;
                filled++;
            }

            Plugin.Log.LogInfo($"INJECTED {newPoints.Count} generated cover points (replacing {bakedCount} baked) + {newWays.Count} ways"
                + (allLoot != null ? $" + {allLoot.Count} loot points (replacing {bakedLoot} baked)" : "")
                + $"; {filled} empty cells seeded, in {sw.ElapsedMilliseconds}ms");
        }

        // --- stage 1: propose + dedup candidates ---

        private static List<GenPoint> ScanCandidates(Vector3 min, Vector3 max)
        {
            var accepted = new List<GenPoint>();
            var hash = new SpatialHash<GenPoint>(2f);
            int mask = LayerMaskClass.HighPolyWithTerrainMask;
            // stage counters — when output stats look wrong these say where candidates died
            long samples = 0, rayHits = 0, smallEdge = 0, offNav = 0, recheckFail = 0, clustered = 0;

            for (float x = min.x; x <= max.x; x += SampleStep)
            {
                for (float z = min.z; z <= max.z; z += SampleStep)
                {
                    // probe the full height range — maps are multi-storey
                    for (float y = min.y; y <= max.y + 2f; y += 4f)
                    {
                        NavMeshHit navHit;
                        if (!NavMesh.SamplePosition(new Vector3(x, y, z), out navHit, 1.9f, -1))
                            continue;
                        var basePos = navHit.position;
                        samples++;

                        for (int i = 0; i < RayDirections; i++)
                        {
                            float ang = i * 2f * Mathf.PI / RayDirections;
                            var dir = new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang));
                            RaycastHit hit;
                            if (!Physics.Raycast(basePos + Vector3.up * DetectHeight, dir, out hit, DetectDist, mask))
                                continue;
                            rayHits++;

                            // wallDirection convention from ground truth: unit, horizontal, pointing
                            // off the wall into open space — i.e. the horizontalized surface normal
                            var wallDir = new Vector3(hit.normal.x, 0f, hit.normal.z);
                            if (wallDir.sqrMagnitude < 0.25f)
                                continue; // near-horizontal surface (floor/ramp), not a wall
                            wallDir.Normalize();

                            // preset SMALL_EDGE_SIZE = 1: BSG skips cover shorter than 1m. the wall
                            // must continue ±0.5m along its tangent, else its a pillar/prop/clutter
                            var tangent = Vector3.Cross(Vector3.up, wallDir);
                            var probeBase = hit.point + wallDir * 0.3f;
                            if (!Physics.Raycast(probeBase + tangent * 0.5f, -wallDir, 0.6f, mask)
                                || !Physics.Raycast(probeBase - tangent * 0.5f, -wallDir, 0.6f, mask))
                            {
                                smallEdge++;
                                continue;
                            }

                            // hit.point is at torso height — drop the candidate to floor level
                            // before navmesh-sampling, or flat ground never validates
                            var candidate = new Vector3(hit.point.x, basePos.y, hit.point.z) + wallDir * StandOff;
                            NavMeshHit candHit;
                            if (!NavMesh.SamplePosition(candidate, out candHit, 0.6f, -1))
                            {
                                offNav++;
                                continue;
                            }
                            var pos = candHit.position;

                            // still covered from the final position?
                            if (!Physics.Raycast(pos + Vector3.up * DetectHeight, -wallDir, out hit, StandOff + 0.5f, mask))
                            {
                                recheckFail++;
                                continue;
                            }
                            bool tall = Physics.Raycast(pos + Vector3.up * StayHeight, -wallDir, StandOff + 0.5f, mask);

                            var gp = new GenPoint
                            {
                                Pos = pos,
                                WallDir = wallDir,
                                CoverLevel = tall ? "Stay" : "Sit",
                            };
                            if (PassesClusterRules(gp, hash))
                            {
                                accepted.Add(gp);
                                hash.Add(pos, gp);
                            }
                            else
                            {
                                clustered++;
                            }
                        }
                    }
                }
            }
            Plugin.Log.LogInfo($"scan stages: samples={samples} rayHits={rayHits} smallEdge={smallEdge} offNav={offNav} recheckFail={recheckFail} clusterRejected={clustered} accepted={accepted.Count}");

            // environment classification after dedup — one raycast per surviving point
            foreach (var p in accepted)
                p.Environment = Physics.Raycast(p.Pos + Vector3.up * 0.5f, Vector3.up, 15f, mask)
                    ? "Indoor" : "Outdoor";

            for (int i = 0; i < accepted.Count; i++)
                accepted[i].Id = i + 1;
            return accepted;
        }

        private static bool PassesClusterRules(GenPoint p, SpatialHash<GenPoint> hash)
        {
            foreach (var q in hash.Near(p.Pos))
            {
                float d = Vector3.Distance(p.Pos, q.Pos);
                if (d < ClusterNearDist)
                    return false;
                // same-facing points cluster harder than divergent ones (preset: 0.8m / 50°)
                if (d < ClusterLargeDist && Vector3.Dot(p.WallDir, q.WallDir) > ClusterLargeAngleCos)
                    return false;
            }
            return true;
        }

        // --- stage 2: bidirectional ways via real navmesh paths ---

        private static List<(int id, int from, int to, float dist)> BuildWays(List<GenPoint> points)
        {
            var hash = new SpatialHash<GenPoint>(NeighborRadius);
            foreach (var p in points)
                hash.Add(p.Pos, p);

            var ways = new List<(int, int, int, float)>();
            var linked = new HashSet<(int, int)>();
            var path = new NavMeshPath();
            int nextWayId = 1;

            foreach (var p in points)
            {
                if (p.WayIds.Count >= MaxDegree)
                    continue;
                var neighbors = hash.Near(p.Pos)
                    .Where(q => q.Id != p.Id && !linked.Contains((p.Id, q.Id)))
                    .OrderBy(q => (q.Pos - p.Pos).sqrMagnitude)
                    .ToList();
                foreach (var q in neighbors)
                {
                    if (p.WayIds.Count >= MaxDegree)
                        break;
                    if (q.WayIds.Count >= MaxDegree)
                        continue;
                    float euclid = Vector3.Distance(p.Pos, q.Pos);
                    if (euclid > NeighborRadius)
                        continue;
                    if (!NavMesh.CalculatePath(p.Pos, q.Pos, -1, path) || path.status != NavMeshPathStatus.PathComplete)
                        continue;
                    float dist = PathLength(path);
                    // ground truth: median stretch ~1.04 — a wildly indirect "neighbor" isnt one
                    if (dist > euclid * 3f + 1f)
                        continue;

                    int idAB = nextWayId++;
                    int idBA = nextWayId++;
                    ways.Add((idAB, p.Id, q.Id, dist));
                    ways.Add((idBA, q.Id, p.Id, dist));
                    p.WayIds.Add(idAB);
                    q.WayIds.Add(idBA);
                    linked.Add((p.Id, q.Id));
                    linked.Add((q.Id, p.Id));
                }
            }
            return ways;
        }

        // greedy linking fills every degree slot with same-wall neighbors, so reachable
        // pockets never join the main graph (real data has bridge links up to 124m).
        // bridge fragments together ignoring the degree cap, then drop whatever still
        // cant connect and is too small to be a legit island (GZ's smallest real is 26).
        private const float BridgeRadius = 40f;
        private const int MinComponentSize = 20;

        private static void BridgeAndPrune(ref List<GenPoint> points, List<(int id, int from, int to, float dist)> ways)
        {
            var byId = points.ToDictionary(p => p.Id);
            int nextWayId = ways.Count > 0 ? ways.Max(w => w.id) + 1 : 1;
            var path = new NavMeshPath();

            for (int round = 0; round < 6; round++)
            {
                var comps = Components(points, ways);
                if (comps.Count <= 1)
                    break;
                var main = comps.OrderByDescending(c => c.Count).First();

                var hash = new SpatialHash<GenPoint>(BridgeRadius / 2f);
                foreach (var p in points)
                    hash.Add(p.Pos, p);
                var compOf = new Dictionary<int, int>();
                for (int i = 0; i < comps.Count; i++)
                    foreach (var pid in comps[i])
                        compOf[pid] = i;

                bool merged = false;
                for (int ci = 0; ci < comps.Count; ci++)
                {
                    if (comps[ci] == main)
                        continue;
                    // closest foreign candidates first, few attempts — this is CalculatePath-heavy
                    var tried = 0;
                    var pairs = comps[ci].Select(pid => byId[pid])
                        .SelectMany(p => hash.Near(p.Pos)
                            .Where(q => compOf[q.Id] != ci)
                            .Select(q => (p, q, d: (p.Pos - q.Pos).sqrMagnitude)))
                        .Where(t => t.d < BridgeRadius * BridgeRadius)
                        .OrderBy(t => t.d);
                    foreach (var (p, q, _) in pairs)
                    {
                        if (tried++ >= 24)
                            break;
                        if (!NavMesh.CalculatePath(p.Pos, q.Pos, -1, path) || path.status != NavMeshPathStatus.PathComplete)
                            continue;
                        float dist = PathLength(path);
                        int idAB = nextWayId++;
                        int idBA = nextWayId++;
                        ways.Add((idAB, p.Id, q.Id, dist));
                        ways.Add((idBA, q.Id, p.Id, dist));
                        p.WayIds.Add(idAB);
                        q.WayIds.Add(idBA);
                        merged = true;
                        break;
                    }
                }
                if (!merged)
                    break;
            }

            // prune leftover micro-fragments and their ways
            var final = Components(points, ways);
            var keepIds = new HashSet<int>(final.Where(c => c.Count >= MinComponentSize).SelectMany(c => c));
            if (keepIds.Count < points.Count)
            {
                points = points.Where(p => keepIds.Contains(p.Id)).ToList();
                var deadWays = new HashSet<int>(ways.Where(w => !keepIds.Contains(w.from) || !keepIds.Contains(w.to)).Select(w => w.id));
                ways.RemoveAll(w => deadWays.Contains(w.id));
                foreach (var p in points)
                    p.WayIds.RemoveAll(id => deadWays.Contains(id));
            }
        }

        private static List<List<int>> Components(List<GenPoint> points, List<(int id, int from, int to, float dist)> ways)
        {
            var parent = points.ToDictionary(p => p.Id, p => p.Id);
            int Find(int a)
            {
                while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
                return a;
            }
            foreach (var w in ways)
                if (parent.ContainsKey(w.from) && parent.ContainsKey(w.to))
                    parent[Find(w.from)] = Find(w.to);
            return points.GroupBy(p => Find(p.Id)).Select(g => g.Select(p => p.Id).ToList()).ToList();
        }

        private static float PathLength(NavMeshPath path)
        {
            float len = 0f;
            var c = path.corners;
            for (int i = 1; i < c.Length; i++)
                len += Vector3.Distance(c[i - 1], c[i]);
            return len;
        }

        // --- stage 3: connection groups (union-find over ways) + core skeleton ---

        private static void AssignGroupsAndCores(List<GenPoint> points,
            List<(int id, int from, int to, float dist)> ways,
            out List<(int id, int group, Vector3 pos, List<int> connections)> cores)
        {
            var byId = points.ToDictionary(p => p.Id);
            var parent = new Dictionary<int, int>();
            int Find(int a)
            {
                while (parent.TryGetValue(a, out int b) && b != a) { parent[a] = parent.GetValueOrDefault(b, b); a = parent[a]; }
                return a;
            }
            foreach (var p in points)
                parent[p.Id] = p.Id;
            foreach (var w in ways)
                parent[Find(w.from)] = Find(w.to);

            // contiguous-ish group ids; BSG's arent contiguous either so exact values dont matter
            var groupOf = new Dictionary<int, int>();
            int nextGroup = 1;
            foreach (var p in points)
            {
                int root = Find(p.Id);
                if (!groupOf.TryGetValue(root, out int g))
                    groupOf[root] = g = nextGroup++;
                p.Group = g;
            }

            // one core per ~30m cell per group, at the point closest to the cell centre
            var coreCells = new Dictionary<(int, int, int, int), GenPoint>();
            foreach (var p in points)
            {
                var key = (p.Group, Mathf.FloorToInt(p.Pos.x / CoreCellSize), Mathf.FloorToInt(p.Pos.y / 10f), Mathf.FloorToInt(p.Pos.z / CoreCellSize));
                var centre = new Vector3((key.Item2 + 0.5f) * CoreCellSize, 0, (key.Item4 + 0.5f) * CoreCellSize);
                if (!coreCells.TryGetValue(key, out var cur)
                    || HorizontalSqr(p.Pos, centre) < HorizontalSqr(cur.Pos, centre))
                    coreCells[key] = p;
            }
            cores = new List<(int, int, Vector3, List<int>)>();
            int coreId = 1;
            foreach (var kv in coreCells)
                cores.Add((coreId++, kv.Key.Item1, kv.Value.Pos, new List<int>()));

            // link each core to its 2 nearest same-group cores; assign every point its nearest core
            foreach (var c in cores)
            {
                var near = cores.Where(o => o.id != c.id && o.group == c.group)
                    .OrderBy(o => (o.pos - c.pos).sqrMagnitude).Take(2);
                foreach (var o in near)
                    if (!c.connections.Contains(o.id))
                        c.connections.Add(o.id);
            }
            foreach (var p in points)
            {
                var best = cores.Where(c => c.group == p.Group)
                    .OrderBy(c => (c.pos - p.Pos).sqrMagnitude).FirstOrDefault();
                p.CoreId = best.id;
            }
        }

        // same double-int-cast floor the game uses in GetIndexes, clamped like GetVoxelSafe
        internal static (int, int, int) CellIndex(AIVoxelesData vox, Vector3 pos)
        {
            int ix = Mathf.Clamp((int)((float)((int)(pos.x - vox.MinVoxelesValues.x)) / 10f), 0, vox.MaxX - 1);
            int iy = Mathf.Clamp((int)((float)((int)(pos.y - vox.MinVoxelesValues.y)) / 5f), 0, vox.MaxY - 1);
            int iz = Mathf.Clamp((int)((float)((int)(pos.z - vox.MinVoxelesValues.z)) / 10f), 0, vox.MaxZ - 1);
            return (ix, iy, iz);
        }

        private static float HorizontalSqr(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        // --- stage 4: dense voxel grid, NavGraphContainer's exact bounds math ---

        private static (Vector3 min, int maxX, int maxY, int maxZ, List<object> cells) BuildVoxels(List<GenPoint> points)
        {
            var mn = points[0].Pos;
            var mx = points[0].Pos;
            foreach (var p in points)
            {
                mn = Vector3.Min(mn, p.Pos);
                mx = Vector3.Max(mx, p.Pos);
            }
            // NavGraphContainer floors min-1 to int and pads dims by +1/+2/+1
            var min = new Vector3((int)(mn.x - 1f), (int)(mn.y - 1f), (int)(mn.z - 1f));
            int maxX = 1 + (int)((mx.x - min.x) / 10f);
            int maxY = 2 + (int)((mx.y - min.y) / 5f);
            int maxZ = 1 + (int)((mx.z - min.z) / 10f);

            var byCell = new Dictionary<(int, int, int), List<GenPoint>>();
            foreach (var p in points)
            {
                var key = ((int)((p.Pos.x - min.x) / 10f), (int)((p.Pos.y - min.y) / 5f), (int)((p.Pos.z - min.z) / 10f));
                if (!byCell.TryGetValue(key, out var list))
                    byCell[key] = list = new List<GenPoint>();
                list.Add(p);
            }

            var cells = new List<object>();
            int id = 0;
            for (int i = 0; i < maxX; i++)
                for (int j = 0; j < maxZ; j++)
                    for (int k = 0; k < maxY; k++)
                    {
                        var pos = new Vector3(min.x + i * 10f, min.y + k * 5f, min.z + j * 10f);
                        byCell.TryGetValue((i, k, j), out var inside);
                        var centre = pos + new Vector3(5f, 2.5f, 5f);
                        int closest = 0;
                        if (inside != null && inside.Count > 0)
                            closest = inside.OrderBy(p => (p.Pos - centre).sqrMagnitude).First().Id;
                        NavMeshHit nh;
                        bool haveNav = NavMesh.SamplePosition(centre, out nh, 4f, -1)
                            && nh.position.x >= pos.x && nh.position.x < pos.x + 10f
                            && nh.position.z >= pos.z && nh.position.z < pos.z + 10f;
                        cells.Add(new
                        {
                            id = id++,
                            ix = i,
                            iy = k,
                            iz = j,
                            position = new { x = pos.x, y = pos.y, z = pos.z },
                            haveNavMesh = haveNav,
                            closestPointId = closest,
                            pointsIds = (object)(inside != null ? inside.Select(p => p.Id).ToList() : new List<int>()),
                            entrancesIds = new List<int>(),
                            doorLinksIds = new List<int>(),
                            lootPointsIds = new List<int>(),
                            exfiltrationPointsIds = new List<int>(),
                        });
                    }
            return (min, maxX, maxY, maxZ, cells);
        }

        // --- output, same shape as AiDump so analyze_dump.py works unchanged ---

        private static void WriteDump(List<GenPoint> points,
            List<(int id, int from, int to, float dist)> ways,
            List<(int id, int group, Vector3 pos, List<int> connections)> cores,
            (Vector3 min, int maxX, int maxY, int maxZ, List<object> cells) grid)
        {
            string location = AiDump.CurrentLocation();
            var root = new
            {
                meta = new
                {
                    location,
                    reason = "generated",
                    dumpedAtUtc = DateTime.UtcNow.ToString("o"),
                    modVersion = BuildInfo.Version,
                    groupPointCount = points.Count,
                    voxelCount = grid.cells.Count,
                },
                corePoints = cores.Select(c => new
                {
                    id = c.id,
                    connectionGroupId = c.group,
                    position = new { x = c.pos.x, y = c.pos.y, z = c.pos.z },
                    connections = c.connections,
                }),
                groupPoints = points.Select(p => new
                {
                    id = p.Id,
                    position = new { x = p.Pos.x, y = p.Pos.y, z = p.Pos.z },
                    altPosition = (object)null,
                    firePosition = new { x = p.Pos.x, y = p.Pos.y + 1.272f, z = p.Pos.z },
                    wallDirection = new { x = p.WallDir.x, y = p.WallDir.y, z = p.WallDir.z },
                    coverLevel = p.CoverLevel,
                    coverType = "Wall",
                    special = "0",
                    environmentType = p.Environment,
                    idEnvironment = 0,
                    neighborType = "cover",
                    connectionGroup = p.Group,
                    corePointId = p.CoreId,
                    placeId = 0,
                    parentNavPoint = -1,
                    alwaysGood = false,
                    isGoodInsideBuilding = false,
                    defenceLevel = p.DefenceLevel,
                    waysIds = p.WayIds,
                }),
                manualPoints = new object[0],
                ways = ways.Select(w => new { id = w.id, idTarget = w.to, dist = w.dist }),
                pathes = new object[0],
                voxelGrid = new
                {
                    min = new { x = grid.min.x, y = grid.min.y, z = grid.min.z },
                    max = new { x = (float)grid.maxX, y = (float)grid.maxY, z = (float)grid.maxZ },
                    cells = grid.cells,
                },
                patrolsData = (object)null,
                botZones = new object[0],
                placeInfos = new object[0],
            };
            string file = AiDump.WriteJson(root, $"{location}_generated");
            Plugin.Log.LogInfo($"generated {points.Count} points / {ways.Count} ways -> {file}");
        }

        // dumb uniform-cell spatial hash; Near() scans the 3x3x3 neighborhood
        private class SpatialHash<T>
        {
            private readonly float _cell;
            private readonly Dictionary<(int, int, int), List<(Vector3, T)>> _map = new Dictionary<(int, int, int), List<(Vector3, T)>>();

            public SpatialHash(float cell) { _cell = cell; }

            private (int, int, int) Key(Vector3 p) =>
                (Mathf.FloorToInt(p.x / _cell), Mathf.FloorToInt(p.y / _cell), Mathf.FloorToInt(p.z / _cell));

            public void Add(Vector3 pos, T item)
            {
                var k = Key(pos);
                if (!_map.TryGetValue(k, out var list))
                    _map[k] = list = new List<(Vector3, T)>();
                list.Add((pos, item));
            }

            public IEnumerable<T> Near(Vector3 pos)
            {
                var (kx, ky, kz) = Key(pos);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                            if (_map.TryGetValue((kx + dx, ky + dy, kz + dz), out var list))
                                foreach (var (_, item) in list)
                                    yield return item;
            }
        }
    }
}
