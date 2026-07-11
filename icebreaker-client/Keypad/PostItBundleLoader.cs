using System;
using System.Threading.Tasks;
using Comfort.Common;
using UnityEngine;

namespace Manimal.Icebreaker.Keypad
{
    // async loader for the post-it prefab bundle — same shape as
    // KeypadBundleLoader: cached after first load, single in-flight task
    // guard, raw-bundle scan fallback.
    internal static class PostItBundleLoader
    {
        private static GameObject _prefab;
        private static Task<GameObject> _loadTask;

        public static Task<GameObject> EnsureLoaded()
        {
            if (_prefab != null) return Task.FromResult(_prefab);
            if (_loadTask != null) return _loadTask;
            _loadTask = LoadAsync();
            return _loadTask;
        }

        private static async Task<GameObject> LoadAsync()
        {
            try
            {
                if (!Singleton<IEasyAssets>.Instantiated)
                {
                    Plugin.Log?.LogError("[PostIt] IEasyAssets not initialized; cannot load bundle.");
                    _loadTask = null;
                    return null;
                }

                var ea = Singleton<IEasyAssets>.Instance;

                Plugin.Log?.LogInfo($"[PostIt] retaining bundle: {PostItConstants.BundleKey}");
                var handle = ea.Retain(new[] { PostItConstants.BundleKey });
                await GClass1857.LoadBundles(handle);

                _prefab = ResolvePrefab(ea);
                return _prefab;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[PostIt] bundle load failed: {ex.GetType().Name}: {ex.Message}");
                _loadTask = null;
                return null;
            }
        }

        private static GameObject ResolvePrefab(IEasyAssets ea)
        {
            if (!ea.IsAssetLoaded(PostItConstants.BundleKey))
            {
                Plugin.Log?.LogError($"[PostIt] bundle '{PostItConstants.BundleKey}' failed to load.");
                return null;
            }

            foreach (var name in PostItConstants.AssetNameCandidates)
            {
                var prefab = ea.GetAsset<GameObject>(PostItConstants.BundleKey, name);
                if (prefab != null)
                {
                    Plugin.Log?.LogInfo($"[PostIt] loaded prefab (asset='{name}'): {prefab.name}");
                    return prefab;
                }
            }

            foreach (var ab in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (ab.name == null || !ab.name.Contains("postit", StringComparison.OrdinalIgnoreCase)) continue;
                Plugin.Log?.LogInfo($"[PostIt] === bundle '{ab.name}' asset inventory ===");
                foreach (var n in ab.GetAllAssetNames())
                    Plugin.Log?.LogInfo($"  asset path: {n}");
                var gos = ab.LoadAllAssets<GameObject>();
                if (gos.Length > 0)
                {
                    Plugin.Log?.LogInfo($"[PostIt] fallback picked: {gos[0].name}");
                    return gos[0];
                }
            }

            Plugin.Log?.LogError("[PostIt] no matching prefab found in bundle.");
            return null;
        }
    }
}
