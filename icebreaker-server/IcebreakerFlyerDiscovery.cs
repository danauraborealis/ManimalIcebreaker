using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Utils;

namespace Manimal.Icebreaker.Server;

public static class IcebreakerFlyerDiscovery
{
    // Use the quest's saved completedConditions, not a machine-wide flag. Raid-end
    // already merges this field, so even a flyer lost on death retains discovery.
    public static bool Observe(PmcData? profile)
    {
        if (profile == null) return false;
        QuestStatus? entry = null;
        if (profile.Quests != null)
            foreach (var quest in profile.Quests)
                if (quest.QId == IcebreakerBoreas.QuestId) { entry = quest; break; }
        bool discovered = entry?.CompletedConditions?.Contains(IcebreakerBoreas.DiscoveryId) == true ||
            entry?.Status is QuestStatusEnum.Started or QuestStatusEnum.AvailableForFinish or
                QuestStatusEnum.Success or QuestStatusEnum.Fail or QuestStatusEnum.FailRestartable;
        if (!discovered && profile.Inventory?.Items != null)
            foreach (var item in profile.Inventory.Items)
                if (item.Template == IcebreakerBoreas.FlyerId && (item.Upd?.StackObjectsCount ?? 1) > 0)
                { discovered = true; break; }
        if (!discovered) return false;
        if (entry == null)
        {
            entry = new QuestStatus { QId = IcebreakerBoreas.QuestId, Status = QuestStatusEnum.AvailableForStart,
                StartTime = 0, StatusTimers = [] };
            (profile.Quests ??= []).Add(entry);
        }
        entry.CompletedConditions ??= [];
        if (!entry.CompletedConditions.Contains(IcebreakerBoreas.DiscoveryId))
            entry.CompletedConditions.Add(IcebreakerBoreas.DiscoveryId);
        if (entry.Status is QuestStatusEnum.Locked or QuestStatusEnum.AvailableAfter)
        {
            entry.Status = QuestStatusEnum.AvailableForStart;
            entry.AvailableAfter = 0;
        }
        return true;
    }
}

// The core item handler has committed the purchase/transfer by this point. Capture
// menu acquisitions now, even if the player sells the flyer before another login.
[Injectable(TypePriority = OnLoadOrder.Routers + 1)]
public sealed class IcebreakerFlyerInventoryRouter(JsonUtil json, ProfileHelper profiles) : StaticRouter(json,
[
    new RouteAction<EmptyRequestData>("/client/game/profile/items/moving", (_, _, sessionId, output, _) =>
    {
        IcebreakerFlyerDiscovery.Observe(profiles.GetPmcProfile(sessionId));
        return new ValueTask<string>(output ?? string.Empty);
    })
]);
