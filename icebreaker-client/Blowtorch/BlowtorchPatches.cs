using System;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.Icebreaker.Blowtorch
{
    // KNOWN-GOOD configuration (hackermod pattern): the torch clones the vanilla BBQ
    // torch and stays a plain EFT.InventoryLogic.Key — keys skip the weapon branch in item
    // creation, so the retail loot model in Prefab spawns/inspects/icons cleanly.
    // the container bundle rides only in UsePrefab for OUR controller. cost: keys get
    // no native usable plumbing, so every entry point below is patched by TEMPLATE ID.
    // known cosmetic issue we accept: third person can hold a broken pose with the
    // torch out (same as the hacker device — the body-pose pipeline has more
    // key-class checks than the two we patch here).

    public class BlowtorchInterfaceClass : EFT.NextObservedPlayer.IObservedUsableItem
    {
        public void Initialize(GameObject gameObject) { }
        public void UpdateData(EFT.NextObservedPlayer.ObservedUsableItemUpdatedData observedUsableItemUpdatedData) { }
        public void Disable() { }
    }

    // manual equip chain: vanilla Proceed<T>'s factory reads Prefab (vanilla usables
    // ship their hands rig THERE); ours lives in UsePrefab — reproduce Process.Execute
    // by hand with the CreateItemUsablePrefab factory.
    internal static class BlowtorchEquip
    {
        private static bool _loading;

        // UsePrefab isnt loaded when the key is pressed on 4.1, and a "not loaded" throw after
        // the old controller is gone strands the player handless — load first, swap after.
        internal static async void Equip(Player player, Item item)
        {
            if (_loading) return;
            _loading = true;
            try
            {
                await EFT.InventoryLogic.Operations.ChangeItemsOperation.LoadBundles(item);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Blowtorch] hands bundle load failed, staying in current hands: {e.Message}");
                return;
            }
            finally { _loading = false; }
            if (player == null || item == null || player.HandsController?.Item == item) return;
            Swap(player, item);
        }

        private static void Swap(Player player, Item item)
        {
            try
            {
                try { player.StopBlindFire(); } catch { }
                try { player.RemoveLeftHandItem(); } catch { }
                try { player.TrySaveLastItemInHands(); } catch { }
                player.DropCurrentController(() => CreateAndSpawn(player, item), false, item);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Blowtorch] equip failed: {e.Message}");
            }
        }

        private static void CreateAndSpawn(Player player, Item item)
        {
            try
            {
                if (player.HandsController != null)
                    player.DestroyController();

                var controller = Player.ItemHandsController.smethod_1<BlowtorchController>(
                    player, item,
                    new Player.ItemHandsController.Delegate8(
                        Singleton<EFT.ObjectsFactory>.Instance.CreateItemUsablePrefab));
                if (controller == null)
                {
                    Plugin.Log.LogDebug("[Blowtorch] controller factory returned null");
                    return;
                }
                Player.UsableItemController.Setup<BlowtorchController>(controller, player);
                player.SpawnController(controller, () => { });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Blowtorch] spawn failed: {e}");
                // the previous controller is already gone — never leave the player handless
                try { player.SetEmptyHands(null); }
                catch (Exception e2) { Plugin.Log.LogWarning($"[Blowtorch] empty-hands recovery failed: {e2.Message}"); }
            }
        }
    }

    // quickslot key path — keys dispatch to nothing in vanilla TryProceed
    [IcebreakerPatchTarget(typeof(Player), nameof(Player.TryProceed))]
    internal sealed class Patch_TorchTryProceed : IcebreakerModulePatch<Patch_TorchTryProceed>
    {
        [PatchPrefix]
        private static bool Prefix(Player __instance, Item item)
        {
            if (!BlowtorchIds.IsTorch(item)) return true;
            BlowtorchEquip.Equip(__instance, item);
            return false;
        }
    }

    [IcebreakerPatchTarget(typeof(Player), nameof(Player.SetInHandsUsableItem))]
    internal sealed class Patch_TorchSetInHands : IcebreakerModulePatch<Patch_TorchSetInHands>
    {
        [PatchPrefix]
        private static bool Prefix(Player __instance, Item item)
        {
            if (!BlowtorchIds.IsTorch(item)) return true;
            BlowtorchEquip.Equip(__instance, item);
            return false;
        }
    }

    // async controller factory during hand swaps only recognizes the rangefinder —
    // for our item it builds a controller with a null item and breaks the swap
    [IcebreakerPatchTarget(typeof(ClientUsableItemController), "CreateAsync")]
    internal sealed class Patch_TorchSmethod11 : IcebreakerModulePatch<Patch_TorchSmethod11>
    {
        [PatchPrefix]
        private static bool Prefix(ref System.Threading.Tasks.Task<ClientUsableItemController> __result,
            ClientPlayer player, string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return true;
            var item = player.InventoryController.FindItem<Item>(itemId);
            if (!BlowtorchIds.IsTorch(item)) return true;
            __result = Player.UsableItemController.CreateControllerAsync<ClientUsableItemController>(player, item);
            return false;
        }
    }

    // EFT.NextObservedPlayer.IObservedUsableItem dispatcher returns null for unknown types — null breaks the chain
    [IcebreakerPatchTarget(typeof(EFT.NextObservedPlayer.ObservedPlayerUsableItemController), "GetObservedUsableItem")]
    internal sealed class Patch_TorchInterfaceDispatch : IcebreakerModulePatch<Patch_TorchInterfaceDispatch>
    {
        [PatchPrefix]
        private static bool Prefix(ref EFT.NextObservedPlayer.IObservedUsableItem __result, Item item)
        {
            if (!BlowtorchIds.IsTorch(item)) return true;
            __result = new BlowtorchInterfaceClass();
            return false;
        }
    }

    // full-body animation type — Pistol is the one-handed held-in-front fit. two call
    // sites; known-insufficient for a perfect third-person pose (accepted).
    [IcebreakerPatchTarget(typeof(EFT.NextObservedPlayer.ObservedPlayerHandsController), "GetWeaponAnimationType")]
    internal sealed class Patch_TorchAnimationType : IcebreakerModulePatch<Patch_TorchAnimationType>
    {
        [PatchPrefix]
        private static bool Prefix(ref PlayerAnimator.EWeaponAnimationType __result, EFT.NextObservedPlayer.ObservedPlayerHandsController __instance)
        {
            if (!BlowtorchIds.IsTorch(__instance.ItemInHands)) return true;
            __result = PlayerAnimator.EWeaponAnimationType.Pistol;
            return false;
        }
    }

    [IcebreakerPatchTarget(typeof(Player), nameof(Player.GetWeaponAnimationType))]
    internal sealed class Patch_TorchWeaponAnimationType : IcebreakerModulePatch<Patch_TorchWeaponAnimationType>
    {
        [PatchPrefix]
        private static bool Prefix(ref PlayerAnimator.EWeaponAnimationType __result,
            Player.AbstractHandsController handsController)
        {
            if (!(handsController is BlowtorchController)) return true;
            __result = PlayerAnimator.EWeaponAnimationType.Pistol;
            return false;
        }
    }

    // vanilla whitelists only certain classes for quickslot binding / drawing.
    // run LAST: WTT-PackNStrap's postfixes on both checks recompute the result from
    // scratch (their own class whitelist), which clobbered the torch's true and left it
    // unbindable. SPT's ModulePatch names each owner after its class, hence the ids.
    [IcebreakerPatchTarget(typeof(InventoryController), nameof(InventoryController.IsAtBindablePlace))]
    internal sealed class Patch_TorchBindable : IcebreakerModulePatch<Patch_TorchBindable>
    {
        [PatchPostfix, HarmonyPriority(Priority.Last)]
        [HarmonyAfter("IsAtBindablePlacePatch", "BindableTacticalPatch", "MedpouchBindablePatch")]
        private static void Postfix(Item item, ref bool __result)
        {
            if (BlowtorchIds.IsTorch(item)) __result = true;
        }
    }

    [IcebreakerPatchTarget(typeof(InventoryController), nameof(InventoryController.IsAtReachablePlace))]
    internal sealed class Patch_TorchReachable : IcebreakerModulePatch<Patch_TorchReachable>
    {
        [PatchPostfix, HarmonyPriority(Priority.Last)]
        [HarmonyAfter("IsAtReachablePlacePatch", "ReachableTacticalPatch", "PhantomReachabilityPatch")]
        private static void Postfix(Item item, ref bool __result)
        {
            if (BlowtorchIds.IsTorch(item)) __result = true;
        }
    }

    // during controller teardown/respawn WeaponRootAnim is briefly null and PWA's
    // update chain derefs it unguarded — same guards the ammo-loading mod ships
    [IcebreakerPatchTarget(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.ProcessEffectors))]
    internal sealed class Patch_PwaEffectorsGuard : IcebreakerModulePatch<Patch_PwaEffectorsGuard>
    {
        [PatchPrefix]
        private static bool Prefix(ProceduralWeaponAnimation __instance)
            => __instance.HandsContainer == null || __instance.HandsContainer.WeaponRootAnim != null;
    }

    [IcebreakerPatchTarget(typeof(Player), nameof(Player.VisualPass))]
    internal sealed class Patch_VisualPassGuard : IcebreakerModulePatch<Patch_VisualPassGuard>
    {
        [PatchPrefix]
        private static bool Prefix(Player __instance)
        {
            var hc = __instance.ProceduralWeaponAnimation?.HandsContainer;
            return hc == null || hc.WeaponRootAnim != null;
        }
    }

    // safety net: a torch spawn/preview failure must never abort raid load
    [IcebreakerPatchTarget(typeof(EFT.ObjectsFactory), nameof(EFT.ObjectsFactory.CreateCleanLootPrefab))]
    internal sealed class Patch_LootPrefabGuard : IcebreakerModulePatch<Patch_LootPrefabGuard>
    {
        [PatchFinalizer]
        private static Exception Finalizer(Exception __exception, Item item, ref GameObject __result)
        {
            if (__exception == null) return null;
            Plugin.Log.LogError($"[Blowtorch] loot prefab FAILED for {item?.TemplateId}: {__exception.GetType().Name}: {__exception.Message}");
            if (BlowtorchIds.IsTorch(item))
            {
                __result = new GameObject("blowtorch_loot_failed");
                return null;
            }
            return __exception;
        }
    }
}
