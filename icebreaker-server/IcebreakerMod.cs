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
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        // blowtorch item registration (custom parent + item clone) goes through
        // WTT CommonLib — already a hard dependency of the icebreaker modpack
        { "com.wtt.commonlib", new SemanticVersioning.Range("~2.0.20") }
    };
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
        // container loot is REAL: db/staticContainers.json carries the 83 retail
        // container instances (Ids + tpls extracted from the 1.0 level bundles) and
        // labs supplies the per-container-type loot pools + ammo, so the ship's PC
        // blocks/duffles/medcases/toolboxes roll labs loot at labs weights.
        var factory = databaseService.GetLocations().Factory4Day;
        var labs = databaseService.GetLocations().Laboratory;
        suburbs.StaticAmmo = labs.StaticAmmo;
        suburbs.AllExtracts = []; // scav extract list — v1 is PMC-only

        // container loot pools: OUR file (gen_static_loot.py = labs pools + the
        // backported-item additions baked in). labs in-memory is only the fallback —
        // never mutate it, the reference is shared with real labs raids.
        var staticLootPath = SysPath.Combine(modDir, "db", "staticLoot.json");
        Dictionary<MongoId, StaticLootDetails>? ourStaticLoot = null;
        if (System.IO.File.Exists(staticLootPath))
        {
            try { ourStaticLoot = await jsonUtil.DeserializeFromFileAsync<Dictionary<MongoId, StaticLootDetails>>(staticLootPath); }
            catch (Exception e) { logger.Warning($"[Icebreaker] db/staticLoot.json unreadable — falling back to labs pools: {e.Message}"); }
        }
        if (ourStaticLoot is not null)
        {
            suburbs.StaticLoot = new LazyLoad<Dictionary<MongoId, StaticLootDetails>>(() => ourStaticLoot);
            logger.Info($"[Icebreaker] container loot pools loaded ({ourStaticLoot.Count} container types)");
        }
        else
        {
            suburbs.StaticLoot = labs.StaticLoot;
        }

        // loose loot: BSG generates positions server-side per raid — unrecoverable
        // from the bundles, and borrowing factory's data spawned floating items at
        // factory coordinates. authored db/looseLoot.json wins when present (Author 12
        // markers -> gen_loose_loot.py); otherwise an EMPTY set so raids run clean on
        // container loot only.
        var loosePath = SysPath.Combine(modDir, "db", "looseLoot.json");
        string? looseJson = null;
        if (System.IO.File.Exists(loosePath))
        {
            try
            {
                looseJson = System.IO.File.ReadAllText(loosePath);
                if (jsonUtil.Deserialize<LooseLoot>(looseJson) is null) looseJson = null;
            }
            catch (Exception e)
            {
                looseJson = null;
                logger.Warning($"[Icebreaker] db/looseLoot.json unreadable — running loose-loot-free: {e.Message}");
            }
        }
        if (looseJson is not null)
        {
            // LazyLoad.Value re-invokes the factory EVERY access, and SPT's generator
            // MUTATES spawnpoint templates during generation — so the factory must
            // return a FRESH deserialization each raid (a cached instance degrades:
            // pools collapse to the previously-chosen item). the fresh copy is also
            // where per-raid randomisation happens, covering two generator gaps:
            // it never reads GroupPositions and never rolls forced probabilities.
            var json = looseJson;
            suburbs.LooseLoot = new LazyLoad<LooseLoot>(() => RandomiseLooseLoot(jsonUtil.Deserialize<LooseLoot>(json)));
            logger.Info("[Icebreaker] authored loose loot loaded (per-raid group positions + forced-spawn rolls)");
        }
        else
        {
            suburbs.LooseLoot = new LazyLoad<LooseLoot>(() => new LooseLoot
            {
                SpawnpointCount = new SpawnpointCount { Mean = 0, Std = 0 },
                Spawnpoints = [],
                SpawnpointsForced = [],
            });
        }

        // NOTE the property is LazyLoad<StaticContainerDetails> — deserialize the
        // INNER model and wrap, or the load silently fails and the factory fallback
        // pairs factory container types with labs pools = KeyNotFound (Bank safe)
        // at raid start
        var containersPath = SysPath.Combine(modDir, "db", "staticContainers.json");
        StaticContainerDetails? ourContainers = null;
        if (System.IO.File.Exists(containersPath))
        {
            try { ourContainers = await jsonUtil.DeserializeFromFileAsync<StaticContainerDetails>(containersPath); }
            catch (Exception e) { logger.Warning($"[Icebreaker] db/staticContainers.json unreadable: {e.Message}"); }
        }
        if (ourContainers is not null)
        {
            suburbs.StaticContainers = new LazyLoad<StaticContainerDetails>(() => ourContainers);
            logger.Info("[Icebreaker] retail container set loaded (83 instances, labs loot pools)");
        }
        else
        {
            // fall back to factory's containers AND factory pools together — mixing
            // fallback containers with our labs-derived pools crashes loot gen
            suburbs.StaticContainers = factory.StaticContainers;
            suburbs.StaticLoot = factory.StaticLoot;
            logger.Warning($"[Icebreaker] {containersPath} missing/unreadable — factory loot this run, container ids wont match the ship");
        }

        // scav raid time settings keyed by map id — clone factory's so lookups resolve
        var locationConfig = configServer.GetConfig<LocationConfig>();
        if (locationConfig.ScavRaidTimeSettings.Maps.TryGetValue("factory4_day", out var factorySettings))
        {
            locationConfig.ScavRaidTimeSettings.Maps["suburbs"] = cloner.Clone(factorySettings);
        }

        // the map screen (and raid summaries etc) show the LOCALE name for the slot,
        // not Base.Name — rebrand the Suburbs keys in every language so the dot reads
        // ICEBREAKER. IconX/IconY in base.json place it top-left coastal to match the
        // live world map. together the slot is visually a first-class location while
        // keeping Suburbs' first-class plumbing (insurance/scav-time/botgen all resolve
        // natively — the reason a truly NEW location keeps failing for others).
        try
        {
            foreach (var kv in databaseService.GetLocales().Global)
            {
                kv.Value.AddTransformer(locale =>
                {
                    locale["5714dc342459777137212e0b Name"] = "Icebreaker";
                    locale["Suburbs"] = "Icebreaker";
                    return locale;
                });
            }
        }
        catch (Exception e)
        {
            logger.Warning($"[Icebreaker] locale rebrand failed (map dot will say Suburbs): {e.Message}");
        }

        logger.Success("[Manimal-Icebreaker] Suburbs slot rebound to Icebreaker — enabled, icon placed, locale rebranded");
    }

    private static readonly Random LootRng = new();

    // per-raid loose loot post-processing, run on the fresh copy the LazyLoad
    // factory deserializes each raid:
    //  1. GROUPS — SPT's LocationLootGenerator never reads GroupPositions (verified
    //     against source), so a grouped point always spawned at its template
    //     position. pick one candidate pose per raid ourselves and bake it in.
    //  2. FORCED — GetForcedDynamicLoot adds every forced point unconditionally
    //     (probability is never rolled), so sub-100% specific-item spots spawned
    //     every raid. roll them here.
    private static LooseLoot? RandomiseLooseLoot(LooseLoot? loose)
    {
        if (loose is null) return null;

        var all = (loose.Spawnpoints ?? []).Concat(loose.SpawnpointsForced ?? []);
        foreach (var sp in all)
        {
            var t = sp.Template;
            if (t?.IsGroupPosition != true) continue;
            var poses = t.GroupPositions?.ToList();
            if (poses is null || poses.Count == 0) continue;
            var pick = poses[LootRng.Next(poses.Count)];
            t.Position = pick.Position;
            t.Rotation = pick.Rotation;
            t.IsGroupPosition = false; // pose is baked now — nothing downstream needs the group
            t.GroupPositions = [];
        }

        loose.SpawnpointsForced = (loose.SpawnpointsForced ?? [])
            .Where(p => (p.Probability ?? 1) >= 1 || LootRng.NextDouble() < p.Probability!.Value)
            .ToList();

        return loose;
    }
}

// registers the usable blowtorch: custom parent node (db/CustomParents) + item clone
// of the BBQ-S43 labyrinth torch (db/CustomItems) via WTT CommonLib. parents BEFORE
// items — the item references the parent id. the hands behavior (draw/fire/holster
// on the custom animator) lives in the icebreaker client plugin.
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 2)]
public class BlowtorchRegistration(WTTServerCommonLib.WTTServerCommonLib wttCommon) : IOnLoad
{
    public async Task OnLoad()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        await wttCommon.CustomItemParentService.CreateCustomParents(assembly);
        await wttCommon.CustomItemServiceExtended.CreateCustomItems(assembly);
    }
}
