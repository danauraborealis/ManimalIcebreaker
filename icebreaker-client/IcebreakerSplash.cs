using System;
using System.Collections.Generic;
using System.IO;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker
{
    internal static class IcebreakerSplash
    {
        private static readonly List<Sprite> ExtraSprites = new List<Sprite>(2);
        private static readonly HashSet<int> SelectedPanels = new HashSet<int>();
        private static bool _loaded;

        private static void LoadSprites()
        {
            if (_loaded) return;
            _loaded = true;
            var assembly = typeof(IcebreakerSplash).Assembly;
            foreach (var name in new[] { "splash_17", "splash_18" })
            {
                Texture2D texture = null;
                try
                {
                    using var stream = assembly.GetManifestResourceStream("Manimal.Icebreaker.Splash." + name + ".png");
                    if (stream == null) throw new InvalidDataException("Embedded PNG missing");
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                    {
                        name = "Icebreaker_" + name,
                        hideFlags = HideFlags.HideAndDontSave,
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Bilinear
                    };
                    if (!ImageConversion.LoadImage(texture, buffer.ToArray(), true))
                        throw new InvalidDataException("PNG decoding failed");
                    var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
                        new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                    sprite.name = texture.name;
                    sprite.hideFlags = HideFlags.HideAndDontSave;
                    ExtraSprites.Add(sprite);
                }
                catch (Exception e)
                {
                    if (texture != null) UnityEngine.Object.Destroy(texture);
                    Plugin.Log.LogWarning($"[Splash] could not load {name}: {e.Message}");
                }
            }
            Plugin.Log.LogInfo($"[Splash] {ExtraSprites.Count} Icebreaker images available alongside the original splash images");
        }

        private static Sprite Choose(SplashScreenPanel panel)
        {
            // Include the native candidates without changing the serialized array.
            // Range's integer upper bound is exclusive, so use the full count: EFT's
            // original Length - 1 would make the last appended image unreachable.
            var choices = new List<Sprite>();
            if (panel._sprites != null)
                foreach (var sprite in panel._sprites)
                    if (sprite != null) choices.Add(sprite);
            choices.AddRange(ExtraSprites);
            var chosen = choices[UnityEngine.Random.Range(0, choices.Count)];
            SelectedPanels.Add(panel.GetInstanceID());
            Plugin.Log.LogInfo($"[Splash] selected '{chosen.name}' from {choices.Count} images");
            return chosen;
        }

        // The panel can have completed Awake before BepInEx loads our dependencies.
        // Update that live panel once, retaining its existing canvas, fades and timing.
        internal static void RefreshExisting()
        {
            if (!FikaBridge.CanRender) return;
            try
            {
                LoadSprites();
                if (ExtraSprites.Count == 0) return;
                foreach (var panel in Resources.FindObjectsOfTypeAll<SplashScreenPanel>())
                {
                    if (panel == null || !panel.gameObject.scene.IsValid() || !panel.gameObject.activeInHierarchy ||
                        SelectedPanels.Contains(panel.GetInstanceID()) || panel._images == null) continue;
                    var chosen = Choose(panel);
                    foreach (var image in panel._images)
                        if (image != null) image.sprite = chosen;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[Splash] existing panel update failed: " + e.Message); }
        }

        [HarmonyPatch(typeof(SplashScreenPanel), "get_RandomSprite")]
        private static class Patch_RandomSplash
        {
            [HarmonyPrefix]
            private static bool Prefix(SplashScreenPanel __instance, ref Sprite __result)
            {
                if (!FikaBridge.CanRender) return true;
                try
                {
                    LoadSprites();
                    if (ExtraSprites.Count == 0) return true;
                    __result = Choose(__instance);
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("[Splash] using native selector after error: " + e.Message);
                    return true;
                }
            }
        }
    }
}
