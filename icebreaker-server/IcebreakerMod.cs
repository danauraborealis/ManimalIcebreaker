using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Json;
using SysPath = System.IO.Path;

namespace Manimal.Icebreaker.Server;

public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.manimal.icebreaker";
    public override string Name { get; init; } = "ManimalIcebreaker";
    public override string Author { get; init; } = "Manimal";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("0.1.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; } = true; // ships the scene + preset bundles
    public override string License { get; init; } = "MIT";
}

// rebinds the dormant "Suburbs" location slot to the backported Icebreaker map.
// suburbs is a shipped stub (disabled, empty scene) with a first-class property on
// SPT's closed Locations record — hijacking it means every native lookup
// (GetLocation("suburbs"), GetDictionary, GenerateAll) resolves with zero patching.
// scene loading is data-driven: Base.Scene points at our preset bundle, which lists
// the scenes inside our scene bundle; both are served by SPT's bundle system.
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 90000)]
public class IcebreakerMod(
    DatabaseService databaseService,
    ConfigServer configServer,
    ICloner cloner,
    JsonUtil jsonUtil,
    ISptLogger<IcebreakerMod> logger)
    : IOnLoad
{
    public async Task OnLoad()
    {
        var modDir = SysPath.GetDirectoryName(typeof(IcebreakerMod).Assembly.Location)!;
        var basePath = SysPath.Combine(modDir, "db", "base.json");
        var newBase = await jsonUtil.DeserializeFromFileAsync<LocationBase>(basePath);
        if (newBase is null)
        {
            logger.Error($"[Icebreaker] could not load {basePath} — location NOT enabled");
            return;
        }

        var suburbs = databaseService.GetLocations().Suburbs;
        if (suburbs is null)
        {
            logger.Error("[Icebreaker] Suburbs location slot missing from database — aborting");
            return;
        }

        suburbs.Base = newBase;

        // the suburbs stub ships base.json ONLY — no loot data files — so the server's
        // raid-start loot generation (LocationLootGenerator.GenerateStaticContainers/
        // GenerateLocationLoot) NREs on null LooseLoot/StaticLoot/StaticContainers.
        // borrow factory4_day's known-valid loot data wholesale. loot spawns at factory
        // WORLD positions (wrong for our ship — floating/clipped items) but generation
        // succeeds and the raid starts. refine to icebreaker-authored loot later.
        var factory = databaseService.GetLocations().Factory4Day;
        suburbs.LooseLoot = factory.LooseLoot;
        suburbs.StaticLoot = factory.StaticLoot;
        suburbs.StaticContainers = factory.StaticContainers;
        suburbs.StaticAmmo = factory.StaticAmmo;
        suburbs.AllExtracts = []; // scav extract list — v1 is PMC-only

        // scav raid time settings keyed by map id — clone factory's so lookups resolve
        var locationConfig = configServer.GetConfig<LocationConfig>();
        if (locationConfig.ScavRaidTimeSettings.Maps.TryGetValue("factory4_day", out var factorySettings))
        {
            locationConfig.ScavRaidTimeSettings.Maps["suburbs"] = cloner.Clone(factorySettings);
        }

        logger.Success("[Manimal-Icebreaker] Suburbs slot rebound to Icebreaker — enabled, 1 exit, scenes via bundles");
    }
}
