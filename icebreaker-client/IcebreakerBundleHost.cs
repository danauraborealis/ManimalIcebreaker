using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ZLinq;

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
    // how it works, from EFT.AssetsManager.BundlesManager.BundleLoadOperation.smethod_0:
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
    // EFT.AssetsManager.AssetsManager.LoadSceneOperation), which works for any loaded bundle regardless of where
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

        // A BundlesManager owns its cache dictionary, while the bundle loaded from
        // our payload is shared by every manager and every spelling of a key.  Keep
        // the exact dictionary/value pair we inserted so teardown can detach only
        // our entries.  A key-only cleanup would remove a game's entry that raced
        // with us or that was already replaced by the native loader.
        private sealed class OwnedEntry
        {
            internal readonly System.Collections.IDictionary Dictionary;
            internal readonly string Name;
            internal readonly object Value;
            internal readonly AssetBundle Bundle;

            internal OwnedEntry(System.Collections.IDictionary dictionary, string name, object value, AssetBundle bundle)
            {
                Dictionary = dictionary;
                Name = name;
                Value = value;
                Bundle = bundle;
            }
        }

        private static readonly List<OwnedEntry> _ownedEntries = new List<OwnedEntry>();
        private static readonly List<System.Collections.IDictionary> _knownDictionaries =
            new List<System.Collections.IDictionary>();

        private static Type _entryType;
        private static FieldInfo _loadedDict;
        private static FieldInfo _entryBundleField;
        private static FieldInfo _entryCounterField;

        internal static void Init()
        {
            try
            {
                _ours = ScanPayload();
                if (_ours.Count == 0)
                {
                    Plugin.Log.LogDebug($"[Bundles] no payload under {PayloadDir}/{Platform} — map bundles unavailable");
                    return;
                }

                _entryType = AccessTools.Inner(typeof(EFT.AssetsManager.BundlesManager), "AssetBundleReference");
                _loadedDict = AccessTools.Field(typeof(EFT.AssetsManager.BundlesManager), "_bundles");
                _entryBundleField = FindField(_entryType, typeof(AssetBundle), "AssetBundle");
                _entryCounterField = FindField(_entryType, typeof(int), "Counter");
                if (_entryType == null || _loadedDict == null || _entryBundleField == null || _entryCounterField == null)
                {
                    Plugin.Log.LogError("[Bundles] EFT.AssetsManager.BundlesManager layout changed — cannot host bundles from the plugin folder");
                    return;
                }

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

        private static FieldInfo FindField(Type type, Type fieldType, string preferredName)
        {
            if (type == null) return null;
            var preferred = AccessTools.Field(type, preferredName);
            if (preferred != null && preferred.FieldType == fieldType) return preferred;
            return type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .AsValueEnumerable()
                .FirstOrDefault(field => field.FieldType == fieldType);
        }

        [IcebreakerPatchScope(IcebreakerPatchScope.Bootstrap)]
        internal sealed class Patch_LoadBundleAsync : IcebreakerModulePatch<Patch_LoadBundleAsync>
        {
            protected override MethodBase GetTargetMethod()
                => AccessTools.Method(typeof(EFT.AssetsManager.BundlesManager), nameof(EFT.AssetsManager.BundlesManager.LoadBundleAsync));

            [SPT.Reflection.Patching.PatchPrefix]
            private static void Prefix(EFT.AssetsManager.BundlesManager __instance, string bundleName)
                => BeforeLoadBundle(__instance, bundleName);
        }

        // The native UnloadBundle implementation unloads the AssetBundle as soon
        // as one manager's counter reaches zero.  That is unsafe here because the
        // same object is deliberately shared by aliases and by multiple managers.
        // Consume the manager's lease and unregister our exact entry, but defer the
        // underlying AssetBundle.Unload(false) to ReleaseRaid().
        [IcebreakerPatchScope(IcebreakerPatchScope.Bootstrap)]
        internal sealed class Patch_UnloadBundle : IcebreakerModulePatch<Patch_UnloadBundle>
        {
            protected override MethodBase GetTargetMethod()
                => AccessTools.Method(
                    typeof(EFT.AssetsManager.BundlesManager),
                    nameof(EFT.AssetsManager.BundlesManager.UnloadBundle),
                    new[] { typeof(string), typeof(bool) });

            [SPT.Reflection.Patching.PatchPrefix]
            private static bool Prefix(EFT.AssetsManager.BundlesManager __instance, string bundleName)
                => !TryUnloadOwnedBundle(__instance, bundleName);
        }

        // every file under streamingassets/Windows becomes a key by its relative path —
        // lowercase with forward slashes, which is the form the game asks for
        // (EFT.AssetsManager.AssetsManager.LoadAssetAsync lowercases the bundle name before lookup)
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

        // the cache entry (EFT.AssetsManager.BundlesManager+Class3523) decompiles as a public
        // .ctor(AssetBundle), but Activator.CreateInstance(type, arg) came back with
        // "Default constructor not found" against the real IL — obfuscated nested types
        // don't always present the signature the decompiler shows. so: bind the ctor
        // explicitly, and if that still misses, build the object without a constructor and
        // write its fields by TYPE (AssetBundle + the int refcount) rather than by name.
        // The counter is intentionally zero here: the game's cache-hit path adds the
        // first real lease for the caller; the host lease is tracked separately.
        private static object CreateEntry(AssetBundle bundle)
        {
            try
            {
                var ctor = AccessTools.Constructor(_entryType, new[] { typeof(AssetBundle) });
                if (ctor != null)
                {
                    var entry = ctor.Invoke(new object[] { bundle });
                    // LoadBundleAsync sees this entry and increments Counter once
                    // for the caller.  The host itself owns no game lease, so the
                    // synthetic constructor value must not become a phantom ref.
                    SetEntryCounter(entry, 0);
                    return entry;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Bundles] entry ctor failed ({e.Message}) — falling back to field init"); }

            try
            {
                var inst = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(_entryType);
                foreach (var f in _entryType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.FieldType == typeof(AssetBundle)) f.SetValue(inst, bundle);
                    else if (f.FieldType == typeof(int)) f.SetValue(inst, 0);
                }
                return inst;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Bundles] could not build a cache entry: {e.Message}");
                return null;
            }
        }

        private static void SetEntryCounter(object entry, int value)
        {
            if (entry == null || _entryCounterField == null) return;
            _entryCounterField.SetValue(entry, value);
        }

        private static int GetEntryCounter(object entry)
        {
            if (entry == null || _entryCounterField == null) return 0;
            return (int)_entryCounterField.GetValue(entry);
        }

        private static AssetBundle GetEntryBundle(object entry)
        {
            if (entry == null || _entryBundleField == null) return null;
            return _entryBundleField.GetValue(entry) as AssetBundle;
        }

        private static void RememberDictionary(System.Collections.IDictionary dictionary)
        {
            if (dictionary == null) return;
            if (_knownDictionaries.AsValueEnumerable().Any(known => ReferenceEquals(known, dictionary))) return;
            _knownDictionaries.Add(dictionary);
        }

        private static OwnedEntry FindOwnedEntry(System.Collections.IDictionary dictionary, string name)
        {
            if (dictionary == null || string.IsNullOrEmpty(name)) return null;
            object current;
            bool exactExists;
            try
            {
                exactExists = dictionary.Contains(name);
                current = exactExists ? dictionary[name] : null;
            }
            catch
            {
                return null;
            }

            if (exactExists)
            {
                // An exact foreign slot wins. Do not reinterpret this unload as a
                // request for a different owned alias in the same manager.
                var matches = _ownedEntries.AsValueEnumerable()
                    .Where(owned => ReferenceEquals(owned.Dictionary, dictionary) &&
                        string.Equals(owned.Name, name, StringComparison.Ordinal) &&
                        ReferenceEquals(owned.Value, current))
                    .ToArray();
                return matches.Length == 0 ? null : matches[matches.Length - 1];
            }

            // AssetsManager.UnloadBundle normalizes its argument before forwarding
            // it, while LoadScene may have registered the original spelling.  If an
            // exact key is absent, resolve one owned alias by canonical name only;
            // never remove a foreign value or a different canonical bundle.
            var canonical = NormalizeBundleName(name);
            var aliases = _ownedEntries.AsValueEnumerable()
                .Where(owned => ReferenceEquals(owned.Dictionary, dictionary) &&
                    string.Equals(NormalizeBundleName(owned.Name), canonical, StringComparison.Ordinal))
                .Where(owned => IsCurrentEntry(owned))
                .ToArray();
            return aliases.Length == 0 ? null : aliases[aliases.Length - 1];
        }

        private static string NormalizeBundleName(string bundleName)
            => string.IsNullOrEmpty(bundleName) ? bundleName : bundleName.Replace('\\', '/').ToLowerInvariant();

        private static bool IsCurrentEntry(OwnedEntry owned)
        {
            try
            {
                return owned != null && owned.Dictionary.Contains(owned.Name) &&
                    ReferenceEquals(owned.Dictionary[owned.Name], owned.Value);
            }
            catch
            {
                return false;
            }
        }

        // Remove only if the dictionary still contains the exact value we inserted.
        // Returning true for a missing/replaced value means the host no longer owns
        // that slot; the foreign value is deliberately left untouched.
        private static bool RemoveExactEntry(OwnedEntry owned)
        {
            if (owned == null || owned.Dictionary == null) return true;
            try
            {
                if (!owned.Dictionary.Contains(owned.Name)) return true;
                if (!ReferenceEquals(owned.Dictionary[owned.Name], owned.Value)) return true;
                owned.Dictionary.Remove(owned.Name);
                if (!owned.Dictionary.Contains(owned.Name)) return true;
                return !ReferenceEquals(owned.Dictionary[owned.Name], owned.Value);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[Bundles] could not detach owned cache entry '{owned.Name}': {e.Message}");
                return false;
            }
        }

        private static void ForgetOwnedEntry(OwnedEntry owned)
        {
            for (var i = _ownedEntries.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_ownedEntries[i], owned))
                {
                    _ownedEntries.RemoveAt(i);
                    return;
                }
        }

        private static bool IsBundleUnavailable(AssetBundle bundle)
        {
            if (ReferenceEquals(bundle, null)) return true;
            try { return bundle == null; }
            catch { return true; }
        }

        private static bool SameBundle(AssetBundle left, AssetBundle right)
            => !ReferenceEquals(left, null) && !ReferenceEquals(right, null) && ReferenceEquals(left, right);

        private static bool ContainsBundle(List<AssetBundle> bundles, AssetBundle bundle)
        {
            if (bundles == null || IsBundleUnavailable(bundle)) return false;
            return bundles.AsValueEnumerable().Any(candidate => SameBundle(candidate, bundle));
        }

        private static bool HasRemainingEntryForBundle(AssetBundle bundle, List<OwnedEntry> remaining)
        {
            if (IsBundleUnavailable(bundle)) return false;
            return remaining.AsValueEnumerable().Any(owned => SameBundle(owned.Bundle, bundle));
        }

        // A foreign entry should normally contain a different AssetBundle instance,
        // but scanning known dictionaries makes teardown conservative if another
        // system deliberately aliases the same object.  In that case we retain our
        // map and retry on a later ReleaseRaid rather than unload out from under it.
        private static bool HasForeignReference(AssetBundle bundle)
        {
            if (IsBundleUnavailable(bundle)) return false;
            try
            {
                return _knownDictionaries.AsValueEnumerable()
                    .Where(dictionary => dictionary != null)
                    .Any(dictionary => DictionaryHasForeignReference(dictionary, bundle));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[Bundles] could not audit foreign cache entries: {e.Message}");
                return true;
            }
        }

        private static bool DictionaryHasForeignReference(System.Collections.IDictionary dictionary, AssetBundle bundle)
        {
            foreach (System.Collections.DictionaryEntry item in dictionary)
            {
                var candidate = GetEntryBundle(item.Value);
                if (!SameBundle(candidate, bundle)) continue;

                if (!_ownedEntries.AsValueEnumerable().Any(owned =>
                    ReferenceEquals(owned.Dictionary, dictionary) &&
                    Equals(owned.Name, item.Key) &&
                    ReferenceEquals(owned.Value, item.Value)))
                    return true;
            }
            return false;
        }

        private static void RemoveLoadedBundle(AssetBundle bundle)
        {
            var keys = _loaded.AsValueEnumerable()
                .Where(pair => SameBundle(pair.Value, bundle))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in keys) _loaded.Remove(key);
        }

        private static bool TryUnloadNewlyLoaded(AssetBundle bundle)
        {
            if (IsBundleUnavailable(bundle))
            {
                RemoveLoadedBundle(bundle);
                return true;
            }
            try
            {
                bundle.Unload(false);
                RemoveLoadedBundle(bundle);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[Bundles] rollback could not unload newly loaded bundle: {e.Message}");
                return false;
            }
        }

        private static bool TryUnloadOwnedBundle(EFT.AssetsManager.BundlesManager manager, string bundleName)
        {
            if (manager == null || string.IsNullOrEmpty(bundleName) || _loadedDict == null) return false;
            try
            {
                var dictionary = _loadedDict.GetValue(manager) as System.Collections.IDictionary;
                var owned = FindOwnedEntry(dictionary, bundleName);
                if (owned == null) return false;

                // Never let the native method see our exact entry: it would unload
                // the AssetBundle when this one manager reaches zero, even when
                // aliases or other managers still hold the same object.
                var counter = GetEntryCounter(owned.Value);
                if (counter > 0) SetEntryCounter(owned.Value, counter - 1);
                if (counter <= 1)
                {
                    if (RemoveExactEntry(owned)) ForgetOwnedEntry(owned);
                    else Plugin.Log?.LogWarning($"[Bundles] deferring native unload for '{bundleName}' because its owned entry could not be detached");
                }
                return true;
            }
            catch (Exception e)
            {
                // Keep the entry installed and skip the native implementation.  A
                // later ReleaseRaid can retry exact removal without risking a shared
                // bundle unload during the raid.
                Plugin.Log?.LogWarning($"[Bundles] owned unload interception failed for '{bundleName}': {e.Message}");
                return true;
            }
        }

        private static void BeforeLoadBundle(EFT.AssetsManager.BundlesManager __instance, string bundleName)
        {
            AssetBundle newlyLoaded = null;
            var preserveInsertedEntry = false;
            try
            {
                if (string.IsNullOrEmpty(bundleName) || __instance == null || _ours == null ||
                    _loadedDict == null || _entryType == null || _entryCounterField == null || _entryBundleField == null)
                    return;
                var key = NormalizeBundleName(bundleName);
                if (!_ours.TryGetValue(key, out var file)) return;

                var dict = _loadedDict.GetValue(__instance) as System.Collections.IDictionary;
                if (dict == null) return;
                RememberDictionary(dict);
                if (dict.Contains(bundleName)) return;      // this exact spelling is served

                if (!_loaded.TryGetValue(key, out var bundle) || IsBundleUnavailable(bundle))
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
                    newlyLoaded = bundle;
                    _loaded[key] = bundle;
                    Plugin.Log.LogDebug($"[Bundles] loaded '{key}' from the plugin folder in {sw.ElapsedMilliseconds}ms " +
                                          "(no copy into EscapeFromTarkov_Data)");
                }
                var entry = CreateEntry(bundle);
                if (entry == null)
                {
                    if (newlyLoaded != null)
                    {
                        if (TryUnloadNewlyLoaded(newlyLoaded)) newlyLoaded = null;
                        else preserveInsertedEntry = true;
                    }
                    return;
                }
                dict[bundleName] = entry;
                try
                {
                    _ownedEntries.Add(new OwnedEntry(dict, bundleName, entry, bundle));
                }
                catch
                {
                    // The dictionary assignment succeeded, but ownership bookkeeping
                    // did not. Remove only our exact value before allowing the native
                    // loader to continue, and release a bundle loaded by this attempt.
                    var removed = RemoveExactEntry(new OwnedEntry(dict, bundleName, entry, bundle));
                    if (removed && newlyLoaded != null)
                    {
                        if (TryUnloadNewlyLoaded(newlyLoaded)) newlyLoaded = null;
                        else preserveInsertedEntry = true;
                    }
                    else if (!removed)
                    {
                        // We cannot prove the entry was detached, so retaining the
                        // bundle is safer than unloading an entry we can no longer
                        // enumerate for ReleaseRaid.
                        preserveInsertedEntry = true;
                    }
                    throw;
                }
            }
            catch (Exception e)
            {
                if (newlyLoaded != null && !preserveInsertedEntry)
                {
                    if (TryUnloadNewlyLoaded(newlyLoaded)) newlyLoaded = null;
                    else preserveInsertedEntry = true;
                }
                // fall through to the game's own loader rather than break the raid
                Plugin.Log.LogError($"[Bundles] serve failed for '{bundleName}': {e.Message}");
            }
        }

        // Called by the central raid lifecycle only after every load producer is
        // terminal and all Icebreaker scenes have gone away.  Detach our exact
        // manager entries first, then unload each shared AssetBundle once.  Any
        // entry we cannot prove detached keeps its bundle alive for a later retry;
        // a stale dictionary value must never point at an unloaded object.
        internal static void ReleaseRaid()
        {
            try
            {
                var remaining = new List<OwnedEntry>();
                foreach (var owned in _ownedEntries)
                    if (!RemoveExactEntry(owned)) remaining.Add(owned);

                _ownedEntries.Clear();
                _ownedEntries.AddRange(remaining);

                var seen = new List<AssetBundle>();
                var retained = new List<AssetBundle>();
                var released = new List<AssetBundle>();
                var loadedKeys = new List<string>();
                foreach (var pair in _loaded)
                {
                    var bundle = pair.Value;
                    if (IsBundleUnavailable(bundle))
                    {
                        loadedKeys.Add(pair.Key);
                        continue;
                    }
                    if (ContainsBundle(seen, bundle)) continue;
                    seen.Add(bundle);

                    if (HasRemainingEntryForBundle(bundle, remaining) || HasForeignReference(bundle))
                    {
                        retained.Add(bundle);
                        continue;
                    }

                    try
                    {
                        // Keep this false: release must preserve assets referenced by
                        // Unity objects that may still be alive outside the bundle.
                        bundle.Unload(false);
                        released.Add(bundle);
                    }
                    catch (Exception e)
                    {
                        retained.Add(bundle);
                        Plugin.Log?.LogWarning($"[Bundles] could not unload '{pair.Key}' at raid teardown: {e.Message}");
                    }
                }

                // Remove every alias for a successfully released object.  Do not
                // clear the entire map: retained/foreign objects remain resolvable.
                foreach (var key in loadedKeys) _loaded.Remove(key);
                foreach (var bundle in released) RemoveLoadedBundle(bundle);

                if (retained.Count == 0 && _ownedEntries.Count == 0)
                {
                    _knownDictionaries.Clear();
                    _loaded.Clear();
                }
                else
                {
                    Plugin.Log?.LogWarning($"[Bundles] retained {retained.Count} bundle(s) because an owned or foreign cache entry is still live");
                }
            }
            catch (Exception e)
            {
                // Teardown must not take down the raid lifecycle.  Keeping the maps
                // intact lets a subsequent call retry without resolving stale data.
                Plugin.Log?.LogWarning($"[Bundles] raid release failed: {e.Message}");
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
