using Comfort.Common;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.Players;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker.Fika
{
    // TEMPORARY door-sync diagnostic (07-28): doors don't replicate between peers on
    // icebreaker in either direction, with zero errors on either side. fika sends
    // door interactions as WorldInteractionPacket keyed by Door.Id and resolves them
    // via GameWorld.FindDoor — these two patches log the SEND (id + world position)
    // and the RECEIVE (id + resolved? + position) so one raid shows whether packets
    // flow, resolve, and land on the SAME physical door on both machines. remove
    // once the door bug is fixed.
    [HarmonyPatch(typeof(FikaPlayer), "vmethod_1", typeof(WorldInteractiveObject), typeof(InteractionResult))]
    internal static class Patch_DoorSendDiag
    {
        [HarmonyPostfix]
        private static void Postfix(FikaPlayer __instance, WorldInteractiveObject door)
        {
            try
            {
                if (!IceGate.On || door == null || !__instance.IsYourPlayer) return;
                FikaAddonPlugin.Log.LogDebug($"[DoorDiag] SEND execute id='{door.Id}' pos={door.transform.position} forceLocal={door.ForceLocalInteraction}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(WorldInteractionPacket), nameof(WorldInteractionPacket.Execute))]
    internal static class Patch_DoorRecvDiag
    {
        [HarmonyPrefix]
        private static void Prefix(WorldInteractionPacket __instance, FikaPlayer player)
        {
            try
            {
                if (!IceGate.On) return;
                var door = Singleton<GameWorld>.Instance?.FindDoor(__instance.InteractiveId);
                string extra = "";
                if (door == null)
                {
                    // registry miss — does ANY door component carry this id? distinguishes
                    // 'registry built before the id stamp' (component found) from
                    // 'this peer stamped a different mapping' (no component at all)
                    foreach (var d in Object.FindObjectsOfType<EFT.Interactive.Door>())
                        if (d != null && d.Id == __instance.InteractiveId)
                        { extra = $" COMPONENT-EXISTS at {d.transform.position} (registry stale!)"; break; }
                    if (extra == "") extra = " NO-COMPONENT (id mapping diverged!)";
                }
                FikaAddonPlugin.Log.LogDebug($"[DoorDiag] RECV id='{__instance.InteractiveId}' stage={__instance.InteractionStage} resolved={(door != null)}"
                    + (door != null ? $" pos={door.transform.position} activeEnabled={door.isActiveAndEnabled} forceLocal={door.ForceLocalInteraction}" : extra)
                    + $" byPlayer={(player != null ? player.Profile?.Nickname : "null")}");
            }
            catch (System.Exception e) { FikaAddonPlugin.Log.LogDebug($"[DoorDiag] recv diag failed: {e.Message}"); }
        }
    }
}
