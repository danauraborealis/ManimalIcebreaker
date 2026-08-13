using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using System.Threading.Tasks;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace Manimal.Icebreaker.Server;

// THE BLACK DIVISION GEAR CRATE (db/CustomItems/BlackDivisionCrate.json). the item is
// a real RandomLootContainer, so the client opens it through the vanilla path and the
// server rolls it with the vanilla weighted generator — that keeps the draw random,
// duplicates and all, which is the behaviour asked for.
//
// the one thing vanilla CANNOT do is money. LootGenerator.GetRandomLootContainerLoot
// builds every reward as `new() { Id, Template }` with NO Upd, and OpenRandomLootContainer
// hands that straight to AddItemsToStash with no stack fixup anywhere in between
// (traced through both, SPT 4.0.13) — a roubles entry in a rewardTplPool therefore
// pays out a stack of ONE. RewardDetails has no field that can express a stack size.
// so the stack is ours to set, in a postfix.
//
// identifying OUR crate in that postfix is the fiddly part: the method is handed a
// RewardDetails and nothing else, no tpl. so we keep the exact RewardDetails instance
// we registered and match it by REFERENCE — the config dictionary hands the same
// object back to the generator, and no other container can ever be that instance.
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 4)]
public class IcebreakerCrateRewards(
    ConfigServer configServer,
    RandomUtil randomUtil,
    ISptLogger<IcebreakerCrateRewards> logger) : IOnLoad
{
    internal const string CrateTpl = "6a7dfef811aec9601b11cc1c";
    private const string RoublesTpl = "5449016a4bdc2d6f028b456f";

    // 50k-100k per the user call. inclusive range, rounded to the nearest 1k so the
    // payout reads like a cash bundle instead of a till receipt.
    private const int RoublesMin = 50000;
    private const int RoublesMax = 100000;

    // how many draws the vanilla generator makes. five is the "full set" the item
    // description promises — helmet, armor, rig, backpack, protective gear — though
    // the draws are INDEPENDENT (no dedupe in GetRandomLootContainerLoot), so a given
    // crate can repeat a category. that randomness is intentional here.
    private const int RewardCount = 5;

    // weights are per-entry, not per-category, so the category totals below are what
    // actually decide the feel: gear ~85%, money ~7%, tags ~8%.
    private static readonly Dictionary<string, double> Pool = new()
    {
        // helmets
        { "5ea17ca01412a1425304d1c0", 100 }, // Diamond Age Bastion (Black)
        { "5a154d5cfcdbcb001a3b00da", 100 }, // Ops-Core FAST MT Super High Cut (Black)
        { "65709d2d21b9f815e208ff95", 100 }, // Diamond Age NeoSteel High Cut (Black)

        // headsets and hearing protection
        { "66b5f6985891c84aab75ca76", 100 }, // Peltor ComTac VI (Coyote Brown)
        { "69c1632ca078dbb51e0e31b5", 100 }, // Peltor ComTac VI (Black)
        { "628e4e576d783146b124c64d", 100 }, // Peltor ComTac IV Hybrid (Coyote)
        { "5c165d832e2216398b5a7e36", 100 }, // Peltor Tactical Sport
        { "69413241b1ce1e5fbb09ed0a", 100 }, // CENS ProFlex DX5 earplug

        // eyewear
        { "5c1a1cc52e221602b3136e3d", 100 }, // Oakley SI M Frame
        { "5d6d2e22a4b9361bd5780d05", 100 }, // Oakley SI Gascan
        { "62a09e410b9d3c46de5b6e78", 100 }, // JohnB Liquid DNB
        { "689b3fffa1295e322207a677", 100 }, // Gatorz Specter MILSPEC
        { "68bede896b0736bebc012ad7", 100 }, // Sunglasses (Deal With It)

        // face cover
        { "689b880fff8b4adc420f5b56", 100 }, // Avon M53A1 gas mask
        { "689b404db49f27df1c0873f6", 100 }, // Gentex Ops-Core SOTR respirator

        // backpacks
        { "68947a8ce4bf255d1b0ca759", 80 },  // TT Modular Pack 45 Plus
        { "68947ab5a733b1602007e2fe", 80 },  // Mystery Ranch 2 Day Assault (Black)
        { "68947ad3e4bf255d1b0ca75c", 80 },  // Mystery Ranch NICE Frame Load Sling
        { "5df8a4d786f77412672a1e3b", 80 },  // 6Sh118 raid backpack (EMR)
        { "5ab8ebf186f7742d8b372e80", 80 },  // SSO Attack 2 raid backpack (Khaki)

        // plate carriers and rigs
        { "689479eb30cc5ba7be00f5ff", 80 },  // Spiritus LV-119
        { "689479a4a733b1602007e2eb", 80 },  // Spiritus LV-119 (variant)
        { "68947a4be4bf255d1b0ca746", 80 },  // First Spear Siege-R M.A.S.S.
        { "689479cb47e5acd1e10be986", 80 },  // Ferro Concepts FCPC V5

        // money and barter currency. red is deliberately the rarest thing in the pool
        // (user call 08-13): wedge is still its primary source, this is a second one.
        { RoublesTpl, 150 },                 // stack set in the postfix, NOT 1 rouble
        { "6a461aed7391ab085a093760", 100 }, // BD ferrum dogtag (common)
        { "6a461bf82b2264dbe10d0ee6", 55 },  // BD green dogtag (rare)
        { "6a461c41ec88c6b9a509fb17", 15 },  // BD red dogtag (very rare)
    };

    // the registered instance, matched by reference in the postfix
    private static RewardDetails _details;
    private static RandomUtil _rng;
    private static ISptLogger<IcebreakerCrateRewards> _log;

    public Task OnLoad()
    {
        _rng = randomUtil;
        _log = logger;

        var inventory = configServer.GetConfig<InventoryConfig>();
        _details = new RewardDetails
        {
            RewardCount = RewardCount,
            FoundInRaid = true,
            RewardTplPool = Pool.ToDictionary(kv => new MongoId(kv.Key), kv => kv.Value),
        };
        inventory.RandomLootContainers[new MongoId(CrateTpl)] = _details;

        try
        {
            var target = AccessTools.Method(typeof(LootGenerator), nameof(LootGenerator.GetRandomLootContainerLoot));
            if (target == null)
            {
                logger.Error("[Icebreaker] LootGenerator.GetRandomLootContainerLoot not found — the BD crate will pay out 1 rouble instead of a stack");
            }
            else
            {
                new Harmony("com.manimal.icebreaker.craterewards").Patch(
                    target,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(IcebreakerCrateRewards), nameof(StackMoneyPostfix))));
            }
        }
        catch (Exception e)
        {
            logger.Error($"[Icebreaker] BD crate money patch failed ({e.Message}) — crate still works, roubles will drop as a single rouble");
        }

        logger.Info($"[Icebreaker] BD gear crate registered: {Pool.Count} pool entries, {RewardCount} draws, roubles {RoublesMin:N0}-{RoublesMax:N0}");
        return Task.CompletedTask;
    }

    // reference match, so this is a no-op for every other random loot container in the
    // game — including any another mod adds later.
    private static void StackMoneyPostfix(RewardDetails rewardContainerDetails, List<List<Item>> __result)
    {
        try
        {
            if (_details == null || !ReferenceEquals(rewardContainerDetails, _details) || __result == null) return;

            foreach (var reward in __result)
            {
                var root = reward?.FirstOrDefault();
                if (root == null || !string.Equals(root.Template.ToString(), RoublesTpl, StringComparison.OrdinalIgnoreCase)) continue;

                int amount = _rng.GetInt(RoublesMin, RoublesMax);
                amount = (int)Math.Round(amount / 1000.0) * 1000;

                root.Upd ??= new Upd();
                root.Upd.StackObjectsCount = amount;
                _log?.Debug($"[Icebreaker] BD crate paid {amount:N0} roubles");
            }
        }
        catch (Exception e)
        {
            // a throw here would surface as a failed container open, which is far worse
            // than a short payout
            _log?.Warning($"[Icebreaker] BD crate money stacking failed: {e.Message}");
        }
    }
}
