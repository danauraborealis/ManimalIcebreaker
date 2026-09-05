using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Icebreaker
{
    // LOD CULL FLOOR (08-08, the vanishing-furniture report), now DISTANCE-TIERED
    // (user's design, same day). a LODGroup's LAST threshold is not a mesh swap â€”
    // below it unity stops rendering the object (crossfaded, hence the fade-in/out).
    // bsg authored those cull heights assuming lod bias >= 2 (their slider clamps
    // there, verified in EFT.Settings.Graphics.GraphicsSettingsGroup), so our sub-2 LodBiasClamp makes
    // aggressive cullers disappear in plain sight. the tier trick resolves the
    // fps-vs-pop tradeoff instead of splitting it: groups NEAR the camera get a
    // tiny protective floor (nothing ever dithers in your face), everything beyond
    // the radius gets the aggressive far floor (the proven fps lever). a rolling
    // budgeted sweep re-tiers groups as you move â€” no visibility bake, no native
    // stack, no 231MB file; pure distance math that ships for every player.
    internal static class IcebreakerLodCullFloor
    {
        // the map carved into 15m cells (same size as retail's visibility grid, built
        // by quantizing positions â€” no bake file needed). every group lives in exactly
        // one cell; tiering is per CELL, and work only happens when the camera crosses
        // a cell boundary or a knob changes â€” zero steady-state cost.
        private const float CellSize = 15f;

        private class Cell
        {
            public readonly List<int> Members = new List<int>();
            public float Applied = float.NaN; // tier floor currently applied to members
        }

        private static LODGroup[] _g;
        private static float[] _orig;
        private static Dictionary<Vector3Int, Cell> _cells;
        // flat snapshot of _cells for indexable, resumable iteration in Tick's re-tier pass
        // (a Dictionary enumerator works too, but an int index survives being paused and
        // resumed across frames without holding a struct enumerator alive between calls).
        private static Vector3Int[] _cellKeys;
        private static Cell[] _cellArray;
        private static readonly Queue<Cell> _dirty = new Queue<Cell>();
        private static readonly Queue<float> _dirtyWant = new Queue<float>();
        private static int _n;
        private static bool _built;
        private static Vector3Int _camCell = new Vector3Int(int.MinValue, 0, 0);
        private static float _lastNear = float.NaN, _lastFar = float.NaN;
        private static float _lastRadius = float.NaN;
        private static bool _wasIndoor;
        private static Cell _draining;
        private static float _drainWant;
        private static int _drainIdx;
        private static readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();

        // budgeted re-tier state (08-29 field report: "walking up onto the ship, frame
        // chugs"): the boundary-crossing recompute below used to walk every cell in the
        // map in one unbounded pass. Fine on a single sparse crossing; a real hitch on the
        // dense run of crossings climbing onto the ship, where several 15m boundaries pass
        // in a couple of steps. See the loop below for how this is spread across frames.
        private static bool _retiering;
        private static int _retierIdx;
        private static Vector3 _retierCam;
        private static float _retierR2, _retierNear, _retierFar;

        private static Vector3Int Key(Vector3 p) => new Vector3Int(
            Mathf.RoundToInt(p.x / CellSize), Mathf.RoundToInt(p.y / CellSize), Mathf.RoundToInt(p.z / CellSize));

        internal static IEnumerator Apply()
        {
            _built = false; _n = 0;
            _cells = new Dictionary<Vector3Int, Cell>(8192);
            _dirty.Clear(); _dirtyWant.Clear(); _draining = null;
            _camCell = new Vector3Int(int.MinValue, 0, 0);
            var groups = UnityEngine.Object.FindObjectsOfType<LODGroup>();
            _g = new LODGroup[groups.Length];
            _orig = new float[groups.Length];
            // lod-count histogram answers "do they even HAVE lower meshes": a 1-LOD
            // group's only perf feature is the cull-out â€” if those dominate, lod bias
            // is mostly moving disappear distances, not mesh swaps (08-08 verdict:
            // 79.8k of 81.8k are 1-lod â€” this map's LODGroups ARE the culling system)
            var histo = new int[5];
            int skippedLoot = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var g in groups)
            {
                if (g == null) continue;
                // map geometry only â€” bot/weapon rigs manage their own lods
                var sc = g.gameObject.scene.name;
                if (sc == null || !sc.StartsWith("Icebreaker")) continue;
                var lods = g.GetLODs();
                if (lods == null || lods.Length == 0) continue;
                // LOOT IS GAMEPLAY, NOT DECORATION (08-12: "loose loot is invisible until
                // I get close"). our floors are tuned for scenery — at the shipped
                // values a ~10cm item drops under the near floor (0.006) around 14m and
                // under the far floor (0.1) inside a metre, so any loot item captured
                // here effectively stops rendering. same principle as the distance
                // culler's container-prop exclusion: never cull what the player loots.
                if (g.GetComponentInParent<EFT.Interactive.LootItem>() != null
                    || g.GetComponentInParent<EFT.Interactive.LootableContainer>() != null)
                { skippedLoot++; continue; }

                histo[Math.Min(lods.Length, histo.Length - 1)]++;
                _g[_n] = g;
                _orig[_n] = lods[lods.Length - 1].screenRelativeTransitionHeight;
                // bucket by RENDERER bounds, not the group's pivot â€” ripped prefab
                // roots share a handful of parent origins, and pivot-bucketing
                // collapsed 81k groups into 79 cells (first live run)
                Vector3 where = g.transform.position;
                var rs = lods[0].renderers;
                if (rs != null)
                    for (int ri = 0; ri < rs.Length; ri++)
                        if (rs[ri] != null) { where = rs[ri].bounds.center; break; }
                var key = Key(where);
                Cell cell;
                if (!_cells.TryGetValue(key, out cell)) _cells[key] = cell = new Cell();
                cell.Members.Add(_n);
                _n++;
                if (sw.ElapsedMilliseconds > 4) { yield return null; sw.Restart(); }
            }
            _cellKeys = new Vector3Int[_cells.Count];
            _cellArray = new Cell[_cells.Count];
            int ci = 0;
            foreach (var kv in _cells) { _cellKeys[ci] = kv.Key; _cellArray[ci] = kv.Value; ci++; }
            _retiering = false; _retierIdx = 0;

            _built = true;
            Plugin.Log.LogInfo($"[LodCullFloor] {_n} map LODGroups in {_cells.Count} cells of {CellSize:0}m (cell-tiered culling live) | "
                + $"lod counts: 1-lod={histo[1]} 2-lod={histo[2]} 3-lod={histo[3]} 4+lod={histo[4]}"
                + (skippedLoot > 0 ? $" | {skippedLoot} loot group(s) left alone" : ""));
        }

        internal static void Tick(Vector3 cam)
        {
            if (!_built) return;
            float near = Plugin.LodCullNearFloor.Value;
            float far = Plugin.LodCullFloor.Value;
            // indoors the sightlines are corridor-length â€” a tighter near bubble lets
            // the far tier eat the rest of the deck (user request 08-08). the interior
            // volumes the fog system uses answer "is the camera inside"
            // retail's own 51-trigger indoor/outdoor state (the acoustics rebuild) â€”
            // it covers the ENGINE ROOM and every other authored interior; the
            // Indoor_*-scene volume fallback misses spaces whose geometry lives in
            // the Design scenes (08-08: engine room read as outdoor, indoor slider
            // was dead exactly where the user was tuning it)
            bool indoor = false;
            try
            {
                var em = EFT.EnvironmentEffect.EnvironmentManager.Instance;
                if (em != null) indoor = em.Environment == global::EnvironmentType.Indoor;
                else indoor = RenderEnvProbe.CameraInsideInterior(cam);
            }
            catch { }
            float radiusM = indoor ? Plugin.LodCullNearRadiusIndoor.Value : Plugin.LodCullNearRadius.Value;
            // which radius is live is invisible without this â€” the user tuned the
            // OUTDOOR slider while standing in the engine room and concluded the
            // system was broken (08-08)
            if (indoor != _wasIndoor)
            {
                _wasIndoor = indoor;
                Plugin.Log.LogInfo($"[LodCullFloor] {(indoor ? "INDOOR" : "OUTDOOR")} â€” active near radius {radiusM:0}m "
                    + $"({(indoor ? "LodCullNearRadiusIndoor" : "LodCullNearRadius")})");
            }
            var camCell = Key(cam);

            if (camCell != _camCell || near != _lastNear || far != _lastFar || radiusM != _lastRadius)
            {
                _camCell = camCell; _lastNear = near; _lastFar = far; _lastRadius = radiusM;
                // true-meters tiering: distance from the camera to each cell's BOUNDS
                // (closest point, per axis). the first cut used whole-cell chebyshev
                // steps with a 1-cell minimum â€” the radius slider only did anything
                // every 15m and could never shrink the bubble below ~22m, which read
                // as "the slider is broken" (it was)
                //
                // Restart the sweep from cell 0 rather than finish the old one: a recompute
                // using the previous camera position is stale the instant a newer crossing
                // supersedes it - same "eventual consistency, no hitch" contract the
                // member-drain below already uses.
                _retierCam = cam;
                _retierR2 = radiusM * radiusM;
                _retierNear = near;
                _retierFar = far;
                _retierIdx = 0;
                _retiering = _cellArray != null && _cellArray.Length > 0;
            }

            if (_retiering)
            {
                const float half = CellSize * 0.5f;
                _sw.Restart();
                while (_sw.Elapsed.TotalMilliseconds < 1.0)
                {
                    var key = _cellKeys[_retierIdx];
                    var cell = _cellArray[_retierIdx];
                    float dx = Mathf.Max(0f, Mathf.Abs(key.x * CellSize - _retierCam.x) - half);
                    float dy = Mathf.Max(0f, Mathf.Abs(key.y * CellSize - _retierCam.y) - half);
                    float dz = Mathf.Max(0f, Mathf.Abs(key.z * CellSize - _retierCam.z) - half);
                    float want = (dx * dx + dy * dy + dz * dz) <= _retierR2 ? _retierNear : _retierFar;
                    if (!Mathf.Approximately(want, cell.Applied))
                    {
                        cell.Applied = want;
                        _dirty.Enqueue(cell);
                        _dirtyWant.Enqueue(want);
                    }
                    _retierIdx++;
                    if (_retierIdx >= _cellArray.Length) { _retiering = false; break; }
                }
            }

            // budgeted drain, MEMBER-granular (08-08 fix): the first cut budgeted
            // between cells but applied each cell's members in one gulp â€” a dense
            // midship cell holds thousands of groups and a boundary crossing fired
            // them all in one frame (a triple-digit-ms hitch mid-movement). the
            // cursor lets a fat cell span as many frames as it needs. a cell
            // re-tiered mid-drain gets stale writes for its tail, but its fresh
            // queue entry re-applies everything â€” eventual consistency, no hitch.
            _sw.Restart();
            while (_sw.Elapsed.TotalMilliseconds < 1.0)
            {
                if (_draining == null)
                {
                    if (_dirty.Count == 0) break;
                    _draining = _dirty.Dequeue();
                    _drainWant = _dirtyWant.Dequeue();
                    _drainIdx = 0;
                    if (!Mathf.Approximately(_drainWant, _draining.Applied)) { _draining = null; continue; } // superseded
                }
                var members = _draining.Members;
                while (_drainIdx < members.Count && _sw.Elapsed.TotalMilliseconds < 1.0)
                {
                    int i = members[_drainIdx++];
                    var g = _g[i];
                    if (g == null) continue;
                    float want = _drainWant < 0f ? _orig[i] : Mathf.Min(_orig[i], _drainWant);
                    var lods = g.GetLODs();
                    if (lods == null || lods.Length == 0) continue;
                    int last = lods.Length - 1;
                    // clamp under the PREVIOUS LOD level's own threshold — SetLODs logs a
                    // warning (and, per field verification, silently no-ops the whole call)
                    // if a level's transition height is allowed to reach or pass the level
                    // above it, which `want` can do unclamped when the camera is close.
                    if (last > 0)
                    {
                        float ceiling = lods[last - 1].screenRelativeTransitionHeight - 0.001f;
                        if (want >= ceiling) want = Mathf.Max(0f, ceiling);
                    }
                    if (Mathf.Abs(lods[last].screenRelativeTransitionHeight - want) > 0.0005f)
                    {
                        lods[last].screenRelativeTransitionHeight = want;
                        g.SetLODs(lods);
                    }
                }
                if (_drainIdx >= members.Count) _draining = null;
            }
        }
    }

}
