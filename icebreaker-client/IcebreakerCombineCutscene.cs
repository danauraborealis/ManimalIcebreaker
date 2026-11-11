using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using SPT.Reflection.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZLinq;

namespace Manimal.Icebreaker
{
    // Render-only Combine clothing for Icebreaker_cutscene_01.  The cutscene owns
    // the actor roots, animators, props, and weapons; this class only adds renderers
    // below those roots and toggles the authored body/gear renderers after a complete
    // replacement has been built.
    internal static class IcebreakerCombineCutscene
    {
        private const float PreparationTimeoutSeconds = 25f;
        private const string DonorRootName = "Icebreaker_CombineCutsceneDonors";

        // These are the exact authored top-level actor roots recovered from the
        // timeline scene.  Actor_Boss_Wedge is a nested mesh group, and Pilot00/01
        // are unrelated helicopter occupants, so neither is ever selected here.
        private static readonly HashSet<string> ActorRootNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Actor_AimerL", "Actor_AimerR", "Actor_Jumper00", "Actor_Jumper01",
            "Actor_Jumper01_Boss", "Actor_Jumper02_Boss", "Actor_RopeHelper",
            "Actor_Gunner", "Actor_GunnerRight", "Actor_GunnerLeft", "Actor_Crew00",
        };

        private static readonly HashSet<string> WedgeRootNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Actor_Jumper01_Boss", "Actor_Jumper02_Boss",
        };

        private static readonly HashSet<string> ShotgunnerRootNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Actor_AimerR", "Actor_Jumper01",
        };

        private static readonly EquipmentSlot[] WeaponSlots =
        {
            EquipmentSlot.FirstPrimaryWeapon,
            EquipmentSlot.SecondPrimaryWeapon,
            EquipmentSlot.Holster,
            EquipmentSlot.Scabbard,
        };

        private static readonly string[] WeaponOrPropTokens =
        {
            "weapon", "cutscene_weapon", "gun", "rifle", "shotgun", "pistol", "smg", "m4", "ak", "mp5",
            "mag", "scope", "optic", "sight", "muzzle", "silencer", "suppress", "laser",
            "flashlight", "grenade", "knife", "holster", "bullet", "shell", "cartridge",
            "rotor", "helicopter", "rope", "camera", "light", "flare", "vfx", "prop",
            "door", "hatch", "seat", "wheel", "pilot",
        };

        private static readonly string[] ClothingTokens =
        {
            "head", "face", "helmet", "mask", "gasmask", "eyewear", "earpiece", "hood",
            "armor", "armour", "vest", "plate", "top", "shirt", "jacket", "pants", "bottom",
            "backpack", "rig", "pouch", "glove", "boot", "shoe", "clothing", "gear", "body",
            "wedge",
        };

        private const string EliteEyeShaderName = "p0/Reflective/Bumped Emissive Specular SMap";
        private const string EliteEyeMaterialName = "Elite_lens_glow";

        private static bool _initialized;
        private static bool _selectionActive;
        private static bool _combineTookPrecedence;
        private static int _generation;
        private static CancellationTokenSource _prepareCancellation;
        private static Task<PreparedDonors> _prepareTask;
        private static PreparedDonors _prepared;
        private static SceneState _sceneState;

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            // Dedicated/headless Fika processes must not load visual donor assets.
            // Keeping the event subscription out of that process also prevents a
            // late host packet from starting a model load there.
            if (!FikaBridge.CanRender)
            {
                Plugin.Log.LogDebug("[CombineCutscene] headless renderer unavailable; donor loading disabled");
                return;
            }

            IcebreakerCombineEncounter.SelectionReady += OnSelectionReady;
            IcebreakerCombineEncounter.RaidReset += OnRaidReset;
            if (IcebreakerCombineEncounter.Ready)
                OnSelectionReady(IcebreakerCombineEncounter.Active);
        }

        // Called after the additive scene has been loaded and before the director is
        // started.  The wait is deliberately bounded so a cold bundle or a missing
        // backend profile leaves the authored scene usable.
        internal static IEnumerator Apply(Scene scene)
        {
            _combineTookPrecedence = false;
            if (!FikaBridge.CanRender || !scene.IsValid() || !scene.isLoaded) yield break;

            // A selection edge normally starts preparation.  This check covers the
            // small race where the scene trigger arrives on the same frame as the
            // authoritative selection edge.
            if (!_selectionActive && IcebreakerCombineEncounter.Active)
            {
                _selectionActive = true;
                StartPreparation();
            }
            if (!_selectionActive) yield break;

            _combineTookPrecedence = true;
            var generation = _generation;
            if (_prepareTask == null) StartPreparation();
            var task = _prepareTask;
            if (task == null)
            {
                Plugin.Log.LogWarning("[CombineCutscene] no preparation task; authored actors kept");
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + PreparationTimeoutSeconds;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
                yield return null;

            if (!task.IsCompleted)
            {
                Plugin.Log.LogWarning($"[CombineCutscene] donor preparation exceeded {PreparationTimeoutSeconds:0}s; authored actors kept");
                yield break;
            }
            if (task.IsCanceled || task.IsFaulted)
            {
                LogTaskFailure(task);
                yield break;
            }

            PreparedDonors donors;
            try { donors = task.GetAwaiter().GetResult(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[CombineCutscene] donor preparation failed; authored actors kept: {e.Message}");
                yield break;
            }

            // Reset may have happened while the backend task was completing.  A
            // stale result is destroyed here and is never allowed to commit to a new
            // raid or scene.
            if (generation != _generation || !_selectionActive || donors == null)
            {
                donors?.Dispose();
                yield break;
            }
            if (_sceneState != null && IsSameScene(scene, _sceneState.Scene))
            {
                // A director can invoke its setup coroutine more than once during
                // a retry.  Keep the first transaction authoritative and release
                // the duplicate donor set returned by the completed task.
                donors.Dispose();
                yield break;
            }

            _prepared = donors;
            _sceneState = new SceneState(scene);
            var actors = FindActorRoots(scene);
            int attempted = 0;
            int applied = 0;
            for (int i = 0; i < actors.Count; i++)
            {
                var actor = actors[i];
                if (!actor) continue;
                int role = RoleForActor(actor.name);
                var donor = donors.Find(role);
                attempted++;
                if (donor == null)
                {
                    Plugin.Log.LogWarning($"[CombineCutscene] actor '{actor.name}' has no donor role {role}; authored actor kept");
                    continue;
                }
                if (TryApplyActor(actor, donor, _sceneState)) applied++;
            }

            Plugin.Log.LogInfo($"[CombineCutscene] actor swap complete: {applied}/{attempted} selected roots; "
                + "unmatched actors retained their authored renderers");
        }

        private static int RoleForActor(string actorName)
        {
            if (WedgeRootNames.Contains(actorName)) return IcebreakerCombineEncounter.CombineElite;
            if (ShotgunnerRootNames.Contains(actorName)) return IcebreakerCombineEncounter.CombineShotgunner;
            return IcebreakerCombineEncounter.CombineSoldier;
        }

        private static bool IsEliteEyeMaterial(int role, string materialName)
        {
            return role == IcebreakerCombineEncounter.CombineElite
                && (string.Equals(materialName, EliteEyeMaterialName, StringComparison.Ordinal)
                    || string.Equals(materialName, EliteEyeMaterialName + " (Instance)", StringComparison.Ordinal));
        }

        // The timeline calls this from its Restore path before unloading the scene.
        // Passing an invalid scene is intentional during an early-bail/OnDestroy;
        // it still releases the current transaction.
        internal static void ReleaseForScene(Scene scene)
        {
            if (_sceneState != null && (IsSameScene(scene, _sceneState.Scene) || !scene.IsValid()))
            {
                _sceneState.Restore();
                _sceneState = null;
            }

            _generation++;
            CancelPreparation();
            if (_prepared != null)
            {
                _prepared.Dispose();
                _prepared = null;
            }
            _combineTookPrecedence = false;
        }

        // True after Apply has observed an authoritative Combine selection.  The
        // timeline uses this to keep the old player-wedge override from winning over
        // a selected Combine take, including the per-actor fallback case.
        internal static bool CombineTookPrecedence(Scene scene)
            => _combineTookPrecedence && (_sceneState == null || IsSameScene(scene, _sceneState.Scene));

        private static void OnSelectionReady(bool active)
        {
            if (!_initialized || !FikaBridge.CanRender) return;
            _selectionActive = active;
            if (!active)
            {
                _generation++;
                CancelPreparation();
                if (_sceneState != null)
                {
                    _sceneState.Restore();
                    _sceneState = null;
                }
                if (_prepared != null) { _prepared.Dispose(); _prepared = null; }
                return;
            }
            StartPreparation();
        }

        private static void OnRaidReset()
        {
            _generation++;
            _selectionActive = false;
            _combineTookPrecedence = false;
            CancelPreparation();
            if (_sceneState != null)
            {
                _sceneState.Restore();
                _sceneState = null;
            }
            if (_prepared != null)
            {
                _prepared.Dispose();
                _prepared = null;
            }
        }

        private static void StartPreparation()
        {
            if (!_selectionActive || !FikaBridge.CanRender) return;
            if (_sceneState != null) return;
            if (_prepareTask != null && !_prepareTask.IsCompleted) return;
            CancelPreparation();
            if (_prepared != null)
            {
                _prepared.Dispose();
                _prepared = null;
            }
            _prepareCancellation = new CancellationTokenSource();
            int generation = _generation;
            _prepareTask = PrepareAsync(generation, _prepareCancellation.Token);
        }

        private static void CancelPreparation()
        {
            var task = _prepareTask;
            _prepareTask = null;
            try { _prepareCancellation?.Cancel(); }
            catch { }
            _prepareCancellation?.Dispose();
            _prepareCancellation = null;

            // A completed task can contain a fully built donor set which has not
            // reached Apply yet.  Retrieve and dispose that result here so a raid
            // reset cannot orphan the donor hierarchy.  An incomplete task keeps
            // ownership in PrepareAsync; it drains the native loader before its
            // catch/finally disposes Unity objects.
            if (task != null && task.IsCompleted)
            {
                try { task.GetAwaiter().GetResult()?.Dispose(); }
                catch { }
            }
        }

        private static async Task<PreparedDonors> PrepareAsync(int generation, CancellationToken cancellationToken)
        {
            var donors = new PreparedDonors(generation);
            DateTime deadline = DateTime.UtcNow.AddSeconds(PreparationTimeoutSeconds);
            try
            {
                var session = await WaitForBackendSessionAsync(deadline, generation, cancellationToken);

                // SPT builds differ in how many entries of CountTypeBotWave they
                // consume.  Keep each request to one role so the backend cannot
                // silently honor only Conditions[0], then combine the returned
                // profile batches after all three requests settle.
                var profileTasks = new Task<Profile[]>[3];
                var roles = new[]
                {
                    IcebreakerCombineEncounter.CombineSoldier,
                    IcebreakerCombineEncounter.CombineShotgunner,
                    IcebreakerCombineEncounter.CombineElite,
                };
                for (int i = 0; i < roles.Length; i++)
                    profileTasks[i] = session.LoadBots(new List<CountTypeBotWave>
                    {
                        new CountTypeBotWave(1, (WildSpawnType)roles[i], BotDifficulty.normal),
                    });

                var profileBatches = await AwaitBounded(Task.WhenAll(profileTasks), Remaining(deadline), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _generation) throw new OperationCanceledException(cancellationToken);

                var profiles = new List<Profile>();
                for (int b = 0; b < profileBatches.Length; b++)
                {
                    var batch = profileBatches[b];
                    if (batch == null) continue;
                    profiles.AddRange(batch.AsValueEnumerable()
                        .Where(profile => profile != null)
                        .ToArray());
                }
                var profileArray = profiles.ToArray();
                for (int i = 0; i < roles.Length; i++)
                {
                    var profile = FindProfile(profileArray, roles[i]);
                    if (profile == null)
                    {
                        Plugin.Log.LogWarning($"[CombineCutscene] LoadBots returned no profile for role {roles[i]}");
                        continue;
                    }

                    var visual = profile.GetVisualEquipmentState(true);
                    if (visual == null || visual.Equipment == null)
                    {
                        Plugin.Log.LogWarning($"[CombineCutscene] role {roles[i]} returned no visual equipment state");
                        continue;
                    }
                    for (int s = 0; s < WeaponSlots.Length; s++)
                        visual.Equipment.GetSlot(WeaponSlots[s])?.ChangeContainedItemDirectly(null);

                    var donorRoot = new GameObject($"{DonorRootName}_{roles[i]}");
                    donorRoot.transform.position = new Vector3(100000f, -100000f, 100000f);
                    donorRoot.SetActive(false); // no one-frame donor render in the raid camera
                    var loader = new PlayerModelLoader();
                    donors.Add(new DonorAsset(roles[i], donorRoot, loader));

                    var loadTask = loader.Load(
                        visual,
                        null,
                        0f,
                        ThirdPersonCustomizationFilter.Default,
                        null,
                        cancellationToken,
                        Vector3.zero,
                        donorRoot.transform,
                        0,
                        false,
                        null,
                        false);
                    await AwaitBounded(loadTask, Remaining(deadline), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (generation != _generation) throw new OperationCanceledException(cancellationToken);
                    if (loader.PlayerBody == null)
                        throw new InvalidOperationException($"PlayerModelLoader returned no PlayerBody for role {roles[i]}");
                    donors.Find(roles[i]).Body = loader.PlayerBody;
                }
                if (generation != _generation) throw new OperationCanceledException(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return donors;
            }
            catch
            {
                donors.Dispose();
                throw;
            }
        }

        private static async Task<IEftSession> WaitForBackendSessionAsync(
            DateTime deadline, int generation, CancellationToken cancellationToken)
        {
            while (Remaining(deadline) > TimeSpan.Zero)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _generation) throw new OperationCanceledException(cancellationToken);
                // SPT registers the active client under ClientApplication<IEftSession>.
                // Singleton<TarkovApplication> is a different holder and can be empty
                // throughout a raid even while bot generation is working normally.
                var session = ClientAppUtils.GetClientApp()?.GetClientBackEndSession();
                if (session != null) return session;
                await Task.Delay(100, cancellationToken);
            }
            throw new TimeoutException("client backend session unavailable before donor preparation deadline");
        }

        private static async Task<T> AwaitBounded<T>(Task<T> task, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (task == null) throw new InvalidOperationException("async operation returned null");
            if (timeout <= TimeSpan.Zero)
            {
                try { await task; }
                catch { }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("Combine donor preparation deadline elapsed");
            }
            var timeoutTask = Task.Delay(timeout);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completed = await Task.WhenAny(task, timeoutTask, cancellationTask);
            if (completed != task)
            {
                // PlayerModelLoader can still own Unity objects after its task
                // reports a timeout/cancellation.  Drain the native task before
                // PrepareAsync's catch disposes the donor root; Apply remains
                // bounded because it polls the preparation task independently.
                try { await task; }
                catch { }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("Combine donor preparation deadline elapsed");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return await task;
        }

        private static async Task AwaitBounded(Task task, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (task == null) throw new InvalidOperationException("async operation returned null");
            if (timeout <= TimeSpan.Zero)
            {
                try { await task; }
                catch { }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("Combine donor preparation deadline elapsed");
            }
            var timeoutTask = Task.Delay(timeout);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completed = await Task.WhenAny(task, timeoutTask, cancellationTask);
            if (completed != task)
            {
                try { await task; }
                catch { }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("Combine donor preparation deadline elapsed");
            }
            cancellationToken.ThrowIfCancellationRequested();
            await task;
        }

        private static TimeSpan Remaining(DateTime deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        private static Profile FindProfile(Profile[] profiles, int role)
        {
            return profiles == null
                ? null
                : profiles.AsValueEnumerable().FirstOrDefault(profile => profile != null
                    && profile.Info != null
                    && profile.Info.Settings != null
                    && profile.Info.Settings.Role is WildSpawnType profileRole
                    && (int)profileRole == role);
        }

        private static List<Transform> FindActorRoots(Scene scene)
        {
            var result = new List<Transform>();
            if (!scene.IsValid() || !scene.isLoaded) return result;
            var roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
                WalkActorRoots(roots[i].transform, result);
            return result;
        }

        private static void WalkActorRoots(Transform node, List<Transform> result)
        {
            if (!node) return;
            if (ActorRootNames.Contains(node.name))
            {
                // Do not descend through a matched root.  This makes the exact
                // top-root list authoritative even if an authored child happens to
                // reuse an Actor_* name.
                result.Add(node);
                return;
            }
            for (int i = 0; i < node.childCount; i++)
                WalkActorRoots(node.GetChild(i), result);
        }

        private static bool TryApplyActor(Transform actorRoot, DonorAsset donor, SceneState sceneState)
        {
            var actor = new ActorState(actorRoot);
            BindingContext bindings = null;
            try
            {
                if (donor.Body == null) return false;
                if (!HasBodyPart(donor.Body, EBodyModelPart.Head)
                    || !HasBodyPart(donor.Body, EBodyModelPart.Body)
                    || !HasBodyPart(donor.Body, EBodyModelPart.Feet))
                {
                    Plugin.Log.LogWarning($"[CombineCutscene] actor '{actorRoot.name}' donor lacks head/body/feet; authored actor kept");
                    return false;
                }

                var sourceRenderers = new List<Renderer>();
                donor.Body.GetRenderersNonAlloc(sourceRenderers);
                var selectedRenderers = SelectHighestRenderers(sourceRenderers, donor.Root.transform);
                if (selectedRenderers.Count == 0)
                {
                    Plugin.Log.LogWarning($"[CombineCutscene] actor '{actorRoot.name}' donor has no mesh renderers; authored actor kept");
                    return false;
                }

                bindings = new BindingContext(actorRoot);
                if (!bindings.HasAnimatedCore)
                {
                    Plugin.Log.LogWarning($"[CombineCutscene] actor '{actorRoot.name}' has no destination skinned core; authored actor kept");
                    return false;
                }

                // Snapshot before adding clones.  Clones live below the authored
                // actor root, so enumerating the hierarchy after construction
                // would also disable each new renderer during the hide pass.
                var authoredRenderers = FindAuthoredBodyAndGear(actorRoot);
                if (authoredRenderers.Length == 0)
                    throw new InvalidOperationException("actor has no authored body/gear renderers to suppress");

                int built = 0;
                for (int i = 0; i < selectedRenderers.Count; i++)
                {
                    var source = selectedRenderers[i];
                    if (!source) continue;
                    GameObject clone;
                    if (source is SkinnedMeshRenderer sourceSkinned)
                        clone = BuildSkinnedClone(sourceSkinned, donor.Role, bindings, actorRoot.gameObject.layer, actor);
                    else if (source is MeshRenderer sourceMesh)
                        clone = BuildMeshClone(sourceMesh, donor.Role, bindings, actorRoot.gameObject.layer, actor);
                    else
                        throw new InvalidOperationException($"unsupported donor renderer {source.GetType().Name}");
                    if (!clone) throw new InvalidOperationException("donor renderer clone returned null");
                    actor.Clones.Add(clone);
                    built++;
                }

                if (built != selectedRenderers.Count || built == 0)
                    throw new InvalidOperationException($"only {built}/{selectedRenderers.Count} donor renderers were built");

                // All clones are disabled while being assembled.  The authored
                // renderers are changed only after every donor mesh has bound.
                HideAuthoredBodyAndGear(authoredRenderers, actor);
                for (int i = 0; i < actor.Clones.Count; i++)
                    if (actor.Clones[i]) actor.Clones[i].SetActive(true);
                actor.Applied = true;
                sceneState.Actors.Add(actor);
                if (donor.Role == IcebreakerCombineEncounter.CombineElite)
                {
                    var shaderName = actor.EliteEyeShader ? actor.EliteEyeShader.name : "<original>";
                    Plugin.Log.LogInfo($"[CombineCutscene] actor '{actorRoot.name}' elite eye material overrides={actor.EliteEyeOverrideCount} shader='{shaderName}'");
                }
                Plugin.Log.LogInfo($"[CombineCutscene] actor '{actorRoot.name}' role={donor.Role} "
                    + $"replaced: worn meshes={built}, authored renderers hidden={authoredRenderers.Length}");
                return true;
            }
            catch (Exception e)
            {
                if (bindings != null) actor.BoundHelpers.AddRange(bindings.ConsumeNewHelpers());
                actor.Restore();
                Plugin.Log.LogWarning($"[CombineCutscene] actor '{actorRoot.name}' swap failed; authored actor kept: {e.Message}");
                return false;
            }
        }

        private static bool HasBodyPart(PlayerBody body, EBodyModelPart part)
        {
            if (body == null || body.BodySkins == null) return false;
            if (!body.BodySkins.TryGetValue(part, out var skin) || skin == null) return false;
            var renderers = new List<Renderer>();
            foreach (var renderer in skin.GetRenderers())
                renderers.Add(renderer);
            return renderers.ToArray().AsValueEnumerable()
                .Where(renderer => renderer is SkinnedMeshRenderer skinned && skinned)
                .Any(renderer => ((SkinnedMeshRenderer)renderer).sharedMesh != null);
        }

        private static List<Renderer> SelectHighestRenderers(List<Renderer> source, Transform donorRoot)
        {
            var groups = new Dictionary<string, Renderer>(StringComparer.Ordinal);
            if (source == null) return new List<Renderer>();
            // EFT caches slot renderers with includeInactive=true before disabling
            // DressItem.LootPrefab. Those inventory/world display meshes are not
            // worn geometry and have no animated bones. The donor's outer root is
            // intentionally hidden, so activeInHierarchy would reject its dress too.
            var candidates = source.AsValueEnumerable()
                .Where(renderer => renderer && MeshFor(renderer) != null
                    && IsActiveBelowRoot(renderer.transform, donorRoot))
                .ToArray();
            for (int i = 0; i < candidates.Length; i++)
            {
                var renderer = candidates[i];
                var mesh = MeshFor(renderer);
                var key = TransformPath(renderer.transform, donorRoot) + "|" + StripLod(mesh.name)
                    + "|" + renderer.sharedMaterials.Length;
                if (!groups.TryGetValue(key, out var current) || MeshVertexCount(mesh) > MeshVertexCount(MeshFor(current)))
                    groups[key] = renderer;
            }
            return groups.Values.AsValueEnumerable().ToList();
        }

        private static GameObject BuildSkinnedClone(
            SkinnedMeshRenderer source,
            int role,
            BindingContext bindings,
            int layer,
            ActorState actor)
        {
            if (!source || !source.sharedMesh || source.bones == null || source.bones.Length == 0)
                throw new InvalidOperationException("skinned donor mesh has no mesh/bones");
            if (!bindings.TryBind(source, out var mappedBones, out var mappedRoot))
                throw new InvalidOperationException($"skinned donor '{source.name}' has no animated bone binding");

            var go = new GameObject("CombineCutscene_" + source.name);
            go.layer = layer;
            PlaceClone(go.transform, source.transform, bindings, mappedRoot);
            go.SetActive(false);
            var clone = go.AddComponent<SkinnedMeshRenderer>();
            CopyRendererSettings(source, clone);
            clone.sharedMesh = source.sharedMesh;
            clone.sharedMaterials = BuildCloneMaterials(source.sharedMaterials, role, source is SkinnedMeshRenderer, actor);
            clone.bones = mappedBones;
            clone.rootBone = mappedRoot;
            clone.localBounds = source.localBounds;
            clone.updateWhenOffscreen = true;
            actor.BoundHelpers.AddRange(bindings.ConsumeNewHelpers());
            return go;
        }

        private static GameObject BuildMeshClone(
            MeshRenderer source,
            int role,
            BindingContext bindings,
            int layer,
            ActorState actor)
        {
            var filter = source ? source.GetComponent<MeshFilter>() : null;
            if (!source || !filter || !filter.sharedMesh)
                throw new InvalidOperationException("rigid donor mesh has no MeshFilter/sharedMesh");
            var anchor = bindings.FindNearestAnchor(source.transform);
            if (!anchor) throw new InvalidOperationException($"rigid donor '{source.name}' has no animated anchor");

            var go = new GameObject("CombineCutscene_" + source.name);
            go.layer = layer;
            PlaceClone(go.transform, source.transform, bindings, anchor);
            go.SetActive(false);
            var cloneFilter = go.AddComponent<MeshFilter>();
            cloneFilter.sharedMesh = filter.sharedMesh;
            var clone = go.AddComponent<MeshRenderer>();
            CopyRendererSettings(source, clone);
            clone.sharedMaterials = BuildCloneMaterials(source.sharedMaterials, role, false, actor);
            actor.BoundHelpers.AddRange(bindings.ConsumeNewHelpers());
            return go;
        }

        private static Material[] BuildCloneMaterials(
            Material[] sourceMaterials,
            int role,
            bool skinnedRenderer,
            ActorState actor)
        {
            if (sourceMaterials == null || sourceMaterials.Length == 0 || role != IcebreakerCombineEncounter.CombineElite)
                return sourceMaterials;
            if (!sourceMaterials.AsValueEnumerable()
                .Any(material => material && IsEliteEyeMaterial(role, material.name)))
                return sourceMaterials;

            int overridesBefore = actor.EliteEyeOverrideCount;
            var cloneMaterials = sourceMaterials.AsValueEnumerable()
                .Select(original => ResolveCloneMaterial(original, role, skinnedRenderer, actor))
                .ToArray();
            return actor.EliteEyeOverrideCount == overridesBefore ? sourceMaterials : cloneMaterials;
        }

        private static Material ResolveCloneMaterial(
            Material original,
            int role,
            bool skinnedRenderer,
            ActorState actor)
        {
            if (!original || !IsEliteEyeMaterial(role, original.name)) return original;

            try
            {
                var replacement = actor.GetOrCreateEliteEyeMaterial(original, skinnedRenderer);
                if (!replacement) return original;
                actor.EliteEyeOverrideCount++;
                return replacement;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[CombineCutscene] actor '{actor.RootName}' elite eye material '{original.name}' override failed; original kept: {e.Message}");
                return original;
            }
        }

        private static Material CreateEliteEyeMaterial(Material original, Shader shader, bool skinnedRenderer)
        {
            if (!original.HasProperty("_MainTex"))
                throw new InvalidOperationException("original elite eye material has no _MainTex property");

            var mainTexture = original.mainTexture;
            if (!mainTexture)
                throw new InvalidOperationException("original elite eye material has no main texture");

            var mainScale = original.GetTextureScale("_MainTex");
            var mainOffset = original.GetTextureOffset("_MainTex");
            var replacement = new Material(shader);
            try
            {
                if (!replacement.HasProperty("_MainTex")
                    || !replacement.HasProperty("_Color")
                    || !replacement.HasProperty("_EmissionMap")
                    || !replacement.HasProperty("_EmissiveColor")
                    || !replacement.HasProperty("_EmissionVisibility")
                    || !replacement.HasProperty("_EmissionPower")
                    || !replacement.HasProperty("_SpecColor")
                    || !replacement.HasProperty("_SkinnedMeshMaterial"))
                {
                    throw new InvalidOperationException("native elite eye shader lacks required properties");
                }

                replacement.SetTexture("_MainTex", mainTexture);
                replacement.SetTextureScale("_MainTex", mainScale);
                replacement.SetTextureOffset("_MainTex", mainOffset);
                replacement.SetTexture("_EmissionMap", mainTexture);
                replacement.SetTextureScale("_EmissionMap", mainScale);
                replacement.SetTextureOffset("_EmissionMap", mainOffset);
                replacement.SetColor("_Color", new Color(0.15f, 0.15f, 0.15f, 1f));
                replacement.SetColor("_EmissiveColor", Color.white);
                replacement.SetFloat("_EmissionPower", 1f);
                replacement.SetFloat("_EmissionVisibility", 1f);
                replacement.SetColor("_SpecColor", new Color(0.08f, 0.08f, 0.08f, 1f));
                replacement.SetFloat("_SkinnedMeshMaterial", skinnedRenderer ? 1f : 0f);
                replacement.SetOverrideTag("RenderType", "Opaque");
                replacement.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
                return replacement;
            }
            catch
            {
                if (replacement) UnityEngine.Object.Destroy(replacement);
                throw;
            }
        }

        private static void PlaceClone(Transform clone, Transform source, BindingContext bindings, Transform preferredAnchor)
        {
            if (!bindings.TryFindNearestSourceAnchor(source, out var sourceAnchor, out var actorAnchor))
            {
                actorAnchor = preferredAnchor;
                if (!actorAnchor) throw new InvalidOperationException("no clone anchor");
                clone.SetParent(actorAnchor, false);
                clone.localPosition = source.localPosition;
                clone.localRotation = source.localRotation;
                clone.localScale = source.localScale;
                return;
            }

            // The donor renderer can be several transforms below its mapped bone.
            // Copying source.localPosition after parenting directly to that bone
            // loses every intervening transform.  Preserve the renderer pose in
            // the source-anchor frame, then apply that same frame below the
            // animated destination bone.
            var localPosition = sourceAnchor.InverseTransformPoint(source.position);
            var localRotation = Quaternion.Inverse(sourceAnchor.rotation) * source.rotation;
            var sourceScale = source.lossyScale;
            var anchorScale = sourceAnchor.lossyScale;
            var localScale = new Vector3(
                DivideScale(sourceScale.x, anchorScale.x),
                DivideScale(sourceScale.y, anchorScale.y),
                DivideScale(sourceScale.z, anchorScale.z));
            clone.SetParent(actorAnchor, false);
            clone.localPosition = localPosition;
            clone.localRotation = localRotation;
            clone.localScale = localScale;
        }

        private static float DivideScale(float value, float divisor)
            => Mathf.Abs(divisor) > 0.00001f ? value / divisor : value;

        private static void CopyRendererSettings(Renderer source, Renderer destination)
        {
            destination.shadowCastingMode = source.shadowCastingMode;
            destination.receiveShadows = source.receiveShadows;
            destination.lightProbeUsage = source.lightProbeUsage;
            destination.reflectionProbeUsage = source.reflectionProbeUsage;
            destination.motionVectorGenerationMode = source.motionVectorGenerationMode;
            destination.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
            destination.sortingLayerID = source.sortingLayerID;
            destination.sortingOrder = source.sortingOrder;
            destination.enabled = true;
        }

        private static Renderer[] FindAuthoredBodyAndGear(Transform actorRoot)
        {
            return actorRoot.GetComponentsInChildren<Renderer>(true).AsValueEnumerable()
                .Where(renderer => renderer
                    && !IsWeaponOrProp(renderer.transform, actorRoot)
                    && IsBodyOrGear(renderer, actorRoot))
                .ToArray();
        }

        private static void HideAuthoredBodyAndGear(Renderer[] renderers, ActorState actor)
        {
            if (renderers == null || renderers.Length == 0)
                throw new InvalidOperationException("no authored body/gear renderers found");
            int hidden = 0;
            bool hasSkinnedBody = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (!renderer) continue;
                actor.OriginalRenderers.Add(new RendererState(renderer));
                renderer.enabled = false;
                if (!renderer.enabled) hidden++;
                if (renderer is SkinnedMeshRenderer && MeshFor(renderer) != null) hasSkinnedBody = true;
            }
            if (hidden == 0 || hidden != renderers.Length || !hasSkinnedBody)
                throw new InvalidOperationException($"authored body suppression incomplete ({hidden}/{renderers.Length})");
        }

        private static bool IsBodyOrGear(Renderer renderer, Transform actorRoot)
        {
            if (renderer is SkinnedMeshRenderer) return true;
            var path = TransformPath(renderer.transform, actorRoot);
            return ClothingTokens.AsValueEnumerable()
                .Any(token => path.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsWeaponOrProp(Transform transform, Transform actorRoot)
        {
            return IsWeaponOrPropPath(TransformPath(transform, actorRoot));
        }

        private static bool IsWeaponOrPropPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            // Match a hierarchy segment's object name, not substrings anywhere
            // in the actor path: "act doorgunner" contains both "door" and "gun"
            // but owns the gunner's entire body. Weapon_root_3rd_anim and its
            // descendants still identify the actual weapon/prop branches.
            return path.Split('/').AsValueEnumerable().Any(segment =>
                WeaponOrPropTokens.AsValueEnumerable().Any(token =>
                    segment.Equals(token, StringComparison.OrdinalIgnoreCase)
                    || segment.StartsWith(token + "_", StringComparison.OrdinalIgnoreCase)
                    || segment.StartsWith(token + " ", StringComparison.OrdinalIgnoreCase)
                    || segment.StartsWith(token + "(", StringComparison.OrdinalIgnoreCase)));
        }

        private static Mesh MeshFor(Renderer renderer)
        {
            if (!renderer) return null;
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            if (renderer is MeshRenderer meshRenderer)
                return meshRenderer.GetComponent<MeshFilter>()?.sharedMesh;
            return null;
        }

        private static int MeshVertexCount(Mesh mesh) => mesh != null ? mesh.vertexCount : -1;

        private static string TransformPath(Transform node, Transform stopAt)
        {
            if (!node) return string.Empty;
            var parts = new List<string>();
            var current = node;
            while (current && current != stopAt)
            {
                parts.Add(StripLod(current.name));
                current = current.parent;
            }
            var result = string.Empty;
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                if (result.Length > 0) result += "/";
                result += parts[i];
            }
            return result;
        }

        private static string StripLod(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            int lod = name.IndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
            return lod > 0 ? name.Substring(0, lod) : name;
        }

        private static bool IsSameScene(Scene left, Scene right)
            => left.IsValid() && right.IsValid() && left.handle == right.handle;

        private static bool IsActiveBelowRoot(Transform node, Transform root)
        {
            // The donor root is deliberately hidden and a timeline actor root may
            // be inactive before its shot. Only visibility inside that root matters.
            while (node && node != root)
            {
                if (!node.gameObject.activeSelf) return false;
                node = node.parent;
            }
            return node == root;
        }

        private static void LogTaskFailure(Task task)
        {
            try
            {
                var error = task.Exception?.GetBaseException();
                Plugin.Log.LogWarning($"[CombineCutscene] donor preparation failed; authored actors kept: {error?.Message ?? "cancelled"}");
            }
            catch { Plugin.Log.LogWarning("[CombineCutscene] donor preparation failed; authored actors kept"); }
        }

        private sealed class PreparedDonors
        {
            private readonly Dictionary<int, DonorAsset> _byRole = new Dictionary<int, DonorAsset>();
            private readonly int _generation;
            private bool _disposed;

            internal PreparedDonors(int generation) { _generation = generation; }

            internal void Add(DonorAsset donor)
            {
                if (donor == null) return;
                _byRole[donor.Role] = donor;
            }

            internal DonorAsset Find(int role)
            {
                _byRole.TryGetValue(role, out var donor);
                return donor;
            }

            internal void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var pair in _byRole) pair.Value?.Dispose();
                _byRole.Clear();
            }
        }

        private sealed class DonorAsset
        {
            internal readonly int Role;
            internal readonly GameObject Root;
            internal readonly PlayerModelLoader Loader;
            internal PlayerBody Body;

            internal DonorAsset(int role, GameObject root, PlayerModelLoader loader)
            {
                Role = role;
                Root = root;
                Loader = loader;
            }

            internal void Dispose()
            {
                try { Loader?.Clear(); }
                catch (Exception e) { Plugin.Log.LogDebug($"[CombineCutscene] donor loader cleanup failed: {e.Message}"); }
                if (Root) UnityEngine.Object.Destroy(Root);
            }
        }

        private sealed class SceneState
        {
            internal readonly Scene Scene;
            internal readonly List<ActorState> Actors = new List<ActorState>();

            internal SceneState(Scene scene) { Scene = scene; }

            internal void Restore()
            {
                for (int i = Actors.Count - 1; i >= 0; i--)
                    Actors[i]?.Restore();
                Actors.Clear();
            }
        }

        private sealed class ActorState
        {
            private readonly Transform _root;
            internal readonly List<RendererState> OriginalRenderers = new List<RendererState>();
            internal readonly List<GameObject> Clones = new List<GameObject>();
            internal readonly List<GameObject> BoundHelpers = new List<GameObject>();
            internal readonly Dictionary<Material, Material> EliteEyeMaterials = new Dictionary<Material, Material>();
            private readonly HashSet<Material> _failedEliteEyeMaterials = new HashSet<Material>();
            private bool _eliteEyeShaderResolved;
            internal Shader EliteEyeShader;
            internal int EliteEyeOverrideCount;
            internal bool Applied;

            internal ActorState(Transform root) { _root = root; }

            internal string RootName => _root ? _root.name : "<destroyed>";

            internal Material GetOrCreateEliteEyeMaterial(Material original, bool skinnedRenderer)
            {
                if (!original) return null;
                if (EliteEyeMaterials.TryGetValue(original, out var owned))
                {
                    if (owned) return owned;
                    EliteEyeMaterials.Remove(original);
                }
                if (_failedEliteEyeMaterials.Contains(original)) return null;

                var shader = ResolveEliteEyeShader();
                if (!shader) return null;

                try
                {
                    owned = CreateEliteEyeMaterial(original, shader, skinnedRenderer);
                    EliteEyeMaterials.Add(original, owned);
                    return owned;
                }
                catch (Exception e)
                {
                    if (owned) UnityEngine.Object.Destroy(owned);
                    _failedEliteEyeMaterials.Add(original);
                    Plugin.Log.LogWarning($"[CombineCutscene] actor '{RootName}' elite eye material '{original.name}' configuration failed; original kept: {e.Message}");
                    return null;
                }
            }

            private Shader ResolveEliteEyeShader()
            {
                if (_eliteEyeShaderResolved) return EliteEyeShader;
                _eliteEyeShaderResolved = true;
                try
                {
                    EliteEyeShader = Shader.Find(EliteEyeShaderName) ?? ShadersFinder.Find(EliteEyeShaderName);
                    if (!EliteEyeShader)
                    {
                        Plugin.Log.LogWarning($"[CombineCutscene] actor '{RootName}' elite eye shader '{EliteEyeShaderName}' is unavailable; original eye kept");
                        return null;
                    }
                    if (!EliteEyeShader.isSupported)
                    {
                        Plugin.Log.LogWarning($"[CombineCutscene] actor '{RootName}' elite eye shader '{EliteEyeShaderName}' is unsupported; original eye kept");
                        EliteEyeShader = null;
                    }
                }
                catch (Exception e)
                {
                    EliteEyeShader = null;
                    Plugin.Log.LogWarning($"[CombineCutscene] actor '{RootName}' elite eye shader lookup failed; original eye kept: {e.Message}");
                }
                return EliteEyeShader;
            }

            internal void Restore()
            {
                for (int i = 0; i < OriginalRenderers.Count; i++) OriginalRenderers[i].Restore();
                OriginalRenderers.Clear();
                for (int i = 0; i < Clones.Count; i++)
                {
                    if (!Clones[i]) continue;
                    Clones[i].SetActive(false);
                    UnityEngine.Object.Destroy(Clones[i]);
                }
                Clones.Clear();
                for (int i = 0; i < BoundHelpers.Count; i++)
                    if (BoundHelpers[i]) UnityEngine.Object.Destroy(BoundHelpers[i]);
                BoundHelpers.Clear();
                foreach (var pair in EliteEyeMaterials)
                    if (pair.Value) UnityEngine.Object.Destroy(pair.Value);
                EliteEyeMaterials.Clear();
                _failedEliteEyeMaterials.Clear();
                Applied = false;
            }
        }

        private readonly struct RendererState
        {
            private readonly Renderer _renderer;
            private readonly bool _enabled;

            internal RendererState(Renderer renderer)
            {
                _renderer = renderer;
                _enabled = renderer != null && renderer.enabled;
            }

            internal void Restore()
            {
                if (_renderer) _renderer.enabled = _enabled;
            }
        }

        // A donor's helper bones are carried only when their parent chain reaches a
        // destination skinned bone.  Main humanoid roots are never cloned as detached
        // armatures; existing destination skinned bones always win by name.
        private sealed class BindingContext
        {
            private readonly Transform _actorRoot;
            private readonly Dictionary<string, Transform> _coreByName = new Dictionary<string, Transform>(StringComparer.Ordinal);
            private readonly Dictionary<Transform, Transform> _sourceToActor = new Dictionary<Transform, Transform>();
            private readonly Dictionary<Transform, GameObject> _helpers = new Dictionary<Transform, GameObject>();
            private readonly List<GameObject> _newHelpers = new List<GameObject>();

            internal bool HasAnimatedCore => _coreByName.Count > 0;

            internal BindingContext(Transform actorRoot)
            {
                _actorRoot = actorRoot;
                // Retail actors retain disabled copies of the old model beside
                // the active *_ClothShader model. Their bones have identical names;
                // binding to the first disabled copy would hide every new mesh.
                var destinations = actorRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .AsValueEnumerable()
                    .Where(renderer => renderer && IsActiveBelowRoot(renderer.transform, actorRoot))
                    .ToArray();
                for (int i = 0; i < destinations.Length; i++)
                {
                    var destination = destinations[i];
                    if (!destination) continue;
                    var bones = destination.bones;
                    if (bones != null)
                        for (int b = 0; b < bones.Length; b++)
                            if (bones[b] && !_coreByName.ContainsKey(bones[b].name)) _coreByName.Add(bones[b].name, bones[b]);
                    if (destination.rootBone && !_coreByName.ContainsKey(destination.rootBone.name))
                        _coreByName.Add(destination.rootBone.name, destination.rootBone);
                }
            }

            internal bool TryBind(SkinnedMeshRenderer source, out Transform[] mapped, out Transform mappedRoot)
            {
                mapped = null;
                mappedRoot = null;
                var sourceBones = source.bones;
                if (sourceBones == null || sourceBones.Length == 0) return false;
                var sourceRoot = source.rootBone;
                if (!sourceRoot)
                {
                    for (int i = 0; i < sourceBones.Length; i++)
                        if (sourceBones[i] && _coreByName.ContainsKey(sourceBones[i].name))
                        {
                            sourceRoot = sourceBones[i];
                            break;
                        }
                }
                if (!sourceRoot || !_coreByName.TryGetValue(sourceRoot.name, out mappedRoot)) return false;
                _sourceToActor[sourceRoot] = mappedRoot;

                mapped = new Transform[sourceBones.Length];
                for (int i = 0; i < sourceBones.Length; i++)
                {
                    var sourceBone = sourceBones[i];
                    if (!sourceBone || (mapped[i] = EnsureMapped(sourceBone, sourceRoot)) == null)
                    {
                        mapped = null;
                        return false;
                    }
                }
                return true;
            }

            private Transform EnsureMapped(Transform source, Transform sourceRoot)
            {
                if (_sourceToActor.TryGetValue(source, out var mapped)) return mapped;
                if (_coreByName.TryGetValue(source.name, out mapped))
                {
                    _sourceToActor[source] = mapped;
                    return mapped;
                }
                if (!source.IsChildOf(sourceRoot)) return null;
                var parent = EnsureMapped(source.parent, sourceRoot);
                if (!parent) return null;
                var helper = new GameObject(source.name + "__CombineClothingBone");
                helper.layer = _actorRoot.gameObject.layer;
                helper.transform.SetParent(parent, false);
                helper.transform.localPosition = source.localPosition;
                helper.transform.localRotation = source.localRotation;
                helper.transform.localScale = source.localScale;
                _helpers[source] = helper;
                _newHelpers.Add(helper);
                mapped = helper.transform;
                _sourceToActor[source] = mapped;
                return mapped;
            }

            internal Transform FindNearestSourceAnchor(Transform source)
            {
                return TryFindNearestSourceAnchor(source, out _, out var actorAnchor) ? actorAnchor : null;
            }

            internal Transform FindNearestAnchor(Transform source)
                => FindNearestSourceAnchor(source);

            internal bool TryFindNearestSourceAnchor(
                Transform source,
                out Transform sourceAnchor,
                out Transform actorAnchor)
            {
                sourceAnchor = null;
                actorAnchor = null;
                var current = source;
                while (current)
                {
                    if (_sourceToActor.TryGetValue(current, out var mapped))
                    {
                        sourceAnchor = current;
                        actorAnchor = mapped;
                        return true;
                    }
                    if (_coreByName.TryGetValue(current.name, out mapped))
                    {
                        _sourceToActor[current] = mapped;
                        sourceAnchor = current;
                        actorAnchor = mapped;
                        return true;
                    }
                    current = current.parent;
                }
                return false;
            }

            internal List<GameObject> ConsumeNewHelpers()
            {
                var helpers = new List<GameObject>(_newHelpers);
                _newHelpers.Clear();
                return helpers;
            }
        }
    }
}
