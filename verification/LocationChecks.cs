using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Manimal.Icebreaker.Server;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;
using Path = System.IO.Path;

internal static class LocationChecks
{
    internal static async Task Run(string install)
    {
        const string key = IcebreakerLocationRegistration.Key;
        const string id = IcebreakerLocationRegistration.Id;
        var json = new JsonUtil([new SptJsonConverterRegistrator()]);
        var props = XDocument.Load("Directory.Build.props").Root!.Element("PropertyGroup")!;
        Check(key == props.Element("ModLocationKey")!.Value && id == props.Element("ModLocationId")!.Value,
            "compiled map identity matches the shared build properties");
        using var authoredBase = JsonDocument.Parse(File.ReadAllText("icebreaker-server/db/base.json"));
        Check(authoredBase.RootElement.GetProperty("Id").GetString() == key && authoredBase.RootElement.GetProperty("_Id").GetString() == id,
            "packaged base identity matches compiled registration");
        int quests = 0;
        foreach (var file in Directory.GetFiles("icebreaker-server/db/CustomQuests", "*.json", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            Check(!text.Contains("5714dc342459777137212e0b"), "quest data has no Suburbs reference: " + Path.GetFileName(file));
            if (text.Contains(id)) quests++;
        }
        Check(quests == 4, "all four retained Icebreaker quest location references migrated");

        var table = Activator.CreateInstance<LocationTable>();
        int ordinal = 1;
        foreach (var property in typeof(LocationTable).GetProperties())
        {
            if (property.PropertyType == typeof(Location))
                property.SetValue(table, new Location { Base = new LocationBase
                {
                    Id = property.Name, IdField = ordinal++.ToString("x24"), Enabled = false,
                    BossLocationSpawn = []
                }, StaticAmmo = [] });
            else if (property.Name == "Base") property.SetValue(table, Activator.CreateInstance(property.PropertyType));
        }
        string data = Path.Combine(install, "SPT_Runtime", "SPT_Data");
        table.Suburbs!.Base = json.Deserialize<LocationBase>(File.ReadAllText(Path.Combine(data, "database", "locations", "suburbs", "base.json")))!;
        var originalSuburbs = table.Suburbs.Base;
        string suburbsBefore = json.Serialize(originalSuburbs)!;
        var dictionaries = table.GetDictionary();
        int originalCount = dictionaries.Count;
        foreach (var file in Directory.GetFiles(Path.Combine(data, "database", "locations"), "base.json", SearchOption.AllDirectories))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var entry = document.RootElement;
            if (!entry.TryGetProperty("Id", out var existingKey)) continue; // location path/link table
            Check((!entry.TryGetProperty("_Id", out var existingId) || existingId.GetString() != id) &&
                !string.Equals(existingKey.GetString(), key, StringComparison.OrdinalIgnoreCase),
                "new identity does not collide with installed " + Path.GetFileName(Path.GetDirectoryName(file)));
        }
        var locales = new LocaleTable
        {
            Global = new() { ["en"] = new LazyLoad<GlobalLocaleDictionary>(() => new GlobalLocaleDictionary
            {
                ["Suburbs"] = "Suburbs", ["5714dc342459777137212e0b Name"] = "Suburbs",
                ["5714dc342459777137212e0b Description"] = "Original description"
            }, cacheValue: true) }, Menu = [], Languages = []
        };
        var bots = json.Deserialize<BotConfig>(File.ReadAllText(Path.Combine(data, "configs", "bot.json")))!;
        var config = json.Deserialize<LocationConfig>(File.ReadAllText(Path.Combine(data, "configs", "location.json")))!;
        // Prove preservation of user-configured slot balance instead of only defaults.
        config.StaticLootMultiplier["suburbs"] = 1.25;
        config.LooseLootMultiplier["suburbs"] = 1.5;
        var cloner = new SPTarkov.Server.Core.Utils.Cloners.FastCloner();
        await new IcebreakerMod(table, locales, bots, config, cloner, json, null!, Quiet<IcebreakerMod>()).OnLoadAsync(CancellationToken.None);
        var ice = table.GetLocation(key)!;
        Check(ice != null && !ReferenceEquals(ice, table.Suburbs), "real mod startup creates an independent Location");
        Check(ReferenceEquals(dictionaries, table.GetDictionary()) && dictionaries.Count == originalCount + 1,
            "startup adds exactly one location to persistent storage");
        Check(ReferenceEquals(ice, table.GetLocation("Icebreaker")), "mixed-case lookup resolves Icebreaker");
        Check(ReferenceEquals(originalSuburbs, table.Suburbs.Base) && json.Serialize(originalSuburbs) == suburbsBefore,
            "startup preserves the actual installed Suburbs base byte-for-byte after serialization");
        var response = new LocationController(Quiet<LocationController>(), table, null!).GenerateAll("ffffffffffffffffffffff02");
        Check(response.Locations!.Count == originalCount + 1 && response.Locations.ContainsKey(id) && response.Locations.ContainsKey(originalSuburbs.IdField),
            "client locations response contains Icebreaker and original Suburbs under distinct IDs");
        Check(ice!.Base.Enabled == true && ice.Base.DisabledForScav == true && ice.Base.Scene!.Path == "maps/icebreaker.bundle",
            "new location retains PMC-only entry and the existing scene preset");
        var locale = locales.Global["en"].Value!;
        Check(locale[id + " Name"] == "Icebreaker" && locale[key] == "Icebreaker" &&
            locale["Suburbs"] == "Suburbs" && locale["5714dc342459777137212e0b Name"] == "Suburbs" &&
            locale["5714dc342459777137212e0b Description"] == "Original description", "locale registration leaves Suburbs intact");
        Check(bots.MaxBotCap[key] == 40 && config.StaticLootMultiplier[key] == 1.25 && config.LooseLootMultiplier[key] == 1.5,
            "new map retains the 40-bot cap and existing configured loot balance");
        Check(config.ScavRaidTimeSettings.Maps.ContainsKey(key) &&
            !ReferenceEquals(config.ScavRaidTimeSettings.Maps[key], config.ScavRaidTimeSettings.Maps["factory4_day"]),
            "raid-time settings exist independently of the donor map");
        Check(ice.StaticLoot?.Value?.Count > 0 && ice.StaticContainers?.Value != null && ice.StaticAmmo != null,
            "new location has authored container pools, containers and ammo");
        var firstLoot = ice.LooseLoot!.Value!;
        var secondLoot = ice.LooseLoot.Value!;
        Check(!ReferenceEquals(firstLoot, secondLoot) && firstLoot.Spawnpoints!.Any() && secondLoot.Spawnpoints!.Any(),
            "loose loot stays fresh and populated on successive raids");
        var pmc = json.Deserialize<PmcConfig>(File.ReadAllText(Path.Combine(data, "configs", "pmc.json")))!;
        var raidBase = cloner.Clone(ice.Base)!;
        string wavesBefore = json.Serialize(raidBase.BossLocationSpawn)!;
        new PmcWaveGenerator(table, pmc).ApplyWaveChangesToMap(raidBase);
        Check(wavesBefore == json.Serialize(raidBase.BossLocationSpawn), "native PMC wave processing preserves authored Icebreaker waves");
        // Execute the real seasonal rewriter: a formerly excluded map must not
        // acquire event hostility overrides just because its registry key changed.
        var seasonalType = typeof(SPTarkov.Server.Core.Services.Server.SeasonalEventService);
        var seasonal = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(seasonalType);
        foreach (var field in seasonalType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (field.FieldType == typeof(LocationTable)) field.SetValue(seasonal, table);
            if (field.FieldType == typeof(LocationConfig)) field.SetValue(seasonal, config);
        }
        var hostilities = ice.Base.BotLocationModifier!.AdditionalHostilitySettings!;
        Check(hostilities.Any(), "authored crew hostility settings are present");
        var replacement = cloner.Clone(hostilities.First())!;
        replacement.BearEnemyChance = replacement.BearEnemyChance == 100 ? 0 : 100;
        var eventSettings = new Dictionary<string, List<AdditionalHostilitySettings>> { ["default"] = [replacement] };
        string hostilityBefore = json.Serialize(hostilities)!;
        seasonalType.GetMethod("ReplaceBotHostility", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(seasonal, new object?[] { eventSettings, null });
        Check(json.Serialize(hostilities) == hostilityBefore, "seasonal events preserve authored Icebreaker crew hostility");
        ExpectCollision(() => IcebreakerLocationRegistration.Register(table, ice), "duplicate registration is rejected");
        dictionaries.Remove(key);
        dictionaries.Add("another-mod", ice);
        ExpectCollision(() => IcebreakerLocationRegistration.Register(table, ice), "duplicate Mongo ID under another key is rejected");
        dictionaries.Remove("another-mod");
        dictionaries.Add("another-mod", new Location { Base = new LocationBase { Id = "Icebreaker", IdField = "ffffffffffffffffffffff03" } });
        ExpectCollision(() => IcebreakerLocationRegistration.Register(table, ice), "duplicate runtime name under another key is rejected");
    }

    private static ISptLogger<T> Quiet<T>() => DispatchProxy.Create<ISptLogger<T>, QuietLogger>();
    public class QuietLogger : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.ReturnType == typeof(bool) ? false : null;
    }
    private static void ExpectCollision(Action action, string message)
    {
        try { action(); } catch (InvalidOperationException) { Check(true, message); return; }
        throw new Exception(message);
    }
    private static void Check(bool pass, string message)
    {
        if (!pass) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
}
