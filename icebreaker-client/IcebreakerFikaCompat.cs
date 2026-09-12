using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Manimal.Icebreaker
{
    // fika swaps two classes out from under our attribute patches (audited against
    // Fika-Plugin source, 07-28):
    //  - CoopGame : BaseLocalGame replaces LocalGame — the smethod_6 map-fare consume
    //    never fires in coop. re-anchor onto CoopGame.Create, which receives the same
    //    (profile, location, localRaidSettings) triple.
    //
    // types are resolved by name at runtime — no compile-time fika reference, and the
    // soft BepInDependency on the plugin guarantees fika loads first when installed.
    internal static class IcebreakerFikaCompat
    {
        internal static void TryApply()
        {
            if (!FikaBridge.Present) return;
            int applied = 0;
            try
            {
                var create = Type.GetType("Fika.Core.Main.GameMode.CoopGame, Fika.Core")
                    ?.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                if (create != null)
                {
                    new CoopCreatePatch(create).Enable();
                    applied++;
                }
                else Plugin.Log.LogError("[Fika] CoopGame.Create not found — map fare will NOT charge in coop, report this");

                // fika's player never passes LocalPlayer.Create, where the icebreaker
                // EnvironmentManager/weather rebuild is anchored — without this, coop
                // Player.Init NRE'd on the missing manager and loading froze at 25%
                var fikaPlayer = Type.GetType("Fika.Core.Main.Players.FikaPlayer, Fika.Core");
                MethodInfo playerCreate = null;
                if (fikaPlayer != null)
                    foreach (var method in fikaPlayer.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        if (method.Name == "Create" && method.DeclaringType == fikaPlayer)
                        {
                            if (playerCreate != null) throw new AmbiguousMatchException("FikaPlayer.Create has multiple candidates");
                            playerCreate = method;
                        }
                if (playerCreate != null)
                {
                    new PlayerCreatePatch(playerCreate).Enable();
                    applied++;
                }
                else Plugin.Log.LogError("[Fika] FikaPlayer.Create not found — icebreaker will NOT load in coop, report this");

                // CoopGame overrides Stop, so the LocalGame.Stop torch-strip never fires
                // in coop — re-anchor it (same param names, harmony binds them through)
                var coopStop = Type.GetType("Fika.Core.Main.GameMode.CoopGame, Fika.Core")
                    ?.GetMethod("Stop", BindingFlags.Public | BindingFlags.Instance);
                if (coopStop != null)
                {
                    new CoopStopPatch(coopStop).Enable();
                    applied++;
                }
                else Plugin.Log.LogWarning("[Fika] CoopGame.Stop not found — blowtorch stays in inventory after coop extracts");
            }
            catch (Exception e) { Plugin.Log.LogError($"[Fika] compat patching failed: {e}"); }
            Plugin.Log.LogDebug($"[Fika] compat patches applied: {applied}/3");
        }

        private sealed class CoopCreatePatch : ModulePatch
        {
            private readonly MethodBase target;
            internal CoopCreatePatch(MethodBase target) { this.target = target; }
            protected override MethodBase GetTargetMethod() => target;
            [PatchPrefix]
            private static void Prefix(Profile profile, JsonType.LocationSettings.Location location, LocalRaidSettings localRaidSettings)
                => CoopGameCreatePrefix(profile, location, localRaidSettings);
        }

        private sealed class PlayerCreatePatch : ModulePatch
        {
            private readonly MethodBase target;
            internal PlayerCreatePatch(MethodBase target) { this.target = target; }
            protected override MethodBase GetTargetMethod() => target;
            [PatchPrefix]
            private static void Prefix() => Patch_EnsureEnvironmentManager.EnsureEnvAndWeather();
        }

        private sealed class CoopStopPatch : ModulePatch
        {
            private readonly MethodBase target;
            internal CoopStopPatch(MethodBase target) { this.target = target; }
            protected override MethodBase GetTargetMethod() => target;
            [PatchPrefix]
            private static void Prefix(string profileId, ExitStatus exitStatus)
                => Patch_StripTorchOnExtract.Strip(profileId, exitStatus);
        }

        private static void CoopGameCreatePrefix(Profile profile, JsonType.LocationSettings.Location location, LocalRaidSettings localRaidSettings)
        {
            // the solo capture (LocalGame.smethod_6) is inert in coop — without this,
            // PendingLocationId stays stale/null on every fika peer and IceGate falls
            // through to the loaded-scene check for the whole pre-GameWorld window
            // (wrong in BOTH directions: late gating on icebreaker, possible leakage
            // onto vanilla maps around scene teardown)
            IceGate.PendingLocationId = location?.Id;
            try { IcebreakerPhysicsRegions.ResetForNewRaid(); } catch { }
            Plugin.Log.LogInfo($"[IceGate] raid location (coop): '{IceGate.PendingLocationId ?? "<null>"}'");
            IcebreakerMapFare.Consume(profile, location, localRaidSettings);
        }

    }
}
