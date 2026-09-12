using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Manimal.Icebreaker.Server;
using Mono.Cecil;
using System.Text.Json;
using SPTarkov.Server.Core.Generators.Loot;
using SPTarkov.Server.Core.Models.Spt.Tables;

// Run without launching EFT or reading/writing any player profiles.
if (args.Length != 2 && args.Length != 4) throw new ArgumentException("Usage: verification <SPT install> <built client DLL> [Fika install] [built addon DLL]");
// Reproduce the dictionary contract used by loot mods. Duplicate candidates or
// distribution keys must fail here instead of dropping all loot at raid load.
using (var loot = JsonDocument.Parse(File.ReadAllText("icebreaker-server/db/looseLoot.json")))
{
    int checkedPools = 0;
    foreach (var point in loot.RootElement.GetProperty("spawnpoints").EnumerateArray())
    {
        var candidates = new Dictionary<string, string>();
        foreach (var item in point.GetProperty("template").GetProperty("Items").EnumerateArray())
            candidates.Add(item.GetProperty("composedKey").GetString()!, item.GetProperty("_tpl").GetString()!);
        var weights = new HashSet<string>();
        foreach (var weight in point.GetProperty("itemDistribution").EnumerateArray())
        {
            var key = weight.GetProperty("composedKey").GetProperty("key").GetString()!;
            if (!weights.Add(key) || !candidates.ContainsKey(key))
                throw new Exception("Invalid loose-loot distribution key: " + key);
        }
        if (weights.Count != candidates.Count) throw new Exception("Unreferenced loose-loot candidate");
        checkedPools++;
    }
    Check(checkedPools > 0, $"{checkedPools} loose-loot pools have unique candidates and matching weights");
}
var metadata = new ModMetadata();
Check(metadata.SptVersion.IsSatisfied("4.1.5"), "SPT 4.1.5 accepted");
Check(!metadata.SptVersion.IsSatisfied("4.0.13") && !metadata.SptVersion.IsSatisfied("4.2.0"), "Other SPT minor versions rejected");
foreach (var (guid, current, previous) in new[] {
    ("com.wtt.commonlib", "3.0.6", "2.0.23"),
    ("com.wtt.contentbackport", "2.0.1", "1.1.4"),
    ("com.morebotsapi.tacticaltoaster", "2.1.1", "2.0.1"),
    ("com.blackdiv.tacticaltoaster", "1.3.1", "1.2.1"),
    ("com.manimal.csgas", "2.0.0", "1.1.0") })
{
    var range = metadata.ModDependencies![guid];
    Check(range.IsSatisfied(current) && !range.IsSatisfied(previous), guid + " version floor");
}
await ProgressionChecks.Run();

// Exercise the real Harmony transpiler against SPT 4.1.5's method body. This
// catches a missing/changed call target and invalid emitted IL without a raid.
await new IcebreakerLootFirewall(null!, null!).OnLoadAsync(CancellationToken.None);
var owner = typeof(SPTarkov.Server.Core.Services.InRaid.LocationLifecycleService);
var target = AccessTools.Method(owner, "GenerateLocationAndLoot");
Check(Harmony.GetPatchInfo(target).Transpilers.Any(p => p.owner == "com.manimal.icebreaker.lootfirewall"), "Loot isolation patch installs on 4.1");
var transpiler = AccessTools.Method(typeof(IcebreakerLootFirewall), "WrapLootGeneration");
var call = new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(LocationLootGenerator), "GenerateLocationLoot"));
var output = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { new[] { call } })!).ToArray();
Check(output.Length == 1 && output[0].opcode == OpCodes.Call && ((MethodInfo)output[0].operand).Name == "GenerateIsolated", "Loot call replaced exactly once");
try
{
    _ = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { Array.Empty<CodeInstruction>() })!).ToArray();
    throw new Exception("Missing loot target was silently accepted");
}
catch (InvalidOperationException) { Console.WriteLine("PASS Missing loot target fails explicitly"); }
new Harmony("com.manimal.icebreaker.lootfirewall").UnpatchSelf();

using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[0], "EscapeFromTarkov_Data", "Managed", "Assembly-CSharp.dll"));
using var client = AssemblyDefinition.ReadAssembly(args[1]);
var gameTypes = AllTypes(game.MainModule.Types).ToDictionary(t => t.FullName);
var stakes = gameTypes["EFT.SynchronizableObjects.TripwireSynchronizableObject"].Methods.Single(m => m.Name == "SetupStakes");
Check(stakes.Body.Instructions.Any(i => i.Operand is MethodReference m &&
    m.DeclaringType.FullName == "EFT.ObjectsFactory" && m.Name == "CreateItem" &&
    m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] {
        "EFT.InventoryLogic.Item", "EFT.CameraControl.ECameraType", "EFT.IPlayer", "System.Boolean" })),
    "Native tripwire SetupStakes uses the repaired CreateItem overload");
using var fika = args.Length == 4 ? AssemblyDefinition.ReadAssembly(Path.Combine(args[2], "BepInEx", "plugins", "Fika", "Fika.Core.dll")) : null;
using var addon = args.Length == 4 ? AssemblyDefinition.ReadAssembly(args[3]) : null;
if (fika != null)
    foreach (var type in AllTypes(fika.MainModule.Types)) gameTypes[type.FullName] = type;
int checkedTargets = 0;
var parameterErrors = new List<string>();
foreach (var patch in AllTypes(client.MainModule.Types).Concat(addon == null ? Enumerable.Empty<TypeDefinition>() : AllTypes(addon.MainModule.Types)))
foreach (var attr in patch.CustomAttributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
{
    var typeArg = attr.ConstructorArguments.FirstOrDefault(a => a.Type.FullName == "System.Type").Value as TypeReference;
    var name = attr.ConstructorArguments.FirstOrDefault(a => a.Type.FullName == "System.String").Value as string;
    if (typeArg == null || name == null || !gameTypes.TryGetValue(typeArg.FullName, out var type)) continue;
    var methodTypes = attr.ConstructorArguments.Where(a => a.Type.FullName == "System.Type[]").Select(a => a.Value).OfType<CustomAttributeArgument[]>().FirstOrDefault();
    var methods = type.Methods.Where(m => m.Name == name).ToList();
    if (methodTypes != null)
    {
        var expected = methodTypes.Select(a => ((TypeReference)a.Value).FullName).ToArray();
        methods = methods.Where(m => m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(expected)).ToList();
    }
    // MethodType.Getter/Setter attributes target a property name.
    if (methods.Count == 0 && type.Properties.Any(p => p.Name == name)) { checkedTargets++; continue; }
    if (methods.Count != 1) throw new Exception($"{patch.FullName}: {type.FullName}.{name} resolved to {methods.Count} targets");
    foreach (var hook in patch.Methods.Where(m => m.Name is "Prefix" or "Postfix" or "Finalizer" ||
        m.CustomAttributes.Any(a => a.AttributeType.Name is "HarmonyPrefix" or "HarmonyPostfix" or "HarmonyFinalizer")))
    foreach (var parameter in hook.Parameters)
    {
        if (parameter.Name.StartsWith("__") || parameter.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyArgument")) continue;
        if (!methods[0].Parameters.Any(p => p.Name == parameter.Name))
            parameterErrors.Add($"{patch.Name}.{hook.Name}: '{parameter.Name}' is absent from {type.FullName}.{name}({string.Join(", ", methods[0].Parameters.Select(p => p.Name))})");
    }
    checkedTargets++;
}
var cameraSafety = AllTypes(client.MainModule.Types).Single(t => t.Name == "Patch_RejectShellCameraPrefab");
Check(cameraSafety.BaseType.FullName == "SPT.Reflection.Patching.ModulePatch" &&
      !cameraSafety.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"), "Camera safety uses SPT ModulePatch without duplicate Harmony registration");
var safetyPrefix = cameraSafety.Methods.Single(m => m.Name == "Prefix");
Check(safetyPrefix.CustomAttributes.Any(a => a.AttributeType.Name == "PatchPrefixAttribute") &&
      !safetyPrefix.Body.Instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.Name == "FikaBridge" && m.Name == "get_CanRender"),
      "Camera safety stays active during headless cleanup");
var cameraTarget = gameTypes["EFT.CameraControl.CameraManager"].Methods.Single(m => m.Name == "SetCameraFromSettings");
Check(cameraTarget.Parameters.Count == 1 && cameraTarget.Parameters[0].Name == safetyPrefix.Parameters[0].Name &&
      safetyPrefix.Parameters[0].ParameterType is ByReferenceType byRef && byRef.ElementType.FullName == cameraTarget.Parameters[0].ParameterType.FullName,
      "Camera safety prefix binds the SPT 4.1 settings parameter");
checkedTargets++;
Check(checkedTargets > 30, $"{checkedTargets} client/addon patch targets resolve uniquely");

Check(parameterErrors.Count == 0, "Harmony parameter bindings: " + string.Join("; ", parameterErrors));
if (fika != null && addon != null) VerifyFika(fika, args[2], args[3], args[1]);
Console.WriteLine("SPT 4.1 migration verification passed.");

static void VerifyFika(AssemblyDefinition fika, string install, string addonPath, string clientPath)
{
    var types = AllTypes(fika.MainModule.Types).ToArray();
    var backend = types.Single(t => t.Name == "FikaBackendUtils");
    foreach (var name in new[] { "IsServer", "IsHeadless", "RaidCode", "ServerGuid" })
        Check(backend.Properties.Any(p => p.Name == name && p.GetMethod is { IsPublic: true, IsStatic: true }), "Fika host/seed property " + name);
    var coop = types.Single(t => t.Name == "CoopHandler");
    Check(coop.Methods.Count(m => m.Name == "TryGetCoopHandler" && m.IsPublic && m.IsStatic && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.IsByReference) == 1,
        "Fika TryGetCoopHandler reflection contract");
    Check(coop.Properties.Any(p => p.Name == "HumanPlayers" && p.GetMethod is { IsPublic: true, IsStatic: false }), "Fika HumanPlayers reflection contract");
    foreach (var name in new[] { "FikaServer", "FikaClient" })
        Check(types.Single(t => t.Name == name).Fields.Any(f => f.Name == "_packetProcessor" && f.FieldType.Name == "NetPacketProcessor"), "Fika packet cleanup field " + name);

    // Exercise the built packet with the installed Fika serializer, rather than
    // just checking its signatures. No game, network manager or profile is created.
    var context = new System.Runtime.Loader.AssemblyLoadContext("Fika packet verification", isCollectible: true);
    context.Resolving += (_, name) =>
    {
        foreach (var directory in new[] { Path.GetDirectoryName(Path.GetFullPath(addonPath))!,
            Path.Combine(install, "BepInEx", "plugins", "Fika"), Path.Combine(install, "BepInEx", "plugins", "spt"), Path.Combine(install, "BepInEx", "core"),
            Path.Combine(install, "EscapeFromTarkov_Data", "Managed") })
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(path)) return context.LoadFromAssemblyPath(Path.GetFullPath(path));
        }
        return null;
    };
    try
    {
        var builtClient = context.LoadFromAssemblyPath(Path.GetFullPath(clientPath));
        var cameraPatchType = builtClient.GetType("Manimal.Icebreaker.Patch_RejectShellCameraPrefab", true)!;
        var cameraPatch = Activator.CreateInstance(cameraPatchType, nonPublic: true)!;
        var cameraMethod = (MethodBase)cameraPatchType.GetMethod("GetTargetMethod", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cameraPatch, null)!;
        Check(cameraMethod.Name == "SetCameraFromSettings" && cameraMethod.DeclaringType!.FullName == "EFT.CameraControl.CameraManager", "SPT camera wrapper resolves the live target");
        var refresh = builtClient.GetType("Manimal.Icebreaker.IcebreakerMapUnlock", true)!
            .GetMethod("RefreshLocation", BindingFlags.Static | BindingFlags.NonPublic)!;
        var locationType = refresh.GetParameters()[0].ParameterType;
        foreach (var (id, enabled, locked, completed, expectedEnabled, expectedLocked) in new[] {
            ("Suburbs", true, false, false, true, false), // disabled gate from server
            ("Suburbs", false, true, false, false, true), // prerequisite not earned
            ("Suburbs", false, true, true, true, false), // completed since login
            ("bigmap", false, true, true, false, true) })
        {
            var location = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(locationType);
            locationType.GetField("Id")!.SetValue(location, id);
            locationType.GetField("Enabled")!.SetValue(location, enabled);
            locationType.GetField("Locked")!.SetValue(location, locked);
            refresh.Invoke(null, new object[] { location, completed });
            Check((bool)locationType.GetField("Enabled")!.GetValue(location)! == expectedEnabled &&
                  (bool)locationType.GetField("Locked")!.GetValue(location)! == expectedLocked,
                  $"Built client map refresh: {id}, server enabled={enabled}, completed={completed}");
        }
        var core = context.LoadFromAssemblyPath(Path.GetFullPath(Path.Combine(install, "BepInEx", "plugins", "Fika", "Fika.Core.dll")));
        var packetType = context.LoadFromAssemblyPath(Path.GetFullPath(addonPath)).GetType("Manimal.Icebreaker.Fika.IceWorldPacket", throwOnError: true)!;
        var writerType = core.GetType("Fika.Core.Networking.LiteNetLib.Utils.NetDataWriter", true)!;
        var readerType = core.GetType("Fika.Core.Networking.LiteNetLib.Utils.NetDataReader", true)!;
        var names = new[] { "Kind", "DoorId", "Sealing", "IntA", "Value" };
        foreach (var values in new object?[][] {
            new object?[] { (byte)0, null, false, 0, 0f },
            new object?[] { (byte)8, "2147483647", true, int.MaxValue, 0.5f },
            new object?[] { (byte)12, "", false, int.MinValue, -179.25f },
            new object?[] { (byte)13, "door-\u00e9-\u51b0", true, 42, 360f } })
        {
            var packet = Activator.CreateInstance(packetType)!;
            for (int i = 0; i < names.Length; i++) packetType.GetField(names[i])!.SetValue(packet, values[i]);
            var writer = Activator.CreateInstance(writerType)!;
            packetType.GetMethod("Serialize")!.Invoke(packet, new[] { writer });
            var bytes = (byte[])writerType.GetMethod("CopyData", Type.EmptyTypes)!.Invoke(writer, null)!;
            var reader = Activator.CreateInstance(readerType, new object[] { bytes })!;
            var decoded = Activator.CreateInstance(packetType)!;
            packetType.GetMethod("Deserialize")!.Invoke(decoded, new[] { reader });
            for (int i = 0; i < names.Length; i++)
                Check(Equals(packetType.GetField(names[i])!.GetValue(decoded), i == 1 ? values[i] ?? "" : values[i]), $"Fika packet kind {values[0]} round-trip {names[i]}");
            Check((int)readerType.GetProperty("AvailableBytes")!.GetValue(reader)! == 0, "Fika packet consumes exact payload");
        }
    }
    finally { context.Unload(); }
}

static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
{
    foreach (var type in types) { yield return type; foreach (var nested in AllTypes(type.NestedTypes)) yield return nested; }
}
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
