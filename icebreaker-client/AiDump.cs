using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // walks the live AICoversData object graph and streams it to json. everything here
    // reads public members verified against the 4.0 assembly except PatrolPoint._corePointId,
    // which is private — pulled via reflection since it links patrol points to the core graph.
    internal static class AiDump
    {
        private static readonly System.Reflection.FieldInfo PatrolCoreIdField =
            AccessTools.Field(typeof(PatrolPoint), "_corePointId");

        public static void TryDump(AICoversData covers, string reason)
        {
            try
            {
                Dump(covers, reason);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"dump failed ({reason}): {e}");
            }
        }

        private static void Dump(AICoversData covers, string reason)
        {
            if (covers == null)
            {
                Plugin.Log.LogDebug($"dump requested ({reason}) but no AICoversData in scene");
                return;
            }

            var points = covers.Points ?? new List<GroupPoint>();
            var voxels = covers.Voxels != null ? covers.Voxels.VoxelsList : null;
            if (points.Count == 0 && (voxels == null || voxels.Count == 0))
            {
                // hideout / menu scenes create empty holders via CreateOrFind — nothing to learn there
                Plugin.Log.LogInfo($"dump skipped ({reason}): covers data is empty");
                return;
            }

            string location = CurrentLocation();

            var root = new
            {
                meta = new
                {
                    location,
                    reason,
                    dumpedAtUtc = DateTime.UtcNow.ToString("o"),
                    modVersion = BuildInfo.Version,
                    groupPointCount = points.Count,
                    voxelCount = voxels?.Count ?? 0,
                },
                corePoints = DumpCorePoints(covers),
                groupPoints = points.Select(DumpGroupPoint),
                manualPoints = (covers.AIManualPointsHolder != null && covers.AIManualPointsHolder.ManualPoints != null
                    ? covers.AIManualPointsHolder.ManualPoints
                    : new List<GroupPoint>()).Select(DumpGroupPoint),
                ways = (covers.Ways ?? new List<GroupPointWay>()).Select(w => new
                {
                    id = w.Id,
                    idTarget = w.IdTarget,
                    dist = w.Dist,
                }),
                pathes = (covers.Pathes ?? new List<GroupPointPath>()).Select(p => new
                {
                    idPath = p.IdPath,
                    idA = p.IdA,
                    idB = p.IdB,
                    dist = p.Dist,
                }),
                voxelGrid = DumpVoxels(covers.Voxels),
                patrolsData = DumpPatrolsData(covers.Patrols),
                botZones = DumpBotZones(),
                placeInfos = DumpPlaceInfos(),
                doorLinks = DumpDoorLinks(),
                doors = DumpDoors(),
            };

            // fully qualified — the game assemblies also define a Paths type that shadows BepInEx's
            string file = WriteJson(root, location);
            Plugin.Log.LogInfo($"dumped {points.Count} group points / {voxels?.Count ?? 0} voxels to {file}");
        }

        internal static string CurrentLocation()
        {
            string location = Singleton<GameWorld>.Instantiated
                ? Singleton<GameWorld>.Instance.LocationId
                : null;
            if (string.IsNullOrEmpty(location))
                location = "unknown";
            foreach (char c in Path.GetInvalidFileNameChars())
                location = location.Replace(c, '_');
            return location;
        }

        internal static string WriteJson(object root, string fileStem)
        {
            string dir = Path.Combine(BepInEx.Paths.PluginPath, "AIDataDumper", "dumps");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{fileStem}_{DateTime.Now:yyyyMMdd_HHmmss}.json");

            var serializer = new JsonSerializer
            {
                Formatting = Plugin.IndentJson.Value ? Formatting.Indented : Formatting.None,
                NullValueHandling = NullValueHandling.Include,
            };
            // stream straight to disk — big maps produce dumps way too large for one string
            using (var sw = new StreamWriter(file))
            using (var jw = new JsonTextWriter(sw))
                serializer.Serialize(jw, root);
            return file;
        }

        private static object V3(Vector3 v) => new { x = v.x, y = v.y, z = v.z };
        private static object V3N(Vector3? v) => v.HasValue ? V3(v.Value) : null;

        private static object DumpCorePoints(AICoversData covers)
        {
            var holder = covers.AICorePointsHolder;
            if (holder == null || holder.CorePoints == null)
                return new object[0];
            return holder.CorePoints.Where(cp => cp != null).Select(cp => new
            {
                id = cp.Id,
                connectionGroupId = cp.ConnectionGroupId,
                position = V3(cp.Position),
                connections = cp.ConnectionsAtNet != null
                    ? cp.ConnectionsAtNet.Where(c => c != null).Select(c => c.Id).ToList()
                    : new List<int>(),
            });
        }

        private static object DumpGroupPoint(GroupPoint p)
        {
            return new
            {
                id = p.Id,
                position = V3(p.Position),
                altPosition = V3N(p.AltPosition),
                firePosition = V3(p.FirePosition),
                wallDirection = V3(p.WallDirection),
                coverLevel = p.CoverLevel.ToString(),
                coverType = p.CoverType.ToString(),
                special = p.Special.ToString(),
                environmentType = p.EnvironmentType.ToString(),
                idEnvironment = p.IdEnvironment,
                neighborType = p.PointWithNeighborType.ToString(),
                connectionGroup = p.ConnectionGroup,
                corePointId = p.CorePointId,
                placeId = p.PlaceId,
                parentNavPoint = p.ParentNavPoint,
                alwaysGood = p.AlwaysGood,
                isGoodInsideBuilding = p.IsGoodInsideBuilding,
                defenceLevel = p.DefenceLevel,
                waysIds = p.NeighbourhoodsWaysIds ?? new List<int>(),
            };
        }

        private static object DumpVoxels(AIVoxelesData v)
        {
            if (v == null)
                return null;
            return new
            {
                min = V3(v.MinVoxelesValues),
                max = V3(v.MaxVoxelesValues),
                cells = (v.VoxelsList ?? new List<NavGraphVoxelSimple>()).Where(c => c != null).Select(c => new
                {
                    id = (int)c.Id,
                    ix = (int)c.IndexX,
                    iy = (int)c.IndexY,
                    iz = (int)c.IndexZ,
                    position = V3(c.Position),
                    haveNavMesh = c.HaveNavMesh,
                    closestPointId = c._closetsPointId,
                    pointsIds = c.PointsIds ?? new List<int>(),
                    entrancesIds = c.EntrancesIds ?? new List<int>(),
                    doorLinksIds = c.DoorLinksIds ?? new List<int>(),
                    lootPointsIds = c.LootPointsIds ?? new List<int>(),
                    exfiltrationPointsIds = c.ExfiltrationPointsIds ?? new List<int>(),
                }),
            };
        }

        private static object DumpPatrolsData(AIPatrolsData patrols)
        {
            if (patrols == null)
                return null;
            return new
            {
                containerLootPoints = DumpLootPoints(patrols.ContainerLootPoints),
                simpleLootPoints = DumpLootPoints(patrols.SimpleLootPoints),
                exfiltrationPoints = (patrols.ExfiltrationPoints ?? new List<AIExfiltrationPoint>())
                    .Where(e => e != null).Select(e => new
                    {
                        id = e.Id,
                        position = V3(e.Position),
                        name = e.Name,
                    }),
                lootClusterCount = patrols.LootPointClusters?.Count ?? 0,
            };
        }

        private static object DumpLootPoints(List<AILootPoint> list)
        {
            return (list ?? new List<AILootPoint>()).Where(l => l != null).Select(l => new
            {
                id = l.Id,
                position = V3(l._pos),
                lookPoint = V3(l.LookPoint),
                coreId = l._coreId,
                lootableContainerId = l._lootableContainerId,
                lootSpawnId = l._lootSpawnId,
            });
        }

        private static object DumpBotZones()
        {
            return UnityEngine.Object.FindObjectsOfType<BotZone>().Select(z => new
            {
                nameZone = z.NameZone,
                id = z.Id,
                canSpawnBoss = z.CanSpawnBoss,
                snipeZone = z.SnipeZone,
                distanceCoef = z.DistanceCoef,
                maxPersons = z.MaxPersons,
                spawnPoints = (z.SpawnPointMarkers ?? new List<EFT.Game.Spawning.SpawnPointMarker>())
                    .Where(m => m != null).Select(m => new
                    {
                        id = m.Id,
                        position = V3(m.Position),
                        sides = m.Sides.ToString(),
                        categories = m.SpawnPoint?.Categories.ToString(),
                        infiltration = m.SpawnPoint?.Infiltration,
                    }),
                patrolWays = (z.PatrolWays ?? new PatrolWay[0]).Where(w => w != null).Select(w => new
                {
                    name = w.name,
                    patrolType = w.PatrolType.ToString(),
                    maxPersons = w.MaxPersons,
                    coefSubPoints = w.CoefSubPoints,
                    points = (w.Points ?? new List<PatrolPoint>()).Where(pt => pt != null).Select(pt => new
                    {
                        id = pt.Id,
                        position = V3(pt.transform.position),
                        pointType = pt.PatrolPointType.ToString(),
                        shallSit = pt.ShallSit,
                        canUseByBoss = pt.CanUseByBoss,
                        corePointId = PatrolCoreIdField != null ? (int)PatrolCoreIdField.GetValue(pt) : -1,
                    }),
                }),
            });
        }

        // baked door-link geometry is the ground truth for the door-link generator:
        // five authored points per door, everything else derived by TryCreateCrave
        private static object DumpDoorLinks()
        {
            return UnityEngine.Object.FindObjectsOfType<NavMeshDoorLink>().Select(l => new
            {
                id = l.Id,
                doorId = l.DoorId,
                name = l.name,
                close1 = V3(l.Close1),
                close2Normal = V3(l.Close2_Normal),
                close2Breach = V3(l.Close2_Breach),
                open1 = V3(l.Open1),
                open2 = V3(l.Open2),
                farestClosePoint = V3(l.FarestClosePoint),
                bottomY = l.BottomY,
                shallTryInteract = l.ShallTryInteract,
            });
        }

        // doors dumped alongside links so the link geometry formula can be reversed
        // offline (join by DoorId): five authored points vs hinge transform + angles
        private static object DumpDoors()
        {
            return UnityEngine.Object.FindObjectsOfType<EFT.Interactive.Door>().Select(d => new
            {
                id = d.Id,
                name = d.name,
                position = V3(d.transform.position),
                rotationEuler = V3(d.transform.rotation.eulerAngles),
                forward = V3(d.transform.forward),
                right = V3(d.transform.right),
                state = d.DoorState.ToString(),
                openAngle = d.OpenAngle,
                closeAngle = d.CloseAngle,
                doorForward = d.DoorForward.ToString(),
                operatable = d.Operatable,
            });
        }

        private static object DumpPlaceInfos()
        {
            return UnityEngine.Object.FindObjectsOfType<AIPlaceInfo>().Select(p => new
            {
                name = p.name,
                id = p.Id,
                areaId = p.AreaId,
                position = V3(p.transform.position),
                isDark = p.IsDark,
                isMute = p.IsMute,
                isInside = p.IsInside,
                blockGrenade = p.BlockGrenade,
                atrZone = p.AtrZone,
            });
        }
    }
}
