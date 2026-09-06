using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.AssetsManager;
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
    // procedural wire mesh, bot awareness (GlobalEventDispatcher), spot/defuse interactions
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
        private static GameObject _visualRoot;
        private static readonly HashSet<string> _visualTpls = new HashSet<string>();

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
                if (_visualRoot != null) UnityEngine.Object.Destroy(_visualRoot);
                _visualRoot = null;
                _visualTpls.Clear();
                _seedReady = false;       // a second raid must re-handshake
                _forceAll = false;
                var host = new GameObject("Icebreaker_Tripwires");
                host.AddComponent<TripwirePlanter>();
            }
        }

        private class TripwirePlanter : MonoBehaviour
        {
            private CancellationTokenSource _loading;

            private void OnDestroy()
            {
                _loading?.Cancel();
                _loading?.Dispose();
                _loading = null;
            }

            private IEnumerator Start()
            {
                // let the raid finish waking up — pool + sync processor exist by then
                yield return new WaitForSeconds(3f);
                var world = Singleton<GameWorld>.Instance;
                var factory = Singleton<EFT.ItemFactory>.Instance;
                var pool = Singleton<EFT.ObjectsFactory>.Instance;
                if (world == null || factory == null || pool == null || !pool.IsPoolReady(ObjectsFactory.PoolsCategory.Raid))
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
                foreach (var job in jobs) _visualTpls.Add(job.grenade.TemplateId.ToString());
                Plugin.Log.LogInfo($"[Tripwires] preparing {jobs.Count} selected wire(s)");

                // the grenade bundles are NOT resident — the game only preloads items
                // that exist in inventories/loot at raid start, and these are conjured.
                // load every payload's + donor's resources before touching the pool.
                var resources = new List<ResourceKey>();
                foreach (var j in jobs)
                    try { resources.AddRange(j.grenade.Template.AllResources); } catch { }
                foreach (var donorTpl in DonorTpls)
                    try
                    {
                        var d = factory.CreateItem(factory.NextId, donorTpl, null);
                        if (d != null) resources.AddRange(d.Template.AllResources);
                    }
                    catch { }
                Task load = null;
                _loading = CancellationTokenSource.CreateLinkedTokenSource(pool.PoolsCancellationToken);
                try
                {
                    load = pool.LoadBundlesAndCreatePools(
                        EFT.ObjectsFactory.PoolsCategory.Raid, EFT.ObjectsFactory.AssemblyType.Local,
                        // In 4.1 Low does not await InitAndFillPools. General awaits
                        // real, ready instances rather than returning pool placeholders.
                        resources.Distinct().ToArray(), Diz.Jobs.JobYieldPriority.General, null, _loading.Token);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Tripwires] bundle preload kickoff failed: {e.Message}"); }
                if (load != null)
                {
                    // Observe faults even if raid teardown cancels this coroutine.
                    _ = load.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    float started = Time.realtimeSinceStartup;
                    float nextReport = started + 30f;
                    while (!load.IsCompleted && Time.realtimeSinceStartup - started < 120f)
                    {
                        if (!IceGate.On || Singleton<GameWorld>.Instance != world) { Destroy(gameObject); yield break; }
                        if (Time.realtimeSinceStartup >= nextReport)
                        {
                            Plugin.Log.LogWarning("[Tripwires] still preparing grenade assets; waiting for usable pools");
                            nextReport += 30f;
                        }
                        yield return null;
                    }
                }
                if (load == null || !load.IsCompleted || load.IsFaulted || load.IsCanceled)
                {
                    Plugin.Log.LogError($"[Tripwires] planted 0/{jobs.Count}: asset preparation failed " +
                        (load?.Exception?.GetBaseException().Message ?? "(cancelled or timed out)"));
                    Destroy(gameObject);
                    yield break;
                }
                if (!IceGate.On || Singleton<GameWorld>.Instance != world) { Destroy(gameObject); yield break; }

                // Retain an independent donor clone for the raid. Returning a borrowed
                // item to its pool must not invalidate the visual used by later wires.
                PrepareDonorVisual(factory, world);

                int planted = 0;
                foreach (var j in jobs)
                {
                    try
                    {
                        if (!EnsureTripwireVisual(j.grenade))
                        {
                            Plugin.Log.LogWarning($"[Tripwires] no usable tripwire visual for '{j.grenade.TemplateId}' — '{j.name}' skipped");
                            continue;
                        }
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
                Plugin.Log.LogInfo($"[Tripwires] planted {planted}/{jobs.Count} selected wire(s)");
                Destroy(gameObject);
            }
        }

        private struct Job
        {
            public string name;
            public EFT.InventoryLogic.ThrowWeap grenade;
            public Vector3 from, to;
        }

        private static List<Job> CollectJobs(EFT.ItemFactory factory)
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
                try { item = factory.CreateItem(factory.NextId, tpl, null); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Tripwires] item create failed for '{tpl}': {e.Message}"); }
                var grenade = item as EFT.InventoryLogic.ThrowWeap;
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

        private static bool UsableVisual(TripwireVisual visual) =>
            visual != null && visual.PivotPosition != null && visual.GrenadeModel != null;

        private static void PrepareDonorVisual(EFT.ItemFactory factory, GameWorld world)
        {
            if (UsableVisual(_donorVisual)) return;
            var pool = Singleton<ObjectsFactory>.Instance;
            foreach (var tpl in DonorTpls)
            {
                GameObject instance = null;
                try
                {
                    var donor = factory.CreateItem(factory.NextId, tpl, null);
                    instance = pool.CreateItem(donor, ECameraType.Default, null, false);
                    var source = instance != null ? instance.GetComponent<GrenadePrefab>()?.TripwireItself : null;
                    if (!UsableVisual(source)) continue;
                    if (_visualRoot == null)
                    {
                        _visualRoot = new GameObject("Icebreaker_TripwireDonor");
                        _visualRoot.transform.SetParent(world.transform, false);
                        _visualRoot.SetActive(false);
                    }
                    _donorVisual = UnityEngine.Object.Instantiate(source, _visualRoot.transform, false);
                    // Hidden by the parent here; native SetupStakes clones an active
                    // visual beneath the actual stake later.
                    _donorVisual.gameObject.SetActive(true);
                    Plugin.Log.LogInfo($"[Tripwires] retained donor visual from '{tpl}'");
                    return;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Tripwires] donor '{tpl}' failed: {e.Message}"); }
                finally { if (instance != null) AssetPoolObject.ReturnToPool(instance); }
            }
            Plugin.Log.LogWarning("[Tripwires] no donor visual available; only grenades with their own valid visual can be planted");
        }

        // Repair the INSTANCE returned to SetupStakes, including instances already
        // created in a pool. Scanning prefab assets by name missed these copies.
        [HarmonyPatch(typeof(ObjectsFactory), nameof(ObjectsFactory.CreateItem),
            typeof(Item), typeof(ECameraType), typeof(IPlayer), typeof(bool))]
        private static class Patch_TripwireItemVisual
        {
            [HarmonyPostfix]
            private static void Postfix(Item item, GameObject __result)
            {
                if (!IceGate.On || item == null || __result == null ||
                    !_visualTpls.Contains(item.TemplateId.ToString()) || !UsableVisual(_donorVisual)) return;
                var prefab = __result.GetComponent<GrenadePrefab>();
                if (prefab != null && !UsableVisual(prefab.TripwireItself))
                    prefab.TripwireItself = _donorVisual;
            }
        }

        private static bool EnsureTripwireVisual(Item grenade)
        {
            GameObject instance = null;
            try
            {
                instance = Singleton<ObjectsFactory>.Instance.CreateItem(grenade, ECameraType.Default, null, false);
                var prefab = instance != null ? instance.GetComponent<GrenadePrefab>() : null;
                if (prefab == null)
                {
                    Plugin.Log.LogWarning($"[Tripwires] prepared item '{grenade.TemplateId}' returned no GrenadePrefab");
                    return false;
                }
                return UsableVisual(prefab.TripwireItself);
            }
            finally { if (instance != null) AssetPoolObject.ReturnToPool(instance); }
        }
    }
}
