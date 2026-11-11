using System;
using EFT;
using SPT.Reflection.Patching;

namespace Manimal.Icebreaker
{
    // ScenePresetLoadConfig is the first stable signal in TarkovApplication's map
    // pipeline. LocalGame.Create runs only after this operation has loaded and
    // activated its scenes, so it cannot be the primary lifetime boundary.
    internal static class IcebreakerScenePresetIdentity
    {
        internal static bool IsIcebreaker(ScenePresetLoadConfig config)
        {
            try
            {
                var path = config.key.path;
                var rcid = config.key.rcid;
                return IsIcebreakerPath(path) || IsIcebreakerRcid(rcid);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsIcebreaker(ScenesPreset preset)
        {
            if (preset == null) return false;
            try
            {
                if (string.Equals(preset.ServerName, BuildInfo.ServerName, StringComparison.OrdinalIgnoreCase))
                    return true;

                var name = preset.PresetName;
                return string.Equals(name, BuildInfo.ServerName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, IcebreakerLocation.Key, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(name)
                        && name.IndexOf(IcebreakerLocation.Key, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsIcebreakerPath(string value)
            => !string.IsNullOrEmpty(value)
                && (string.Equals(value, "maps/icebreaker.bundle", StringComparison.OrdinalIgnoreCase)
                    || value.EndsWith("/icebreaker.bundle", StringComparison.OrdinalIgnoreCase)
                    || value.EndsWith("\\icebreaker.bundle", StringComparison.OrdinalIgnoreCase));

        private static bool IsIcebreakerRcid(string value)
            => !string.IsNullOrEmpty(value)
                && (string.Equals(value, "icebreaker.scenespreset.asset", StringComparison.OrdinalIgnoreCase)
                    || value.EndsWith("/icebreaker.scenespreset.asset", StringComparison.OrdinalIgnoreCase)
                    || value.EndsWith("\\icebreaker.scenespreset.asset", StringComparison.OrdinalIgnoreCase));
    }

    [IcebreakerPatchScope(IcebreakerPatchScope.Bootstrap)]
    [IcebreakerPatchTarget(
        typeof(LoadScenesFromPresetOperation),
        nameof(LoadScenesFromPresetOperation.LoadPresetFromConfigAsync),
        typeof(ScenePresetLoadConfig))]
    internal sealed class Patch_IcebreakerPresetConfig : IcebreakerModulePatch<Patch_IcebreakerPresetConfig>
    {
        [PatchPrefix]
        private static void Prefix(ScenePresetLoadConfig preset)
        {
            if (IcebreakerScenePresetIdentity.IsIcebreaker(preset))
                IcebreakerPatchLifecycle.BeginOuterPreset(icebreaker: true);
            else
                IcebreakerPatchLifecycle.BeginOuterPreset(icebreaker: false);
        }

        [PatchPostfix]
        private static void Postfix(LoadScenesFromPresetOperation __instance, object __result)
            => IcebreakerPatchLifecycle.TrackOuterLoadTask(__result, __instance);

        [PatchFinalizer]
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception != null)
                IcebreakerPatchLifecycle.NotifyOuterLoadFailure("scene preset config load failed");
            return __exception;
        }
    }

    [IcebreakerPatchScope(IcebreakerPatchScope.Bootstrap)]
    [IcebreakerPatchTarget(
        typeof(LoadScenesFromPresetOperation),
        nameof(LoadScenesFromPresetOperation.LoadPresetAsync),
        typeof(ScenesPreset))]
    internal sealed class Patch_IcebreakerPreset : IcebreakerModulePatch<Patch_IcebreakerPreset>
    {
        [PatchPrefix]
        private static void Prefix(ScenesPreset preset)
        {
            // Child presets are allowed to omit ServerName. They are still covered
            // by the config producer's task and must not request a release merely
            // because this nested callback is not independently identifiable.
            if (IcebreakerScenePresetIdentity.IsIcebreaker(preset))
                IcebreakerPatchLifecycle.ObserveResolvedPreset();
        }

        [PatchPostfix]
        private static void Postfix(LoadScenesFromPresetOperation __instance, object __result)
            => IcebreakerPatchLifecycle.TrackChildLoadTask(__result, __instance);

        [PatchFinalizer]
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception != null)
                IcebreakerPatchLifecycle.NotifyLoadFailure("scene preset load failed");
            return __exception;
        }
    }
}
