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
    ICloner cloner)
    : BotGenerator(logger, hashUtil, randomUtil, databaseService, botInventoryGenerator,
        botLevelGenerator, botEquipmentFilterService, weightedRandomHelper, botHelper,
        seasonalEventService, itemFilterService, botNameService, configServer, cloner)
{
    private readonly ISptLogger<BotGenerator> _log = logger;
    private readonly RandomUtil _randomUtil = randomUtil;

    private static bool _aliveLogged;

    public override BotBase PrepareAndGenerateBot(MongoId sessionId, BotGenerationDetails botGenerationDetails)
    {
        // one-time proof-of-life: if a player's server log LACKS this line, our DI
        // override lost the BotGenerator slot to another mod (the 08-09 field log
        // theory for APBS 2.2.0) — that diagnosis becomes a grep instead of a maybe
        if (!_aliveLogged)
        {
            _aliveLogged = true;
            _log.Info("[Icebreaker] bot firewall generator ACTIVE (DI override holds the BotGenerator slot)");
        }
        IcebreakerPbsMasquerade.Apply(_log);
        var bot = base.PrepareAndGenerateBot(sessionId, botGenerationDetails);
        TryInjectSpecials(bot, botGenerationDetails);
        return bot;
    }

    // (the knight SZ-1 inject that lived here died the same day it shipped — his
    // pockets are too small for the charge. the one-guaranteed-charge-per-raid now
    // lives CLIENT-side in IcebreakerCrew.PlaceChargeSweep: zone-squad aware,
    // backpack-preferring — things this server hook can't see.)

    // WEDGE CARRIES EUROS (user call 08-08): the BlackDiv mod gives bossWedge dollar
    // stacks, but the icebreaker's economy runs on euros (heli fare, quests). tpl
    // swap at generation, stack counts untouched. ids verified in items.json.
    private const string DollarsTpl = "5696686a4bdc2da3298b456a";
    private const string EurosTpl = "569668774bdc2da2298b4568";

    // BD DOGTAG DROP, take two (user call 08-12). take one ADDED a tag via
    // AddItemWithChildrenToEquipmentSlot and never once worked: SPT clears each bot's
    // container cache at the END of inventory generation
    // (BotInventoryGenerator: "Inventory cache isn't needed, clear to save memory"),
    // and we run after that — so every container reports NO_CONTAINERS and the helper
    // falls through to a misleading NO_SPACE. the bots' pockets were never full.
    // (the SZ-1 charge works because it is placed CLIENT-side on a live bot, a
    // different code path entirely — the C-3 keycard drop had the same latent bug and
    // moved client-side with it.)
    //
    // SWAPPING a template needs no container logic at all, so it always works — and
    // the BlackDiv loadout hands every blackDivIb a shot at a Labs access keycard
    // (weight 60 in their pocket pool), an item that has no business on this ship.
    // so the keycard IS the dogtag: swap it in place, green or ferrum at even odds.
    // the keycard's own spawn rate becomes the tag drop rate.
    private const string BdDogtagGreenTpl = "6a461bf82b2264dbe10d0ee6";
    private const string BdDogtagFerrumTpl = "6a461aed7391ab085a093760";
    // wedge is their most formidable fighter and the red tag's own description says
    // so — he always carries one, and it's the only source of red in the raid. his
    // loadout has NO keycard to swap, so the guarantee is placed client-side by
    // IcebreakerCrew (the proven SZ-1 path); this only covers the rare case where he
    // rolls a keycard anyway.
    private const string BdDogtagRedTpl = "6a461c41ec88c6b9a509fb17";
    private const string LabsKeycardTpl = "5c94bbff86f7747ee735c08f";

    // ONE TAG PER BODY (user call 08-12): the pocket pool can hand the same bot more
    // than one keycard, and a naive swap-them-all turns that into a pile of dogtags —
    // which would quietly devalue the barter currency. so: the FIRST keycard becomes
    // the tag, every other keycard is DELETED outright (the whole point was getting
    // labs cards off this ship), and any duplicate tag that somehow survives is culled
    // too, whatever its source.
    private void SwapKeycardForDogtag(BotBase bot, bool isWedge)
    {
        var items = bot.Inventory?.Items;
        if (items is null || !TagsPresent()) return;

        var tagTpls = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { BdDogtagGreenTpl, BdDogtagFerrumTpl, BdDogtagRedTpl };

        Item first = null;
        var doomed = new List<Item>();
        foreach (var it in items)
        {
            if (it is null) continue;
            var tpl = it.Template.ToString();
            bool isCard = string.Equals(tpl, LabsKeycardTpl, StringComparison.OrdinalIgnoreCase);
            bool isTag = tagTpls.Contains(tpl);
            if (!isCard && !isTag) continue;
            if (first is null && isCard) first = it; // only a card can become the tag
            else doomed.Add(it);                     // spare cards + any duplicate tags
        }

        string what = null;
        if (first != null)
        {
            bool green = _randomUtil.GetChance100(50.0);
            what = isWedge ? "red" : (green ? "green" : "ferrum");
            first.Template = new MongoId(isWedge ? BdDogtagRedTpl : (green ? BdDogtagGreenTpl : BdDogtagFerrumTpl));
        }
        foreach (var d in doomed) items.Remove(d);

        if (first != null || doomed.Count > 0)
            _log.Info($"[Icebreaker] {(first != null ? $"labs keycard -> BD {what} dogtag" : "no keycard")} on "
                + $"{(isWedge ? "wedge" : "a black division body")}"
                + (doomed.Count > 0 ? $"; culled {doomed.Count} spare card/tag(s) — one tag per body" : ""));
    }

    // icebreaker raids only (raid-context latch); goons + rogues on vanilla maps stay
    // untouched by construction.
    private void TryInjectSpecials(BotBase bot, BotGenerationDetails details)
    {
        try
        {
            if (!IcebreakerRaidContext.OnIcebreaker || bot?.Inventory == null) return;

            // (the C-3 keycard roll MOVED CLIENT-SIDE 08-12 — IcebreakerCrew.C3KeycardSweep.
            // it was an AddItemWithChildrenToEquipmentSlot call here, which cannot work
            // after generation returns: SPT frees the bot's container cache at the end of
            // inventory generation, so the add always failed with a misleading NO_SPACE.
            // nothing server-side adds items to bots any more — only swaps templates.)

            var role = details?.RoleLowercase;
            bool isWedge = string.Equals(role, "bosswedge", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(role, "blackdivib", StringComparison.OrdinalIgnoreCase) || isWedge)
                SwapKeycardForDogtag(bot, isWedge);
            if (isWedge && bot.Inventory.Items != null)
            {
                int swapped = 0;
                foreach (var it in bot.Inventory.Items)
                    if (it != null && string.Equals(it.Template.ToString(), DollarsTpl, StringComparison.OrdinalIgnoreCase))
                    {
                        it.Template = new MongoId(EurosTpl);
                        swapped++;
                    }
                if (swapped > 0)
                    _log.Info($"[Icebreaker] wedge currency fix — {swapped} dollar stack(s) swapped to euros");
            }
        }
        catch (Exception e)
        {
            _log.Warning($"[Icebreaker] special-item injection failed: {e.Message}");
        }
    }

    // dependency guard for the BD dogtags — resolved once, warned once
    private readonly DatabaseService _db = databaseService;
    private bool? _tagsPresent;

    private bool TagsPresent()
    {
        if (_tagsPresent == null)
        {
            // both, not just the one this roll wants — they ship in the same config,
            // so a half-present set means the dependency is broken either way
            try
            {
                var items = _db.GetItems();
                _tagsPresent = items.ContainsKey(new MongoId(BdDogtagGreenTpl))
                            && items.ContainsKey(new MongoId(BdDogtagFerrumTpl));
            }
            catch { _tagsPresent = false; }
            if (_tagsPresent == false)
                _log.Warning("[Icebreaker] BD dogtag templates not in the item DB (WTT-ContentBackport kordbreach set missing?) "
                    + "— black division bodies will carry no dogtags, and the ragman/skier barters will be unbuyable");
        }
        return _tagsPresent == true;
    }

    // (InjectItem removed 08-12 — see the C-3 note above. every server-side ADD was
    // dead on arrival; what remains here only SWAPS existing templates, which needs no
    // container state and therefore always works.)
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

                    // fast path: the 2.0.0 layout we verified
                    var ri = asm.GetType("ProgressiveBotSystem.Models.RaidInformation");
                    _prop = ri?.GetProperty("RaidLocation", BindingFlags.Public | BindingFlags.Static);

                    // version-adaptive fallback (08-09 field log: APBS 2.2.0 on a player's
                    // server, no masquerade engagement, every vanilla bot dead with
                    // "Map 'Suburbs' not found" — if the type moved, find ANY static
                    // string RaidLocation in their assembly instead of giving up silently)
                    if (_prop == null)
                    {
                        Type[] types;
                        try { types = asm.GetTypes(); }
                        catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
                        foreach (var t in types)
                        {
                            if (t == null) continue;
                            var p = t.GetProperty("RaidLocation", BindingFlags.Public | BindingFlags.Static);
                            if (p != null && p.PropertyType == typeof(string) && p.CanWrite)
                            {
                                _prop = p;
                                log.Info($"[Icebreaker] APBS layout drifted — RaidLocation found on '{t.FullName}' (adaptive search)");
                                break;
                            }
                        }
                    }

                    if (_prop == null)
                        // presence without a hook point is the one state that must be LOUD:
                        // this exact silence cost a player every vanilla bot on the map
                        log.Warning($"[Icebreaker] Progressive Bot System assembly '{n}' is present but no static RaidLocation "
                            + "property was found — the labs masquerade CANNOT protect this raid; expect 'Map Suburbs not found' "
                            + "bot failures. report the APBS version so the shim can be updated");
                    break;
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
