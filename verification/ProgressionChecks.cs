using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;
using Manimal.Icebreaker.Server;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Quest;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Location;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Routers.Static;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Json;
using Path = System.IO.Path;

internal static class ProgressionChecks
{
    private const string Oil = "6a75661b478c184bd220c433";
    private const string Roman = "8b2b4eea617e2be2e54df123";
    private const string Bitter = "c7a1f0b93e64d5827ab1cc40";
    private const string Hangover = "3f8d2c5a9b17e04d6ca8f312";
    private const string Boreas = "9d5e3f7d6320a7fd139a2772";
    private static readonly string[] FirstVisit = { Oil, "e857e9c34949ecbf1cf5a5b2", "6a752a6bc498772c6a150baf", "6a753b58478c184bd220c417", "6a757a2f478c184bd220c458", "6a7e0916b881de241018539d" };
    private static PmcData profile = new() { Quests = [] };
    private static readonly LocationsGenerateAllResponse maps = new()
    {
        Locations = new() { ["5714dc342459777137212e0b"] = new LocationBase { Id = "Suburbs", IdField = "5714dc342459777137212e0b", Enabled = true, Locked = false } }
    };

    internal static async Task Run()
    {
        foreach (var config in new string?[] { null, "{}", "{\"finalQuestId\":null}", "{\"finalQuestId\":\"\"}", "{\"finalQuestId\":\"  \"}" })
            Check(IcebreakerLockRouter.ReadQuestId(config) == null, "map lock disabled by " + (config ?? "missing file"));
        Check(IcebreakerLockRouter.ReadQuestId("{\"finalQuestId\":\"" + Boreas + "\"}") == Boreas, "configured Boreas requirement retained");
        var directory = Path.GetFullPath(Path.Combine(".tmp", "progression-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "visits.json");
            var visits = new IcebreakerVisitLedger(path);
            Check(Filtered(visits).Count == 0, "fresh profile hides all eight crossing-gated quests");
            foreach (var status in new[] { QuestStatusEnum.Locked, QuestStatusEnum.AvailableForStart, QuestStatusEnum.AvailableAfter })
            {
                profile.Quests = [Status(Oil, status)];
                Check(Filtered(visits).Count == 0, $"{status} profile entries cannot bypass visit gates");
            }
            foreach (var status in new[] { QuestStatusEnum.Started, QuestStatusEnum.AvailableForFinish, QuestStatusEnum.Success })
            {
                profile.Quests = [Status(Oil, status)];
                Check(Filtered(visits).Single().Id == Oil, $"preserve existing {status} quest progress");
            }
            profile.Quests = [];
            Check(visits.Record("p1", "suburbs.pmc 1") == 1, "first crossing recorded");
            Check(Filtered(visits).Count == FirstVisit.Length, "first crossing unlocks all six opener quests");
            Check(visits.Record("p1", "suburbs.pmc 1") == null, "retried raid end is not a second crossing");
            profile.Quests = [Status(Roman, QuestStatusEnum.Success)];
            Check(!Filtered(visits).Any(q => q.Id == Bitter), "old crossings cannot unlock Bitter Victory");
            Check(visits.Record("p1", "suburbs.pmc 2") == 2 && Filtered(visits).Any(q => q.Id == Bitter), "crossing after Roman unlocks Bitter Victory");
            profile.Quests.Add(Status(Bitter, QuestStatusEnum.Success));
            Check(!Filtered(visits).Any(q => q.Id == Hangover), "Hangover requires another crossing after Bitter Victory");
            visits.Record("p1", "suburbs.pmc 3");
            Check(Filtered(visits).Any(q => q.Id == Hangover), "later crossing unlocks Hangover");
            visits = new IcebreakerVisitLedger(path);
            Check(visits.HasVisitedSince("p1", Bitter) && visits.Record("p1", "suburbs.pmc 3") == null, "visits, marks and retry protection survive restart");
            Check(!visits.HasVisited("p2") && visits.Record("p2", "suburbs.pmc 3") == 1, "Fika participants have independent progression");
            foreach (var outcome in Enum.GetValues<ExitStatus>())
                Check(visits.RecordRaidEnd("outcome-" + outcome, new EndLocalRaidRequestData { ServerId = "Suburbs.pmc 42", Results = new EndRaidResult { Result = outcome } }) == 1,
                    "SPT 4.1 raid-end records " + outcome);
            Check(visits.RecordRaidEnd("aborted", new EndLocalRaidRequestData { ServerId = "suburbs.pmc 43" }) == null && !visits.HasVisited("aborted"), "aborted load does not count");
            Check(visits.RecordRaidEnd("other-map", new EndLocalRaidRequestData { ServerId = "bigmap.pmc 43", Results = new() { Result = Enum.GetValues<ExitStatus>()[0] } }) == null && !visits.HasVisited("other-map"), "other maps do not count");
            File.WriteAllText(path, "[\"legacy\"]");
            Check(new IcebreakerVisitLedger(path).HasVisited("legacy"), "legacy boolean ledger retains earned visits");
            File.WriteAllText(path, "{\"legacy\":{\"Visits\":2,\"Marks\":{\"" + Roman + "\":1}}}");
            Check(new IcebreakerVisitLedger(path).HasVisitedSince("legacy", Roman), "existing count-and-mark ledger migrates");
            File.WriteAllText(path, "broken json");
            try { new IcebreakerVisitLedger(path).Record("p1", "raid"); throw new Exception("corrupt ledger silently overwritten"); }
            catch (JsonException) { Check(File.ReadAllText(path) == "broken json", "unreadable ledger is preserved for recovery"); }

            await Routes();
        }
        finally
        {
            // This uniquely created test directory contains only our synthetic ledger.
            if (!directory.StartsWith(Path.GetFullPath(".tmp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Test cleanup escaped workspace");
            Directory.Delete(directory, recursive: true);
        }
    }

    private static List<Quest> Candidates() => FirstVisit.Concat(new[] { Bitter, Hangover }).Select(id => new Quest
    {
        Id = id, Type = QuestTypeEnum.Completion, TraderId = "54cb50c76803fa8b248b4571", Side = "Pmc",
        CanShowNotificationsInGame = false, Conditions = new(), Description = "test", Name = "test",
        Location = "any", Image = "", Restartable = false
    }).ToList();
    private static List<Quest> Filtered(IcebreakerVisitLedger visits)
    {
        var quests = Candidates();
        IcebreakerFlyerGateRouter.FilterQuests(quests, profile, "p1", visits);
        return quests;
    }
    private static QuestStatus Status(string id, QuestStatusEnum status) => new() { QId = id, Status = status, StartTime = 0, StatusTimers = [] };
    private static T Uninitialized<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static int Priority(Type type) => type.GetCustomAttribute<Injectable>()!.TypePriority;

    private static async Task Routes()
    {
        // Replace only data providers with synthetic fixtures. Exercise the real
        // SPT HttpRouter, mod routers, JSON serialization and Harmony bindings.
        var fixtures = new Harmony("verification.progression.fixtures");
        foreach (var (type, method, prefix) in new[] {
            (typeof(ProfileHelper), "GetPmcProfile", nameof(Profile)),
            (typeof(QuestController), "GetClientQuests", nameof(Quests)),
            (typeof(QuestHelper), "GetClientQuests", nameof(Quests)),
            (typeof(QuestHelper), "GetNewlyAccessibleQuestsWhenStartingQuest", nameof(Quests)),
            (typeof(LocationController), "GenerateAll", nameof(Maps)) })
            fixtures.Patch(AccessTools.Method(type, method), prefix: new HarmonyMethod(typeof(ProgressionChecks), prefix));
        try
        {
            AccessTools.Field(typeof(IcebreakerLockRouter), "_configLoaded").SetValue(null, true);
            AccessTools.Field(typeof(IcebreakerLockRouter), "_finalQuestId").SetValue(null, Boreas);
            profile = new() { Quests = [] };
            var json = new JsonUtil([new SptJsonConverterRegistrator()]);
            var response = new HttpResponseUtil(json, null!);
            var profiles = Uninitialized<ProfileHelper>();
            var gate = new IcebreakerFlyerGateRouter(json, Uninitialized<QuestController>(), null!, profiles, null!, response, null!);
            var map = new IcebreakerLockRouter(json, Uninitialized<LocationController>(), new SPTarkov.Server.Core.Utils.Cloners.FastCloner(), profiles, response, null!);
            var session = new MongoId("111111111111111111111111");
            foreach (var (mod, coreType, url, original) in new[] {
                ((StaticRouter)gate, typeof(QuestStaticRouter), "/client/quest/list", response.GetBody(Candidates())),
                ((StaticRouter)map, typeof(LocationStaticRouter), "/client/locations", response.GetBody(maps)) })
            {
                Check(Priority(mod.GetType()) > Priority(coreType), url + " runs after SPT's core handler");
                var routes = new[] { (Priority(coreType), (StaticRouter)new CoreFixture(json, url, original)), (Priority(mod.GetType()), mod) };
                var http = new HttpRouter(routes.OrderBy(pair => pair.Item1).Select(pair => pair.Item2), []);
                var context = new DefaultHttpContext();
                context.Request.Path = url;
                var body = (string)(await http.GetResponseObjectAsync(context.Request, session, "{}"))!;
                using var document = JsonDocument.Parse(body);
                if (url.Contains("quest")) Check(document.RootElement.GetProperty("data").GetArrayLength() == 0, "final HTTP quest response retains crossing filter");
                else
                {
                    Check(!document.RootElement.GetProperty("data").GetProperty("locations").GetProperty("5714dc342459777137212e0b").GetProperty("Enabled").GetBoolean(), "fresh profile map entry hidden in final HTTP response");
                    Check(maps.Locations!["5714dc342459777137212e0b"].Enabled == true, "profile map lock does not mutate shared database");
                }
            }
            foreach (var status in new[] { QuestStatusEnum.Started, QuestStatusEnum.AvailableForFinish, QuestStatusEnum.Success })
            {
                profile.Quests = [Status(Boreas, status)];
                var body = (string)await map.HandleStaticAsync("/client/locations", "{}", session, "");
                using var document = JsonDocument.Parse(body);
                var location = document.RootElement.GetProperty("data").GetProperty("locations").GetProperty("5714dc342459777137212e0b");
                Check(location.GetProperty("Enabled").GetBoolean() == (status == QuestStatusEnum.Success) && location.GetProperty("Locked").GetBoolean() != (status == QuestStatusEnum.Success), "Boreas " + status + " map state");
            }
            profile.Quests = [];
            AccessTools.Field(typeof(IcebreakerLockRouter), "_finalQuestId").SetValue(null, null);
            using (var disabled = JsonDocument.Parse((string)await map.HandleStaticAsync("/client/locations", "{}", session, "")))
            {
                var location = disabled.RootElement.GetProperty("data").GetProperty("locations").GetProperty("5714dc342459777137212e0b");
                Check(location.GetProperty("Enabled").GetBoolean() && !location.GetProperty("Locked").GetBoolean(), "disabled map lock shows entry for a fresh profile");
            }
            await new IcebreakerProgression(profiles).OnLoadAsync(CancellationToken.None);
            var helper = Uninitialized<QuestHelper>();
            Check(helper.GetClientQuests(session).Count == 0, "QuestHelper list filter executes on SPT 4.1");
            Check(helper.GetNewlyAccessibleQuestsWhenStartingQuest(Boreas, session).Count == 0, "item-event quest deltas retain crossing filter");
            var beforeRaid = AccessTools.Method(typeof(SPTarkov.Server.Core.Services.InRaid.LocationLifecycleService), "StartLocalRaidAsync");
            Check(Harmony.GetPatchInfo(beforeRaid).Prefixes.Any(p => p.owner == "com.manimal.icebreaker.progression"), "prerequisite stamp installed before raid departure");
        }
        finally
        {
            new Harmony("com.manimal.icebreaker.progression").UnpatchSelf(); fixtures.UnpatchSelf();
            AccessTools.Field(typeof(IcebreakerLockRouter), "_configLoaded").SetValue(null, false);
            AccessTools.Field(typeof(IcebreakerLockRouter), "_finalQuestId").SetValue(null, null);
        }
    }

    private static bool Profile(ref PmcData __result) { __result = profile; return false; }
    private static bool Quests(ref List<Quest> __result) { __result = Candidates(); return false; }
    private static bool Maps(ref LocationsGenerateAllResponse __result) { __result = maps; return false; }
    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private sealed class CoreFixture(JsonUtil json, string url, string output) : StaticRouter(json,
        [new RouteAction<EmptyRequestData>(url, (_, _, _, _, _) => new ValueTask<string>(output))]);
}
