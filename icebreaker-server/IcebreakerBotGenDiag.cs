using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Bot;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Common.Models.Logging;

namespace Manimal.Icebreaker.Server;

// Originally a bot-generation diagnostic tap. Request/response logging is retired,
// but the hooks remain necessary for APBS compatibility and special-item fallback.
[Injectable(TypePriority = OnLoadOrder.Preload + 91000)]
public class IcebreakerBotGenDiag(
    ISptLogger<IcebreakerBotGenDiag> logger,
    SPTarkov.Server.Core.Utils.RandomUtil randomUtil,
    TemplateTable templateTable) : IOnLoad
{
    private static ISptLogger<IcebreakerBotGenDiag>? _log;
    private static SPTarkov.Server.Core.Utils.RandomUtil? _rng;
    private static TemplateTable? _db;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _log = logger;
        _rng = randomUtil;
        _db = templateTable;
        try
        {
            var h = new Harmony("com.manimal.icebreaker.botgendiag");
            h.Patch(AccessTools.Method(typeof(BotController), nameof(BotController.Generate)),
                prefix: new HarmonyMethod(typeof(IcebreakerBotGenDiag), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(IcebreakerBotGenDiag), nameof(Postfix)));
            if (!Harmony.GetPatchInfo(AccessTools.Method(typeof(BotGenerator), nameof(BotGenerator.PrepareAndGenerateBot))).Owners.Contains("com.manimal.icebreaker.botfirewall"))
                logger.Warning("[Icebreaker] generator hook missing; special-item fallback remains active");
        }
        catch (Exception e)
        {
            logger.Warning($"[Icebreaker] bot-generation compatibility hook failed: {e.Message}");
        }
        return Task.CompletedTask;
    }

    private static void Prefix(GenerateBotsRequestData request)
    {
        // THE MASQUERADE'S SECOND HOME (08-13 field log, jagrr: rogues/goons/scavs all
        // gone with APBS installed, BD unaffected). IcebreakerBotFirewall applies it at
        // the top of PrepareAndGenerateBot, but that is a DI override of BotGenerator
        // and APBS overrides the SAME class — last mod registered wins the slot, and
        // when APBS wins, our generator never runs, so the masquerade never fires. that
        // log had ZERO "bot firewall generator ACTIVE" lines and 13,080 bots dead with
        // "Map 'Suburbs' not found": every assault, marksman, exUsec and bossKnight,
        // while blackDivIb sailed through untouched because APBS ignores custom roles.
        //
        // this tap is a HARMONY patch on BotController.Generate, which no DI race can
        // displace, and it runs once per request before any bot is generated. applying
        // here as well costs a property read on a path that already exists, and it is
        // idempotent (Apply no-ops unless the value currently reads "Suburbs").
        if (_log != null) IcebreakerPbsMasquerade.Apply(_log);

    }

    // Materialize the lazy generation result once for special-item fallback, then
    // pass it onward without re-enumerating the generation pipeline.
    private static void Postfix(ref Task<IEnumerable<BotBase?>> __result, GenerateBotsRequestData request)
    {
        try { __result = CountAndPass(__result, request); }
        catch { }
    }

    private static bool _slotLossReported;

    private static async Task<IEnumerable<BotBase?>> CountAndPass(Task<IEnumerable<BotBase?>> orig, GenerateBotsRequestData request)
    {
        var bots = (await orig).ToList();
        try
        {
            // a whole request generated without our BotGenerator ever running means
            // another mod owns the DI slot. the masquerade is covered from the prefix
            // above, but the per-bot work in TryInjectSpecials is NOT, so say so out
            // loud once rather than leaving the dogtags to quietly never appear.
            // SPECIALS FALLBACK. when we DON'T hold the generator slot, this is the only
            // place left that sees each finished bot, so the dogtag swap and the wedge
            // euro fix run from here instead. the HoldsGeneratorSlot gate is what keeps
            // this from double-applying: SwapKeycardForDogtag run twice DELETES the tag
            // it created, because on the second pass that tag matches the duplicate-cull
            // branch. exactly one of the two paths may ever touch a given bot.
            if (!IcebreakerBotFirewall.HoldsGeneratorSlot && _rng != null && _db != null && _log != null)
            {
                if (!_slotLossReported && bots.Count > 0)
                {
                    _slotLossReported = true;
                    _log.Warning("[Icebreaker] another mod owns the BotGenerator DI slot (APBS overrides the same class) — "
                        + "masquerade and per-bot injections are running from the bot-generate tap instead");
                }
                foreach (var b in bots)
                {
                    if (b == null) continue;
                    var role = b.Info?.Settings?.Role.ToString()?.ToLowerInvariant();
                    IcebreakerBotSpecials.Apply(b, role, _rng, _db, _log);
                }
            }
        }
        catch { }
        return bots;
    }
}
