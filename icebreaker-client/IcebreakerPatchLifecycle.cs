using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZLinq;

namespace Manimal.Icebreaker
{
    internal enum IcebreakerPatchScope
    {
        Bootstrap,
        Raid
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    internal sealed class IcebreakerPatchScopeAttribute : Attribute
    {
        internal IcebreakerPatchScope Scope { get; }

        internal IcebreakerPatchScopeAttribute(IcebreakerPatchScope scope)
        {
            Scope = scope;
        }
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    internal sealed class IcebreakerPatchTargetAttribute : Attribute
    {
        internal Type TargetType { get; }
        internal string MethodName { get; }
        internal Type[] ArgumentTypes { get; }

        internal IcebreakerPatchTargetAttribute()
        {
        }

        internal IcebreakerPatchTargetAttribute(Type targetType, string methodName, params Type[] argumentTypes)
        {
            TargetType = targetType;
            MethodName = methodName;
            ArgumentTypes = argumentTypes ?? Array.Empty<Type>();
        }
    }

    internal interface IIcebreakerModulePatch
    {
    }

    // Every scoped client patch gets its own Harmony owner. ModulePatch's default
    // owner is the wrapper's short type name, which collides for nested wrappers and
    // makes Disable() remove another patch's hooks. The target identity is included for
    // dynamic multi-target wrappers so each overload can be disabled independently.
    // The generic self type is intentional: it lets the owner include the complete
    // wrapper name even though C# does not permit GetType() in a base constructor call.
    internal abstract class IcebreakerModulePatch<TWrapper> : ModulePatch, IIcebreakerModulePatch
        where TWrapper : IcebreakerModulePatch<TWrapper>
    {
        private readonly MethodBase _target;

        protected IcebreakerModulePatch()
            : base(BuildInfo.ModGuid + "." + typeof(TWrapper).FullName + ".static")
        {
        }

        protected IcebreakerModulePatch(MethodBase target)
            : base(BuildInfo.ModGuid + "." + typeof(TWrapper).FullName + "." + IcebreakerPatchTargets.Identity(target))
        {
            _target = target;
        }

        protected override MethodBase GetTargetMethod()
            => _target ?? IcebreakerPatchTargets.Resolve(GetType());

    }

    internal static class IcebreakerPatchFactory
    {
        internal static IEnumerable<ModulePatch> CreateInstances(Type wrapperType)
        {
            var targets = IcebreakerPatchTargets.Enumerate(wrapperType).AsValueEnumerable().ToArray();
            var targetCtor = wrapperType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(MethodBase) },
                modifiers: null);
            if (targets.Length == 0)
            {
                // A reflection-only optional patch may legitimately have no target on
                // this install.  Do not try its MethodBase constructor (or let one
                // missing optional type abort discovery of every other wrapper).
                if (targetCtor != null)
                    yield break;
                yield return (ModulePatch)Activator.CreateInstance(
                    wrapperType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    args: null,
                    culture: null);
                yield break;
            }

            if (targetCtor == null)
            {
                // Static target attributes are resolved by the inherited GetTargetMethod
                // implementation. The wrapper's own constructor remains parameterless.
                yield return (ModulePatch)Activator.CreateInstance(
                    wrapperType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    args: null,
                    culture: null);
                yield break;
            }

            foreach (var target in targets)
                yield return (ModulePatch)Activator.CreateInstance(
                    wrapperType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    args: new object[] { target },
                    culture: null);
        }
    }

    internal static class IcebreakerPatchTargets
    {
        internal static MethodBase Resolve(Type wrapperType)
        {
            var target = wrapperType.GetCustomAttributes(typeof(IcebreakerPatchTargetAttribute), false)
                .AsValueEnumerable().OfType<IcebreakerPatchTargetAttribute>().SingleOrDefault();
            if (target != null && target.TargetType != null)
                return Resolve(target.TargetType, target.MethodName, target.ArgumentTypes);

            var method = wrapperType.GetMethod("TargetMethod",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (method != null && typeof(MethodBase).IsAssignableFrom(method.ReturnType))
                return method.Invoke(null, null) as MethodBase;

            throw new InvalidOperationException(
                $"{wrapperType.FullName} has no IcebreakerPatchTarget or TargetMethod resolver");
        }

        internal static IEnumerable<MethodBase> Enumerate(Type wrapperType)
        {
            var methods = wrapperType.GetMethod("TargetMethods",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (methods != null)
            {
                if (methods.Invoke(null, null) is IEnumerable values)
                    foreach (var value in values)
                        if (value is MethodBase target) yield return target;
                yield break;
            }

            var single = wrapperType.GetCustomAttributes(typeof(IcebreakerPatchTargetAttribute), false).Length > 0
                ? Resolve(wrapperType)
                : wrapperType.GetMethod("TargetMethod",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null, types: Type.EmptyTypes, modifiers: null)?.Invoke(null, null) as MethodBase;
            if (single != null) yield return single;
        }

        internal static MethodBase Resolve(Type targetType, string methodName, Type[] argumentTypes)
        {
            if (argumentTypes != null && argumentTypes.Length > 0)
                return AccessTools.Method(targetType, methodName, argumentTypes)
                    ?? throw new MissingMethodException(targetType.FullName, methodName);
            return AccessTools.Method(targetType, methodName)
                ?? throw new MissingMethodException(targetType.FullName, methodName);
        }

        internal static string Identity(MethodBase method)
        {
            var parameters = string.Join(",", method.GetParameters().AsValueEnumerable()
                .Select(p => p.ParameterType.FullName).ToArray());
            return method.DeclaringType?.FullName + "." + method.Name + "(" + parameters + ")";
        }
    }

    internal static class IcebreakerPatchLifecycle
    {
        private static readonly List<ModulePatch> Bootstrap = new List<ModulePatch>();
        private static readonly List<ModulePatch> Raid = new List<ModulePatch>();
        private static bool _initialized;
        private static bool _raidActive;
        private static bool _sceneEventsSubscribed;
        private static bool _shuttingDown;
        private static bool _shutdownRequested;
        private static bool _releaseRequested;
        // Retain ownership through the final scene-unloaded callback. This covers
        // a partial load that loses _raidActive while an owned scene remains.
        private static bool _cleanupOwned;
        private static bool _loadProducerExpected;
        private static bool _loadTaskCaptured;
        private static bool _loadTaskTerminal;
        private static Task _loadTask;
        // LoadPresetFromConfigAsync invokes LoadPresetAsync before its own async
        // method has returned a Task.  Keep every accepted producer instead of
        // treating whichever postfix happens to run first as the outer load.
        private static readonly HashSet<Task> LoadTasks = new HashSet<Task>();
        private static readonly Dictionary<Task, object> LoadOperations = new Dictionary<Task, object>();
        private static int _outerProducersInFlight;
        private static GameObject _lifetimeHost;

        internal static bool RaidActive => _raidActive;

        // Kept as the compatibility entry point used by source-linked lifecycle
        // tests and older optional hooks.  New preset hooks use the explicit
        // outer/child methods below.
        internal static void TrackLoadTask(object result)
            => TrackChildLoadTask(result);

        internal static void BeginOuterPreset(bool icebreaker)
        {
            if (_shuttingDown || _shutdownRequested) return;
            // A normal off-map preset does not own any Icebreaker patches. Avoid
            // retaining a producer or doing per-frame lifecycle work for it.
            if (!icebreaker && !_raidActive && !HasOwnedScene()) return;
            _outerProducersInFlight++;
            _loadProducerExpected = true;
            if (icebreaker)
            {
                _cleanupOwned = true;
                IceGate.PendingLocationId = IcebreakerLocation.Key;
                if (_raidActive)
                {
                    // A second top-level load may overlap the first.  Preserve the
                    // first producer in LoadTasks and only clear a pending release.
                    _releaseRequested = false;
                    return;
                }
                Enter(IcebreakerLocation.Key);
            }
            else
            {
                NotifyNonIcebreakerPreset();
            }
        }

        internal static void TrackOuterLoadTask(object result)
            => TrackOuterLoadTask(result, null);

        internal static void TrackOuterLoadTask(object result, object operation)
        {
            if (_shuttingDown) return;
            if (_outerProducersInFlight == 0 && !_raidActive && !HasOwnedScene()) return;
            if (_outerProducersInFlight > 0) _outerProducersInFlight--;
            AddLoadTask(result as Task, operation);
            _loadProducerExpected = _outerProducersInFlight > 0;
            _loadTaskCaptured = !_loadProducerExpected;
            _loadTaskTerminal = _loadTaskCaptured && AllLoadTasksTerminal();
            TryFinalizeRelease("raid load task terminal");
        }

        internal static void TrackChildLoadTask(object result)
            => TrackChildLoadTask(result, null);

        internal static void TrackChildLoadTask(object result, object operation)
        {
            if (_shuttingDown) return;
            if (_outerProducersInFlight == 0 && !_raidActive && !HasOwnedScene()) return;
            AddLoadTask(result as Task, operation);
            _loadTaskCaptured = _outerProducersInFlight == 0;
            _loadTaskTerminal = _loadTaskCaptured && AllLoadTasksTerminal();
            TryFinalizeRelease("child raid load task terminal");
        }

        internal static void Tick()
        {
            if (_shuttingDown) return;
            if (!_releaseRequested && !_raidActive && !_loadProducerExpected
                && _outerProducersInFlight == 0 && LoadTasks.Count == 0)
                return;
            var terminalPresetOperation = false;
            foreach (var task in LoadTasks.AsValueEnumerable().Where(task => task.IsCompleted).ToArray())
            {
                terminalPresetOperation |= LoadOperations.ContainsKey(task);
                if (task.IsCanceled || task.IsFaulted || OperationFailed(task))
                    _releaseRequested = true;
                LoadTasks.Remove(task);
                LoadOperations.Remove(task);
            }
            _loadTaskTerminal = _outerProducersInFlight == 0 && AllLoadTasksTerminal();
            // A successful terminal preset with no activated owned scene cannot use
            // the raid patch set. This covers an empty/invalid preset whose Task did
            // not fault or cancel but left no scene for the unload gate.
            if (_raidActive && _loadTaskCaptured && _outerProducersInFlight == 0
                && terminalPresetOperation && LoadTasks.Count == 0 && !HasOwnedScene())
                _releaseRequested = true;
            TryFinalizeRelease("raid load task completed");
        }

        internal static void RegisterBootstrap(ModulePatch patch)
        {
            if (patch == null || _shutdownRequested || _shuttingDown || Bootstrap.Contains(patch)) return;
            Bootstrap.Add(patch);
            if (!_initialized) return;
            try
            {
                patch.Enable();
            }
            catch (Exception e)
            {
                SafeDisable(patch);
                Plugin.Log?.LogWarning($"[PatchLifecycle] bootstrap patch {patch.GetType().FullName} failed: {e.Message}");
            }
        }

        internal static void RegisterRaid(ModulePatch patch)
        {
            if (patch == null || _shutdownRequested || _shuttingDown || Raid.Contains(patch)) return;
            Raid.Add(patch);
            if (!_raidActive) return;
            try
            {
                patch.Enable();
            }
            catch (Exception e)
            {
                SafeDisable(patch);
                Plugin.Log?.LogWarning($"[PatchLifecycle] optional raid patch {patch.GetType().FullName} failed: {e.Message}");
            }
        }

        internal static void Initialize()
        {
            if (_initialized || _shutdownRequested || _shuttingDown) return;
            // A source-linked lifecycle harness (and a real plugin reload after a
            // failed bootstrap) can leave completed producer bookkeeping behind.
            // Never carry those tasks into a fresh registration generation.
            LoadTasks.Clear();
            LoadOperations.Clear();
            _outerProducersInFlight = 0;
            _cleanupOwned = false;
            _initialized = true;
            foreach (var type in typeof(IcebreakerPatchLifecycle).Assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(IIcebreakerModulePatch).IsAssignableFrom(type)) continue;
                if (type.DeclaringType == typeof(IcebreakerFikaCompat)) continue;
                try
                {
                    var scope = type.GetCustomAttributes(typeof(IcebreakerPatchScopeAttribute), false)
                        .AsValueEnumerable().OfType<IcebreakerPatchScopeAttribute>().SingleOrDefault()?.Scope
                        ?? IcebreakerPatchScope.Raid;
                    var destination = scope == IcebreakerPatchScope.Bootstrap ? Bootstrap : Raid;
                    foreach (var patch in IcebreakerPatchFactory.CreateInstances(type))
                        destination.Add(patch);
                }
                catch (Exception e)
                {
                    // A missing optional dependency is isolated to its wrapper.  Core
                    // bootstrap and menu patches must still be discovered and enabled.
                    Plugin.Log?.LogWarning($"[PatchLifecycle] skipped {type.FullName}: {e.Message}");
                }
            }

            if (!_sceneEventsSubscribed)
            {
                _sceneEventsSubscribed = true;
                SceneManager.sceneUnloaded += OnSceneUnloaded;
            }
            EnsureLifetimeHost();
            Enable(Bootstrap, "bootstrap");
        }

        internal static void Enter(string locationId)
        {
            if (_shutdownRequested || _shuttingDown)
            {
                // A late synchronous LocalGame/CoopGame callback can arrive after
                // plugin destruction. It must not leave the gate claiming that an
                // Icebreaker raid is pending, nor retain a render probe.
                IceGate.PendingLocationId = null;
                RenderEnvProbe.DestroyGlobal();
                return;
            }
            if (!string.Equals(locationId, IcebreakerLocation.Key, StringComparison.OrdinalIgnoreCase))
            {
                DisableRaid("non-icebreaker location");
                return;
            }

            _cleanupOwned = true;

            // A fresh map-load producer supersedes a stop request from an overlapping
            // previous load. Existing owned scenes still keep the final release gate
            // closed until their teardown callback arrives.
            if (_raidActive)
            {
                _releaseRequested = false;
                return;
            }

            _releaseRequested = false;
            _loadTask = null;
            _loadTaskCaptured = false;
            _loadTaskTerminal = false;
            RegisterOptionalRaidPatches();
            var attempted = new List<ModulePatch>();
            try
            {
                foreach (var patch in Raid)
                {
                    // Record before Enable: ModulePatch can partially install before
                    // throwing, and rollback must still attempt Disable on that object.
                    attempted.Add(patch);
                    try
                    {
                        patch.Enable();
                    }
                    catch (Exception e)
                    {
                        // Optional diagnostics and compatibility shims are allowed to
                        // fail independently; keep the remaining raid fixes active.
                        SafeDisable(patch);
                        Plugin.Log?.LogWarning($"[PatchLifecycle] raid {patch.GetType().FullName} failed: {e.Message}");
                    }
                }
                _raidActive = true;
                Plugin.Log?.LogDebug($"[PatchLifecycle] enabled {attempted.Count} raid patch(es)");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"[PatchLifecycle] raid patch enable failed; rolling back: {e}");
                for (var i = attempted.Count - 1; i >= 0; i--) SafeDisable(attempted[i]);
                _raidActive = false;
                IceGate.PendingLocationId = null;
                RenderEnvProbe.DestroyGlobal();
            }
        }

        // LoadScenesFromPresetOperation is the earliest reliable map signal. It
        // runs before LoadPresetAsync activates a scene, unlike LocalGame.Create,
        // which is a synchronous construction callback after the scenes are up.
        internal static void EnterFromPreset()
        {
            if (_shutdownRequested || _shuttingDown) return;
            IceGate.PendingLocationId = IcebreakerLocation.Key;
            if (_raidActive)
            {
                // A new top-level preset supersedes an overlapping load generation.
                // Do not discard the previous producer; its Task remains in LoadTasks.
                _releaseRequested = false;
                return;
            }
            Enter(IcebreakerLocation.Key);
        }

        internal static void ObserveResolvedPreset()
        {
            if (_shutdownRequested || _shuttingDown) return;
            if (_raidActive)
            {
                _releaseRequested = false;
                return;
            }
            // A direct LoadPresetAsync invocation has no outer config producer to
            // await. Enter the scope, but do not invent an expected outer Task.
            IceGate.PendingLocationId = IcebreakerLocation.Key;
            Enter(IcebreakerLocation.Key);
        }

        internal static void DisableRaid(string reason)
        {
            RequestRelease(reason);
        }

        internal static void NotifyNonIcebreakerPreset()
        {
            if (_raidActive || _loadProducerExpected || HasOwnedScene())
                RequestRelease("non-icebreaker scene preset");
        }

        internal static void Shutdown(string reason)
        {
            if (_shuttingDown) return;
            _shutdownRequested = true;
            RequestRelease(reason);
        }

        internal static void OnSceneLoaded(Scene scene)
        {
            if (_shuttingDown) return;
            if (!IsOwnedScene(scene)) return;
            _cleanupOwned = true;
            if (_releaseRequested) return;
            if (string.Equals(IceGate.PendingLocationId, IcebreakerLocation.Key, StringComparison.OrdinalIgnoreCase)
                && !_raidActive)
                Enter(IceGate.PendingLocationId);
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            if (!IsOwnedScene(scene)) return;
            if (!HasOwnedScene()) RequestRelease("icebreaker scenes unloaded");
        }

        private static bool IsOwnedScene(Scene scene)
            => !string.IsNullOrEmpty(scene.name)
                && scene.name.StartsWith("Icebreaker", StringComparison.OrdinalIgnoreCase);

        private static void Enable(IEnumerable<ModulePatch> patches, string group)
        {
            foreach (var patch in patches)
            {
                try { patch.Enable(); }
                catch (Exception e)
                {
                    SafeDisable(patch);
                    Plugin.Log?.LogError($"[PatchLifecycle] {group} {patch.GetType().FullName} failed: {e}");
                }
            }
        }

        private static void SafeDisable(ModulePatch patch)
        {
            try { patch.Disable(); }
            catch (Exception e) { Plugin.Log?.LogWarning($"[PatchLifecycle] disable {patch.GetType().FullName} failed: {e.Message}"); }
        }

        internal static void NotifyLoadFailure(string reason)
        {
            if (_shuttingDown) return;
            // LocalGame/CoopGame.Create are synchronous construction hooks, not
            // producers for the scene load.  Their failure requests teardown but
            // must not erase an outer preset producer that is still in flight.
            RequestRelease(reason);
        }

        internal static void NotifyOuterLoadFailure(string reason)
        {
            if (_shuttingDown) return;
            if (_outerProducersInFlight > 0) _outerProducersInFlight--;
            _loadProducerExpected = _outerProducersInFlight > 0;
            _loadTaskCaptured = !_loadProducerExpected;
            _loadTaskTerminal = _loadTaskCaptured && AllLoadTasksTerminal();
            RequestRelease(reason);
        }

        private static void RequestRelease(string reason)
        {
            if (_shuttingDown) return;
            var ownsWork = HasReleaseOwnership();
            // Stop/failure callbacks are installed globally, so they also run for
            // ordinary maps. Do not manufacture release state for those callbacks:
            // the lifecycle must already own a raid, producer, task, or scene before
            // a normal release can mutate any bookkeeping.
            if (!_shutdownRequested && !ownsWork) return;
            if (ownsWork) _cleanupOwned = true;
            _releaseRequested = true;
            // A stop can arrive before the async producer's postfix. Keep the patch
            // scope until that producer has either returned or reported failure.
            if (!_loadProducerExpected && !_loadTaskCaptured && _outerProducersInFlight == 0 && LoadTasks.Count == 0)
            {
                _loadTaskCaptured = true;
                _loadTaskTerminal = true;
            }
            TryFinalizeRelease(reason);
        }

        private static void TryFinalizeRelease(string reason)
        {
            if (_shuttingDown || !_releaseRequested) return;
            if (!HasReleaseOwnership())
            {
                if (_shutdownRequested) FinalizeBootstrapShutdown();
                else _releaseRequested = false;
                return;
            }
            if (_outerProducersInFlight > 0 || _loadProducerExpected) return;
            if (!AllLoadTasksTerminal()) return;
            if (HasOwnedScene()) return;

            for (var i = Raid.Count - 1; i >= 0; i--) SafeDisable(Raid[i]);
            _raidActive = false;
            IceGate.PendingLocationId = null;
            IcebreakerRaidResources.Release();
            _releaseRequested = false;
            _loadProducerExpected = false;
            _loadTaskCaptured = false;
            _loadTaskTerminal = false;
            LoadTasks.Clear();
            LoadOperations.Clear();
            _loadTask = null;
            _cleanupOwned = false;
            Plugin.Log?.LogDebug($"[PatchLifecycle] disabled raid patches ({reason})");

            if (_shutdownRequested)
                FinalizeBootstrapShutdown();
        }

        private static bool HasReleaseOwnership()
            => _raidActive || _loadProducerExpected || _outerProducersInFlight != 0
                || LoadTasks.Count != 0 || HasOwnedScene() || _cleanupOwned;

        private static void FinalizeBootstrapShutdown()
        {
            if (_shuttingDown) return;
            for (var i = Bootstrap.Count - 1; i >= 0; i--) SafeDisable(Bootstrap[i]);
            _shuttingDown = true;
            if (_sceneEventsSubscribed)
            {
                _sceneEventsSubscribed = false;
                SceneManager.sceneUnloaded -= OnSceneUnloaded;
            }
            if (_lifetimeHost != null)
            {
                var host = _lifetimeHost;
                _lifetimeHost = null;
                UnityEngine.Object.Destroy(host);
            }
        }

        private static void EnsureLifetimeHost()
        {
            if (_lifetimeHost != null) return;
            try
            {
                _lifetimeHost = new GameObject("ManimalIcebreaker.PatchLifecycle");
                UnityEngine.Object.DontDestroyOnLoad(_lifetimeHost);
                _lifetimeHost.AddComponent<IcebreakerPatchLifetimeHost>();
            }
            catch (Exception e)
            {
                _lifetimeHost = null;
                Plugin.Log?.LogWarning($"[PatchLifecycle] lifetime host unavailable: {e.Message}");
            }
        }

        private static bool HasOwnedScene()
        {
            return ValueEnumerable.Range(0, SceneManager.sceneCount)
                .Select(SceneManager.GetSceneAt)
                .Any(scene => scene.isLoaded && IsOwnedScene(scene));
        }

        private static void AddLoadTask(Task task, object operation)
        {
            if (task == null) return;
            _loadTask = task;
            LoadTasks.Add(task);
            if (operation != null) LoadOperations[task] = operation;
            if (task.IsCanceled || task.IsFaulted)
                _releaseRequested = true;
        }

        private static bool AllLoadTasksTerminal()
        {
            return LoadTasks.AsValueEnumerable().All(task => task == null || task.IsCompleted);
        }

        private static bool OperationFailed(Task task)
        {
            if (!LoadOperations.TryGetValue(task, out var operation) || operation == null)
                return false;
            try
            {
                var type = operation.GetType();
                var cancelled = type.GetProperty("IsCanceled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(operation) as bool?;
                if (cancelled == true) return true;
                var succeed = type.GetProperty("Succeed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(operation) as bool?;
                if (succeed == false)
                {
                    var error = type.GetProperty("Error", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.GetValue(operation);
                    return error != null && !string.IsNullOrEmpty(error.ToString());
                }
            }
            catch { }
            return false;
        }

        private static void RegisterOptionalRaidPatches()
        {
            try { SainLocationCompat.TryPatch(); }
            catch (Exception e) { Plugin.Log?.LogWarning($"[SainCompat] registration failed: {e.Message}"); }
            try { IcebreakerLockableDoorsOff.TryPatch(); }
            catch (Exception e) { Plugin.Log?.LogWarning($"[LockableDoors] registration failed: {e.Message}"); }
            try { IcebreakerQuestingBotsOff.TryPatch(); }
            catch (Exception e) { Plugin.Log?.LogWarning($"[QuestingBotsOff] registration failed: {e.Message}"); }
        }

        private sealed class IcebreakerPatchLifetimeHost : MonoBehaviour
        {
            private void Update() => IcebreakerPatchLifecycle.Tick();
        }
    }
}
