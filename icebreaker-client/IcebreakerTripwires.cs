using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.InventoryLogic;
using EFT.PrefabSettings;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Icebreaker
{
    // authored grenade tripwires. labyrinth-style pre-placed wires dont exist as a
    // data path anywhere (scene or server db — verified) — the game's ONLY tripwire
    // entry is GameWorld.PlantTripwire (the player-plant API), so we author markers
    // in the SDK scenes and plant through it at raid start. that buys the real
    // procedural wire mesh, bot awareness (BotEventHandler), spot/defuse interactions
    // (10s bare / 5s multitool) and the grenade detonation for free.
    //
    // authoring: ManimalTripwireMarker component (or bare empty) named
    // 'manimal_tripwire*', optional '@tpl' name suffix for a per-wire grenade.
    //   position   = wire start (ankle height — endpoints must be within 0.2m Y)
    //   +Z forward = wire direction
    //   scale.z    = wire length in meters (engine limits: 0.8 .. 3.0)
    public static class IcebreakerTripwires
    {
        private const string MarkerPrefix = "manimal_tripwire";
        // synthetic owner for authored wires — also the marker the never-inert and
        // no-defuse patches key on
        public const string OwnerId = "649ceb1a9bdc2d0a7a8b4567";

        // M18 smoke first (a can on the wire suits the gas payload), F-1 as backstop —
        // vanilla may not author a tripwire visual on smokes
        private static readonly string[] DonorTpls =
        {
            "617aa4dd8166f034d57de9c5", // M18 smoke grenade (Green)
            "5710c24ad2720bc3458b45a3", // F-1
        };
        private static TripwireVisual _donorVisual;
        private static readonly HashSet<string> _patchedTpls = new HashSet<string>();

        // --- coop seed handshake ---
        // every peer plants its OWN local wires, so a per-marker roll below 1.0 only
        // stays consistent if all peers roll the same numbers in the same order. the
        // authority rolls a seed and the fika sync addon broadcasts it (kind 12);
        // clients block on it rather than rolling their own, which would leave each
        // player walking through a different set of wires.
        public static event Action<int> SeedRolled;
        private static int _seed;
        private static bool _seedReady;
        private static bool _forceAll;

        public static void ApplyRemoteSeed(int seed)
        {
            _seed = seed;
            _seedReady = true;
            Plugin.Log.LogInfo($"[Tripwires] seed {seed} received from host");
        }

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        private static class Patch_PlantAuthoredTripwires
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!IceGate.On) return;
                // SAIN's assembly is definitely loaded by raid start, whatever the
                // plugin init order was — safe moment for the soft-dependency patch
                SainLocationCompat.TryPatch(new Harmony("com.manimal.icebreaker.saincompat"));
                if (!Plugin.Tripwires.Value) return;
                _donorVisual = null;      // per-raid: pooled assets die with the raid
                _patchedTpls.Clear();
                _seedReady = false;       // a second raid must re-handshake
                _forceAll = false;
                var host = new GameObject("Icebreaker_Tripwires");
                host.AddComponent<TripwirePlanter>();
            }
        }

        private class TripwirePlanter : MonoBehaviour
        {
            private IEnumerator Start()
            {
                // let the raid finish waking up — pool + sync processor exist by then
                yield return new WaitForSeconds(3f);
                var world = Singleton<GameWorld>.Instance;
                var factory = Singleton<ItemFactoryClass>.Instance;
                var pool = Singleton<PoolManagerClass>.Instance;
                if (world == null || factory == null || pool == null)
                {
                    Plugin.Log.LogWarning("[Tripwires] world/factory/pool not ready");
                    Destroy(gameObject);
                    yield break;
                }

                // settle who decides the layout before any marker is rolled
                if (FikaBridge.Present && !FikaBridge.BotsAuthority)
                {
                    float waited = 0f;
                    while (!_seedReady && waited < 12f) { waited += Time.deltaTime; yield return null; }
                    if (!_seedReady)
                    {
                        // no addon installed, or the packet never landed. arming the FULL
                        // set is the safe miss: a wire the host lacks is a false positive
                        // the player walks around, whereas a missing one would let them
                        // stroll through a hazard everyone else can see them ignore
                        _forceAll = true;
                        Plugin.Log.LogError("[Tripwires] no host seed after 12s — arming EVERY wire locally. "
                                            + "install the icebreaker fika sync addon on all peers to share the roll");
                    }
                }
                else if (!_seedReady)
                {
                    _seed = unchecked(Environment.TickCount * 397);
                    _seedReady = true;
                    SeedRolled?.Invoke(_seed);   // addon broadcasts it; no-op in solo
                }

                var jobs = CollectJobs(factory);
                if (jobs.Count == 0) { Destroy(gameObject); yield break; }

                // the grenade bundles are NOT resident — the game only preloads items
                // that exist in inventories/loot at raid start, and these are conjured.
                // load every payload's + donor's resources before touching the pool.
                var resources = new List<ResourceKey>();
                foreach (var j in jobs)
                    try { resources.AddRange(j.grenade.Template.AllResources); } catch { }
                foreach (var donorTpl in DonorTpls)
                    try
                    {
                        var d = factory.CreateItem(factory.MongoID_0, donorTpl, null);
                        if (d != null) resources.AddRange(d.Template.AllResources);
                    }
                    catch { }
                Task load = null;
                try
                {
                    load = pool.LoadBundlesAndCreatePools(
                        PoolManagerClass.PoolsCategory.Raid, PoolManagerClass.AssemblyType.Online,
                        resources.ToArray(), JobPriorityClass.Low, null, default);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Tripwires] bundle preload kickoff failed: {e.Message}"); }
                if (load != null)
                {
                    float deadline = Time.realtimeSinceStartup + 30f;
                    while (!load.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                    if (!load.IsCompleted) Plugin.Log.LogWarning("[Tripwires] bundle preload timed out — planting anyway");
                    else if (load.IsFaulted) Plugin.Log.LogWarning($"[Tripwires] bundle preload faulted: {load.Exception?.GetBaseException().Message}");
                }

                int planted = 0;
                foreach (var j in jobs)
                {
                    if (!EnsureTripwireVisual(factory, j.grenade))
                    {
                        Plugin.Log.LogWarning($"[Tripwires] no tripwire visual for '{j.grenade.TemplateId}' — '{j.name}' skipped");
                        continue;
                    }
                    try
                    {
                        // attribution: the wires belong to the ship, not the player —
                        // absent-owner grenades are a handled path in EFT
                        world.PlantTripwire(j.grenade, OwnerId, j.from, j.to);
                        planted++;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"[Tripwires] plant failed at '{j.name}' ({j.from}): {e.Message}");
                    }
                }
                Plugin.Log.LogWarning($"[Tripwires] planted {planted}/{jobs.Count}");
                Destroy(gameObject);
            }
        }

        private struct Job
        {
            public string name;
            public ThrowWeapItemClass grenade;
            public Vector3 from, to;
        }

        private static List<Job> CollectJobs(ItemFactoryClass factory)
        {
            var jobs = new List<Job>();
            var markers = new List<Transform>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scn = SceneManager.GetSceneAt(i);
                if (!scn.isLoaded || scn.name == null || !scn.name.StartsWith("Icebreaker")) continue;
                foreach (var rgo in scn.GetRootGameObjects())
                    foreach (var tr in rgo.GetComponentsInChildren<Transform>(true))
                        if (tr.name.StartsWith(MarkerPrefix, StringComparison.OrdinalIgnoreCase))
                            markers.Add(tr);
            }
            if (markers.Count == 0)
            {
                Plugin.Log.LogInfo("[Tripwires] no markers in scenes");
                return jobs;
            }

            // a shared seed is only worth anything if every peer rolls the markers in the
            // SAME sequence, and scene traversal order is not a contract — sort by name
            // then position so the ordering is derived from the data itself
            markers.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.name, b.name);
                if (c != 0) return c;
                var pa = a.position; var pb = b.position;
                c = pa.x.CompareTo(pb.x); if (c != 0) return c;
                c = pa.y.CompareTo(pb.y); if (c != 0) return c;
                return pa.z.CompareTo(pb.z);
            });

            int skipped = 0;
            var rng = new System.Random(_seed);
            foreach (var m in markers)
            {
                // roll ALWAYS, even when forcing them all on: keeping the sequence in
                // lockstep matters more than the branch, so a late seed cant reorder it
                bool armed = rng.NextDouble() <= Plugin.TripwireChance.Value || _forceAll;
                if (!armed) { skipped++; continue; }

                float len = Mathf.Clamp(Mathf.Abs(m.lossyScale.z), 0.8f, 3f); // engine limits
                // per-marker tpl override: the SDK marker component encodes it into the
                // GO name as 'name@tpl' (component data doesnt survive the bundle)
                string tpl = Plugin.TripwireTpl.Value;
                int at = m.name.IndexOf('@');
                if (at >= 0 && m.name.Length > at + 1) tpl = m.name.Substring(at + 1).Trim();

                Item item = null;
                try { item = factory.CreateItem(factory.MongoID_0, tpl, null); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Tripwires] item create failed for '{tpl}': {e.Message}"); }
                var grenade = item as ThrowWeapItemClass;
                if (grenade == null)
                {
                    Plugin.Log.LogWarning($"[Tripwires] tpl '{tpl}' is not a throwable — marker '{m.name}' skipped");
                    continue;
                }
                jobs.Add(new Job { name = m.name, grenade = grenade, from = m.position, to = m.position + m.forward * len });
            }
            if (skipped > 0) Plugin.Log.LogInfo($"[Tripwires] chance skipped {skipped}");
            return jobs;
        }

        // modded grenades (CS gas) have no TripwireItself on their GrenadePrefab — the
        // authored "grenade hanging on the wire" visual only exists on vanilla
        // tripwire-compatible nades — and SetupGrenade NREs on it (stake-but-no-wire).
        // fix: graft a donor visual onto the LOADED PREFAB ASSET once per tpl; every
        // pooled instance after that carries it.
        private static bool EnsureTripwireVisual(ItemFactoryClass factory, Item grenade)
        {
            if (_patchedTpls.Contains(grenade.TemplateId.ToString())) return true;
            var pool = Singleton<PoolManagerClass>.Instance;
            if (pool == null) return false;

            var inst = pool.CreateItem(grenade, ECameraType.Default, null, false);
            var gp = inst != null ? inst.GetComponent<GrenadePrefab>() : null;
            bool ok = gp != null && gp.TripwireItself != null;
            string prefabName = inst != null ? inst.name.Replace("(Clone)", "").Trim() : null;
            if (inst != null) UnityEngine.Object.Destroy(inst.gameObject);
            if (ok) { _patchedTpls.Add(grenade.TemplateId.ToString()); return true; }
            if (gp == null)
            {
                Plugin.Log.LogWarning($"[Tripwires] no GrenadePrefab on '{grenade.TemplateId}' — bundle still not loaded?");
                return false;
            }

            if (_donorVisual == null)
                foreach (var donorTpl in DonorTpls)
                {
                    try
                    {
                        var donor = factory.CreateItem(factory.MongoID_0, donorTpl, null);
                        var dInst = donor != null ? pool.CreateItem(donor, ECameraType.Default, null, false) : null;
                        var dgp = dInst != null ? dInst.GetComponent<GrenadePrefab>() : null;
                        _donorVisual = dgp != null ? dgp.TripwireItself : null;
                        if (dInst != null) UnityEngine.Object.Destroy(dInst.gameObject);
                        if (_donorVisual != null)
                        {
                            Plugin.Log.LogInfo($"[Tripwires] donor visual from '{donorTpl}'");
                            break;
                        }
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"[Tripwires] donor '{donorTpl}' fetch failed: {e.Message}"); }
                }
            if (_donorVisual == null || prefabName == null) return false;

            int patched = 0;
            foreach (var asset in Resources.FindObjectsOfTypeAll<GrenadePrefab>())
                if (!asset.gameObject.scene.IsValid() && asset.TripwireItself == null
                    && asset.name == prefabName)
                {
                    asset.TripwireItself = _donorVisual;
                    patched++;
                }
            if (patched > 0)
            {
                Plugin.Log.LogWarning($"[Tripwires] grafted donor tripwire visual onto '{prefabName}' ({patched} asset)");
                _patchedTpls.Add(grenade.TemplateId.ToString());
                return true;
            }
            Plugin.Log.LogWarning($"[Tripwires] prefab asset '{prefabName}' not found to graft");
            return false;
        }
    }
}
