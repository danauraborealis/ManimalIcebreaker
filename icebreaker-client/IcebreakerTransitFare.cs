using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // the smugglers charge for the crossing. transits have NO native price: the engine's
    // only transit requirement is AccessKeys, which demands an item in your equipment
    // (how Labyrinth gates on the Labrys keycard). the paid-extract system is richer but
    // bolted to ExfiltrationPoint, where the point builds a fake stash and you physically
    // drag roubles into it (TransferItemRequirement).
    //
    // so we intercept the transit itself and take the fare with the same inventory
    // primitives the exfil uses: a fake stash owned by a TraderControllerClass, and a
    // SplitExact/Move network transaction into it, which is what makes the money actually
    // leave the profile rather than just decrementing something client-side.
    //
    // fail closed: if the player is short, or the transaction doesn't complete, the
    // transit is blocked. never let someone ride for free because a move failed.
    internal static class IcebreakerTransitFare
    {
        private const string RoubleTpl = "5449016a4bdc2d6f028b456f";

        // ACCESS GATE, at the moment the interaction would be offered. this mirrors how
        // the engine handles a keyed transit: method_14 always fires on entry, runs its
        // requirement check, and on failure calls method_18 to clear the interaction
        // state so no prompt ever appears. Labyrinth does exactly this without the Labrys
        // keycard, silently. we do the same but say why, since an unexplained dead zone
        // reads as a bug rather than a locked door.
        [HarmonyPatch(typeof(TransitInteractionControllerAbstractClass), "method_14")]
        internal static class Patch_AccessGate
        {
            [HarmonyPrefix]
            private static bool Prefix(TransitInteractionControllerAbstractClass __instance, int pointId, Player player)
            {
                try
                {
                    if (pointId != IcebreakerTransit.PointId) return true;
                    // scavs never cross, chain or no chain — the smugglers deal with the
                    // PMC who ran their errands, not whoever wandered up the beach. the
                    // quest check below would ALSO deny a scav (a scav profile carries
                    // none of the chain), but that's incidental; this is the rule.
                    if (player != null && player.Side == EPlayerSide.Savage)
                    {
                        NotificationManagerClass.DisplayWarningNotification(
                            "Access is denied", ENotificationDurationType.Default);
                        __instance.method_18(player);
                        return false;
                    }
                    if (IcebreakerTransit.ChainComplete()) return true;

                    NotificationManagerClass.DisplayWarningNotification(
                        "Access is denied", ENotificationDurationType.Default);
                    __instance.method_18(player);   // drop the interaction, close the panels
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Fare] access gate failed, denying: {e.Message}");
                    return false;
                }
            }
        }

        // FARE GATE, at the moment the prompt is hit. method_15 is the action wired into
        // the interaction in method_14, so this lands before the 2 second long tap and
        // before any countdown exists. refusing here leaves the prompt in place, so you
        // can walk off, find the money and come back. without it the only refusal was
        // inside Transit(), which is the countdown reaching zero: too late to be useful,
        // and it strands you in the zone with an expired timer.
        [HarmonyPatch(typeof(TransitInteractionControllerAbstractClass), "method_15")]
        internal static class Patch_FareGate
        {
            [HarmonyPrefix]
            private static bool Prefix(int pointId, Player player)
            {
                try
                {
                    if (pointId != IcebreakerTransit.PointId) return true;
                    int cost = Plugin.TransitCost.Value;
                    if (cost <= 0) return true;
                    if (player == null || !player.IsYourPlayer) return true;
                    if (CarriedRoubles(player) >= cost) return true;

                    NotificationManagerClass.DisplayWarningNotification(
                        $"You need {cost:N0} roubles for the crossing", ENotificationDurationType.Default);
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Fare] gate failed, refusing: {e.Message}");
                    return false;
                }
            }
        }

        // the FARE, taken at confirm. Transit() is reached from the interaction action,
        // not from the trigger, so nobody is charged merely for walking through.
        //
        // the FARE, taken when the crossing is CONFIRMED. InteractWithTransit is the commit:
        // it is what registers the transit player and calls GroupEnter, which is what starts
        // the countdown. taking the money here means you pay the smugglers to cast off, not
        // on arrival, and the boarding is refused outright if it can't be paid.
        //
        // patch the OVERRIDE, never TransitControllerAbstractClass. the base Transit is a
        // virtual with an empty body, and a Harmony patch on a base virtual never sees a call
        // that dispatches to the derived implementation, which is exactly why the first
        // version of this silently never ran and the crossing was free.
        [HarmonyPatch(typeof(LocalGameTransitControllerClass), nameof(LocalGameTransitControllerClass.InteractWithTransit))]
        internal static class Patch_ChargeFare
        {
            [HarmonyPrefix]
            private static bool Prefix(Player player, TransitInteractionPacketStruct packet)
            {
                try
                {
                    if (packet.pointId != IcebreakerTransit.PointId) return true;
                    int cost = Plugin.TransitCost.Value;
                    if (cost <= 0) return true;
                    if (player == null || !player.IsYourPlayer) return true;

                    return TryTakeFare(player, cost);
                }
                catch (Exception e)
                {
                    // a thrown fare check must not hand out free rides
                    Plugin.Log.LogWarning($"[Fare] check failed, boarding blocked: {e.Message}");
                    return false;
                }
            }
        }

        // what the gate and the charge both read, so they can never disagree about
        // whether you can afford the trip
        private static List<Item> RoubleStacks(Player player)
        {
            var inv = player?.InventoryController;
            if (inv == null) return new List<Item>();
            return inv.Inventory.GetPlayerItems(EPlayerItems.Equipment)
                .Where(i => i != null && i.TemplateId == RoubleTpl)
                .OrderByDescending(i => i.StackObjectsCount)
                .ToList();
        }

        private static int CarriedRoubles(Player player)
            => RoubleStacks(player).Sum(i => i.StackObjectsCount);

        // internal: the fika compat patch charges through this too (FikaPlayer.vmethod_3
        // is the confirm moment in coop — see IcebreakerFikaCompat)
        //
        // the SANCTIONED op pattern (verified against the assembly + fika source):
        // create with simulate:TRUE (validates + builds the changeset, applies nothing),
        // then dispatch through TryRunNetworkTransaction — which executes it ONCE via
        // vmethod_1, the exact method fika overrides to replicate inventory to peers.
        // BSG's bot looting (BotDeadBodyWork) and fika's own quest item removal both do
        // exactly this. simulate:false applies inline and is INVISIBLE to fika — and
        // combining simulate:false WITH the transaction double-executes (the old
        // chain-door flashing-item bug).
        internal static bool TryTakeFare(Player player, int cost)
        {
            var inv = player.InventoryController;
            if (inv == null) { Plugin.Log.LogWarning("[Fare] no inventory controller"); return false; }

            var stacks = RoubleStacks(player);
            int carried = stacks.Sum(i => i.StackObjectsCount);
            if (carried < cost)
            {
                Notify($"You need {cost:N0} roubles for the crossing ({carried:N0} carried)");
                Plugin.Log.LogWarning($"[Fare] short: {carried}/{cost}");
                return false;
            }

            // validate EVERYTHING before applying ANYTHING (all-or-nothing): each op is
            // simulated against its own throwaway till so simulated ops can't fight
            // over the same grid slot
            int remaining = cost;
            var dispatch = new List<Action>();
            foreach (var stack in stacks)
            {
                if (remaining <= 0) break;
                var fakeStash = Singleton<ItemFactoryClass>.Instance.CreateFakeStash(null);
                var till = new TraderControllerClass(fakeStash, "IcebreakerFare", "IcebreakerFare", true, EOwnerType.ExfilPoint);
                var slot = ((StashItemClass)till.RootItem).Grid.FindLocationForItem(stack);
                if (slot == null) { Plugin.Log.LogWarning("[Fare] till has no room for a stack, aborting"); break; }

                int take = Mathf.Min(remaining, stack.StackObjectsCount);
                if (take >= stack.StackObjectsCount)
                {
                    var m = InteractionsHandlerClass.Move(stack, slot, inv, true);
                    if (m.Failed) { Plugin.Log.LogWarning($"[Fare] move validation failed: {m.Error}"); break; }
                    dispatch.Add(() => inv.TryRunNetworkTransaction(m, r =>
                    { if (!r.Succeed) Plugin.Log.LogWarning($"[Fare] move execution failed post-validation: {r.Error}"); }));
                }
                else
                {
                    var s = InteractionsHandlerClass.SplitExact(stack, take, slot, inv, inv, true);
                    if (s.Failed) { Plugin.Log.LogWarning($"[Fare] split validation failed: {s.Error}"); break; }
                    dispatch.Add(() => inv.TryRunNetworkTransaction(s, r =>
                    { if (!r.Succeed) Plugin.Log.LogWarning($"[Fare] split execution failed post-validation: {r.Error}"); }));
                }
                remaining -= take;
            }

            if (remaining > 0)
            {
                Notify("The smugglers wave you off, the payment did not go through");
                Plugin.Log.LogWarning($"[Fare] validation incomplete, {remaining} short of {cost} — transit blocked, nothing charged");
                return false;
            }

            foreach (var d in dispatch) d();
            Notify($"Paid {cost:N0} roubles for the crossing");
            Plugin.Log.LogWarning($"[Fare] collected {cost} roubles ({dispatch.Count} ops dispatched)");
            return true;
        }

        private static void Notify(string text)
        {
            try
            {
                NotificationManagerClass.DisplayMessageNotification(
                    text, ENotificationDurationType.Long, ENotificationIconType.Default, Color.yellow);
            }
            catch { }
        }
    }
}
