using System;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker.Blowtorch
{
    // KNOWN-GOOD configuration (hackermod pattern): the torch clones the vanilla BBQ
    // torch and stays a plain KeyItemClass — keys skip the weapon branch in item
    // creation, so the retail loot model in Prefab spawns/inspects/icons cleanly.
    // the container bundle rides only in UsePrefab for OUR controller. cost: keys get
    // no native usable plumbing, so every entry point below is patched by TEMPLATE ID.
    // known cosmetic issue we accept: third person can hold a broken pose with the
    // torch out (same as the hacker device — the body-pose pipeline has more
    // key-class checks than the two we patch here).

    public class BlowtorchInterfaceClass : GInterface323
    {
        public void Initialize(GameObject gameObject) { }
        public void UpdateData(GStruct337 observedUsableItemUpdatedData) { }
        public void Disable() { }
    }

    // manual equip chain: vanilla Proceed<T>'s factory reads Prefab (vanilla usables
    // ship their hands rig THERE); ours lives in UsePrefab — reproduce Process.Execute
    // by hand with the CreateItemUsablePrefab factory.
    internal static class BlowtorchEquip
    {
        internal static void Equip(Player player, Item item)
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
                        Singleton<PoolManagerClass>.Instance.CreateItemUsablePrefab));
                if (controller == null)
                {
                    Plugin.Log.LogDebug("[Blowtorch] controller factory returned null");
                    return;
                }
                Player.UsableItemController.smethod_8<BlowtorchController>(controller, player);
                player.SpawnController(controller, () => { });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Blowtorch] spawn failed: {e}");
            }
        }
    }

    // quickslot key path — keys dispatch to nothing in vanilla TryProceed
    [HarmonyPatch(typeof(Player), nameof(Player.TryProceed))]
    internal static class Patch_TorchTryProceed
    {
        [HarmonyPrefix]
        private static bool Prefix(Player __instance, Item item)
        {
            if (!BlowtorchIds.IsTorch(item)) return true;
            BlowtorchEquip.Equip(__instance, item);
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.SetInHandsUsableItem))]
    internal static class Patch_TorchSetInHands
    {
        [HarmonyPrefix]
        private static bool Prefix(Player __instance, Item item)
        {
            if (!BlowtorchIds.IsTorch(item)) return true;
            BlowtorchEquip.Equip(__instance, item);
            return false;
        }
    }

    // async controller factory during hand swaps only recognizes the rangefinder —
    // for our item it builds a controller with a null item and breaks the swap
    [HarmonyPatch(typeof(ClientUsableItemController), "smethod_11")]
    internal static class Patch_TorchSmethod11
    {
        [HarmonyPrefix]
        private static bool Prefix(ref System.Threading.Tasks.Task<ClientUsableItemController> __result,
            ClientPlayer player, string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return true;
            var item = player.InventoryController.FindItem<Item>(itemId);
            if (!BlowtorchIds.IsTorch(item)) return true;
            __result = Player.UsableItemController.smethod_7<ClientUsableItemController>(player, item);
            return false;
        }
    }

    // GInterface323 dispatcher returns null for unknown types — null breaks the chain
    [HarmonyPatch(typeof(GClass2970), "smethod_0")]
    internal static class Patch_TorchInterfaceDispatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ref GInterface323 __result, Item item)
        {
            if (!BlowtorchIds.IsTorch(item)) return true;
            __result = new BlowtorchInterfaceClass();
            return false;
        }
    }

    // full-body animation type — Pistol is the one-handed held-in-front fit. two call
    // sites; known-insufficient for a perfect third-person pose (accepted).
    [HarmonyPatch(typeof(HandsControllerClass), "method_49")]
    internal static class Patch_TorchAnimationType
    {
        [HarmonyPrefix]
        private static bool Prefix(ref PlayerAnimator.EWeaponAnimationType __result, HandsControllerClass __instance)
        {
            if (!BlowtorchIds.IsTorch(__instance.ItemInHands)) return true;
            __result = PlayerAnimator.EWeaponAnimationType.Pistol;
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.GetWeaponAnimationType))]
    internal static class Patch_TorchWeaponAnimationType
    {
        [HarmonyPrefix]
        private static bool Prefix(ref PlayerAnimator.EWeaponAnimationType __result,
            Player.AbstractHandsController handsController)
        {
            if (!(handsController is BlowtorchController)) return true;
            __result = PlayerAnimator.EWeaponAnimationType.Pistol;
            return false;
        }
    }

    // vanilla whitelists only certain classes for quickslot binding / drawing
    [HarmonyPatch(typeof(InventoryController), nameof(InventoryController.IsAtBindablePlace))]
    internal static class Patch_TorchBindable
    {
        [HarmonyPostfix]
        private static void Postfix(Item item, ref bool __result)
        {
            if (BlowtorchIds.IsTorch(item)) __result = true;
        }
    }

    [HarmonyPatch(typeof(InventoryController), nameof(InventoryController.IsAtReachablePlace))]
    internal static class Patch_TorchReachable
    {
        [HarmonyPostfix]
        private static void Postfix(Item item, ref bool __result)
        {
            if (BlowtorchIds.IsTorch(item)) __result = true;
        }
    }

    // during controller teardown/respawn WeaponRootAnim is briefly null and PWA's
    // update chain derefs it unguarded — same guards the ammo-loading mod ships
    [HarmonyPatch(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.ProcessEffectors))]
    internal static class Patch_PwaEffectorsGuard
    {
        [HarmonyPrefix]
        private static bool Prefix(ProceduralWeaponAnimation __instance)
            => __instance.HandsContainer == null || __instance.HandsContainer.WeaponRootAnim != null;
    }

    [HarmonyPatch(typeof(Player), nameof(Player.VisualPass))]
    internal static class Patch_VisualPassGuard
    {
        [HarmonyPrefix]
        private static bool Prefix(Player __instance)
        {
            var hc = __instance.ProceduralWeaponAnimation?.HandsContainer;
            return hc == null || hc.WeaponRootAnim != null;
        }
    }

    // safety net: a torch spawn/preview failure must never abort raid load
    [HarmonyPatch(typeof(PoolManagerClass), nameof(PoolManagerClass.CreateCleanLootPrefab))]
    internal static class Patch_LootPrefabGuard
    {
        [HarmonyFinalizer]
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
