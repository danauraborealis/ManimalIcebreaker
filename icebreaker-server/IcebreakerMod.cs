using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Helpers.Quest;
using SPTarkov.Server.Core.DI.Routing;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Generators.Loot;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Json;
using SysPath = System.IO.Path;

namespace Manimal.Icebreaker.Server;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.manimal.icebreaker";
    public string Name { get; init; } = "ManimalIcebreaker";
    public string Author { get; init; } = "Manimal";
    public List<string>? Contributors { get; init; }
    // read from the assembly rather than repeated as a literal. forge requires every
    // version a mod declares to match exactly, and the csproj already feeds ModVersion
    // (Directory.Build.props) into <Version> — so this stays correct across a bump
    // instead of silently drifting from the client's BuildInfo.Version.
    public SemanticVersioning.Version Version { get; init; } =
        new(typeof(ModMetadata).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.1.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.5");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        // blowtorch item registration (custom parent + item clone) goes through
        // WTT CommonLib — already a hard dependency of the icebreaker modpack
        { "com.wtt.commonlib", new SemanticVersioning.Range("~3.0.6") },
        // HARD since 0.2.4: the kordbreach set supplies the C-3 keycard AND the
        // black division dogtags — the tags are the currency for the ragman/skier
        // quest-reward barters, so without this the barters are unbuyable and BD
        // bodies drop nothing. 1.1.4 is the release the tags shipped in.
        { "com.wtt.contentbackport", new SemanticVersioning.Range("~2.0.1") },
        { "com.morebotsapi.tacticaltoaster", new SemanticVersioning.Range("~2.1.1") },
        { "com.blackdiv.tacticaltoaster", new SemanticVersioning.Range("~1.3.1") },
        { "com.manimal.csgas", new SemanticVersioning.Range("~2.0.0") }
    };
    public string? Url { get; init; }
    public bool HasPrepatcher { get; init; } = false;
    public string License { get; init; } = "MIT";
}
// rebinds the dormant "Suburbs" location slot to the backported Icebreaker map.
// suburbs is a shipped stub (disabled, empty scene) with a first-class property on
// SPT's closed Locations record — hijacking it means every native lookup
// (GetLocation("suburbs"), GetDictionary, GenerateAll) resolves with zero patching.
// scene loading is data-driven: Base.Scene points at our preset bundle, which lists
// the scenes inside our scene bundle; both are served by SPT's bundle system.
[Injectable(TypePriority = OnLoadOrder.Preload + 90000)]
public class IcebreakerMod(
    LocationTable locationTable,
    LocaleTable localeTable,
    BotConfig botConfig,
    LocationConfig locationConfig,
    ICloner cloner,
    JsonUtil jsonUtil,
    ImageRouter imageRouter,
    ISptLogger<IcebreakerMod> logger)
    : IOnLoad
{
    // the retail trigger table's own ceiling — see the cap note in OnLoad
    private const int IcebreakerBotCap = 40;

    // banner captions, written into EVERY global locale the same way the Suburbs
    // rebrand above is: english text in every language beats a raw key showing
    // through on the loading screen. keys are "<bannerId> Name"/"<bannerId>
    // Description", matching the ids in base.json's Banners array.
    // the ship blurb, shared by the loading screen banner and the map-select card so
    // the two can never drift apart. live breaks it into two paragraphs at "It appears",
    // so the break lives in the text rather than being a card-only flourish.
    private const string IcebreakerBlurb =
        "In the Gulf of Finland, trapped within the blockade surrounding Tarkov, lies the nuclear-powered icebreaker \"Boreas\", owned by the logistics corporation Paradigm Shipping. The exact purpose of \"Boreas\" and the cargo it carries remain unknown."
        + "\n\n"
        + "It appears that Paradigm Shipping and TerraGroup made special efforts to minimize any mention of the icebreaker in the press and keep its routes and assignments classified.";

    private static readonly (string Key, string Text)[] BannerLocales =
    {
        ("icebreaker_cover Name", "Icebreaker"),
        ("icebreaker_cover Description", IcebreakerBlurb),

        ("blackdiv_banner Name", "Black Division"),
        ("blackdiv_banner Description",
            "From time to time, rumors spread through Tarkov about a special unit that belongs neither to USEC nor BEAR. According to campfire stories, these special operatives conduct night-time operations to clear restricted facilities and extract valuable TerraGroup data. But among PMC operators, very few believe in the existence of this so-called \"Black Division\", because under the current circumstances, freely inserting and extracting entire combat groups through the blockade ring is practically impossible."),

        ("wedge_banner Name", "\"The Wedge’s\" Special Squadron"),
        ("wedge_banner Description",
            "Reconnaissance and monitoring reports from the few PMC networks still active in Tarkov have indicated that several combat helicopters are moving toward the Gulf of Finland. Intercepted radio frequencies mention a codename: \"The Wedge\". Accompanied by a squad of operatives from some of the world’s most diverse special forces, such as the SAS and Mossad, Wedge is a senior Black Division operative in charge of a covert operation aboard the ship \"Boreas\". No one has come out alive to reveal their motives there."),
    };

    // player-facing text of the client plugin (interaction menu, hold-progress panel,
    // notifications). the client resolves these keys through Localized() and falls
    // back to its own english copy when a key is missing, so an older server stays
    // compatible. english goes into every language, same as the captions above.
    private static readonly (string Key, string Text)[] UiLocales =
    {
        ("Icebreaker_UI_ChainDoor_Open", "Open"),
        ("Icebreaker_UI_ChainDoor_Plant", "Plant"),
        ("Icebreaker_UI_ChainDoor_Planting", "Planting charge"),
        ("Icebreaker_UI_SealedDoor_Unseal", "Unseal Door"),
        ("Icebreaker_UI_SealedDoor_Reseal", "Reseal Door"),
        ("Icebreaker_UI_SealedDoor_Unsealing", "Unsealing Door"),
        ("Icebreaker_UI_SealedDoor_Sealing", "Sealing Door"),
        ("Icebreaker_UI_Keypad_EnterCode", "Enter code"),
        ("Icebreaker_UI_Keypad_Unlocked", "Door unlocked"),
        ("Icebreaker_UI_Hatch_Melt", "Melt the ice (blowtorch)"),
        ("Icebreaker_UI_Hatch_TurnHandle", "Turn handle"),
        ("Icebreaker_UI_Hatch_Open", "Open hatch"),
        ("Icebreaker_UI_Heli_Signal", "Signal the helicopter with a green flare to extract"),
        ("Icebreaker_UI_Heli_Inbound", "The helicopter has been signaled — inbound, hold the pad"),
        ("Icebreaker_UI_Heli_Landed", "The helicopter has landed — extraction active"),
        ("Icebreaker_UI_Fare_NotEnough", "The smugglers want {0:N0} roubles for the crossing ({1:N0} carried)"),
    };

    // per-language overrides, applied on top of the english above. a language with
    // no table here keeps english. keys must match the ones written in OnLoad.
    private const string IcebreakerBlurbRu =
        "В Финском заливе, в кольце блокады вокруг Таркова, застрял атомный ледокол «Борей», принадлежащий логистической корпорации Paradigm Shipping. Истинное назначение «Борея» и его груз до сих пор неизвестны."
        + "\n\n"
        + "Похоже, Paradigm Shipping и TerraGroup приложили немало усилий, чтобы ледокол не мелькал в прессе, а его маршруты и задачи оставались засекреченными.";

    private static readonly Dictionary<string, (string Key, string Text)[]> TranslatedLocales = new()
    {
        ["ru"] = new[]
        {
            ("5714dc342459777137212e0b Name", "Ледокол"),
            ("Suburbs", "Ледокол"),
            ("5714dc342459777137212e0b Description", IcebreakerBlurbRu),
            ("Icebreaker_Exit_Heli", "Вертолёт"),

            ("icebreaker_cover Name", "Ледокол"),
            ("icebreaker_cover Description", IcebreakerBlurbRu),
            ("blackdiv_banner Description",
                "Время от времени по Таркову ползут слухи о спецподразделении, которое не относится ни к USEC, ни к BEAR. Если верить байкам у костра, эти оперативники по ночам зачищают закрытые объекты и вывозят ценные данные TerraGroup. Но среди бойцов ЧВК мало кто верит в существование этого «Black Division»: в нынешних условиях свободно заводить и выводить через кольцо блокады целые боевые группы практически невозможно."),
            ("wedge_banner Name", "Особый отряд «Wedge»"),
            ("wedge_banner Description",
                "Разведсводки немногих ещё действующих в Таркове сетей ЧВК сообщают, что несколько боевых вертолётов движутся в сторону Финского залива. В перехваченных радиопереговорах звучит позывной «Wedge». Во главе отряда оперативников из самых разных спецподразделений мира, включая SAS и «Моссад», Wedge — старший оперативник Black Division — руководит тайной операцией на борту «Борея». Никто из побывавших там не вернулся живым, чтобы рассказать, что им там нужно."),

            ("Icebreaker_UI_ChainDoor_Open", "Открыть"),
            ("Icebreaker_UI_ChainDoor_Plant", "Заложить заряд"),
            ("Icebreaker_UI_ChainDoor_Planting", "Закладка заряда"),
            ("Icebreaker_UI_SealedDoor_Unseal", "Отдраить дверь"),
            ("Icebreaker_UI_SealedDoor_Reseal", "Задраить дверь"),
            ("Icebreaker_UI_SealedDoor_Unsealing", "Отдраивание двери"),
            ("Icebreaker_UI_SealedDoor_Sealing", "Задраивание двери"),
            ("Icebreaker_UI_Keypad_EnterCode", "Ввести код"),
            ("Icebreaker_UI_Keypad_Unlocked", "Дверь открыта"),
            ("Icebreaker_UI_Hatch_Melt", "Растопить лёд горелкой"),
            ("Icebreaker_UI_Hatch_TurnHandle", "Повернуть ручку"),
            ("Icebreaker_UI_Hatch_Open", "Открыть люк"),
            ("Icebreaker_UI_Heli_Signal", "Запустите зелёную ракету, чтобы вызвать вертолёт"),
            ("Icebreaker_UI_Heli_Inbound", "Сигнал принят — вертолёт летит, удерживайте площадку"),
            ("Icebreaker_UI_Heli_Landed", "Вертолёт приземлился — эвакуация доступна"),
            ("Icebreaker_UI_Fare_NotEnough", "Контрабандисты просят {0:N0} руб. за переправу (у вас {1:N0})"),
        },
    };

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modDir = SysPath.GetDirectoryName(typeof(IcebreakerMod).Assembly.Location)!;
        var basePath = SysPath.Combine(modDir, "db", "base.json");
        var newBase = await jsonUtil.DeserializeFromFileAsync<LocationBase>(basePath, cancellationToken);
        if (newBase is null)
        {
            logger.Error($"[Icebreaker] could not load {basePath} — location NOT enabled");
            return;
        }

        var suburbs = locationTable.Suburbs;
        if (suburbs is null)
        {
            logger.Error("[Icebreaker] Suburbs location slot missing from database — aborting");
            return;
        }

        suburbs.Base = newBase;

        // ALIVE-BOT CAP (2026-08-12): SPT caps concurrent bots per map from
        // configs/bot.json maxBotCap, and an unlisted map silently falls through to
        // "default" = 18. the retail trigger table peaks near 40 alive if the player
        // kills nobody (12ish rogues + knight detail, then hides 4, stern 9, wedges 4,
        // T3 3, T4 5) — so from mid-raid on, every later wave was being truncated:
        // squads arriving short, and half-built "invisible gear" bots where a spawn was
        // cut off partway (field report 08-12, T4 delivered 2 of 5 and one was a shell).
        // 40 matches the table's own ceiling; terminal needed the same treatment.
        if (botConfig?.MaxBotCap != null)
        {
            botConfig.MaxBotCap["suburbs"] = IcebreakerBotCap;
            logger.Info($"[Icebreaker] alive-bot cap set to {IcebreakerBotCap} (SPT's unlisted-map default of "
                + $"{botConfig.MaxBotCap.GetValueOrDefault("default", 18)} truncated the late trigger waves)");
        }
        // scavs never cross: DisabledForScav is the exact lever labs uses, and the map
        // screen natively greys the location out for scav raids on it.
        newBase.DisabledForScav = true;

        // the suburbs stub ships base.json ONLY — no loot data files — so the server's
        // raid-start loot generation (LocationLootGenerator.GenerateStaticContainers/
        // GenerateLocationLoot) NREs on null LooseLoot/StaticLoot/StaticContainers.
        // container loot is REAL: db/staticContainers.json carries the 83 retail
        // container instances (Ids + tpls extracted from the 1.0 level bundles) and
        // labs supplies the per-container-type loot pools + ammo, so the ship's PC
        // blocks/duffles/medcases/toolboxes roll labs loot at labs weights.
        var factory = locationTable.Factory4Day;
        var labs = locationTable.Laboratory;
        suburbs.StaticAmmo = labs.StaticAmmo;
        suburbs.AllExtracts = []; // scav extract list — v1 is PMC-only

        // container loot pools: OUR file (gen_static_loot.py = labs pools + the
        // backported-item additions baked in). labs in-memory is only the fallback —
        // never mutate it, the reference is shared with real labs raids.
        var staticLootPath = SysPath.Combine(modDir, "db", "staticLoot.json");
        Dictionary<MongoId, StaticLootDetails>? ourStaticLoot = null;
        if (System.IO.File.Exists(staticLootPath))
        {
            try { ourStaticLoot = await jsonUtil.DeserializeFromFileAsync<Dictionary<MongoId, StaticLootDetails>>(staticLootPath, cancellationToken); }
            catch (Exception e) when (e is not OperationCanceledException) { logger.Warning($"[Icebreaker] db/staticLoot.json unreadable — falling back to labs pools: {e.Message}"); }
        }
        if (ourStaticLoot is not null)
        {
            suburbs.StaticLoot = new LazyLoad<Dictionary<MongoId, StaticLootDetails>>(() => ourStaticLoot, cacheValue: true);
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
            catch (Exception e) when (e is not OperationCanceledException)
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
            suburbs.LooseLoot = new LazyLoad<LooseLoot>(() => RandomiseLooseLoot(jsonUtil.Deserialize<LooseLoot>(json))!, cacheValue: false);
            logger.Info("[Icebreaker] authored loose loot loaded (per-raid group positions + forced-spawn rolls)");
        }
        else
        {
            suburbs.LooseLoot = new LazyLoad<LooseLoot>(() => new LooseLoot
            {
                SpawnpointCount = new SpawnpointCount { Mean = 0, Std = 0 },
                Spawnpoints = [],
                SpawnpointsForced = [],
            }, cacheValue: false);
        }

        // NOTE the property is LazyLoad<StaticContainerDetails> — deserialize the
        // INNER model and wrap, or the load silently fails and the factory fallback
        // pairs factory container types with labs pools = KeyNotFound (Bank safe)
        // at raid start
        var containersPath = SysPath.Combine(modDir, "db", "staticContainers.json");
        StaticContainerDetails? ourContainers = null;
        if (System.IO.File.Exists(containersPath))
        {
            try { ourContainers = await jsonUtil.DeserializeFromFileAsync<StaticContainerDetails>(containersPath, cancellationToken); }
            catch (Exception e) when (e is not OperationCanceledException) { logger.Warning($"[Icebreaker] db/staticContainers.json unreadable: {e.Message}"); }
        }
        if (ourContainers is not null)
        {
            suburbs.StaticContainers = new LazyLoad<StaticContainerDetails>(() => ourContainers, cacheValue: true);
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
            foreach (var kv in localeTable.Global)
            {
                TranslatedLocales.TryGetValue(kv.Key, out var translated);
                kv.Value.AddTransformer(locale =>
                {
                    locale["5714dc342459777137212e0b Name"] = "Icebreaker";
                    locale["Suburbs"] = "Icebreaker";
                    // the map-select card prints the LOCALE description, not
                    // Base.Description — LocationInfoPanel does
                    // (location._Id + " Description").Localized(), so the slot kept
                    // advertising suburbs' "commuter areas of Tarkov" under our banner.
                    // live runs the same ship blurb here as on the banner.
                    locale["5714dc342459777137212e0b Description"] = IcebreakerBlurb;
                    // the extraction panel prints Settings.Name.Localized() for the row
                    // label (ExitTimerPanel), and the "EXFIL01" tag beside it is generated
                    // from the point's index, not from us. with no locale entry the raw
                    // exit id rendered, underscores and all. renaming the exit itself is
                    // not an option: the name is the key the panel, the exfil controller
                    // and the raid-end ExitName all match on.
                    locale["Icebreaker_Exit_Heli"] = "Helicopter";
                    // loading screen banner captions. keyed "<bannerId> Name" /
                    // "<bannerId> Description", matching the ids in base.json's Banners.
                    foreach (var (key, text) in BannerLocales) locale[key] = text;
                    // client plugin UI text, then any translation for this language
                    foreach (var (key, text) in UiLocales) locale[key] = text;
                    if (translated is not null)
                        foreach (var (key, text) in translated) locale[key] = text;
                    return locale;
                });
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.Warning($"[Icebreaker] locale rebrand failed (map dot will say Suburbs): {e.Message}");
        }

        // LOADING SCREEN BANNERS. the client reads the Banners array off the location
        // base and asks for each pic by its path, but NOTHING serves /files/banners on
        // its own — ImageRouter has to be told where the file actually lives, the same
        // way the quest icons are wired. retail shipped its own Banners list here with
        // MongoId filenames we don't have, so ours replaces it wholesale rather than
        // appending, otherwise the screen rolls a missing image most of the time.
        // ImageRouter lowercases the key and strips the extension off the REQUEST, so
        // the extension in base.json only has to be plausible, not exact.
        try
        {
            var bannerDir = SysPath.Combine(modDir, "db", "banners");
            int wired = 0;
            foreach (var banner in newBase.Banners ?? [])
            {
                var file = banner?.Picture?.File;
                if (string.IsNullOrEmpty(file)) continue;
                var full = SysPath.Combine(bannerDir, file);
                if (!System.IO.File.Exists(full))
                {
                    logger.Warning($"[Icebreaker] banner image missing, that slot will render blank: {full}");
                    continue;
                }
                imageRouter.AddRoute($"/files/banners/{SysPath.GetFileNameWithoutExtension(file)}", full);
                wired++;
            }
            logger.Info($"[Icebreaker] {wired} loading screen banner(s) wired (icebreaker_cover leads the list)");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.Warning($"[Icebreaker] banner routing failed (loading screens fall back to the default art): {e.Message}");
        }

        logger.Success("[Manimal-Icebreaker] Suburbs slot rebound to Icebreaker — enabled, icon placed, locale rebranded");
        logger.Info("[Icebreaker] loot isolation armed (suspend/restore) — icebreaker raids run vanilla loot generation; other maps untouched");
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
[Injectable(TypePriority = OnLoadOrder.Preload + 10)]
public class BlowtorchRegistration(WTTServerCommonLib.WTTServerCommonLib wttCommon) : IOnLoad
{
    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        await wttCommon.CustomItemServiceExtended.CreateCustomItems(assembly);
    }
}

// the retail-style unlock chain (mechanic, per the official wiki) — quests, locales
// and zones load from db/CustomQuests + db/CustomQuestZones via WTT CommonLib. the
// folders may be absent while the chain is being authored; the services no-op then.
[Injectable(TypePriority = OnLoadOrder.Preload + 11)]
public class IcebreakerQuestRegistration(
    WTTServerCommonLib.WTTServerCommonLib wttCommon,
    LocationTable locationTable,
    ISptLogger<IcebreakerQuestRegistration> logger) : IOnLoad
{
    // quest-item spawn points that should yield exactly ONE instance per raid, picked
    // at random from all candidates so the player has to actually search. tagged by
    // template-Id prefix in the CustomLootspawns json.
    private const string AmgSpawnPrefix = "boreas_amg";
    private static readonly Random SpawnRng = new();

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        // quest ITEMS ride the regular custom-item pipeline (db/CustomItems/BoreasItems
        // .json, QuestItem override — same pattern as Mitsuru's chem containers; this
        // CommonLib version has no dedicated quest-item service). BlowtorchRegistration
        // already ran CreateCustomItems at +10, which picked them up.
        await wttCommon.CustomQuestService.CreateCustomQuests(assembly);
        await wttCommon.CustomQuestZoneService.CreateCustomQuestZones(assembly);
        // forced world spawns for the quest items (db/CustomLootspawns)
        await wttCommon.CustomLootspawnService.CreateCustomLootSpawns(assembly);
        // in-raid trader dialogue trees (db/CustomDialogues) — the BTR driver's Boreas
        // conversation. quests point at their tree through the quest's dialogueId.
        await wttCommon.CustomDialogueService.CreateCustomDialogues(assembly);
        // trader stock we ADD (db/CustomAssortSchemes) — Ragman's Black Division carrier
        // barter. the offer only becomes visible once Fresh Stock is handed in: that gate
        // is the QuestAssort mapping shipped alongside the quest, NOT this file. the
        // AssortmentUnlock reward on the quest is only the "unlocks purchase" UI line.
        await wttCommon.CustomAssortSchemeService.CreateCustomAssortSchemes(assembly);

        // ONE canister, random spot. CommonLib injected every candidate as a forced
        // spawn (its own LooseLoot transformer), so ours registers AFTER and prunes all
        // but one. GroupPositions can't do this: nothing in the server ever reads that
        // field for loose loot, it's a model property only, so a grouped point always
        // lands on its template position. LazyLoad.Value re-deserializes + re-runs
        // transformers on every access, so the pick is genuinely per raid.
        PruneToSingleSpawn("rezervbase", AmgSpawnPrefix);
    }

    private void PruneToSingleSpawn(string locationId, string idPrefix)
    {
        try
        {
            var locations = locationTable.GetDictionary();
            if (!locations.TryGetValue(locationId, out var location) || location.LooseLoot is null)
            {
                logger.Warning($"[Icebreaker] {locationId} has no loose loot to prune '{idPrefix}' spawns from");
                return;
            }

            location.LooseLoot.AddTransformer(loose =>
            {
                if (loose?.SpawnpointsForced is null) return loose;

                var all = loose.SpawnpointsForced.ToList();
                var ours = all.Where(p => p.Template?.Id?.StartsWith(idPrefix, StringComparison.Ordinal) == true).ToList();
                if (ours.Count <= 1) return loose;

                var keep = ours[SpawnRng.Next(ours.Count)];
                loose.SpawnpointsForced = all.Where(p => !ours.Contains(p) || ReferenceEquals(p, keep)).ToList();
                return loose;
            });
            logger.Info($"[Icebreaker] '{idPrefix}' spawns on {locationId} reduced to one random point per raid");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.Warning($"[Icebreaker] spawn prune setup failed for {locationId}: {e.Message}");
        }
    }
}

// QUEST VISIBILITY GATES. Boreas Part 1 no longer has one: it starts on Mechanic
// LL3, which is a TraderLoyalty condition SPT's own quest filter understands, and the
// poster is now the first OBJECTIVE (hand it over, no found-in-raid) rather than a
// precondition for the quest appearing.
//
// that replaced a custom gate which hid the quest until the flyer was in the profile.
// it worked, but only by accident of timing: RequestQuestsTemplates is called exactly
// twice by the client -- at login and after a raid -- so a poster bought on the flea
// mid-session unlocked nothing until the player happened to run a raid. a native start
// condition has the same refresh behaviour as every vanilla quest, which is what
// players already expect.
//
// what remains here are the CROSSING gates below, which cannot be expressed natively:
// "you have been to the icebreaker" is not a condition type SPT evaluates.
[Injectable(TypePriority = OnLoadOrder.Routers - 1)]
public class IcebreakerFlyerGateRouter(
    JsonUtil jsonUtil,
    SPTarkov.Server.Core.Controllers.QuestController questController,
    SPTarkov.Server.Core.Helpers.Quest.QuestHelper questHelper,
    SPTarkov.Server.Core.Helpers.Profile.ProfileHelper profileHelper,
    SPTarkov.Server.Core.Routers.EventOutputHolder eventOutputHolder,
    SPTarkov.Server.Core.Utils.HttpResponseUtil httpResponseUtil,
    ISptLogger<IcebreakerFlyerGateRouter> logger)
    : SPTarkov.Server.Core.DI.StaticRouter(
        jsonUtil,
        [
            new SPTarkov.Server.Core.DI.RouteAction<SPTarkov.Server.Core.Models.Eft.Common.EmptyRequestData>(
                "/client/quest/list",
                async (url, info, sessionID, output, cancellationToken) =>
                    await GateQuests(questController, questHelper, profileHelper, eventOutputHolder,
                                     httpResponseUtil, logger, sessionID)
            ),
        ])
{
    private const string StickToIt = "e857e9c34949ecbf1cf5a5b2";  // BTR follow-up, needs an icebreaker visit (any outcome)
    private const string PrivateRoman = "8b2b4eea617e2be2e54df123";
    private const string BitterVictory = "c7a1f0b93e64d5827ab1cc40";
    // Hangover, the END of the BTR chain — its reward is the halved 250k map fare
    // (client-side, IcebreakerMapFare.CostFor). the crossing gate below still applies:
    // it only appears after a crossing made post-Bitter Victory.
    private const string Hangover = "3f8d2c5a9b17e04d6ca8f312";
    // Ragman wants Black Division kit, and he can only have heard about the ship once
    // you've actually come back off it — same first-crossing gate as Stick to It, but
    // an entirely separate trader and chain.
    private const string FreshStock = "6a752a6bc498772c6a150baf";
    // Therapist's iodide order. same first-crossing gate: the high dose only turns up on
    // the ship, and her surprise at the source ("The Icebreaker?") only lands if she is
    // asking someone who has already been out there.
    private const string WarNeverChanges = "6a753b58478c184bd220c417";
    // Prapor's helicopter upkeep. the callback ("you already know about my little
    // helicopter secret") is satisfied for free by this gate: the hard map lock means
    // nobody visits Icebreaker without having finished Boreas P3 first (map reveal).
    private const string OilChange = "6a75661b478c184bd220c433";
    // Skier's courier bags. part 2 chains off this one natively, so only the opener
    // needs a crossing gate.
    private const string PackMule = "6a757a2f478c184bd220c458";
    // Peacekeeper's impounded BD container. same first-crossing gate: he opens by asking
    // about "that ship in the bay" and offers to sell you the shipment, which only reads
    // right to someone who has already been out there and seen who is guarding it.
    private const string ChainOfCustody = "6a7e0916b881de241018539d";

    // quests gated on "you have been to the icebreaker" (any raid outcome counts,
    // user call 08-19 — die on the deck, still counts). AfterQuest null means the
    // first crossing ever; otherwise it must be a crossing made AFTER that quest was
    // finished, so trips banked earlier in the chain don't pay for a later gate. adding
    // the next one in the chain is a line here.
    private sealed record CrossingGate(string QuestId, string? AfterQuest);

    private static readonly CrossingGate[] CrossingGates =
    {
        new(StickToIt, null),
        new(BitterVictory, PrivateRoman),
        new(Hangover, BitterVictory),
        new(FreshStock, null),
        new(WarNeverChanges, null),
        new(OilChange, null),
        new(PackMule, null),
        new(ChainOfCustody, null),
    };

    private static ValueTask<string> GateQuests(
        SPTarkov.Server.Core.Controllers.QuestController questController,
        SPTarkov.Server.Core.Helpers.Quest.QuestHelper questHelper,
        SPTarkov.Server.Core.Helpers.Profile.ProfileHelper profileHelper,
        SPTarkov.Server.Core.Routers.EventOutputHolder eventOutputHolder,
        SPTarkov.Server.Core.Utils.HttpResponseUtil httpResponseUtil,
        ISptLogger<IcebreakerFlyerGateRouter> logger,
        MongoId sessionID)
    {
        // BEFORE the list is read, so a quest that hands itself in here shows as done in
        // this very response rather than one refresh later
        IcebreakerRaidWatchRouter.AutoTurnIn(questHelper, profileHelper, eventOutputHolder, sessionID);

        var quests = questController.GetClientQuests(sessionID);
        try
        {
            var pmc = profileHelper.GetPmcProfile(sessionID);

            // stamp the crossing count for any gating quest already finished. doing it
            // here (not only at raid end) catches a hand-in made at the trader screen,
            // so the very next crossing counts toward the gate instead of being eaten by
            // a mark stamped after the fact.
            foreach (var after in CrossingGates.Select(g => g.AfterQuest).Where(a => a is not null).Distinct())
                if (pmc?.Quests?.Any(q => q.QId.ToString() == after &&
                        q.Status == SPTarkov.Server.Core.Models.Enums.QuestStatusEnum.Success) == true)
                    IcebreakerRaidWatchRouter.MarkQuestFinished(sessionID.ToString(), after!);

            // the BTR follow-ups only exist once you've made the crossing and come back
            // alive. once the quest is in the profile it is never hidden again, so an
            // accepted quest can't be stranded by losing whatever unlocked it.
            foreach (var gate in CrossingGates)
            {
                if (pmc?.Quests?.Any(q => q.QId.ToString() == gate.QuestId) == true) continue;
                bool earned = gate.AfterQuest is null
                    ? IcebreakerRaidWatchRouter.HasVisited(sessionID.ToString())
                    : IcebreakerRaidWatchRouter.HasVisitedSince(sessionID.ToString(), gate.AfterQuest);
                if (!earned)
                    quests = quests.Where(q => q.Id.ToString() != gate.QuestId).ToList();
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { logger.Warning($"[Icebreaker] quest gate check failed (quests left visible): {e.Message}"); }
        return new ValueTask<string>(httpResponseUtil.GetBody(quests));
    }
}

// "you have actually been there" flag. SPT only evaluates Quest/Level/TraderStanding/
// TraderLoyalty as quest START conditions (verified across every vanilla quest), so
// "extract from the icebreaker once" cannot be expressed as one. instead we watch raid
// ends: this router reads the END request and passes core's response through untouched,
// so nothing is processed twice, and records the profile once it ends a raid on our
// slot. StartLocalRaid builds ServerId as "{location}.{side} {timestamp}", so the map
// comes back to us on the way out.
[Injectable(TypePriority = OnLoadOrder.Routers + 1)]
public class IcebreakerRaidWatchRouter(
    JsonUtil jsonUtil,
    SPTarkov.Server.Core.Helpers.Quest.QuestHelper questHelper,
    SPTarkov.Server.Core.Helpers.Profile.ProfileHelper profileHelper,
    SPTarkov.Server.Core.Routers.EventOutputHolder eventOutputHolder,
    ISptLogger<IcebreakerRaidWatchRouter> logger)
    : SPTarkov.Server.Core.DI.StaticRouter(
        jsonUtil,
        [
            new SPTarkov.Server.Core.DI.RouteAction<SPTarkov.Server.Core.Models.Eft.Match.EndLocalRaidRequestData>(
                "/client/match/local/end",
                async (url, info, sessionID, output, cancellationToken) =>
                {
                    var passthrough = await Watch(logger, info, sessionID, output);
                    AutoTurnIn(questHelper, profileHelper, eventOutputHolder, sessionID, logger);
                    return passthrough;
                }
            ),
        ])
{
    private const string SlotId = "suburbs";

    // quests that hand themselves in. the BTR driver only exists in raid, so a quest
    // whose last objective lands on a map he isn't on would otherwise be unturnable
    // in practice. BSG's own lever for this is dead here: RawQuestClass carries
    // instantComplete and GClass3999.OnConditionalStatusChangedEvent calls
    // TryToInstantComplete on it, but the QuestClass override (GClass4005) is an empty
    // body — only prestige implements it. so we do the hand-in ourselves.
    private static readonly string[] AutoComplete =
    {
        // Stick to It is deliberately NOT here: its turn-in is the face-to-face exchange
        // with the driver, and the quest-list refresh between raids is what makes Saving
        // Private Roman appear on the visit after it — that gap is fine.
        "8b2b4eea617e2be2e54df123",   // Saving Private Roman
        "c7a1f0b93e64d5827ab1cc40",   // A Bitter Victory
    };

    // runs at raid end AND on the quest screen. raid end covers objectives that land in
    // there: core's handler has already merged the client's post-raid quest statuses
    // (ProcessPostRaidQuests replaces Quests wholesale), so anything finished in the
    // raid reads as AvailableForFinish by the time we get called. the quest screen pass
    // covers objectives finished in the menu — the last hand over of A Bitter Victory
    // would otherwise leave the quest sitting ready until the player went and ended
    // another raid. doing either mid-raid instead would apply rewards to a profile the
    // raid-end merge is about to overwrite.
    public static void AutoTurnIn(
        SPTarkov.Server.Core.Helpers.Quest.QuestHelper questHelper,
        SPTarkov.Server.Core.Helpers.Profile.ProfileHelper profileHelper,
        SPTarkov.Server.Core.Routers.EventOutputHolder eventOutputHolder,
        MongoId sessionID,
        ISptLogger<IcebreakerRaidWatchRouter>? logger = null)
    {
        try
        {
            var pmc = profileHelper.GetPmcProfile(sessionID);
            if (pmc?.Quests is null) return;

            foreach (var questId in AutoComplete)
            {
                var entry = pmc.Quests.FirstOrDefault(q => q.QId.ToString() == questId);
                if (entry is null ||
                    entry.Status != SPTarkov.Server.Core.Models.Enums.QuestStatusEnum.AvailableForFinish)
                    continue;

                questHelper.CompleteQuest(pmc, new SPTarkov.Server.Core.Models.Eft.Quests.CompleteQuestRequestData
                {
                    Action = "QuestComplete",
                    QuestId = new MongoId(questId),
                }, sessionID);

                // CompleteQuest fills the shared item-event output, which is only drained
                // by ItemEventRouter. left alone it would replay onto the next real item
                // event, so clear it — rewards already went out by trader mail and the
                // client refetches quests on the way back to the menu.
                eventOutputHolder.ResetOutput(sessionID);
                MarkQuestFinished(sessionID.ToString(), questId);
                logger?.Info($"[Icebreaker] auto handed in quest {questId}");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { logger?.Warning($"[Icebreaker] auto turn-in failed: {e.Message}"); }
    }

    // per-profile crossing ledger. a bare "has been there" bool was enough for one gate,
    // but "go there AGAIN, after quest X" needs to tell trips apart, so we count
    // them and stamp the count reached when each gating quest was finished. the gate is
    // then just Visits > Marks[questId], which cannot be satisfied by trips the player
    // had already banked before taking the quest.
    private sealed class Ledger
    {
        public int Visits { get; set; }
        public Dictionary<string, int> Marks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Dictionary<string, Ledger> Profiles = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;
    private static readonly object Gate = new();

    private static string StorePath =>
        SysPath.Combine(SysPath.GetDirectoryName(typeof(IcebreakerRaidWatchRouter).Assembly.Location)!,
                        "db", "icebreaker_visits.json");

    // total successful crossings, ever. this is the Stick to It gate.
    public static bool HasVisited(string sessionId)
    {
        EnsureLoaded();
        lock (Gate) return Profiles.TryGetValue(sessionId, out var l) && l.Visits > 0;
    }

    // a crossing made AFTER afterQuestId was handed in. false while the quest is
    // unfinished (no mark yet), which is what keeps the next quest hidden.
    public static bool HasVisitedSince(string sessionId, string afterQuestId)
    {
        EnsureLoaded();
        lock (Gate)
            return Profiles.TryGetValue(sessionId, out var l)
                && l.Marks.TryGetValue(afterQuestId, out var mark)
                && l.Visits > mark;
    }

    // stamps the crossing count a quest was finished at, once. callers hit this from
    // both the raid-end pass and the quest-list gate, so a hand-in at the trader screen
    // is marked in the menu rather than waiting for the next raid to end — otherwise a
    // crossing made before we noticed would be swallowed by a too-high mark.
    public static void MarkQuestFinished(string sessionId, string questId)
    {
        EnsureLoaded();
        lock (Gate)
        {
            if (!Profiles.TryGetValue(sessionId, out var l)) Profiles[sessionId] = l = new Ledger();
            if (l.Marks.ContainsKey(questId)) return;
            l.Marks[questId] = l.Visits;
        }
        Save(null);
    }

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!System.IO.File.Exists(StorePath)) return;
                var raw = System.IO.File.ReadAllText(StorePath);
                // the original store was a flat list of session ids. read it as one trip
                // each so an existing profile keeps the gate it already earned.
                if (raw.TrimStart().StartsWith("["))
                {
                    foreach (var id in System.Text.Json.JsonSerializer.Deserialize<List<string>>(raw) ?? [])
                        Profiles[id] = new Ledger { Visits = 1 };
                    return;
                }
                foreach (var kv in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Ledger>>(raw) ?? [])
                    Profiles[kv.Key] = kv.Value;
            }
            catch { }
        }
    }

    private static void Save(ISptLogger<IcebreakerRaidWatchRouter>? logger)
    {
        try
        {
            System.IO.Directory.CreateDirectory(SysPath.GetDirectoryName(StorePath)!);
            Dictionary<string, Ledger> snapshot;
            lock (Gate) snapshot = Profiles.ToDictionary(kv => kv.Key, kv => kv.Value);
            System.IO.File.WriteAllText(StorePath, System.Text.Json.JsonSerializer.Serialize(snapshot));
        }
        catch (Exception e) when (e is not OperationCanceledException) { logger?.Warning($"[Icebreaker] visit store write failed: {e.Message}"); }
    }

    private static ValueTask<string> Watch(
        ISptLogger<IcebreakerRaidWatchRouter> logger,
        SPTarkov.Server.Core.Models.Eft.Match.EndLocalRaidRequestData info,
        MongoId sessionID,
        string? output)
    {
        try
        {
            EnsureLoaded();
            var location = info?.ServerId?.Split('.').FirstOrDefault();
            // ANY raid end on our slot counts as a visit — survive, die, MIA, whatever
            // (user call 08-19: "you can live or die, you just have to go there"). the
            // gated quests are all "you have seen the ship" beats, and a corpse on the
            // deck has very much seen the ship. the one exclusion left is a raid that
            // never really started (the client posts an end even when loading aborts,
            // with no Results block) — no Results = no visit.
            if (info?.Results != null && string.Equals(location, SlotId, StringComparison.OrdinalIgnoreCase))
            {
                int total;
                lock (Gate)
                {
                    if (!Profiles.TryGetValue(sessionID.ToString(), out var l))
                        Profiles[sessionID.ToString()] = l = new Ledger();
                    total = ++l.Visits;
                }
                Save(logger);
                logger.Info($"[Icebreaker] icebreaker visit recorded, outcome {info.Results.Result} (crossing #{total})");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { logger.Warning($"[Icebreaker] raid watch failed: {e.Message}"); }

        return new ValueTask<string>(output ?? string.Empty);
    }
}

// HARD MAP LOCK: Icebreaker stays hidden in map select until the profile completes
// Boreas Part 3. The map is menu-only now, so this route is the sole access gate.
// Mod StaticRouters run after core's for the same route and receive its output; we
// recompute the response with the per-profile flag instead of editing serialized JSON.
// Gate config: db/maplock.json { "finalQuestId": "..." }.
[Injectable(TypePriority = OnLoadOrder.Routers - 1)]
public class IcebreakerLockRouter(
    JsonUtil jsonUtil,
    SPTarkov.Server.Core.Controllers.LocationController locationController,
    SPTarkov.Server.Core.Helpers.Profile.ProfileHelper profileHelper,
    SPTarkov.Server.Core.Utils.HttpResponseUtil httpResponseUtil,
    ISptLogger<IcebreakerLockRouter> logger)
    : SPTarkov.Server.Core.DI.StaticRouter(
        jsonUtil,
        [
            new SPTarkov.Server.Core.DI.RouteAction<SPTarkov.Server.Core.Models.Eft.Common.EmptyRequestData>(
                "/client/locations",
                async (url, info, sessionID, output, cancellationToken) =>
                    await LockLocations(locationController, profileHelper, httpResponseUtil, logger, sessionID)
            ),
        ])
{
    private static string? _finalQuestId;
    private static bool _configLoaded;

    private static string? FinalQuestId(ISptLogger<IcebreakerLockRouter> logger)
    {
        if (_configLoaded) return _finalQuestId;
        _configLoaded = true;
        try
        {
            var modDir = SysPath.GetDirectoryName(typeof(IcebreakerLockRouter).Assembly.Location)!;
            var path = SysPath.Combine(modDir, "db", "maplock.json");
            if (System.IO.File.Exists(path))
            {
                var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("finalQuestId", out var element))
                    _finalQuestId = element.GetString();
            }
        }
        catch (Exception e)
        {
            logger.Warning($"[Icebreaker] map lock config unreadable; leaving map unlocked: {e.Message}");
        }
        return _finalQuestId;
    }

    // LocationBase instances are shared with the database. Mutation and serialization
    // must stay atomic so concurrent profiles cannot observe one another's lock state.
    private static readonly object SerializeGate = new();

    private static ValueTask<string> LockLocations(
        SPTarkov.Server.Core.Controllers.LocationController locationController,
        SPTarkov.Server.Core.Helpers.Profile.ProfileHelper profileHelper,
        SPTarkov.Server.Core.Utils.HttpResponseUtil httpResponseUtil,
        ISptLogger<IcebreakerLockRouter> logger,
        MongoId sessionID)
    {
        var response = locationController.GenerateAll(sessionID);
        var questId = FinalQuestId(logger);
        if (string.IsNullOrEmpty(questId))
            return new ValueTask<string>(httpResponseUtil.GetBody(response));

        lock (SerializeGate)
        {
            try
            {
                var pmc = profileHelper.GetPmcProfile(sessionID);
                bool unlocked = pmc?.Quests?.Any(q =>
                    q.QId.ToString() == questId &&
                    q.Status == SPTarkov.Server.Core.Models.Enums.QuestStatusEnum.Success) == true;
                foreach (var location in response.Locations!.Values)
                {
                    if (location?.IdField.ToString() != "5714dc342459777137212e0b") continue;
                    location.Enabled = unlocked;
                    location.Locked = !unlocked;
                    break;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.Warning($"[Icebreaker] map lock check failed (leaving unlocked): {e.Message}");
            }
            return new ValueTask<string>(httpResponseUtil.GetBody(response));
        }
    }
}
