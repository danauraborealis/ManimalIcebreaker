using HarmonyLib;
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
        var harmony = new Harmony("com.manimal.icebreaker.progression");
        foreach (string name in new[] { nameof(QuestHelper.GetClientQuests), nameof(QuestHelper.GetNewlyAccessibleQuestsWhenStartingQuest) })
            harmony.Patch(AccessTools.Method(typeof(QuestHelper), name),
                postfix: new HarmonyMethod(typeof(IcebreakerProgression), nameof(Filter)));
        harmony.Patch(AccessTools.Method(typeof(LocationLifecycleService), nameof(LocationLifecycleService.StartLocalRaidAsync)),
            prefix: new HarmonyMethod(typeof(IcebreakerProgression), nameof(BeforeRaid)));
        return Task.CompletedTask;
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
