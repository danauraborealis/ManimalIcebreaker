using System.Reflection;
using EFT.Quests;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Manimal.Icebreaker
{
    // The template must reach the client BEFORE discovery: native FindItem connects
    // to successful inventory add/remove events in both the stash and the raid.
    // Keep that normal event/notification path, but latch discovery once earned.
    internal static class IcebreakerFlyerUnlock
    {
        internal static void PreserveDiscovery(Quest quest, ConditionFindItem condition, ref int count)
        {
            if (quest.Id != IcebreakerBoreas.QuestId || condition.id.ToString() != IcebreakerBoreas.DiscoveryId) return;
            if (quest.CompletedConditions.Contains(condition.id) || HasAcceptedProgress(quest.QuestStatus))
                count = 1;
        }

        private static bool HasAcceptedProgress(EQuestStatus status)
            => status == EQuestStatus.Started || status == EQuestStatus.AvailableForFinish ||
               status == EQuestStatus.Success || status == EQuestStatus.Fail || status == EQuestStatus.FailRestartable;

        internal sealed class Patch_BackendCount : ModulePatch
        {
            protected override MethodBase GetTargetMethod()
                => AccessTools.Method(typeof(ConditionsConnectorsManagerQuestClientBackend), "GetFindItemConditionCount");
            [PatchPostfix]
            private static void Postfix(Quest quest, ConditionFindItem condition, ref int __result)
                => PreserveDiscovery(quest, condition, ref __result);
        }

        internal sealed class Patch_GameCount : ModulePatch
        {
            protected override MethodBase GetTargetMethod()
                => AccessTools.Method(typeof(ConditionsConnectorsManagerQuestClientGame), "GetFindItemConditionCount");
            [PatchPostfix]
            private static void Postfix(Quest quest, ConditionFindItem condition, ref int __result)
                => PreserveDiscovery(quest, condition, ref __result);
        }

        internal sealed class Patch_Visibility : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => AccessTools.PropertyGetter(typeof(Quest), nameof(Quest.IsVisible));
            [PatchPostfix]
            private static void Postfix(Quest __instance, ref bool __result)
            {
                if (__instance.Id == IcebreakerBoreas.QuestId &&
                    (__instance.QuestStatus == EQuestStatus.Locked || __instance.QuestStatus == EQuestStatus.AvailableAfter))
                    __result = false;
            }
        }
    }
}
