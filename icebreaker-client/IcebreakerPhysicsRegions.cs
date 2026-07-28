using System;
using System.Reflection;
using HarmonyLib;

namespace Manimal.Icebreaker
{
    // PhysX's Multibox Pruning regions are per-WORLD, not per-scene. every vanilla map
    // lays out its own in LevelPhysicsSettings.Awake, which calls
    // Physics.RebuildBroadphaseRegions(mapBounds, subdivisions).
    //
    // that Awake refuses to run twice:
    //     if (levelPhysicsSettings_0 != null) { LogError("multiple LevelPhysicsSetup"); return; }
    // the guard is written for one map at a time, and a TRANSIT breaks that assumption.
    // the map we came from is still holding the static when the new map's component
    // wakes, so the new map silently keeps the OLD map's regions, every one of its
    // colliders lands outside them in the single overflow region, and that region dies
    // at 64K objects. hand the static to whichever map is loading now.
    internal static class IcebreakerPhysicsRegions
    {
        // did a LevelPhysicsSettings actually lay out regions this raid? the fallback
        // sweep in IceGate only runs when nothing did, which keeps the two approaches
        // from fighting over the same grid.
        internal static bool RegionsApplied;

        internal static void ResetForNewRaid() => RegionsApplied = false;

        [HarmonyPatch(typeof(LevelPhysicsSettings), "Awake")]
        internal static class Patch_NewestMapOwnsRegions
        {
            private static readonly FieldInfo Owner =
                AccessTools.Field(typeof(LevelPhysicsSettings), "levelPhysicsSettings_0");

            [HarmonyPrefix]
            private static void Prefix(LevelPhysicsSettings __instance)
            {
                try
                {
                    if (Owner == null) return;
                    var current = Owner.GetValue(null) as LevelPhysicsSettings;
                    // Unity's fake null already covers a destroyed previous owner, so
                    // anything still standing here is a live component from the map we
                    // just came out of. newest map wins.
                    if (current == null || ReferenceEquals(current, __instance)) return;
                    Owner.SetValue(null, null);
                    Plugin.Log.LogWarning(
                        $"[PhysRegions] '{current.name}' still owned the broadphase regions, " +
                        $"handing them to '{__instance.name}' for this map");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[PhysRegions] handover failed: {e.Message}"); }
            }

            [HarmonyPostfix]
            private static void Postfix(LevelPhysicsSettings __instance)
            {
                try
                {
                    if (!IceGate.On) return;
                    RegionsApplied = true;
                    Plugin.Log.LogWarning($"[PhysRegions] '{__instance.name}' laid out the broadphase regions");
                }
                catch { }
            }
        }
    }
}
