using System;
using System.Threading.Tasks;
using Comfort.Common;
using UnityEngine;

namespace Manimal.Icebreaker.Keypad
{
    // async loader for the two keypad bundles. cached after first load (once
    // per game launch, not per raid — unity keeps the prefabs alive as long as
    // anything references them), single in-flight task guard, raw-bundle scan
    // fallback so a misnamed asset surfaces in the log instead of silently
    // nulling. the world-prop prefab is only mined for its audio children —
    // the map ships its own terminal panels.
    internal static class KeypadBundleLoader
    {
        private static GameObject _keypadPrefab;
        private static GameObject _keypadUIPrefab;
        private static Task<(GameObject, GameObject)> _loadTask;

        public static Task<(GameObject keypad, GameObject keypadUI)> EnsureLoaded()
        {
            if (_keypadPrefab != null && _keypadUIPrefab != null)
                return Task.FromResult((_keypadPrefab, _keypadUIPrefab));
            if (_loadTask != null) return _loadTask;
            _loadTask = LoadAsync();
            return _loadTask;
        }

        private static async Task<(GameObject, GameObject)> LoadAsync()
        {
            try
            {
                if (!Singleton<IEasyAssets>.Instantiated)
                {
                    Plugin.Log?.LogError("[Keypad] IEasyAssets not initialized; cannot load bundles.");
                    _loadTask = null;
                    return (null, null);
                }

                var ea = Singleton<IEasyAssets>.Instance;

                Plugin.Log?.LogInfo(
                    $"[Keypad] retaining bundles: {KeypadConstants.KeypadBundleKey}, {KeypadConstants.KeypadUIBundleKey}");

                var handle = ea.Retain(new[]
                {
                    KeypadConstants.KeypadBundleKey,
                    KeypadConstants.KeypadUIBundleKey,
                });
                await GClass1857.LoadBundles(handle);

                _keypadPrefab   = ResolvePrefab(ea, KeypadConstants.KeypadBundleKey,   KeypadConstants.KeypadAssetNameCandidates,   "keypad");
                _keypadUIPrefab = ResolvePrefab(ea, KeypadConstants.KeypadUIBundleKey, KeypadConstants.KeypadUIAssetNameCandidates, "keypad_ui");

                return (_keypadPrefab, _keypadUIPrefab);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[Keypad] bundle load failed: {ex.GetType().Name}: {ex.Message}");
                _loadTask = null;
                return (null, null);
            }
        }

        private static GameObject ResolvePrefab(IEasyAssets ea, string bundleKey, string[] candidates, string tag)
        {
            if (!ea.IsAssetLoaded(bundleKey))
            {
                Plugin.Log?.LogError($"[Keypad] bundle '{bundleKey}' failed to load.");
                return null;
            }

            foreach (var name in candidates)
            {
                var prefab = ea.GetAsset<GameObject>(bundleKey, name);
                if (prefab != null)
                {
                    Plugin.Log?.LogInfo($"[Keypad] [{tag}] loaded prefab (asset='{name}'): {prefab.name}");
                    return prefab;
                }
            }

            // fallback — scan loaded bundles and dump the asset inventory so a
            // rename shows up in the log
            foreach (var ab in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (ab.name == null || !ab.name.Contains(tag, StringComparison.OrdinalIgnoreCase)) continue;
                Plugin.Log?.LogInfo($"[Keypad] [{tag}] === bundle '{ab.name}' asset inventory ===");
                foreach (var n in ab.GetAllAssetNames())
                    Plugin.Log?.LogInfo($"  asset path: {n}");
                var gos = ab.LoadAllAssets<GameObject>();
                if (gos.Length > 0)
                {
                    Plugin.Log?.LogInfo($"[Keypad] [{tag}] fallback picked: {gos[0].name}");
                    return gos[0];
                }
            }

            Plugin.Log?.LogError($"[Keypad] [{tag}] no matching prefab found in bundle.");
            return null;
        }
    }
}
