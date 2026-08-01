using System;
using System.Linq;
using System.Reflection;
using EFT;
using HarmonyLib;

namespace Manimal.Icebreaker
{
    // fika swaps two classes out from under our attribute patches (audited against
    // Fika-Plugin source, 07-28):
    //  - CoopGame : BaseLocalGame replaces LocalGame — the smethod_6 map-fare consume
    //    never fires in coop. re-anchor onto CoopGame.Create, which receives the same
    //    (profile, location, localRaidSettings) triple.
    //  - FikaHostTransitController overrides InteractWithTransit — the transit charge
    //    prefix never fires, and clients run a different controller entirely.
    //    re-anchor onto FikaPlayer.vmethod_3, the transit CONFIRM moment: it runs only
    //    on the machine of the confirming player (ObservedPlayer's override is empty),
    //    so the traveler pays locally and no remote inventory is ever touched — the
    //    desync trap the fika modding wiki warns about.
    //
    // types are resolved by name at runtime — no compile-time fika reference, and the
    // soft BepInDependency on the plugin guarantees fika loads first when installed.
    // NOTE the method_14/method_15 access+fare GATES need no re-anchoring: they patch
    // TransitInteractionControllerAbstractClass, the shared base of both fika
    // controllers, so scav-deny / chain-check / cant-afford already work on every peer.
    internal static class IcebreakerFikaCompat
    {
        internal static void TryApply(Harmony h)
        {
            if (!FikaBridge.Present) return;
            int applied = 0;
            try
            {
                var create = Type.GetType("Fika.Core.Main.GameMode.CoopGame, Fika.Core")
                    ?.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                if (create != null)
                {
                    h.Patch(create, prefix: new HarmonyMethod(typeof(IcebreakerFikaCompat), nameof(CoopGameCreatePrefix)));
                    applied++;
                }
                else Plugin.Log.LogError("[Fika] CoopGame.Create not found — map fare will NOT charge in coop, report this");

                // by name + param shape, not exact types: the 4th param (EDateTime)
                // stays out of our compile-time surface
                var fikaPlayer = Type.GetType("Fika.Core.Main.Players.FikaPlayer, Fika.Core");
                var vm3 = fikaPlayer
                    ?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "vmethod_3"
                        && m.GetParameters().Length == 4
                        && m.GetParameters()[0].ParameterType == typeof(TransitControllerAbstractClass));
                if (vm3 != null)
                {
                    h.Patch(vm3, prefix: new HarmonyMethod(typeof(IcebreakerFikaCompat), nameof(TransitConfirmPrefix)));
                    applied++;
                }
                else Plugin.Log.LogError("[Fika] FikaPlayer.vmethod_3(transit) not found — transit fare will NOT charge in coop, report this");

                // fika's player never passes LocalPlayer.Create, where the icebreaker
                // EnvironmentManager/weather rebuild is anchored — without this, coop
                // Player.Init NRE'd on the missing manager and loading froze at 25%
                var playerCreate = fikaPlayer
                    ?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Create" && m.DeclaringType == fikaPlayer);
                if (playerCreate != null)
                {
                    h.Patch(playerCreate, prefix: new HarmonyMethod(typeof(IcebreakerFikaCompat), nameof(PlayerCreatePrefix)));
                    applied++;
                }
                else Plugin.Log.LogError("[Fika] FikaPlayer.Create not found — icebreaker will NOT load in coop, report this");

                // CoopGame overrides Stop, so the LocalGame.Stop torch-strip never fires
                // in coop — re-anchor it (same param names, harmony binds them through)
                var coopStop = Type.GetType("Fika.Core.Main.GameMode.CoopGame, Fika.Core")
                    ?.GetMethod("Stop", BindingFlags.Public | BindingFlags.Instance);
                if (coopStop != null)
                {
                    h.Patch(coopStop, prefix: new HarmonyMethod(typeof(IcebreakerFikaCompat), nameof(CoopStopPrefix)));
                    applied++;
                }
                else Plugin.Log.LogWarning("[Fika] CoopGame.Stop not found — blowtorch stays in inventory after coop extracts");
            }
            catch (Exception e) { Plugin.Log.LogError($"[Fika] compat patching failed: {e}"); }
            Plugin.Log.LogDebug($"[Fika] compat patches applied: {applied}/4");
        }

        private static void PlayerCreatePrefix() => Patch_EnsureEnvironmentManager.EnsureEnvAndWeather();

        private static void CoopStopPrefix(string profileId, ExitStatus exitStatus)
            => Patch_StripTorchOnExtract.Strip(profileId, exitStatus);

        private static void CoopGameCreatePrefix(Profile profile, LocationSettingsClass.Location location, LocalRaidSettings localRaidSettings)
            => IcebreakerMapFare.Consume(profile, location, localRaidSettings);

        private static bool TransitConfirmPrefix(Player __instance, int transitPointId)
        {
            try
            {
                if (transitPointId != IcebreakerTransit.PointId) return true;
                if (__instance == null || !__instance.IsYourPlayer) return true;
                int cost = Plugin.TransitCost.Value;
                if (cost <= 0) return true;
                // false blocks the whole interaction — fail closed, same as solo
                return IcebreakerTransitFare.TryTakeFare(__instance, cost);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Fare] fika confirm check failed, boarding blocked: {e.Message}");
                return false;
            }
        }
    }
}
