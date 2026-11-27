using HarmonyLib;
using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators.RepeatableQuests;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Json;

namespace Manimal.Icebreaker.Server;

// ragman's daily kit trade (retail's "Daily_icebreaker" slot): hand over tools or
// materials found in raid, get kits. the slot is a copy of the stock daily narrowed
// to one ragman completion task, so spt builds, stores, re-rolls and pays it like any
// other daily — the postfix only swaps what it asks for, what it pays and its text.
[Injectable(TypePriority = OnLoadOrder.Preload + 90002)]
public class IcebreakerKitTask(
    QuestConfig questConfig,
    TemplateTable templateTable,
    ItemHelper itemHelper,
    ICloner cloner,
    ISptLogger<IcebreakerKitTask> logger) : IOnLoad
{
    public const string SlotName = "Daily_icebreaker";
    public const string SlotId = "6b09d210a3f2b67dccada521";
    public const string TextId = "6b09d210a3f2b67dccada520"; // locale keys, ragman/Locales
    public const int KitsPerTask = 2; // what the retail task was seen paying
    private const string Ragman = "5ac3b934156ae10c4430e83c";

    // tool, building material, fuel — what the retail task was seen asking for
    private static readonly string[] RequestClasses =
        ["57864bb7245977548b3b66c2", "57864ada245977548638de91", "57864e4c24597754843f8723"];
    private const double MinPrice = 3000, MaxPrice = 65000, PairPrice = 20000;

    private static TemplateTable templates = null!;
    private static ItemHelper prices = null!;
    private static ISptLogger<IcebreakerKitTask> log = null!;
    private static readonly Random Rng = new();

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!templateTable.Items.ContainsKey(new MongoId(IcebreakerFareKit.KitTpl))) return Task.CompletedTask;
            templates = templateTable;
            prices = itemHelper;
            log = logger;
            if (!AddSlot(questConfig, cloner))
            {
                logger.Warning("[Icebreaker] no stock daily with a ragman completion task to copy - kit task not added");
                return Task.CompletedTask;
            }
            new AccessPatch().Enable();
            new GeneratePatch().Enable();
            logger.Info($"[Icebreaker] ragman kit task registered ({RequestPool().Count} requestable items)");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.Warning($"[Icebreaker] kit task setup failed: {e.Message}");
        }
        return Task.CompletedTask;
    }

    public static bool AddSlot(QuestConfig config, ICloner cloner)
    {
        if (config.RepeatableQuests.Any(r => r.Name == SlotName)) return true;
        var daily = config.RepeatableQuests.FirstOrDefault(r => r.Name == "Daily");
        var ragman = daily?.TraderWhitelist.FirstOrDefault(t => t.TraderId == Ragman && t.QuestTypes.Contains("Completion"));
        if (daily == null || ragman == null) return false;

        var slot = cloner.Clone(daily)!;
        var trader = cloner.Clone(ragman)!;
        trader.QuestTypes = ["Completion"];
        slot.Id = new MongoId(SlotId);
        slot.Name = SlotName;
        slot.Types = ["Completion"];
        slot.NumQuests = 1;
        slot.MinPlayerLevel = 1; // AccessPatch decides instead
        slot.TraderWhitelist = [trader];
        config.RepeatableQuests.Add(slot);
        return true;
    }

    // ragman unlocked matters: spt draws the trader from the unlocked ones and
    // dereferences a null task when there are none
    public static bool Unlocked(PmcData? pmc)
        => pmc?.Quests?.Any(q => q.QId.ToString() == IcebreakerFareKit.PayTheFare && q.Status == QuestStatusEnum.Success) == true
           && pmc.TradersInfo != null && pmc.TradersInfo.TryGetValue(Ragman, out var ragman) && ragman.Unlocked == true;

    // requestTpl null keeps whatever spt asked for
    public static void Shape(RepeatableQuest quest, string? requestTpl, int requestCount)
    {
        var finish = quest.Conditions?.AvailableForFinish;
        if (requestTpl != null && finish is { Count: > 0 })
        {
            var ask = finish[0];
            ask.Target = new ListOrT<string>([requestTpl], null);
            ask.Value = requestCount;
            ask.MinDurability = 0;
            ask.MaxDurability = 100;
            ask.OnlyFoundInRaid = true;
            finish.RemoveRange(1, finish.Count - 1);
        }

        // xp stays, the rest of the generated payout is replaced by the kit
        var payout = new List<Reward>();
        if (quest.Rewards != null && quest.Rewards.TryGetValue("Success", out var generated) && generated != null)
            payout.AddRange(generated.Where(r => r.Type == RewardType.Experience));
        var kits = new List<Item>();
        for (int i = 0; i < KitsPerTask; i++)
            kits.Add(new Item { Id = new MongoId(), Template = IcebreakerFareKit.KitTpl, Upd = new Upd { StackObjectsCount = 1 } });
        payout.Add(new Reward
        {
            Id = new MongoId(),
            Unknown = false,
            GameMode = Array.Empty<string>(),
            AvailableInGameEditions = new HashSet<string>(),
            Index = payout.Count,
            Target = kits[0].Id,
            Value = KitsPerTask,
            IsEncoded = false,
            FindInRaid = false,
            Type = RewardType.Item,
            Items = kits
        });
        quest.Rewards ??= new Dictionary<string, List<Reward>>();
        quest.Rewards["Success"] = payout;

        quest.Description = TextId + " description";
        quest.SuccessMessageText = TextId + " successMessageText";
        quest.ChangeQuestMessageText = TextId + " changeQuestMessageText";
    }

    private static List<(string Tpl, double Price)> RequestPool()
    {
        var pool = new List<(string, double)>();
        foreach (var item in templates.Items.Values)
        {
            if (item.Type != "Item" || item.Properties?.QuestItem == true) continue;
            if (!RequestClasses.Contains(item.Parent.ToString())) continue;
            double price = prices.GetItemPrice(item.Id) ?? 0;
            if (price >= MinPrice && price <= MaxPrice) pool.Add((item.Id.ToString(), price));
        }
        return pool;
    }

    private sealed class AccessPatch() : AbstractPatch(BuildInfo.ModGuid + ".kittask")
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(RepeatableQuestController), "CanProfileAccessRepeatableQuests");

        [PatchPostfix]
        private static void Postfix(RepeatableQuestConfig repeatableConfig, PmcData pmcData, ref bool __result)
        {
            if (repeatableConfig?.Name != SlotName) return;
            try { __result = Unlocked(pmcData); }
            catch { __result = false; }
        }
    }

    private sealed class GeneratePatch() : AbstractPatch(BuildInfo.ModGuid + ".kittask")
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(CompletionQuestGenerator), nameof(CompletionQuestGenerator.Generate));

        [PatchPostfix]
        private static void Postfix(RepeatableQuestConfig repeatableConfig, RepeatableQuest? __result)
        {
            if (__result is null || repeatableConfig?.Name != SlotName) return;
            try
            {
                var pool = RequestPool();
                if (pool.Count == 0) { Shape(__result, null, 0); return; }
                var (tpl, price) = pool[Rng.Next(pool.Count)];
                Shape(__result, tpl, price <= PairPrice ? 2 : 1);
            }
            catch (Exception e)
            {
                log.Warning($"[Icebreaker] kit task left as generated: {e.Message}");
            }
        }
    }
}
