using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace Manimal.Icebreaker.Server;

// the sudak-tudak kit pays the map fare in place of roubles (client side:
// IcebreakerMapFare). content backport ships it special-slot-only but never lets it
// into a special slot, so as shipped it cant leave the stash. late priority — content
// backport's items have to be registered first.
[Injectable(TypePriority = OnLoadOrder.Preload + 90001)]
public class IcebreakerFareKit(TemplateTable templateTable, ISptLogger<IcebreakerFareKit> logger) : IOnLoad
{
    public const string KitTpl = "6a8c4c7999baf8bd5802f7fe";
    public const string PayTheFare = "6b09d210a3f2b67dccada501"; // ragman, pays out the kits
    private const string PocketsParent = "557596e64bdc2dc2118b4571";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            int slots = Apply(templateTable.Items, templateTable.Quests);
            if (slots < 0)
                logger.Warning("[Icebreaker] Sudak-Tudak kit not in the item DB (older WTT-ContentBackport?) "
                    + "- Pay the Fare! removed, the crossing is roubles only");
            else
                logger.Info($"[Icebreaker] Sudak-Tudak kit allowed in {slots} special slot(s)");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.Warning($"[Icebreaker] fare kit setup failed: {e.Message}");
        }
        return Task.CompletedTask;
    }

    // -1 = no kit template: the quest would reward an item nobody has, so it goes too
    public static int Apply(Dictionary<MongoId, TemplateItem> items, Dictionary<MongoId, Quest> quests)
    {
        var kit = new MongoId(KitTpl);
        if (!items.ContainsKey(kit))
        {
            quests.Remove(new MongoId(PayTheFare));
            return -1;
        }

        // by parent, not the two stock ids — covers pockets other mods add
        int slots = 0;
        foreach (var item in items.Values)
        {
            if (item.Parent != PocketsParent || item.Properties?.Slots == null) continue;
            foreach (var slot in item.Properties.Slots)
            {
                if (slot.Name?.StartsWith("SpecialSlot", StringComparison.Ordinal) != true) continue;
                var filter = slot.Properties?.Filters?.FirstOrDefault()?.Filter;
                if (filter == null) continue;
                filter.Add(kit);
                slots++;
            }
        }
        return slots;
    }
}
