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

        private void Update()
        {
            if (!IceGate.On || Time.time < _next) return;
            _next = Time.time + SweepEvery;

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
            if (IsKeyLike(item))
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
