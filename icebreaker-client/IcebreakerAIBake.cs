using System;
using System.Collections.Generic;
using System.Linq;
using SysIoPath = System.IO.Path;
using System.Runtime.Serialization;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Icebreaker
{
    // resurrection #5: BSG's baked AI data. the synthesized graph (CoverScanner +
    // BuildVoxelGrid) proved the runtime-baker thesis; this replaces it ON THIS MAP with
    // the retail bake recovered from level706 (analysis/extract_aibake.py — every
    // component byte-exact): 1885 GroupPoints across the 20 authored core points, 4502
    // ways, 476 navmesh voxel cells, 477 loot points, 57 mine points, 54 door links.
    //
    // integration: a PREFIX on AICoversData.RestoreData fills the healed scene holders
    // with the real data — RestoreData then runs CLEAN like on a retail map (builds the
    // cache itself, resolves ids), and the exception-finalizer generation fallback never
    // fires. if filling fails, the prefix backs off and the synth path takes over.
    internal static class IcebreakerAIBake
    {
        private static JObject _sidecar;
        private static bool _sidecarTried;
        private static GameObject _marker;
        internal static bool Loaded; // true once the bake landed this raid

        private static readonly Dictionary<long, Component> _comps = new Dictionary<long, Component>();

        private static string DataDir =>
            SysIoPath.Combine(SysIoPath.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "aibake");

        private static JObject Sidecar()
        {
            if (_sidecarTried) return _sidecar;
            _sidecarTried = true;
            try
            {
                var path = SysIoPath.Combine(DataDir, "icebreaker_aibake.json");
                if (System.IO.File.Exists(path))
                    _sidecar = JObject.Parse(System.IO.File.ReadAllText(path));
                else
                    Plugin.Log.LogDebug($"[AIBake] no sidecar at {path} — synthesized AI graph stays");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[AIBake] sidecar parse failed: {e.Message}"); }
            return _sidecar;
        }

        // called from the AICoversData.RestoreData PREFIX
        public static void TryFill(AICoversData covers)
        {
            Loaded = false;
            if (_marker != null) { Loaded = true; return; }
            var sc = Sidecar();
            if (sc == null || covers == null || !IcebreakerAcoustics.IcebreakerLoaded()) return;

            try
            {
                var comps = sc["components"] as JObject;
                _comps.Clear();

                // path index over the AI scene (core points, mines, door links live there)
                var index = new Dictionary<string, Transform>();
                var scn = SceneManager.GetSceneByName("Icebreaker_AI");
                if (!scn.IsValid() || !scn.isLoaded) { Plugin.Log.LogDebug("[AIBake] AI scene not loaded"); return; }
                foreach (var root in scn.GetRootGameObjects())
                    Walk(root.transform, root.name, index);

                // --- core points (components on surviving GOs) ---
                var coreHolder = UnityEngine.Object.FindObjectOfType<AICorePointHolder>();
                var coreList = new List<AICorePoint>();
                foreach (var row in Rows(comps, "AICorePoint"))
                {
                    if (!index.TryGetValue(row.Value<string>("go") ?? "", out var t)) continue;
                    var c = t.gameObject.GetComponent<AICorePoint>() ?? t.gameObject.AddComponent<AICorePoint>();
                    FillFields(c, row["fields"] as JObject, name => name != "ConnectionsAtNet");
                    _comps[row.Value<long>("path_id")] = c;
                    coreList.Add(c);
                }
                foreach (var row in Rows(comps, "AICorePoint"))
                    if (Comp<AICorePoint>(row) is AICorePoint c && row["fields"]?["ConnectionsAtNet"] is JArray conns)
                    {
                        var list = new List<AICorePoint>();
                        foreach (var r in conns)
                            if (Ref(r) is AICorePoint other) list.Add(other);
                        AccessTools.Field(typeof(AICorePoint), "ConnectionsAtNet")?.SetValue(c, list);
                    }
                if (coreHolder != null) coreHolder.CorePoints = coreList;

                // --- mine points + door links (components on surviving GOs) ---
                RebuildOnGos<AIMinePoint>(comps, "AIMinePoint", index);

                // retail links are the ONLY door-link source on this map (Waypoints' per-Door
                // generator NREs here — full-stack confirmed — and one raid with links off
                // proved zero-links = bots ghost everything). unconditional; the ghosting
                // hunt continues on the CONSUMER side via [DoorProbe].
                RebuildOnGos<NavMeshDoorLink>(comps, "NavMeshDoorLink", index);
                {
                    int carved = 0, carveFail = 0;
                    foreach (var link in UnityEngine.Object.FindObjectsOfType<NavMeshDoorLink>())
                    {
                        link.ShallTryInteract = true;
                        // MidOpen/MidClose are Awake-computed ((Open1+Open2)/2 etc) and retail
                        // serializes them ZEROED — our fill faithfully wrote zeros back over
                        // Awake's too-early result. BotDoorOpener.method_4 measures distance
                        // TO MidOpen with a 2m gate, so every link sat "100m away" at world
                        // origin and no bot ever selected a door: THE ghosting root cause.
                        link.MidOpen = (link.Open1 + link.Open2) / 2f;
                        link.MidClose = (link.Close1 + link.Close2_Normal) / 2f;
                        if (link.Carver_Opened != null && link.Carver_Breached != null && link.Carver_Closed != null) continue;
                        try { link.TryCreateCrave(); carved++; }
                        catch { carveFail++; UnityEngine.Object.Destroy(link); } // a carver-less link is a live grenade — drop it
                    }
                    if (carved + carveFail > 0)
                        Plugin.Log.LogInfo($"[AIBake] door link carvers rebuilt: {carved} ok, {carveFail} dropped");
                }

                // --- patrol routes (the reason bots stood still facing bulkheads) ---
                // AssetRipper stripped every PatrolPoint/PatrolWay in the AI scene (the
                // 668 patrol GOs survive as bare transforms) and BotZone's serialized
                // PatrolWays refs died with them — zones had zero ways, so a spawned bot
                // had nowhere to walk, nothing to sweep, and held its spawn heading
                // forever ("staring at a wall until touched", 07-30; the vision chain was
                // healthy the whole time). rebuild from the retail data: 647 points (580
                // of them authored sub-points), 20 ways, parse-validated byte-clean
                // against the 4.0 layouts. BotZone itself DRIFTED between versions, so
                // each zone's way membership was recovered by raw ref-scan instead.
                //
                // components first, fields second — subPoints/Points/PatrolWay refs need
                // every component to exist before any fill (same rule as the core-point
                // net above). BotZone.Init then runs BSG's own chain at bots-controller
                // start: Restore() binds core points by id, InitPoints() sets back-refs.
                {
                    int pts = 0, ways = 0, zonesWired = 0, waysWired = 0;
                    var allPoints = new List<PatrolPoint>();
                    var allWays = new List<PatrolWay>();
                    foreach (var row in Rows(comps, "PatrolPoint"))
                    {
                        if (!index.TryGetValue(row.Value<string>("go") ?? "", out var t)) continue;
                        var c = t.gameObject.GetComponent<PatrolPoint>() ?? t.gameObject.AddComponent<PatrolPoint>();
                        _comps[row.Value<long>("path_id")] = c; pts++;
                        allPoints.Add(c);
                    }
                    foreach (var row in Rows(comps, "PatrolWay"))
                    {
                        if (!index.TryGetValue(row.Value<string>("go") ?? "", out var t)) continue;
                        var c = t.gameObject.GetComponent<PatrolWay>() ?? t.gameObject.AddComponent<PatrolWay>();
                        _comps[row.Value<long>("path_id")] = c; ways++;
                        allWays.Add(c);
                    }
                    foreach (var row in Rows(comps, "PatrolPoint"))
                        if (Comp<PatrolPoint>(row) is PatrolPoint p) FillFields(p, row["fields"] as JObject, null);
                    foreach (var row in Rows(comps, "PatrolWay"))
                        if (Comp<PatrolWay>(row) is PatrolWay w) FillFields(w, row["fields"] as JObject, null);

                    // --- reconcile the restored points against OUR navmesh ---
                    // the point positions are authored against RETAIL's navmesh; ours is a
                    // separate bake, so a point can sit just off the mesh. that fails silently
                    // AND permanently: PatrollingData discards the NavMeshPathStatus that
                    // GoToPoint returns and pins Status = go regardless (the else-branch at
                    // PatrollingData.cs ~476), so a bot sent to an unreachable point reports
                    // "go" forever while standing still. 07-31 headless log: patrol=go with
                    // moving=False in 96% of samples, every bot, both spawn sources.
                    // PatrolPoint.Position IS transform.position, so snapping the transform
                    // is the whole repair for a point that merely drifted.
                    SnapPatrolPointsToNavMesh(allPoints, allWays);

                    foreach (var row in Rows(comps, "BotZone"))
                    {
                        if (!index.TryGetValue(row.Value<string>("go") ?? "", out var t)) continue;
                        var zone = t.GetComponent<BotZone>();
                        if (zone == null) continue;
                        var list = new List<PatrolWay>();
                        foreach (var r in (row["fields"]?["PatrolWays"] as JArray) ?? new JArray())
                            if (Ref(r) is PatrolWay w) list.Add(w);
                        zone.PatrolWays = list.ToArray();
                        zonesWired++; waysWired += list.Count;
                    }
                    Plugin.Log.LogDebug($"[AIBake] patrol routes rebuilt: {pts} points, {ways} ways, " +
                                          $"{zonesWired} zones wired ({waysWired} memberships)");
                }

                // --- voxels ---
                var voxData = UnityEngine.Object.FindObjectOfType<AIVoxelesData>();
                var vrow = Rows(comps, "AIVoxelesData").FirstOrDefault();
                if (voxData != null && vrow?["fields"] is JObject vf)
                {
                    var cells = new List<NavGraphVoxelSimple>();
                    foreach (var cj in (vf["VoxelsList"] as JArray) ?? new JArray())
                    {
                        var pos = V3(cj["Position"]);
                        var cell = new NavGraphVoxelSimple(pos,
                            cj.Value<int?>("IndexX") ?? 0, cj.Value<int?>("IndexY") ?? 0,
                            cj.Value<int?>("IndexZ") ?? 0, cj.Value<int?>("Id") ?? 0);
                        FillFields(cell, cj as JObject, name =>
                            name != "Position" && name != "IndexX" && name != "IndexY" && name != "IndexZ" && name != "Id");
                        if (cell.DoorLinks == null) cell.DoorLinks = new List<NavMeshDoorLink>();
                        cells.Add(cell);
                    }
                    voxData.VoxelsList = cells;
                    voxData.MaxX = vf.Value<int?>("MaxX") ?? 0;
                    voxData.MaxY = vf.Value<int?>("MaxY") ?? 0;
                    voxData.MaxZ = vf.Value<int?>("MaxZ") ?? 0;
                    voxData.MinVoxelesValues = V3(vf["MinVoxelesValues"]);
                    // KEEP MaxVoxelesValues verbatim: it's not a world corner — the
                    // restore path allocates `new NavGraphVoxelSimple[(int)Max.x, .y, .z]`
                    // so these ARE the array dimensions (4,7,17; max cell index 3,6,16 ✓).
                    // "fixing" it to a world corner mis-shaped the grid on the first run.
                    voxData.MaxVoxelesValues = V3(vf["MaxVoxelesValues"]);
                }

                // --- patrols (loot/exfil points; container refs unresolvable -> null) ---
                var patData = UnityEngine.Object.FindObjectOfType<AIPatrolsData>();
                var prow = Rows(comps, "AIPatrolsData").FirstOrDefault();
                if (patData != null && prow?["fields"] is JObject pf)
                    FillFields(patData, pf, null);

                // --- small holders ---
                FillSingle<AIMinesPositionsHolder>(comps, "AIMinesPositionsHolder");
                FillSingle<AIManualPointsHolder>(comps, "AIManualPointsHolder");
                FillSingle<AIDangerPlacesHolder>(comps, "AIDangerPlacesHolder");
                FillSingle<BotZoneEntranceInfo>(comps, "BotZoneEntranceInfo");

                // --- the cover bake itself ---
                var crow = Rows(comps, "AICoversData").FirstOrDefault();
                if (crow?["fields"] is JObject cf)
                {
                    var points = new List<GroupPoint>();
                    int maxId = 0;
                    foreach (var pj in (cf["Points"] as JArray) ?? new JArray())
                    {
                        var gp = (GroupPoint)FormatterServices.GetUninitializedObject(typeof(GroupPoint));
                        FillFields(gp, pj as JObject, null);
                        points.Add(gp);
                        int id = pj.Value<int?>("Id") ?? 0;
                        if (id > maxId) maxId = id;
                    }
                    var ways = new List<GroupPointWay>();
                    foreach (var wj in (cf["Ways"] as JArray) ?? new JArray())
                    {
                        var w = (GroupPointWay)FormatterServices.GetUninitializedObject(typeof(GroupPointWay));
                        FillFields(w, wj as JObject, null);
                        ways.Add(w);
                    }
                    covers.Points = points;
                    covers.Ways = ways;
                    covers.Pathes = new List<GroupPointPath>();
                    AccessTools.Field(typeof(AICoversData), "_lastId")?.SetValue(covers, maxId + 1);
                }

                // wire the holder refs on the covers component
                covers.Voxels = voxData;
                covers.Patrols = patData;
                covers.AICorePointsHolder = coreHolder;
                covers.AIManualPointsHolder = UnityEngine.Object.FindObjectOfType<AIManualPointsHolder>();
                covers.AIMinesPositions = UnityEngine.Object.FindObjectOfType<AIMinesPositionsHolder>();
                covers.AIDangerPlacesHolder = UnityEngine.Object.FindObjectOfType<AIDangerPlacesHolder>();
                covers.AIPlaceInfoHolder = UnityEngine.Object.FindObjectOfType<AIPlaceInfoHolder>();
                // Places is a bare public field the engine never null-guards — ExUsecBrainClass
                // foreaches it in its decision layer, so null = every rogue brain NREs silently
                // and the whole map stands still. the old exception-finalizer used to heal this;
                // now that RestoreData succeeds that path never runs, so heal it here.
                if (covers.AIPlaceInfoHolder != null && covers.AIPlaceInfoHolder.Places == null)
                    covers.AIPlaceInfoHolder.Places = new List<AIPlaceInfo>();
                covers.EntranceInfo = UnityEngine.Object.FindObjectOfType<BotZoneEntranceInfo>();

                _marker = new GameObject("Icebreaker_AIBakeStaged");
                SceneManager.MoveGameObjectToScene(_marker, scn);
                Loaded = true;
                Plugin.Log.LogDebug($"[AIBake] RETAIL AI BAKE LOADED: {covers.Points.Count} covers, {covers.Ways.Count} ways, "
                    + $"{(voxData != null ? voxData.VoxelsList.Count : 0)} voxels, {coreList.Count} cores — RestoreData gets the real thing");
            }
            catch (Exception e)
            {
                Loaded = false;
                Plugin.Log.LogWarning($"[AIBake] fill failed — synthesized graph takes over: {e}");
            }
        }

        // ------------------------------------------------------------------ helpers
        // reconcile restored patrol points with OUR navmesh bake (see the call site).
        // NOTHING is dropped unless the navmesh is demonstrably present: sampling against an
        // unbuilt navmesh returns false for EVERY point, and acting on that would delete all
        // 647 routes and leave the map far worse than the bug being fixed. two independent
        // gates guard it — an explicit triangulation check, and a floor on the hit rate.
        // either one failing makes this pass read-only.
        private const float SnapRadius = 1.5f;    // ordinary bake drift
        private const float RescueRadius = 6f;    // clearly displaced, still worth saving

        private static void SnapPatrolPointsToNavMesh(List<PatrolPoint> points, List<PatrolWay> ways)
        {
            if (points == null || points.Count == 0) return;
            try
            {
                bool meshReady = false;
                try { meshReady = UnityEngine.AI.NavMesh.CalculateTriangulation().indices.Length > 0; }
                catch { }
                if (!meshReady)
                {
                    Plugin.Log.LogError("[AIBake] navmesh has no triangulation at patrol rebuild — patrol snap SKIPPED "
                                        + "(sampling an unbuilt navmesh would drop every route)");
                    return;
                }

                var offMesh = new List<PatrolPoint>();
                int onMesh = 0, snapped = 0, rescued = 0;
                float worst = 0f;
                foreach (var p in points)
                {
                    if (p == null) continue;
                    var pos = p.transform.position;
                    UnityEngine.AI.NavMeshHit hit;
                    float radius = SnapRadius;
                    bool found = UnityEngine.AI.NavMesh.SamplePosition(pos, out hit, radius, UnityEngine.AI.NavMesh.AllAreas);
                    if (!found)
                    {
                        radius = RescueRadius;
                        found = UnityEngine.AI.NavMesh.SamplePosition(pos, out hit, radius, UnityEngine.AI.NavMesh.AllAreas);
                        if (found) rescued++;
                    }
                    if (!found) { offMesh.Add(p); continue; }

                    float d = Vector3.Distance(pos, hit.position);
                    if (d > worst) worst = d;
                    if (d > 0.05f) { p.transform.position = hit.position; snapped++; }
                    else onMesh++;
                }

                // sanity floor: if almost nothing sampled, believe the navmesh, not the points
                float hitRate = 1f - (float)offMesh.Count / points.Count;
                if (hitRate < 0.25f)
                {
                    Plugin.Log.LogError($"[AIBake] patrol snap ABORTED: only {hitRate:P0} of {points.Count} points found navmesh "
                                        + "— that reads as a navmesh problem, not a patrol-data problem. nothing dropped");
                    return;
                }

                int pruned = 0;
                if (offMesh.Count > 0 && ways != null)
                {
                    var dead = new HashSet<PatrolPoint>(offMesh);
                    foreach (var w in ways)
                    {
                        if (w == null || w.Points == null) continue;
                        pruned += w.Points.RemoveAll(p => p == null || dead.Contains(p));
                    }
                }

                Plugin.Log.LogDebug($"[AIBake] patrol navmesh reconcile: {onMesh} already on-mesh, {snapped} snapped "
                                      + $"({rescued} needed the {RescueRadius}m rescue, worst {worst:F2}m), "
                                      + $"{offMesh.Count} off-mesh -> {pruned} way membership(s) pruned");
                if (snapped == 0 && offMesh.Count == 0)
                    Plugin.Log.LogDebug("[AIBake] every patrol point was already on the navmesh — the standing-still stall is NOT patrol placement");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[AIBake] patrol snap failed: {e.Message} — points left as authored"); }
        }

        private static void Walk(Transform t, string path, Dictionary<string, Transform> index)
        {
            index[path] = t;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                Walk(c, path + "/" + c.name, index);
            }
        }

        private static IEnumerable<JToken> Rows(JObject comps, string cls)
            => (comps?[cls] as JArray) ?? Enumerable.Empty<JToken>();

        private static T Comp<T>(JToken row) where T : Component
            => _comps.TryGetValue(row.Value<long>("path_id"), out var c) ? c as T : null;

        private static Component Ref(JToken tok)
        {
            var id = (tok as JObject)?.Value<long?>("ref");
            return id.HasValue && _comps.TryGetValue(id.Value, out var c) ? c : null;
        }

        private static void RebuildOnGos<T>(JObject comps, string cls, Dictionary<string, Transform> index) where T : Component
        {
            int n = 0;
            foreach (var row in Rows(comps, cls))
            {
                if (!index.TryGetValue(row.Value<string>("go") ?? "", out var t)) continue;
                var c = t.gameObject.GetComponent<T>() ?? t.gameObject.AddComponent<T>();
                FillFields(c, row["fields"] as JObject, null);
                _comps[row.Value<long>("path_id")] = c;
                n++;
            }
            if (n > 0) Plugin.Log.LogInfo($"[AIBake] {cls}: {n} rebuilt");
        }

        private static void FillSingle<T>(JObject comps, string cls) where T : Component
        {
            var row = Rows(comps, cls).FirstOrDefault();
            var target = UnityEngine.Object.FindObjectOfType<T>();
            if (row?["fields"] is JObject f && target != null)
            {
                FillFields(target, f, null);
                _comps[row.Value<long>("path_id")] = target;
            }
        }

        // ------------------------------------------------- generic reflection filler
        private static void FillFields(object target, JObject fields, Func<string, bool> filter)
        {
            if (target == null || fields == null) return;
            var type = target.GetType();
            foreach (var prop in fields.Properties())
            {
                if (filter != null && !filter(prop.Name)) continue;
                var fi = AccessTools.Field(type, prop.Name);
                if (fi == null) continue;
                try
                {
                    var v = ConvertToken(prop.Value, fi.FieldType, fi.GetValue(target));
                    if (v != null || !fi.FieldType.IsValueType) fi.SetValue(target, v);
                }
                catch { /* drifted/unresolvable — keep default */ }
            }
        }

        private static object ConvertToken(JToken tok, Type type, object existing)
        {
            if (tok == null || tok.Type == JTokenType.Null) return null;
            if (type == typeof(string)) return tok.Value<string>();
            if (type == typeof(bool)) return tok.Type == JTokenType.Boolean ? tok.Value<bool>() : tok.Value<int>() != 0;
            if (type == typeof(short)) return (short)tok.Value<int>();
            if (type.IsEnum) return Enum.ToObject(type, tok.Value<long>());
            if (type.IsPrimitive) return Convert.ChangeType(((JValue)tok).Value, type,
                System.Globalization.CultureInfo.InvariantCulture);

            if (tok is JObject o)
            {
                if (o["ref"] != null && o.Count <= 2)
                {
                    var c = Ref(o);
                    return c != null && type.IsInstanceOfType(c) ? c : null;
                }
                if (type == typeof(Vector2)) return new Vector2(o.Value<float>("x"), o.Value<float>("y"));
                if (type == typeof(Vector3)) return V3(o);
                if (type == typeof(Quaternion)) return new Quaternion(o.Value<float>("x"), o.Value<float>("y"), o.Value<float>("z"), o.Value<float>("w"));
                if (type == typeof(Color)) return new Color(o.Value<float>("r"), o.Value<float>("g"), o.Value<float>("b"), o.Value<float>("a"));
                if (type == typeof(Bounds)) return new Bounds(V3(o["m_Center"]), V3(o["m_Extent"]) * 2f);
                if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return null;
                object inst = existing;
                if (inst == null)
                {
                    try { inst = Activator.CreateInstance(type, true); }
                    catch { inst = FormatterServices.GetUninitializedObject(type); }
                }
                FillFields(inst, o, null);
                return inst;
            }

            if (tok is JArray arr)
            {
                Type elem = type.IsArray ? type.GetElementType()
                    : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0]
                    : null;
                if (elem == null) return null;
                var items = new List<object>(arr.Count);
                foreach (var e in arr)
                {
                    object v;
                    if (e is JObject eo && eo["ref"] == null && !elem.IsPrimitive && !elem.IsEnum
                        && elem != typeof(string) && !typeof(UnityEngine.Object).IsAssignableFrom(elem)
                        && elem.IsClass)
                    {
                        // plain serializable element — uninitialized + fill (retail path:
                        // unity's deserializer also skips ctors)
                        object inst;
                        try { inst = Activator.CreateInstance(elem, true); }
                        catch { inst = FormatterServices.GetUninitializedObject(elem); }
                        FillFields(inst, eo, null);
                        v = inst;
                    }
                    else v = ConvertToken(e, elem, null);
                    items.Add(v);
                }
                if (type.IsArray)
                {
                    var a = Array.CreateInstance(elem, items.Count);
                    for (int i = 0; i < items.Count; i++) a.SetValue(items[i], i);
                    return a;
                }
                var list = (System.Collections.IList)Activator.CreateInstance(type);
                foreach (var it in items) list.Add(it);
                return list;
            }
            return null;
        }

        private static Vector3 V3(JToken t)
        {
            if (t is JArray a) return new Vector3(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>());
            return new Vector3(t.Value<float>("x"), t.Value<float>("y"), t.Value<float>("z"));
        }
    }
}
