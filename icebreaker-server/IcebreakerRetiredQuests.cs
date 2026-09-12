using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace Manimal.Icebreaker.Server;

public static class IcebreakerRetiredQuests
{
    private static readonly HashSet<MongoId> Ids =
    [
        new("6a757a2f478c184bd220c458"), // Pack Mule
        new("6a757a2f478c184bd220c461"), // Pack Mule Part 2
        new("6a752a6bc498772c6a150baf"), // Fresh Stock
        new("6a752fa8c498772c6a150bbc"), // Fresh Stock Part 2
    ];

    public static bool Contains(MongoId id) => Ids.Contains(id);

    public static void RemoveFromDatabase(Dictionary<MongoId, Quest> quests, IEnumerable<Trader> traders)
    {
        foreach (var id in Ids) quests.Remove(id);
        foreach (var trader in traders)
        {
            if (trader.QuestAssort == null) continue;
            foreach (var locks in trader.QuestAssort.Values)
            {
                var removed = new List<MongoId>();
                foreach (var pair in locks)
                    if (Contains(pair.Value)) removed.Add(pair.Key);
                foreach (var id in removed) locks.Remove(id);
            }
        }
    }
}
