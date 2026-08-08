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
public class IcebreakerBotGenDiag(ISptLogger<IcebreakerBotGenDiag> logger) : IOnLoad
{
    private static ISptLogger<IcebreakerBotGenDiag>? _log;

    public Task OnLoad()
    {
        _log = logger;
        try
        {
            var h = new Harmony("com.manimal.icebreaker.botgendiag");
            h.Patch(AccessTools.Method(typeof(BotController), nameof(BotController.Generate)),
                prefix: new HarmonyMethod(typeof(IcebreakerBotGenDiag), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(IcebreakerBotGenDiag), nameof(Postfix)));
            logger.Info("[Icebreaker] bot-generate diagnostic tap armed (request + response count)");
        }
        catch (Exception e)
        {
            logger.Warning($"[Icebreaker] bot-generate tap failed (diagnostic only, mod unaffected): {e.Message}");
        }
        return Task.CompletedTask;
    }

    private static void Prefix(GenerateBotsRequestData request)
    {
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

    private static async Task<IEnumerable<BotBase?>> CountAndPass(Task<IEnumerable<BotBase?>> orig, GenerateBotsRequestData request)
    {
        var bots = (await orig).ToList();
        try
        {
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
