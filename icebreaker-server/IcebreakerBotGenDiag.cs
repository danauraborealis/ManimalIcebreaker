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
using SPTarkov.Server.Core.Models.Utils;

namespace Manimal.Icebreaker.Server;

// BOT-GENERATE DIAGNOSTIC TAP (08-05, the transit-leg naked storm). on the second
// raid of a fika session every on-demand crew request comes back with ZERO profiles
// and zero server errors. the server core has exactly one silent empty path:
// `request.Conditions` null/empty. this tap logs every generate request's conditions
// at Info, so the rental's log finally answers the split: requests arriving WITH
// conditions (server-side refusal — impossible without errors per the code) vs
// arriving EMPTY (client-side request builder broke) vs never arriving at all
// (client short-circuited before the wire). BotController.Generate is not virtual,
// so this is a harmony prefix rather than the usual DI override.
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 91000)]
public class IcebreakerBotGenDiag(
    ISptLogger<IcebreakerBotGenDiag> logger,
    SPTarkov.Server.Core.Utils.RandomUtil randomUtil,
    SPTarkov.Server.Core.Services.DatabaseService databaseService,
    SPTarkov.Server.Core.Generators.BotGenerator botGenerator) : IOnLoad
{
    // WHO ACTUALLY OWNS THE SLOT (08-13). twice now a field log has been diagnosed by
    // GUESSING which mod displaced our BotGenerator override, and twice the guess was
    // wrong — APBS 2.2.1 turned out not to override it at all. the container knows the
    // answer, so ask it at startup and print the concrete type plus its assembly. a
    // one-line answer in the log beats reading somebody's whole modlist.
    private void ReportGeneratorOwner()
    {
        try
        {
            var t = botGenerator.GetType();
            if (t == typeof(IcebreakerBotFirewall)) return; // the normal, healthy case
            logger.Warning($"[Icebreaker] the BotGenerator DI slot is held by '{t.FullName}' "
                + $"(assembly '{t.Assembly.GetName().Name}'), NOT our firewall — that mod wins the last-registration "
                + "race, so our per-bot hooks are bypassed. the APBS masquerade and the BD dogtag/euro injections are "
                + "running from the bot-generate tap instead, which harmony guarantees. report this line if bots misbehave");
        }
        catch (Exception e) { logger.Warning($"[Icebreaker] could not resolve the BotGenerator owner: {e.Message}"); }
    }

    private static ISptLogger<IcebreakerBotGenDiag>? _log;
    private static SPTarkov.Server.Core.Utils.RandomUtil? _rng;
    private static SPTarkov.Server.Core.Services.DatabaseService? _db;

    public Task OnLoad()
    {
        _log = logger;
        _rng = randomUtil;
        _db = databaseService;
        try
        {
            var h = new Harmony("com.manimal.icebreaker.botgendiag");
            h.Patch(AccessTools.Method(typeof(BotController), nameof(BotController.Generate)),
                prefix: new HarmonyMethod(typeof(IcebreakerBotGenDiag), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(IcebreakerBotGenDiag), nameof(Postfix)));
            logger.Info("[Icebreaker] bot-generate diagnostic tap armed (request + response count)");
            ReportGeneratorOwner();
        }
        catch (Exception e)
        {
            logger.Warning($"[Icebreaker] bot-generate tap failed (diagnostic only, mod unaffected): {e.Message}");
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

        try
        {
            var conds = request?.Conditions;
            _log?.Info(conds == null || conds.Count == 0
                ? "[Icebreaker/BotGen] request with NO CONDITIONS — core returns an empty list for this"
                : "[Icebreaker/BotGen] request: " + string.Join(", ", conds.Select(c => $"{c.Role}/{c.Difficulty}x{c.Limit}")));
        }
        catch { }
    }

    // RESPONSE side (08-07, griffy's no-fika repro of the naked storm: BD/knight stop
    // spawning after a few raids until a server restart). the request log alone can't
    // split "server returned bots" from "server returned an empty list with no errors".
    // Generate's result is a LAZY plinq query (the core's 'Materialise' comment has no
    // ToList) — we materialize it exactly once here, count it, and hand the concrete
    // list onward so the serializer doesn't re-enumerate the generation pipeline.
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
            int asked = request?.Conditions?.Sum(c => c.Limit) ?? 0;
            var perRole = bots.Where(b => b != null)
                .GroupBy(b => b!.Info?.Settings?.Role.ToString() ?? "?")
                .Select(g => $"{g.Key}x{g.Count()}");
            _log?.Info($"[Icebreaker/BotGen] response: {bots.Count} bot(s) [{string.Join(", ", perRole)}] for a request of {asked}"
                + (bots.Count == 0 && asked > 0 ? " — EMPTY RESPONSE (the storm), check for errors above" : ""));
        }
        catch { }
        return bots;
    }
}
