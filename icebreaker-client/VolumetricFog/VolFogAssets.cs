using System.IO;
using UnityEngine;

namespace VolumetricFogAndMist
{
    // asset provider for the EFT embed. the store asset loads its shaders via
    // Shader.Find and its textures via Resources.Load — in-game neither exists until
    // our bundle (built in the SDK from the asset's Resources content) is loaded.
    // loading the bundle registers its shaders so Shader.Find resolves natively;
    // textures are fetched here (the two Resources.Load sites are patched to this).
    public static class VolFogAssets
    {
        private static AssetBundle _bundle;
        private static bool _tried;
        private static readonly System.Collections.Generic.Dictionary<string, Shader> _shaders =
            new System.Collections.Generic.Dictionary<string, Shader>();

        // Shader.Find proved blind to bundle shaders in-game (null shader -> dead fog
        // material) — index the bundle's shaders by their shader NAME and resolve here
        public static Shader FindShader(string name)
        {
            EnsureBundle();
            if (_shaders.TryGetValue(name, out var s) && s != null) return s;
            var fallback = Shader.Find(name);
            if (fallback == null) Debug.LogWarning($"[VolFog] shader '{name}' not found in bundle OR engine");
            return fallback;
        }

        public static bool EnsureBundle()
        {
            if (_bundle != null) return true;
            if (_tried) return false;
            _tried = true;
            string path = Path.Combine(Path.GetDirectoryName(typeof(VolFogAssets).Assembly.Location), "volumetricfog.bundle");
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[VolFog] bundle missing at {path} — volumetric fog unavailable");
                return false;
            }
            _bundle = AssetBundle.LoadFromFile(path);
            if (_bundle == null)
            {
                Debug.LogWarning("[VolFog] bundle failed to load (already loaded elsewhere?)");
                return false;
            }
            foreach (var sh in _bundle.LoadAllAssets<Shader>())
                if (sh != null) _shaders[sh.name] = sh;
            Debug.Log($"[VolFog] bundle loaded — shaders: {string.Join(", ", _shaders.Keys)}");
            return true;
        }

        public static Texture2D LoadTexture(string name)
        {
            if (!EnsureBundle()) return Resources.Load<Texture2D>("Textures/" + name);
            foreach (var an in _bundle.GetAllAssetNames())
                if (an.EndsWith("/" + name.ToLowerInvariant() + ".png"))
                    return _bundle.LoadAsset<Texture2D>(an);
            return _bundle.LoadAsset<Texture2D>(name);
        }
    }
}
