using System;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Bot;
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
                prefix: new HarmonyMethod(typeof(IcebreakerBotGenDiag), nameof(Prefix)));
            logger.Info("[Icebreaker] bot-generate diagnostic tap armed");
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
}
