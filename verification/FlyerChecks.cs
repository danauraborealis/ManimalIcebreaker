using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Manimal.Icebreaker.Server;
using Microsoft.AspNetCore.Http;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Routers.Static;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;
using HarmonyLib;

internal static class FlyerChecks
{
    private const string Boreas = "ff56532d9100ce03006493e9";
    private const string Flyer = "699f0b877c23862b4b0ee19c";
    private const string Discovery = "d9e685a0b6b54ccaa39ef120";
    private static PmcData profile = new() { Quests = [], Inventory = new() { Items = [] } };

    internal static async Task Run()
    {
        using var document = JsonDocument.Parse(File.ReadAllText("icebreaker-server/db/CustomQuests/mechanic/Quests/BoreasQuests.json"));
        var quest = document.RootElement.GetProperty(Boreas);
        var start = quest.GetProperty("conditions").GetProperty("AvailableForStart");
        Check(start.GetArrayLength() == 1 && start[0].GetProperty("conditionType").GetString() == "FindItem" &&
            start[0].GetProperty("target")[0].GetString() == Flyer && start[0].GetProperty("id").GetString() == Discovery &&
            start[0].GetProperty("value").GetInt32() == 1 && !start[0].GetProperty("onlyFoundInRaid").GetBoolean() &&
            !start[0].GetProperty("countInRaid").GetBoolean(), "Boreas starts only on flyer possession, including purchases and raid pickups");
        Check(quest.GetProperty("status").GetInt32() == 0 && quest.GetProperty("canShowNotificationsInGame").GetBoolean(),
            "Boreas begins locked and permits immediate native quest notification");
        Check(quest.GetProperty("conditions").GetProperty("AvailableForFinish").GetArrayLength() == 2,
            "Boreas handover and tower repair objectives retained");
        Check(!IcebreakerFlyerDiscovery.Observe(profile), "fresh profile cannot discover Boreas without owning a flyer");
        profile.Quests.Add(new() { QId = Boreas, Status = QuestStatusEnum.AvailableForStart, StartTime = 0, StatusTimers = [] });
        Check(!IcebreakerFlyerDiscovery.Observe(profile), "old level/loyalty availability does not count as flyer discovery");
        profile.Inventory!.Items!.Add(new() { Id = "111111111111111111111110", Template = Flyer, ParentId = "111111111111111111111112" });
        Check(IcebreakerFlyerDiscovery.Observe(profile), "purchased flyer in a nested owned container discovers Boreas");
        profile.Inventory!.Items!.Clear();
        Check(IcebreakerFlyerDiscovery.Observe(profile) && profile.Quests.Single().CompletedConditions!.Contains(Discovery),
            "discovery remains after the flyer leaves inventory");
        profile.Quests = [new() { QId = Boreas, Status = QuestStatusEnum.Locked, StartTime = 0, StatusTimers = [], CompletedConditions = [Discovery] }];
        Check(IcebreakerFlyerDiscovery.Observe(profile) && profile.Quests.Single().Status == QuestStatusEnum.AvailableForStart,
            "saved raid discovery unlocks Boreas even if the flyer was lost on death");
        profile.Quests = [new() { QId = Boreas, Status = QuestStatusEnum.Started, StartTime = 42, StatusTimers = [] }];
        Check(IcebreakerFlyerDiscovery.Observe(profile) && profile.Quests.Single().StartTime == 42 && profile.Quests.Single().Status == QuestStatusEnum.Started,
            "upgrade preserves an already accepted Boreas quest");

        var fixtures = new Harmony("verification.flyer.fixtures");
        fixtures.Patch(AccessTools.Method(typeof(ProfileHelper), nameof(ProfileHelper.GetPmcProfile)),
            prefix: new HarmonyMethod(typeof(FlyerChecks), nameof(Profile)));
        try
        {
            profile = new() { Quests = [], Inventory = new() { Items = [] } };
            var json = new JsonUtil([new SptJsonConverterRegistrator()]);
            var router = new IcebreakerFlyerInventoryRouter(json, (ProfileHelper)RuntimeHelpers.GetUninitializedObject(typeof(ProfileHelper)));
            var priority = typeof(IcebreakerFlyerInventoryRouter).GetCustomAttribute<SPTarkov.DI.Annotations.Injectable>()!.TypePriority;
            Check(priority > typeof(ItemEventStaticRouter).GetCustomAttribute<SPTarkov.DI.Annotations.Injectable>()!.TypePriority,
                "flyer persistence runs after the core inventory transaction");
            var http = new HttpRouter([new CorePurchase(json), router], []);
            var context = new DefaultHttpContext();
            context.Request.Path = "/client/game/profile/items/moving";
            var response = await http.GetResponseObjectAsync(context.Request, new MongoId("111111111111111111111111"), "{}");
            Check((string)response! == "purchase-response" && profile.Quests.Single().CompletedConditions!.Contains(Discovery),
                "purchase HTTP response is preserved and flyer discovery persists before another login");
        }
        finally { fixtures.UnpatchSelf(); }
    }

    internal static void Client(Assembly client)
    {
        var unlock = client.GetType("Manimal.Icebreaker.IcebreakerFlyerUnlock", true)!;
        var preserve = unlock.GetMethod("PreserveDiscovery", BindingFlags.Static | BindingFlags.NonPublic)!;
        var questType = preserve.GetParameters()[0].ParameterType;
        var conditionType = preserve.GetParameters()[1].ParameterType;
        var quest = RuntimeHelpers.GetUninitializedObject(questType);
        var condition = RuntimeHelpers.GetUninitializedObject(conditionType);
        questType.GetField("string_0")!.SetValue(quest, Boreas);
        var mongoType = conditionType.GetProperty("id")!.PropertyType;
        var discoveryId = Activator.CreateInstance(mongoType, Discovery)!;
        conditionType.GetProperty("id")!.SetValue(condition, discoveryId);
        var conditionsProperty = questType.GetProperty("CompletedConditions")!;
        var completed = Activator.CreateInstance(conditionsProperty.PropertyType)!;
        conditionsProperty.SetValue(quest, completed);
        var statusProperty = questType.GetProperty("QuestStatus")!;
        var locked = Enum.Parse(statusProperty.PropertyType, "Locked");
        statusProperty.SetValue(quest, locked);
        object[] countArgs = [quest, condition, 0];
        preserve.Invoke(null, countArgs);
        Check((int)countArgs[2] == 0, "compiled client does not manufacture flyer discovery");
        completed.GetType().GetMethod("Add")!.Invoke(completed, [discoveryId]);
        preserve.Invoke(null, countArgs);
        Check((int)countArgs[2] == 1, "compiled client preserves saved discovery without another flyer");
        var visibility = unlock.GetNestedType("Patch_Visibility", BindingFlags.NonPublic)!.GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic)!;
        object[] visibleArgs = [quest, true];
        visibility.Invoke(null, visibleArgs);
        Check(!(bool)visibleArgs[1], "compiled client hides locked Boreas even though its template is loaded");
        statusProperty.SetValue(quest, Enum.Parse(statusProperty.PropertyType, "AvailableForStart"));
        visibleArgs[1] = true;
        visibility.Invoke(null, visibleArgs);
        Check((bool)visibleArgs[1], "compiled client reveals Boreas immediately when native inventory condition unlocks it");

        // Drive EFT's actual state machine with a synthetic inventory count. The
        // inventory/Unity world is the only fixture; comparison, completion marking,
        // status transitions and our built discovery latch execute unchanged.
        completed.GetType().GetMethod("Clear")!.Invoke(completed, null);
        statusProperty.SetValue(quest, locked);
        conditionType.GetProperty("value")!.SetValue(condition, 1f);
        var compare = conditionType.GetProperty("compareMethod")!;
        compare.SetValue(condition, Enum.Parse(compare.PropertyType, "MoreOrEqual"));
        var templateType = questType.GetField("_template")!.FieldType;
        var template = RuntimeHelpers.GetUninitializedObject(templateType);
        var conditionsType = templateType.GetProperty("Conditions")!.PropertyType;
        var conditions = Activator.CreateInstance(conditionsType)!;
        var collectionType = conditionsType.BaseType!.GenericTypeArguments[1];
        var collection = Activator.CreateInstance(collectionType)!;
        collectionType.GetMethods().Single(m => m.Name == "Add" && m.GetParameters().Length == 1 &&
            m.GetParameters()[0].ParameterType.Name == "Condition").Invoke(collection, [condition]);
        var available = Enum.Parse(statusProperty.PropertyType, "AvailableForStart");
        conditionsType.GetMethod("Add")!.Invoke(conditions, [available, collection]);
        templateType.GetProperty("Conditions")!.SetValue(template, conditions);
        questType.GetField("_template")!.SetValue(quest, template);
        var checkersType = questType.GetField("dictionary_0")!.FieldType;
        var checkers = Activator.CreateInstance(checkersType)!;
        var checkerType = checkersType.GenericTypeArguments[1];
        var checker = Activator.CreateInstance(checkerType, condition)!;
        int inventoryCount = 0;
        Func<double> currentCount = () => {
            object[] values = [quest, condition, inventoryCount];
            preserve.Invoke(null, values);
            return (int)values[2];
        };
        var getterField = checkerType.GetField("_currentValueGetter")!;
        var parameter = System.Linq.Expressions.Expression.Parameter(checkerType);
        var getter = System.Linq.Expressions.Expression.Lambda(getterField.FieldType,
            System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(currentCount)), parameter).Compile();
        getterField.SetValue(checker, getter);
        checkersType.GetMethod("Add")!.Invoke(checkers, [condition, checker]);
        questType.GetField("dictionary_0")!.SetValue(quest, checkers);
        var timersType = questType.GetField("StatusStartTimestamps")!.FieldType;
        var timers = Activator.CreateInstance(timersType)!;
        timersType.GetMethod("Add")!.Invoke(timers, [available, 1d]);
        questType.GetField("StatusStartTimestamps")!.SetValue(quest, timers);
        var dataField = questType.GetField("questDataClass")!;
        dataField.SetValue(quest, Activator.CreateInstance(dataField.FieldType));
        var transition = questType.GetMethods().Single(m => m.Name == "CheckForStatusChange" && m.GetParameters().Length == 6);
        object?[] transitionArgs = [available, true, false, false, null, false];
        transition.Invoke(quest, transitionArgs);
        Check(statusProperty.GetValue(quest)!.Equals(locked), "native EFT transition keeps Boreas locked without the flyer");
        inventoryCount = 1;
        transition.Invoke(quest, transitionArgs);
        Check(statusProperty.GetValue(quest)!.Equals(available) &&
            (bool)completed.GetType().GetMethod("Contains")!.Invoke(completed, [discoveryId])!,
            "native EFT transition unlocks and saves discovery immediately on inventory count changing to one");
        inventoryCount = 0;
        transition.Invoke(quest, transitionArgs);
        Check(statusProperty.GetValue(quest)!.Equals(available), "native EFT transition cannot relock discovered Boreas after losing the flyer");
    }

    private static bool Profile(ref PmcData __result) { __result = profile; return false; }
    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private sealed class CorePurchase(JsonUtil json) : StaticRouter(json,
        [new RouteAction<EmptyRequestData>("/client/game/profile/items/moving", (_, _, _, _, _) =>
        {
            profile.Inventory!.Items!.Add(new() { Id = "111111111111111111111110", Template = Flyer });
            return new ValueTask<string>("purchase-response");
        })]);
}
