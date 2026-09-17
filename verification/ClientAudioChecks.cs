using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;

internal static class ClientAudioChecks
{
    internal static void Run(AssemblyDefinition client)
    {
        const string prefix = "Manimal.Icebreaker.DoorBlizzard.";
        byte[] Resource(string name) => client.MainModule.Resources.OfType<EmbeddedResource>()
            .Single(r => r.Name == prefix + name).GetResourceData();
        byte[] pcm = Resource("blizzard.pcm");
        using var manifest = JsonDocument.Parse(Resource("manifest.json"));
        var root = manifest.RootElement;
        Check(root.GetProperty("formatVersion").GetInt32() == 1, "door manifest format");
        Check(Convert.ToHexString(SHA256.HashData(pcm)).Equals(root.GetProperty("pcmSha256").GetString(),
            StringComparison.OrdinalIgnoreCase), "embedded PCM matches extraction hash");
        Check(root.GetProperty("channels").GetInt32() == 2 && root.GetProperty("frequency").GetInt32() == 44100 &&
            pcm.Length == 5468400 && pcm.Length % 4 == 0, "retail PCM dimensions");
        var emitters = root.GetProperty("doors").EnumerateArray().ToArray();
        Check(emitters.Length == 12 && emitters.Count(e => e.GetProperty("kind").GetString() == "hatch") == 1,
            "11 doors and one frozen hatch");
        Check(emitters.Select(e => e.GetProperty("playTrigger").GetString()).Distinct().Count() == 12,
            "unique sound triggers, including the bare Open_01 ID");
        using var original = JsonDocument.Parse(File.ReadAllText("analysis/icebreaker_doors.json"));
        var doors = original.RootElement.GetProperty("components").GetProperty("Door").EnumerateArray().ToArray();
        foreach (var emitter in emitters)
        {
            bool hatch = emitter.GetProperty("kind").GetString() == "hatch";
            Check(emitter.GetProperty("volume").GetSingle() == .75f && emitter.GetProperty("fadeOut").GetSingle() == 1f &&
                emitter.GetProperty("fadeIn").GetSingle() == (hatch ? 2.5f : 2f) &&
                emitter.GetProperty("rollOff").GetInt32() == (hatch ? 5 : 10), "authored blizzard tuning");
            if (hatch) continue;
            string path = emitter.GetProperty("doorPath").GetString()!;
            var pos = emitter.GetProperty("doorPosition").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            int matches = doors.Count(d => Regex.Replace(d.GetProperty("go").GetString()!, @"~\d+", "") == path &&
                d.GetProperty("world").EnumerateArray().Select((v, i) => Math.Pow(v.GetDouble() - pos[i], 2)).Sum() < .25);
            Check(matches == 1, "retail anchor uniquely matches the original backported door: " + path);
        }
        var plugin = client.MainModule.Types.Single(t => t.BaseType?.FullName == "BepInEx.BaseUnityPlugin");
        Check(plugin.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInDependency" &&
            (string)a.ConstructorArguments[0].Value == "com.arys.unitytoolkit"), "UnityToolkit dependency declared");
        Check(client.MainModule.AssemblyReferences.Any(a => a.Name == "ZLinq"), "installed ZLinq API referenced");
        // client collection queries go through ZLinq (EFT extensions shadow System.Linq, and
        // its enumerators allocate). IGrouping is the interface ZLinq's own GroupBy returns.
        var linqCalls = client.MainModule.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions.Select(i => (m, r: i.Operand as MethodReference)))
            .Where(x => x.r != null && x.r.DeclaringType.Namespace == "System.Linq" && !x.r.DeclaringType.Name.StartsWith("IGrouping"))
            .Select(x => x.m.DeclaringType.Name + "." + x.m.Name + " -> " + x.r.DeclaringType.Name + "." + x.r.Name).Distinct().ToList();
        Check(linqCalls.Count == 0, "client makes no System.Linq calls: " + string.Join(", ", linqCalls));
        var fog = client.MainModule.Types.Single(t => t.FullName == "Manimal.Icebreaker.RetailFogRemap");
        var key = fog.Methods.Single(m => m.Name == "Key");
        Check(key.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "set_weightedMode"),
            "fog keys restore authored weightedMode after construction");
        Check(fog.Methods.Single(m => m.Name == "Build").Body.Instructions.Count(i =>
            i.Operand is MethodReference m && m.DeclaringType.FullName == fog.FullName && m.Name == "Key") == 44,
            "all 44 fog keys use the mode-preserving constructor");
        // Wiring the Vessel breaker panels: both retail clips ride the same embedded-PCM path.
        const string panel = "Manimal.Icebreaker.PanelRepair.";
        byte[] PanelResource(string name) => client.MainModule.Resources.OfType<EmbeddedResource>()
            .Single(r => r.Name == panel + name).GetResourceData();
        using var panelManifest = JsonDocument.Parse(PanelResource("manifest.json"));
        var panelRoot = panelManifest.RootElement;
        Check(panelRoot.GetProperty("formatVersion").GetInt32() == 1 && panelRoot.GetProperty("channels").GetInt32() == 2 &&
            panelRoot.GetProperty("frequency").GetInt32() == 44100, "panel manifest format");
        foreach (var (clipKey, frames) in new[] { ("loop", 286650), ("done", 53345) })
        {
            var entry = panelRoot.GetProperty("clips").GetProperty(clipKey);
            byte[] samples = PanelResource(entry.GetProperty("file").GetString()!);
            Check(samples.Length == frames * 4 && entry.GetProperty("frames").GetInt32() == frames, "retail panel clip length: " + clipKey);
            Check(Convert.ToHexString(SHA256.HashData(samples)).Equals(entry.GetProperty("pcmSha256").GetString(),
                StringComparison.OrdinalIgnoreCase), "embedded panel PCM matches extraction hash: " + clipKey);
        }
        var repair = client.MainModule.Types.Single(t => t.FullName == "Manimal.Icebreaker.IcebreakerPanelRepair");
        Check(repair.Fields.Any(f => f.Name == "ToolkitTpl") && repair.Fields.Any(f => f.Name == "QuestId"), "panel repair constants present");
        Console.WriteLine("PASS: retail door audio resources, original-map anchor compatibility, panel repair audio, dependency and fog metadata");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Client audio verification failed: " + message);
    }
}
