using System;
using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;

namespace Manimal.Icebreaker.Server;

// PBS MASQUERADE, applied where it cannot be raced. the loot-firewall call sets the
// static during StartLocalRaid — but PBS's own router hook is a POST-processing chain
// (it receives the computed output and returns it), so it runs AFTER StartLocalRaid
// and stomps RaidInformation.RaidLocation back to 'Suburbs'. bot generation arrives on
// a LATER request and read the stomped value: every wave bot failed with
// "Map 'Suburbs' not found" while loot worked fine (measured, 2026-08-03).
//
// this DI override of BotGenerator re-applies the masquerade at the top of
// PrepareAndGenerateBot — per bot, inside the very call that reads the static, with
// nothing scheduled between. PBS overrides different generator classes
// (CustomBotWeaponGenerator / CustomBotEquipmentModGenerator), so no DI clash.
[Injectable]
public class IcebreakerBotFirewall(
    ISptLogger<BotGenerator> logger,
    HashUtil hashUtil,
    RandomUtil randomUtil,
    DatabaseService databaseService,
    BotInventoryGenerator botInventoryGenerator,
    BotLevelGenerator botLevelGenerator,
    BotEquipmentFilterService botEquipmentFilterService,
    WeightedRandomHelper weightedRandomHelper,
    BotHelper botHelper,
    SeasonalEventService seasonalEventService,
    ItemFilterService itemFilterService,
    BotNameService botNameService,
    ConfigServer configServer,
    BotGeneratorHelper botGeneratorHelper,
    ICloner cloner)
    : BotGenerator(logger, hashUtil, randomUtil, databaseService, botInventoryGenerator,
        botLevelGenerator, botEquipmentFilterService, weightedRandomHelper, botHelper,
        seasonalEventService, itemFilterService, botNameService, configServer, cloner)
{
    private readonly ISptLogger<BotGenerator> _log = logger;
    private readonly BotGeneratorHelper _botGeneratorHelper = botGeneratorHelper;
    private readonly RandomUtil _randomUtil = randomUtil;

    public override BotBase PrepareAndGenerateBot(MongoId sessionId, BotGenerationDetails botGenerationDetails)
    {
        IcebreakerPbsMasquerade.Apply(_log);
        var bot = base.PrepareAndGenerateBot(sessionId, botGenerationDetails);
        TryInjectSpecials(bot, botGenerationDetails);
        return bot;
    }

    // (the knight SZ-1 inject that lived here died the same day it shipped — his
    // pockets are too small for the charge. the one-guaranteed-charge-per-raid now
    // lives CLIENT-side in IcebreakerCrew.PlaceChargeSweep: zone-squad aware,
    // backpack-preferring — things this server hook can't see.)

    // C-3 KEYCARD DROP (user call 08-03): the Boreas Compartment C-3 keycard
    // (WTT-ContentBackport 1.0 item) as an EXTREMELY RARE find on any rogue's body —
    // ~1% per rogue, so with the 8-12 crew roughly one raid in ten sees one at all.
    private const string C3KeycardTpl = "69bb3f7df94327bc0f0230c9";
    private const double C3ChancePercent = 1.0;

    // icebreaker raids only (raid-context latch); goons + rogues on vanilla maps stay
    // untouched by construction.
    private void TryInjectSpecials(BotBase bot, BotGenerationDetails details)
    {
        try
        {
            if (!IcebreakerRaidContext.OnIcebreaker || bot?.Inventory == null) return;

            if (string.Equals(details?.RoleLowercase, "exusec", StringComparison.OrdinalIgnoreCase)
                && _randomUtil.GetChance100(C3ChancePercent))
                InjectItem(bot, C3KeycardTpl, "C-3 keycard on a rogue", warnOnFail: false);
        }
        catch (Exception e)
        {
            _log.Warning($"[Icebreaker] special-item injection failed: {e.Message}");
        }
    }

    private void InjectItem(BotBase bot, string tpl, string label, bool warnOnFail)
    {
        var root = new MongoId();
        var item = new Item { Id = root, Template = new MongoId(tpl) };
        var result = _botGeneratorHelper.AddItemWithChildrenToEquipmentSlot(
            bot.Id ?? new MongoId(),
            [EquipmentSlots.Pockets, EquipmentSlots.Backpack, EquipmentSlots.TacticalVest],
            root,
            item.Template,
            [item],
            bot.Inventory);
        if (result == ItemAddedResult.SUCCESS)
            _log.Info($"[Icebreaker] {label} — injected");
        else if (warnOnFail)
            _log.Warning($"[Icebreaker] couldn't fit {label} ({result})");
    }
}

// the shared shim: if PBS's static says our map, present it as labs instead. the
// condition doubles as the gate — only an icebreaker raid ever puts 'Suburbs' there,
// so no location plumbing is needed and vanilla raids are untouched by construction.
internal static class IcebreakerPbsMasquerade
{
    private static bool _logged;
    private static PropertyInfo _prop;
    private static bool _resolved;

    internal static void Apply<T>(ISptLogger<T> log)
    {
        try
        {
            if (!_resolved)
            {
                _resolved = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var n = asm.GetName().Name ?? "";
                    if (n.IndexOf("progressivebotsystem", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("ProgressiveBotSystem", StringComparison.Ordinal) < 0) continue;
                    var ri = asm.GetType("ProgressiveBotSystem.Models.RaidInformation");
                    _prop = ri?.GetProperty("RaidLocation", BindingFlags.Public | BindingFlags.Static);
                    if (_prop != null) break;
                }
            }
            if (_prop == null || _prop.PropertyType != typeof(string)) return; // PBS absent or drifted

            if (string.Equals(_prop.GetValue(null) as string, "Suburbs", StringComparison.OrdinalIgnoreCase))
            {
                _prop.SetValue(null, "laboratory"); // exact key casing from their map switch
                if (!_logged)
                {
                    _logged = true;
                    log.Info("[Icebreaker] Progressive Bot System detected — presenting the icebreaker as "
                        + "'laboratory' to it (tight-interior CQB, high-tier gear tables) so tiered bots "
                        + "generate instead of throwing on the unknown map");
                }
            }
        }
        catch (Exception e)
        {
            if (!_logged) { _logged = true; log.Warning($"[Icebreaker] PBS masquerade failed: {e.Message}"); }
        }
    }
}
