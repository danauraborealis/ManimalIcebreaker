using System;
using System.Reflection;
using HarmonyLib;

namespace Manimal.Icebreaker
{
    // LOCKABLE DOORS, OFF ON THIS MAP (field report 08-06: "this mod breaks every door in
    // icebreaker"). Jehree.LockableDoors is a perfectly good mod that simply cannot work
    // here: its GameStartedPatch walks every operatable door at raid start, FORCES open
    // ones shut and bolts a DoorLock onto them from a server-side list. this map's doors
    // are progression — the sealed doors, the chain door, the keycard rooms and the
    // cutscene gate all own their own state — so a blanket shut-and-lock pass breaks the
    // route through the ship.
    //
    // suppression, not exception-swallowing: the mod never throws, it just does the wrong
    // thing here. so the two patch methods are prefixed to no-op while IceGate.On, which
    // leaves the mod fully intact on every other map — the reporter's actual ask ("i want
    // to lock doors in other map"). all reflection: the mod is optional and absent from
    // most installs, and nothing here may hard-reference it.
    internal static class IcebreakerLockableDoorsOff
    {
        private const string PluginGuid = "Jehree.LockableDoors";
        private static bool _tried;

        // (type, method) pairs to neutralise. names verified against the 2.0.0 source:
        //   GameStartedPatch.PatchPrefix   — the raid-start shut+lock sweep
        //   GetAvailableActionsPatch.PatchPostfix — the lock/unlock action menu entries,
        //     which would otherwise stack onto our own door actions
        private static readonly (string type, string method)[] Targets =
        {
            ("LockableDoors.Patches.GameStartedPatch", "PatchPrefix"),
            ("LockableDoors.Patches.GetAvailableActionsPatch", "PatchPostfix"),
        };

        internal static void TryPatch(Harmony h)
        {
            if (_tried) return;
            _tried = true;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(PluginGuid)) return;

                var skip = new HarmonyMethod(AccessTools.Method(typeof(IcebreakerLockableDoorsOff), nameof(SkipOnIcebreaker)));
                int done = 0;
                foreach (var (typeName, methodName) in Targets)
                {
                    try
                    {
                        var t = AccessTools.TypeByName(typeName);
                        var m = t != null ? AccessTools.Method(t, methodName) : null;
                        if (m == null)
                        {
                            Plugin.Log.LogWarning($"[LockableDoors] {typeName}.{methodName} not found — mod updated? "
                                + "doors on the icebreaker may be locked/broken until this shim is refreshed");
                            continue;
                        }
                        h.Patch(m, prefix: skip);
                        done++;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"[LockableDoors] couldn't neutralise {typeName}.{methodName}: {e.Message}");
                    }
                }
                if (done > 0)
                    Plugin.Log.LogWarning($"[LockableDoors] detected — its door locking is suppressed on the icebreaker "
                        + $"({done} hook(s) gated). the mod stays fully active on every other map.");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[LockableDoors] shim failed: {e.Message}"); }
        }

        // false = skip the original. IceGate is the ONLY condition, so every other map
        // keeps the mod's behaviour untouched.
        private static bool SkipOnIcebreaker() => !IceGate.On;
    }
}
