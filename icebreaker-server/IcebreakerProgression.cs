using HarmonyLib;
using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Quest;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services.InRaid;

namespace Manimal.Icebreaker.Server;

// Filtering only /client/quest/list misses quests delivered in item-event
// responses (including the delta produced when handing in a predecessor).
[Injectable(TypePriority = OnLoadOrder.Preload + 7)]
public class IcebreakerProgression(ProfileHelper profileHelper) : IOnLoad
{
    private static ProfileHelper profiles = null!;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        profiles = profileHelper;
        new QuestListPatch().Enable();
        new QuestStartDeltaPatch().Enable();
        new QuestFailDeltaPatch().Enable();
        new BeforeRaidPatch().Enable();
        return Task.CompletedTask;
    }

    private sealed class QuestListPatch() : AbstractPatch(BuildInfo.ModGuid + ".progression")
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(QuestHelper), nameof(QuestHelper.GetClientQuests));
        [PatchPostfix]
        private static void Postfix(MongoId sessionId, List<Quest> __result) => Filter(sessionId, __result);
    }

    private sealed class QuestStartDeltaPatch() : AbstractPatch(BuildInfo.ModGuid + ".progression")
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(QuestHelper), nameof(QuestHelper.GetNewlyAccessibleQuestsWhenStartingQuest));
        [PatchPostfix]
        private static void Postfix(MongoId sessionId, List<Quest> __result) => Filter(sessionId, __result);
    }

    private sealed class QuestFailDeltaPatch() : AbstractPatch(BuildInfo.ModGuid + ".progression")
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(QuestHelper), nameof(QuestHelper.FailedUnlocked));
        [PatchPostfix]
        private static void Postfix(MongoId sessionId, List<Quest> __result) => Filter(sessionId, __result);
    }

    private sealed class BeforeRaidPatch() : AbstractPatch(BuildInfo.ModGuid + ".progression")
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LocationLifecycleService), nameof(LocationLifecycleService.StartLocalRaidAsync));
        [PatchPrefix]
        private static void Prefix(MongoId sessionId) => BeforeRaid(sessionId);
    }

    private static void Filter(MongoId sessionId, List<Quest> __result)
        => IcebreakerFlyerGateRouter.FilterQuests(__result, profiles.GetPmcProfile(sessionId),
            sessionId.ToString(), IcebreakerRaidWatchRouter.Visits);

    // Capture completed prerequisites before departure, even if the client has
    // not requested another quest list since the hand-in. This crossing then counts.
    private static void BeforeRaid(MongoId sessionId)
        => IcebreakerFlyerGateRouter.MarkFinishedQuests(profiles.GetPmcProfile(sessionId),
            sessionId.ToString(), IcebreakerRaidWatchRouter.Visits);
}
