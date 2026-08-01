using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // SERVES OUR BUNDLES STRAIGHT OUT OF THE PLUGIN FOLDER — nothing is ever written into
    // EscapeFromTarkov_Data.
    //
    // this replaces the old EnsureStreamingAssets materializer, which copied the 1.8GB
    // scene bundle into the game's StreamingAssets at startup. that satisfied the FORGE
    // packaging rule (the zip installs nothing into the game folder) but not the spirit of
    // it: the files still landed there on first launch. this does.
    //
    // how it works, from BundlesManagerClass.Class3524.smethod_0:
    //
    //     if (manager.Dictionary_0.TryGetValue(bundleName, out c))   // already loaded?
    //     { c.Counter++; return new Class3524 { Result = c.AssetBundle }; }
    //     ...
    //     string text = manager.DownloadingUrl + bundleName;         // else fetch from URL
    //
    // the loaded-bundle dictionary is checked FIRST. so we load the file ourselves with
    // AssetBundle.LoadFromFile (any path on disk is fine) and insert it under the key the
    // game is about to ask for — the lookup hits, and the URL branch never runs. that URL
    // branch is also exactly why the SPT manifest route 404'd: with no dictionary entry it
    // falls through to a fetch for a file that was never in StreamingAssets.
    //
    // the scene itself is then loaded BY NAME (SceneManager.LoadSceneAsync in
    // AssetsManagerClass.Class3526), which works for any loaded bundle regardless of where
    // it came from — so no manifest entry is needed either. our bundles are not in the
    // game's Windows.json and don't need to be.
    internal static class IcebreakerBundleHost
    {
        // payload root: BepInEx/plugins/ManimalIcebreaker/streamingassets/Windows/<key>
        private const string PayloadDir = "streamingassets";
        private const string Platform = "Windows";

        private static Dictionary<string, string> _ours;   // bundle key -> absolute file path
        // loaded once, but registered under EVERY spelling the game asks for: LoadAssetAsync
        // lowercases the name while LoadScene passes it through raw, and a dictionary miss
        // on the second spelling would drop through to the URL fetch that 404s
        private static readonly Dictionary<string, AssetBundle> _loaded =
            new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);

        private static Type _entryType;
        private static FieldInfo _loadedDict;

        internal static void Init(Harmony harmony)
        {
            try
            {
                _ours = ScanPayload();
                if (_ours.Count == 0)
                {
                    Plugin.Log.LogDebug($"[Bundles] no payload under {PayloadDir}/{Platform} — map bundles unavailable");
                    return;
                }

                _entryType = AccessTools.Inner(typeof(BundlesManagerClass), "Class3523");
                _loadedDict = AccessTools.Field(typeof(BundlesManagerClass), "Dictionary_0");
                if (_entryType == null || _loadedDict == null)
                {
                    Plugin.Log.LogError("[Bundles] BundlesManagerClass layout changed — cannot host bundles from the plugin folder");
                    return;
                }

                harmony.Patch(
                    AccessTools.Method(typeof(BundlesManagerClass), nameof(BundlesManagerClass.LoadBundleAsync)),
                    prefix: new HarmonyMethod(typeof(IcebreakerBundleHost), nameof(BeforeLoadBundle)));

                // FindBundle reads the same dictionary, so anything already served stays
                // resolvable without a second hook
                // print what the cache entry actually looks like — if the fallback above
                // ever has to run, this is the line that says why
                var ctors = new List<string>();
                foreach (var c in _entryType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var ps = new List<string>();
                    foreach (var p in c.GetParameters()) ps.Add(p.ParameterType.Name);
                    ctors.Add("(" + string.Join(", ", ps.ToArray()) + ")");
                }
                Plugin.Log.LogDebug($"[Bundles] hosting {_ours.Count} bundle(s) from the plugin folder: " +
                                      $"{string.Join(", ", new List<string>(_ours.Keys).ToArray())} " +
                                      $"[entry={_entryType.FullName} ctors: {(ctors.Count > 0 ? string.Join(" ", ctors.ToArray()) : "NONE")}]");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Bundles] host init failed: {e}");
            }
        }

        // every file under streamingassets/Windows becomes a key by its relative path —
        // lowercase with forward slashes, which is the form the game asks for
        // (AssetsManagerClass.LoadAssetAsync lowercases the bundle name before lookup)
        private static Dictionary<string, string> ScanPayload()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var root = Path.Combine(
                Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                PayloadDir, Platform);
            if (!Directory.Exists(root)) return map;
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) continue;
                var key = file.Substring(root.Length + 1).Replace('\\', '/').ToLowerInvariant();
                map[key] = file;
            }
            return map;
        }

        // the cache entry (BundlesManagerClass+Class3523) decompiles as a public
        // .ctor(AssetBundle), but Activator.CreateInstance(type, arg) came back with
        // "Default constructor not found" against the real IL — obfuscated nested types
        // don't always present the signature the decompiler shows. so: bind the ctor
        // explicitly, and if that still misses, build the object without a constructor and
        // write its fields by TYPE (AssetBundle + the int refcount) rather than by name.
        private static object CreateEntry(AssetBundle bundle)
        {
            try
            {
                var ctor = AccessTools.Constructor(_entryType, new[] { typeof(AssetBundle) });
                if (ctor != null) return ctor.Invoke(new object[] { bundle });
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Bundles] entry ctor failed ({e.Message}) — falling back to field init"); }

            try
            {
                var inst = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(_entryType);
                foreach (var f in _entryType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.FieldType == typeof(AssetBundle)) f.SetValue(inst, bundle);
                    else if (f.FieldType == typeof(int)) f.SetValue(inst, 1);   // refcount starts at 1
                }
                return inst;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Bundles] could not build a cache entry: {e.Message}");
                return null;
            }
        }

        private static void BeforeLoadBundle(BundlesManagerClass __instance, string bundleName)
        {
            try
            {
                if (string.IsNullOrEmpty(bundleName)) return;
                var key = bundleName.Replace('\\', '/').ToLowerInvariant();
                if (!_ours.TryGetValue(key, out var file)) return;

                var dict = _loadedDict.GetValue(__instance) as System.Collections.IDictionary;
                if (dict == null) return;
                if (dict.Contains(bundleName)) return;      // this exact spelling is served

                if (!_loaded.TryGetValue(key, out var bundle) || bundle == null)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    // LoadFromFile is memory-mapped — a 1.8GB bundle costs a fraction of
                    // what copying it did, and none of the disk footprint
                    bundle = AssetBundle.LoadFromFile(file);
                    if (bundle == null)
                    {
                        Plugin.Log.LogError($"[Bundles] '{key}' failed to load from {file}");
                        return;
                    }
                    _loaded[key] = bundle;
                    Plugin.Log.LogDebug($"[Bundles] loaded '{key}' from the plugin folder in {sw.ElapsedMilliseconds}ms " +
                                          "(no copy into EscapeFromTarkov_Data)");
                }
                var entry = CreateEntry(bundle);
                if (entry == null) return;
                dict[bundleName] = entry;
            }
            catch (Exception e)
            {
                // fall through to the game's own loader rather than break the raid
                Plugin.Log.LogError($"[Bundles] serve failed for '{bundleName}': {e.Message}");
            }
        }

        // CLEANUP for installs that ran the old materializer: it copied our payload into
        // EscapeFromTarkov_Data/StreamingAssets, and those files are dead weight now (up to
        // ~1.8GB) as well as the thing we told people wasn't there. delete exactly what we
        // wrote — matched by relative path against our own payload, so nothing of the
        // game's own is ever touched.
        internal static void CleanLegacyStreamingAssets()
        {
            try
            {
                if (_ours == null || _ours.Count == 0) return;
                var sa = Application.streamingAssetsPath;
                if (string.IsNullOrEmpty(sa)) return;
                long freed = 0; int removed = 0;
                foreach (var key in _ours.Keys)
                {
                    var stale = Path.Combine(sa, Platform, key.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        if (!File.Exists(stale)) continue;
                        freed += new FileInfo(stale).Length;
                        File.Delete(stale);
                        removed++;
                        // take the now-empty folders with it, but never past our own subtree
                        var dir = Path.GetDirectoryName(stale);
                        while (!string.IsNullOrEmpty(dir) &&
                               dir.StartsWith(Path.Combine(sa, Platform), StringComparison.OrdinalIgnoreCase) &&
                               Directory.Exists(dir) &&
                               Directory.GetFileSystemEntries(dir).Length == 0)
                        {
                            Directory.Delete(dir);
                            dir = Path.GetDirectoryName(dir);
                        }
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"[Bundles] could not remove stale '{key}': {e.Message}"); }
                }
                if (removed > 0)
                    Plugin.Log.LogDebug($"[Bundles] removed {removed} leftover file(s) from the old StreamingAssets copy " +
                                          $"({freed / 1048576}MB reclaimed) — the game folder is clean now");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Bundles] legacy cleanup failed: {e.Message}"); }
        }
    }
}
