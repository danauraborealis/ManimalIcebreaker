using System;
using System.Collections;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // one-raid diagnostic for the unlootable-containers report: dumps what the client
    // actually sees — do the rebaked LootableContainer components exist, are they
    // enabled/active, on what layer, with colliders, and did the server's generated
    // loot bind an item to them. removable once container loot proves out.
    [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
    internal static class Patch_IcebreakerLootDiag
    {
        [HarmonyPostfix]
        private static void Postfix(GameWorld __instance)
        {
            try
            {
                if (!string.Equals(__instance?.LocationId, "Suburbs", StringComparison.OrdinalIgnoreCase)) return;
                var host = new GameObject("IcebreakerLootDiag");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<LootDiagRunner>();
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"[LootDiag] arm failed: {e.Message}"); }
        }
    }

    internal sealed class LootDiagRunner : MonoBehaviour
    {
        private IEnumerator Start()
        {
            // loot binding happens during load; wait a few seconds past game start
            yield return new WaitForSeconds(5f);
            try { Dump(); } catch (Exception e) { Plugin.Log?.LogWarning($"[LootDiag] dump failed: {e}"); }
            Destroy(gameObject);
        }

        private void Dump()
        {
            var all = FindObjectsOfType<LootableContainer>(true);

            // HEAL: the Author 11 rebake didnt carry _doorState, so containers shipped
            // DoorState=None — the action menu gates Search on Shut (retail authored 2
            // on all 83), so bound containers showed NO prompt. field write, not the
            // property setter, to avoid door-state side effects. remove once a bundle
            // built with the fixed whitelist ships.
            var dsField = AccessTools.Field(typeof(WorldInteractiveObject), "_doorState");
            int healed = 0;
            foreach (var lc in all)
            {
                if (lc == null || string.IsNullOrEmpty(lc.Id)) continue; // skip prefab templates
                try
                {
                    // enum-typed field — convert both ways so unbox/assign cant throw
                    if (dsField != null && Convert.ToInt32(dsField.GetValue(lc)) == 0)
                    {
                        dsField.SetValue(lc, Enum.ToObject(dsField.FieldType, 2)); // EDoorState.Shut
                        healed++;
                    }
                }
                catch { }
            }
            if (healed > 0) Plugin.Log?.LogWarning($"[LootDiag] healed DoorState None->Shut on {healed} container(s)");

            // GHOST KILL: containers that "didnt spawn" this raid get their visuals
            // deactivated by the game, but the interactive object (collider + LC)
            // survives — an invisible searchable spot. if the prop root has NO
            // renderer on an ACTIVE GameObject, shut the container off entirely.
            // active-state, not renderer.enabled — the culling driver flips enabled
            // on distant groups and those must keep their prompts.
            int ghosts = 0;
            foreach (var lc in all)
            {
                if (lc == null || string.IsNullOrEmpty(lc.Id) || !lc.gameObject.activeInHierarchy) continue;
                // prop root = nearest LODGroup ancestor (the prop), else the direct
                // parent — deliberately NOT higher, or sibling props' renderers would
                // mask a ghost under a shared scene node
                Transform root = null;
                var walk = lc.transform;
                for (int hop = 0; walk != null && hop < 4; hop++, walk = walk.parent)
                {
                    if (walk.GetComponent<LODGroup>() != null) { root = walk; break; }
                }
                if (root == null) root = lc.transform.parent != null ? lc.transform.parent : lc.transform;
                bool anyVisible = false;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r != null && r.gameObject.activeInHierarchy) { anyVisible = true; break; }
                }
                if (!anyVisible)
                {
                    lc.gameObject.SetActive(false);
                    ghosts++;
                }
            }
            if (ghosts > 0) Plugin.Log?.LogWarning($"[LootDiag] {ghosts} unspawned container(s) had live interactions — deactivated (ghost-search fix)");

            int enabled = 0, active = 0, withCollider = 0, bound = 0, interactiveLayer = 0;
            int shown = 0;
            // ItemOwner is a FIELD (TraderControllerClass) — set when the game binds
            // the server-generated container item to this LC
            var itemOwnerField = AccessTools.Field(typeof(LootableContainer), "ItemOwner");
            foreach (var lc in all)
            {
                if (lc == null) continue;
                if (lc.enabled) enabled++;
                if (lc.gameObject.activeInHierarchy) active++;
                if (lc.GetComponent<Collider>() != null || lc.GetComponentInChildren<Collider>(true) != null) withCollider++;
                if (lc.gameObject.layer == LayerMask.NameToLayer("Interactive")) interactiveLayer++;
                object owner = null;
                try { owner = itemOwnerField?.GetValue(lc); } catch { }
                if (owner != null) bound++;
                if (shown < 6)
                {
                    shown++;
                    Plugin.Log?.LogWarning(
                        $"[LootDiag] '{lc.Id}' tpl={lc.Template} enabled={lc.enabled} active={lc.gameObject.activeInHierarchy} " +
                        $"layer={LayerMask.LayerToName(lc.gameObject.layer)} collider={(lc.GetComponentInChildren<Collider>(true) != null)} " +
                        $"itemBound={(owner != null)} pos={lc.transform.position}");
                }
            }
            Plugin.Log?.LogWarning(
                $"[LootDiag] containers={all.Length} enabled={enabled} activeInHierarchy={active} " +
                $"withCollider={withCollider} onInteractiveLayer={interactiveLayer} itemBound={bound} " +
                $"(itemOwnerField={(itemOwnerField != null ? "found" : "MISSING — member name drift")})");

            // LocationScene wiring check — the arrays Author 11 filled
            int scenes = 0, wired = 0;
            foreach (var ls in FindObjectsOfType<LocationScene>(true))
            {
                scenes++;
                if (ls.LootableContainers != null && ls.LootableContainers.Length > 0) wired++;
            }
            Plugin.Log?.LogWarning($"[LootDiag] LocationScene components={scenes}, with populated LootableContainers arrays={wired}");
        }
    }
}
