using System;
using System.Linq;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using HarmonyLib;

namespace Manimal.Icebreaker
{
    // the map-screen crossing fare, shaped exactly like the LABS access flow:
    //   1. the READY button refuses unless the fare is in the gear you're taking
    //      (method_54 is the same gate that refuses without a labs keycard, and it
    //      checks EPlayerItems.Equipment — carried gear, not stash)
    //   2. the fare is CONSUMED at raid creation, before anything loads
    //      (labs does this in BaseLocalGame pre-spawn via a local grid removal; ours
    //      runs in LocalGame.smethod_6, the factory that receives the Profile, which
    //      is the same point of no return one call earlier)
    // the removal is deliberately LOCAL, no network transaction — identical to the
    // labs keycard, whose removal persists through the raid-end inventory sync.
    //
    // transit arrivals are exempt: the hovercraft already charged the same fare
    // in-raid at the point of boarding, and one crossing must not bill twice.
    internal static class IcebreakerMapFare
    {
        private const string RoubleTpl = "5449016a4bdc2d6f028b456f";
        private const string SuburbsId = "Suburbs";

        [HarmonyPatch(typeof(MainMenuControllerClass), "method_54")]
        internal static class Patch_ReadyGate
        {
            [HarmonyPostfix]
            private static void Postfix(MainMenuControllerClass __instance, ref bool __result)
            {
                try
                {
                    if (!__result) return;
                    var rs = __instance.RaidSettings_0;
                    if (rs == null || rs.IsScav) return;
                    if (rs.SelectedLocation == null || rs.SelectedLocation.Id != SuburbsId) return;
                    int cost = Plugin.TransitCost.Value;
                    if (cost <= 0) return;

                    int carried = __instance.InventoryController.Inventory
                        .GetPlayerItems(EPlayerItems.Equipment)
                        .Where(i => i != null && i.TemplateId == RoubleTpl)
                        .Sum(i => i.StackObjectsCount);
                    if (carried >= cost) return;

                    NotificationManagerClass.DisplayWarningNotification(
                        $"The smugglers want {cost:N0} roubles for the crossing ({carried:N0} carried)",
                        ENotificationDurationType.Long);
                    __result = false;
                }
                // an error here must not lock the player out of every map — let it
                // through and let the consume pass log the shortfall instead
                catch (Exception e) { Plugin.Log.LogWarning($"[MapFare] gate failed (letting through): {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(LocalGame), "smethod_6")]
        internal static class Patch_ConsumeFare
        {
            [HarmonyPrefix]
            private static void Prefix(Profile profile, LocationSettingsClass.Location location, LocalRaidSettings raidSettings)
            {
                try
                {
                    if (location == null || location.Id != SuburbsId) return;
                    if (profile == null || profile.Side == EPlayerSide.Savage) return;
                    int cost = Plugin.TransitCost.Value;
                    if (cost <= 0) return;

                    // arriving by hovercraft: the fare was taken in-raid at boarding.
                    // transitionCount>0 is the ONLY reliable discriminator — the first
                    // version also exempted on transitionType != None, and fresh raids
                    // evidently don't carry None there, so every crossing rode free with
                    // no log line to say why. log the state; never skip silently again.
                    var tr = raidSettings != null ? raidSettings.transition : null;
                    if (tr != null)
                        Plugin.Log.LogWarning($"[MapFare] transition state: type={tr.transitionType} count={tr.transitionCount}");
                    if (tr != null && tr.transitionCount > 0)
                    {
                        Plugin.Log.LogWarning("[MapFare] transit arrival — fare was paid at boarding, not charging");
                        return;
                    }

                    // smallest stacks first, so change stays consolidated in one stack
                    var stacks = profile.Inventory.GetPlayerItems(EPlayerItems.Equipment)
                        .Where(i => i != null && i.TemplateId == RoubleTpl)
                        .OrderBy(i => i.StackObjectsCount)
                        .ToList();

                    int remaining = cost;
                    foreach (var s in stacks)
                    {
                        if (remaining <= 0) break;
                        if (s.StackObjectsCount <= remaining)
                        {
                            // whole stack spent — off the grid, the exact mechanism the
                            // labs flow uses on a spent keycard
                            var grid = s.Parent != null ? s.Parent.Container as StashGridClass : null;
                            if (grid == null)
                            {
                                Plugin.Log.LogWarning($"[MapFare] rouble stack not in a grid ('{s.Parent?.Container?.GetType().Name}'), skipping it");
                                continue;
                            }
                            var op = grid.Remove(s, false);
                            if (op.Failed) { Plugin.Log.LogWarning($"[MapFare] stack removal failed: {op.Error}"); continue; }
                            remaining -= s.StackObjectsCount;
                        }
                        else
                        {
                            s.StackObjectsCount -= remaining;
                            remaining = 0;
                        }
                    }

                    if (remaining > 0)
                        Plugin.Log.LogWarning($"[MapFare] came up {remaining} short of {cost} — the ready gate should have refused this raid");
                    else
                        Plugin.Log.LogWarning($"[MapFare] consumed {cost} roubles for the crossing");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[MapFare] consume failed: {e.Message}"); }
            }
        }
    }
}
