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
    // there, verified in GraphicsSettingsClass), so our sub-2 LodBiasClamp makes
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
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var g in groups)
            {
                if (g == null) continue;
                // map geometry only â€” bot/weapon rigs manage their own lods
                var sc = g.gameObject.scene.name;
                if (sc == null || !sc.StartsWith("Icebreaker")) continue;
                var lods = g.GetLODs();
                if (lods == null || lods.Length == 0) continue;
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
            _built = true;
            Plugin.Log.LogInfo($"[LodCullFloor] {_n} map LODGroups in {_cells.Count} cells of {CellSize:0}m (cell-tiered culling live) | "
                + $"lod counts: 1-lod={histo[1]} 2-lod={histo[2]} 3-lod={histo[3]} 4+lod={histo[4]}");
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
                float r2 = radiusM * radiusM;
                const float half = CellSize * 0.5f;
                foreach (var kv in _cells)
                {
                    float dx = Mathf.Max(0f, Mathf.Abs(kv.Key.x * CellSize - cam.x) - half);
                    float dy = Mathf.Max(0f, Mathf.Abs(kv.Key.y * CellSize - cam.y) - half);
                    float dz = Mathf.Max(0f, Mathf.Abs(kv.Key.z * CellSize - cam.z) - half);
                    float want = (dx * dx + dy * dy + dz * dz) <= r2 ? near : far;
                    if (!Mathf.Approximately(want, kv.Value.Applied))
                    {
                        kv.Value.Applied = want;
                        _dirty.Enqueue(kv.Value);
                        _dirtyWant.Enqueue(want);
                    }
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
