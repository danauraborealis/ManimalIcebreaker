using System;
using System.Collections.Generic;
using SPT.Reflection.Patching;

namespace Manimal.Icebreaker.Fika
{
    // Keep the optional Fika diagnostics isolated from one another. A packet shape
    // can change between Fika releases; one stale target must not prevent the weather
    // guard (or the remaining diagnostics) from being installed.
    internal static class FikaAddonPatchRegistry
    {
        private sealed class Factory
        {
            internal readonly string Name;
            internal readonly Func<ModulePatch> Create;

            internal Factory(string name, Func<ModulePatch> create)
            {
                Name = name;
                Create = create;
            }
        }

        private static readonly Factory[] Factories =
        {
            new Factory(nameof(Patch_DoorSendDiag), () => new Patch_DoorSendDiag()),
            new Factory(nameof(Patch_DoorRecvDiag), () => new Patch_DoorRecvDiag()),
            new Factory(nameof(Patch_HostWeatherDiag), () => new Patch_HostWeatherDiag()),
            new Factory(nameof(Patch_WeatherRequestGuard), () => new Patch_WeatherRequestGuard()),
        };

        private static readonly List<ModulePatch> Active = new List<ModulePatch>();

        internal static void EnableAll()
        {
            if (Active.Count != 0) return;

            foreach (var factory in Factories)
            {
                ModulePatch patch = null;
                try
                {
                    // Keep construction and Enable in the same guarded boundary:
                    // target resolution can fail before Harmony is called, while an
                    // Enable failure can leave a partial patch behind.
                    patch = factory.Create();
                    patch.Enable();
                    Active.Add(patch);
                    FikaAddonPlugin.Log?.LogDebug($"[Fika] enabled patch {patch.GetType().Name}");
                }
                catch (Exception e)
                {
                    TryDisable(patch);
                    FikaAddonPlugin.Log?.LogError($"[Fika] patch {factory.Name} failed: {e}");
                }
            }
        }

        internal static void DisableAll()
        {
            for (var i = Active.Count - 1; i >= 0; i--)
                TryDisable(Active[i]);
            Active.Clear();
        }

        private static void TryDisable(ModulePatch patch)
        {
            if (patch == null) return;
            try { patch.Disable(); }
            catch (Exception e) { FikaAddonPlugin.Log?.LogWarning($"[Fika] patch {patch.GetType().Name} cleanup failed: {e.Message}"); }
        }
    }
}
