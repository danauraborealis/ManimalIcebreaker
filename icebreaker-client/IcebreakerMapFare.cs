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
    // the price is a CONST on purpose (user call 08-19, the transit teardown): it used
    // to read the TransitCost config, which let anyone zero the fare from their
    // bepinex config. the map screen is now the only way aboard and the fare is part
    // of the map's economy, so it ships hardcoded like the labs keycard requirement.
    //
    // the BTR questline's payoff (user call, same day): finishing Hangover, the end of
    // the Saving Private Roman chain, HALVES the fare. that replaced the chain's old
    // reward of unlocking the map screen, which Boreas P3 now grants much earlier.
    internal static class IcebreakerMapFare
    {
        private const string RoubleTpl = "5449016a4bdc2d6f028b456f";
        private const string SuburbsId = "Suburbs";
        internal const int CrossingCost = 500_000;
        internal const int CrossingCostDiscounted = 250_000;
        private const string HangoverQuestId = "3f8d2c5a9b17e04d6ca8f312"; // BTR chain final

        // reads the PROFILE's own quest list, so it works at the menu (ready gate,
        // Profile_0) and at game creation (the incoming Profile) alike, solo and fika
        internal static int CostFor(Profile profile)
        {
            try
            {
                var qs = profile?.QuestsData;
                if (qs != null)
                    for (int i = 0; i < qs.Count; i++)
                        if (qs[i] != null && qs[i].Id == HangoverQuestId && qs[i].Status == EFT.Quests.EQuestStatus.Success)
                            return CrossingCostDiscounted;
            }
            catch (Exception e) { Plugin.Log.LogDebug($"[MapFare] discount check failed (full fare): {e.Message}"); }
            return CrossingCost;
        }

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
                    int cost = CostFor(__instance.Profile_0);

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
                => Consume(profile, location, raidSettings);
        }

        // shared by the solo path (LocalGame.smethod_6 above) and the fika path
        // (CoopGame.Create, re-anchored at runtime by IcebreakerFikaCompat — CoopGame
        // is not a LocalGame, so the attribute patch never fires in coop). every peer
        // creates its own game with its own profile, so in coop each player pays their
        // own fare on their own machine.
        //
        // deliberately NOT the vmethod_1/TryRunNetworkTransaction route the in-raid
        // fares use: this runs at game CREATION, before any Player or live inventory
        // controller exists to dispatch through — the same pre-raid window where BSG's
        // own labs flow removes the spent keycard with a bare grid removal, which is
        // the mechanism mirrored here. nothing to replicate either: the raid hasn't
        // started, and the deduction persists through each player's own raid-end sync.
        internal static void Consume(Profile profile, LocationSettingsClass.Location location, LocalRaidSettings raidSettings)
        {
            try
            {
                if (location == null || location.Id != SuburbsId) return;
                if (profile == null || profile.Side == EPlayerSide.Savage) return;
                int cost = CostFor(profile);

                // smallest stacks first, so change stays consolidated in one stack
                var stacks = profile.Inventory.GetPlayerItems(EPlayerItems.Equipment)
                    .Where(i => i != null && i.TemplateId == RoubleTpl)
                    .OrderBy(i => i.StackObjectsCount)
                    .ToList();

                // ALL OR NOTHING: a fika client joins through fika's own lobby, which
                // may bypass the method_54 ready gate — without this check a short
                // player would get partially drained and still load in. a free ride
                // with a loud log beats eating someone's last 100k.
                int carried = stacks.Sum(s => s.StackObjectsCount);
                if (carried < cost)
                {
                    Plugin.Log.LogDebug($"[MapFare] only {carried}/{cost} carried and the ready gate didn't refuse — NOT charging (free crossing, check the gate)");
                    return;
                }

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
                    Plugin.Log.LogWarning($"[MapFare] came up {remaining} short of {cost} mid-deduction — grid ops failed above");
                else
                    Plugin.Log.LogDebug($"[MapFare] consumed {cost} roubles for the crossing");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[MapFare] consume failed: {e.Message}"); }
        }
    }
}
