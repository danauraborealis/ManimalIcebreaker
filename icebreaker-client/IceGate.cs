using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Icebreaker
{
    // THE map gate (audit P0): one authoritative answer to "are we on the icebreaker",
    // used by every patch that would otherwise leak behavior onto vanilla maps.
    // resolution order:
    //   1. GameWorld.LocationId — authoritative once the raid is up ("Suburbs" is our
    //      hijacked slot)
    //   2. the location id captured at LocalGame creation — covers game-construction
    //      patches (wave slots) that run before GameWorld carries an id
    //   3. loaded-scene check — covers early load-time patches
    // default is FALSE: when nothing is resolvable, vanilla behavior wins.
    internal static class IceGate
    {
        internal const string LocationId = "Suburbs";

        // set by Patch_CaptureLocationId below at raid creation, before GameWorld
        internal static string PendingLocationId;

        internal static bool On
        {
            get
            {
                try
                {
                    var w = Singleton<GameWorld>.Instance;
                    if (w != null && !string.IsNullOrEmpty(w.LocationId))
                        return string.Equals(w.LocationId, LocationId, StringComparison.OrdinalIgnoreCase);
                }
                catch { }
                if (!string.IsNullOrEmpty(PendingLocationId))
                    return string.Equals(PendingLocationId, LocationId, StringComparison.OrdinalIgnoreCase);
                try { return IcebreakerAcoustics.IcebreakerLoaded(); }
                catch { return false; }
            }
        }

        // the game hands smethod_6 the authoritative Location object before any other
        // identity exists — capture the id so construction-time patches can gate on it
        [HarmonyPatch(typeof(LocalGame), "smethod_6")]
        internal static class Patch_CaptureLocationId
        {
            private static void Prefix(LocationSettingsClass.Location location)
            {
                PendingLocationId = location?.Id;
                // before any scene (and so any LevelPhysicsSettings.Awake) loads
                IcebreakerPhysicsRegions.ResetForNewRaid();
                // the fare is charged once per crossing, and every leg is its own raid
                IcebreakerTransitFare.ResetForNewRaid();
                Plugin.Log.LogInfo($"[IceGate] raid location: '{PendingLocationId ?? "<null>"}'");
            }
        }

        // arriving by transit freezes then crashes, and the log shows PhysX MBP hitting its
        // 64K-objects-per-region cap the moment loot starts instantiating. that cap is a
        // property of how many colliders share one broadphase region, so the useful question
        // is whether a transit arrival carries more of them than a normal load does. this
        // measures the map BEFORE loot spawns, so the two can be compared directly.
        // if the collider count matches but only the transit run overflows, the regions
        // themselves are stale from the previous map rather than the map being too big.
        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_PhysicsAudit
        {
            [HarmonyPostfix]
            private static void Postfix(GameWorld __instance)
            {
                try
                {
                    if (!On) return;

                    int sceneCount = SceneManager.sceneCount;
                    var names = new List<string>();
                    for (int i = 0; i < sceneCount; i++)
                    {
                        var s = SceneManager.GetSceneAt(i);
                        names.Add($"{s.name}{(s.isLoaded ? "" : "(loading)")}");
                    }

                    int colliders = UnityEngine.Object.FindObjectsOfType<Collider>(true).Length;
                    int rigidbodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>(true).Length;

                    int transitCount = 0;
                    bool viaTransit = false;
                    try
                    {
                        var pid = __instance?.MainPlayer?.ProfileId;
                        if (!string.IsNullOrEmpty(pid))
                            viaTransit = TransitControllerAbstractClass.IsTransit(pid, out transitCount);
                    }
                    catch { }

                    Plugin.Log.LogWarning(
                        // broadphase type isn't exposed on Physics in this Unity, but the
                        // engine's own "MBP::addObject" error already names it
                        $"[PhysAudit] viaTransit={viaTransit}({transitCount}) colliders={colliders} " +
                        $"rigidbodies={rigidbodies} scenes={sceneCount} mono={GC.GetTotalMemory(false) / 1048576}MB");
                    Plugin.Log.LogDebug($"[PhysAudit] scenes: {string.Join(" | ", names.ToArray())}");

                    ProbePhysicsSetup();

                    // only when the map didn't lay out its own. once the baked
                    // LevelPhysicsSettings actually runs, this never fires.
                    if (!IcebreakerPhysicsRegions.RegionsApplied)
                        ApplyRetailRegions();
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[PhysAudit] failed: {e.Message}"); }
            }

            // the bundle verifiably ships PhysicsSetup (active, component enabled, script
            // bound to Assembly-CSharp LevelPhysicsSettings — read straight out of the
            // deployed bundle with UnityPy), the Awake patch is woven, and yet no Awake
            // ever fired. this reports what the LIVE object actually looks like so the
            // bind-vs-stripped-vs-inactive question stops being guesswork.
            private static void ProbePhysicsSetup()
            {
                try
                {
                    GameObject host = null;
                    for (int i = 0; i < SceneManager.sceneCount && host == null; i++)
                    {
                        var s = SceneManager.GetSceneAt(i);
                        if (!s.isLoaded || s.name.IndexOf("Scripts", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        foreach (var rgo in s.GetRootGameObjects())
                            if (rgo.name == "PhysicsSetup") { host = rgo; break; }
                    }
                    if (host == null) { Plugin.Log.LogDebug("[PhysAudit] probe: NO 'PhysicsSetup' root in the loaded Scripts scene"); return; }

                    var comps = host.GetComponents<Component>();
                    var names = new List<string>();
                    foreach (var c in comps)
                        names.Add(c == null ? "<MISSING SCRIPT>" : c.GetType().FullName);
                    var lps = host.GetComponent<LevelPhysicsSettings>();
                    Plugin.Log.LogDebug($"[PhysAudit] probe: PhysicsSetup active={host.activeInHierarchy} " +
                                          $"components=[{string.Join(", ", names)}] " +
                                          $"resolved={(lps != null)} enabled={(lps != null ? lps.enabled.ToString() : "n/a")}");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[PhysAudit] probe failed: {e.Message}"); }
            }

            // retail 1.0's own LevelPhysicsSettings for this map, recovered from level698
            // by analysis/extract_levelphysics.py: PhysicsSetup at the origin, bounds
            // centre (0,50,0) extent (360,50,400), subdivisions 16 (the max).
            //
            // hardcoded ON PURPOSE. the first version of this fallback measured the map's
            // colliders instead, and the live run showed why that fails: outliers (exiled
            // exfil point, far water/trigger volumes) stretched the box to 1628x467x1947,
            // and over a box that size an 8x8 grid makes ~200m cells — the ship then
            // concentrates most of its 144k colliders into a handful of cells and the 64K
            // cap blows anyway (38k MBP errors that raid). the retail values put 256 tight
            // cells over exactly the hull, which the editor pass verified holds every
            // collider with the busiest cell at ~39k.
            private static void ApplyRetailRegions()
            {
                try
                {
                    var bounds = new Bounds(new Vector3(0f, 50f, 0f), new Vector3(720f, 100f, 800f));
                    Physics.RebuildBroadphaseRegions(bounds, 16);
                    Plugin.Log.LogDebug("[PhysAudit] applied RETAIL broadphase regions: " +
                                          "centre (0,50,0) size 720x100x800, subdivisions 16");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[PhysAudit] region rebuild failed: {e.Message}"); }
            }
        }
    }
}
