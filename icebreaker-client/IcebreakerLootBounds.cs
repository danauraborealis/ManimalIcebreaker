using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // LOOT VISIBILITY (08-12): loose loot was invisible until the camera got close AND
    // pointed a certain way — tilt so the item sits high on screen and it pops in, centre
    // it and it vanishes. UnityExplorer settled what it is NOT: the GameObject stays
    // active and Renderer.enabled stays true, so none of our cullers (distance, occlusion
    // driver, cross-cull — all of which work by toggling Renderer.enabled) are involved.
    //
    // enabled-but-not-drawn, with visibility that depends on where you LOOK rather than
    // where you STAND, is the signature of the engine frustum-culling a renderer against
    // bounds that don't sit on the visible mesh. Unity tests the AABB, not the triangles,
    // so a stale/mislocated AABB gets rejected while the mesh is plainly in view.
    //
    // two real causes, both handled here:
    //   - MeshRenderer: the shared mesh's local bounds are wrong -> RecalculateBounds().
    //     done once per unique mesh (dedup by instance id), so it costs nothing to repeat.
    //   - SkinnedMeshRenderer: bounds are only refreshed while on screen, which is exactly
    //     the trap -> updateWhenOffscreen = true, the standard Unity fix.
    //
    // sweeps GameWorld.LootItems (an indexed registry, no FindObjectsOfType scan) every
    // few seconds because loot keeps appearing: corpses drop it, players drop it, and
    // containers spill it long after raid start. each item is processed once.
    internal class IcebreakerLootBounds : MonoBehaviour
    {
        private const float SweepEvery = 4f;
        private const float SuspectDistance = 1.5f; // bounds centre this far off the item = wrong
        // small enough that a 0.7 lod bias still keeps loot alive to well past any
        // distance the player could care about
        private const float LootCullHeight = 0.0004f;

        private readonly HashSet<int> _done = new HashSet<int>();
        private readonly HashSet<int> _meshesFixed = new HashSet<int>();
        private float _next;
        private int _fixedRenderers, _suspects, _lodFixed;
        private bool _summarised;

        // RADIUS CULLING (user call 08-13, replacing the keys-only carve-out). one entry
        // per loot item, holding its renderers so the per-frame pass never calls
        // GetComponentsInChildren. positions are read LIVE from the transform every
        // check, never cached — the distance culler in RaidFixPatches caches positions
        // and is why loot is excluded there ("it culled the player's melee out of his
        // hands"). loot moves: it gets dropped, kicked, and spilled out of containers.
        private sealed class Tracked
        {
            public LootItem Item;
            public Renderer[] Rends;
            public bool Hidden;
        }
        private readonly List<Tracked> _tracked = new List<Tracked>();
        private int _cursor;                 // round-robin so the per-frame cost is flat
        private bool _radiusMode;            // whether the LOD exemption was applied to ALL loot
        private const int ChecksPerFrame = 64;
        private const float ShowHysteresis = 1.06f; // hide at R, show again at R*1.06

        private static readonly System.Diagnostics.Stopwatch _updSw = new System.Diagnostics.Stopwatch();

        private void Update()
        {
            if (!IceGate.On) return;
            _updSw.Restart();
            try { UpdateInner(); }
            finally { RenderEnvProbe.AddTick(RenderEnvProbe.TickLootVis, _updSw.Elapsed.TotalMilliseconds); }
        }

        private void UpdateInner()
        {
            TickRadiusCull();

            if (Time.time < _next) return;
            _next = Time.time + SweepEvery;

            // a live change of LootCullRadius across the 0 boundary changes which items
            // need the LOD exemption, so re-walk everything once when the mode flips
            bool wantRadius = Plugin.LootCullRadius.Value > 0f;
            if (wantRadius != _radiusMode)
            {
                _radiusMode = wantRadius;
                _done.Clear();
                if (!wantRadius) UnhideAll(); // radius turned off — nothing may stay hidden
                Plugin.Log.LogInfo($"[LootVis] loot cull radius {(wantRadius ? Plugin.LootCullRadius.Value.ToString("0") + "m" : "OFF (LOD bias governs loot again)")}");
            }

            var world = Singleton<GameWorld>.Instance;
            var loot = world?.LootItems;
            if (loot == null) return;

            for (int i = 0; i < loot.Count; i++)
            {
                LootItem item;
                try { item = loot.GetByIndex(i); }
                catch { continue; }
                if (item == null) continue;
                int id = item.GetInstanceID();
                if (!_done.Add(id)) continue;
                try { Fix(item); }
                catch (Exception e) { Plugin.Log.LogDebug($"[LootBounds] '{item.name}' failed: {e.Message}"); }
            }

            // one summary once the initial pile has been walked — silent afterwards
            if (!_summarised && _done.Count > 0 && Time.time > 30f)
            {
                _summarised = true;
                Plugin.Log.LogInfo($"[LootVis] {_done.Count} loot item(s) checked — {_lodFixed} key/keycard LODGroup cull height(s) lowered "
                    + $"(compensating lodBias {QualitySettings.lodBias:0.00}), {_fixedRenderers} skinned renderer(s) set to update offscreen"
                    + (_suspects > 0 ? $", {_suspects} renderer(s) with odd bounds" : ""));
            }
        }

        // the per-frame half. flat cost: a fixed slice of the list per frame, a squared
        // distance each, and a renderer write ONLY on a boundary crossing. forceRenderingOff
        // rather than .enabled so we never fight whatever else owns the renderer's enabled
        // flag (ObservedCullingManager drives bodies through the same field).
        private void TickRadiusCull()
        {
            if (!_radiusMode || _tracked.Count == 0) return;
            var cam = RenderEnvProbe.CameraRef;
            if (cam == null) return;

            float r = Plugin.LootCullRadius.Value;
            float hideSq = r * r;
            float showSq = (r * ShowHysteresis) * (r * ShowHysteresis);
            var camPos = cam.transform.position;

            int n = Mathf.Min(ChecksPerFrame, _tracked.Count);
            for (int k = 0; k < n; k++)
            {
                if (_cursor >= _tracked.Count) _cursor = 0;
                var t = _tracked[_cursor];

                // picked up / destroyed — drop it. nothing to restore: the renderers went
                // with the object, and an item that becomes inventory is no longer a LootItem
                if (t == null || t.Item == null)
                {
                    _tracked.RemoveAt(_cursor);
                    continue;
                }
                _cursor++;

                float d = (t.Item.transform.position - camPos).sqrMagnitude;
                // hysteresis so an item sitting exactly on the boundary cannot strobe
                bool hide = t.Hidden ? d > showSq : d > hideSq;
                if (hide == t.Hidden) continue;

                t.Hidden = hide;
                var rends = t.Rends;
                for (int i = 0; i < rends.Length; i++)
                    if (rends[i] != null) rends[i].forceRenderingOff = hide;
            }
        }

        private void UnhideAll()
        {
            foreach (var t in _tracked)
            {
                if (t?.Rends == null) continue;
                for (int i = 0; i < t.Rends.Length; i++)
                    if (t.Rends[i] != null) t.Rends[i].forceRenderingOff = false;
                t.Hidden = false;
            }
            _tracked.Clear();
            _cursor = 0;
        }

        private void OnDestroy() { try { UnhideAll(); } catch { } }

        // keycards carry KeycardComponent, ordinary keys KeyComponent — cheap template
        // lookups, no name matching to drift out of date as items are added
        private static bool IsKeyLike(LootItem item)
        {
            try
            {
                var it = item.Item;
                if (it == null) return false;
                return it.GetItemComponent<EFT.InventoryLogic.KeycardComponent>() != null
                    || it.GetItemComponent<EFT.InventoryLogic.KeyComponent>() != null;
            }
            catch { return false; }
        }

        private void Fix(LootItem item)
        {
            var pos = item.transform.position;

            // THE ACTUAL CAUSE (08-12): our own LodBiasClamp. vanilla EFT floors lod bias
            // at 2.0; we run 0.7 for the dense-view fps win, and that is a GLOBAL ~3x
            // reduction of every LOD transition AND cull distance — loot included. loot
            // ships real LOD ladders (AR_PACA_lod0/lod1), so it started dying at a third
            // of the distance BSG intended: "invisible until I get close".
            //
            // KEYS AND KEYCARDS ONLY (user call, same day): compensating EVERY loot item
            // measurably cost fps looking at the superstructure — hundreds of densely
            // packed loot models rendering to subpixel size across the whole ship is
            // exactly the draw-submission load the low bias was bought to avoid. keys and
            // keycards are a handful of items per raid, they are quest/progress critical,
            // and losing one to a cull is the failure that actually hurts. everything else
            // keeps vanilla-for-this-bias behaviour.
            // ...and RADIUS CULLING (user call 08-13) supersedes that carve-out. the
            // keys-only compromise existed because exempting every item meant hundreds of
            // loot models rendering to subpixel size across the ship. a radius removes
            // that cost differently and better: exempt EVERYTHING from the LOD cull so
            // nothing fades or dithers at range, then delete it outright past
            // LootCullRadius. a hard cutoff draws strictly less than subpixel geometry,
            // so this is cheaper than the old all-items experiment AND tunable, instead
            // of a fixed list of item types that has to be curated forever.
            if (_radiusMode || IsKeyLike(item))
            {
                foreach (var g in item.GetComponentsInChildren<LODGroup>(true))
                {
                    if (g == null) continue;
                    var lods = g.GetLODs();
                    if (lods == null || lods.Length == 0) continue;
                    int last = lods.Length - 1;
                    if (lods[last].screenRelativeTransitionHeight <= LootCullHeight) continue;
                    lods[last].screenRelativeTransitionHeight = LootCullHeight;
                    g.SetLODs(lods);
                    _lodFixed++;
                }
            }

            if (_radiusMode)
            {
                var rends = item.GetComponentsInChildren<Renderer>(true);
                if (rends != null && rends.Length > 0)
                    _tracked.Add(new Tracked { Item = item, Rends = rends, Hidden = false });
            }

            foreach (var r in item.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;

                // skinned meshes cull against bounds that only refresh while visible —
                // the classic "disappears at certain angles" trap. cheap for loot-sized
                // meshes and correct by construction.
                if (r is SkinnedMeshRenderer smr)
                {
                    if (!smr.updateWhenOffscreen) { smr.updateWhenOffscreen = true; _fixedRenderers++; }
                    continue;
                }

                // bounds sanity kept purely as a REPORT: the 08-12 sweep found 3 of 283
                // items with bounds ~950m off the mesh (corpse gear on odd rigs) and
                // RecalculateBounds changed nothing, so this is not the loot-visibility
                // cause. left in because a spike here would still be worth knowing.
                if ((r.bounds.center - pos).sqrMagnitude > SuspectDistance * SuspectDistance
                    || r.bounds.size.sqrMagnitude < 1e-6f)
                    _suspects++;
            }
        }
    }
}
