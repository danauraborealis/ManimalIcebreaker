using System;
using System.Collections;
using BepInEx.Bootstrap;
using Comfort.Common;
using EFT;
using UnityEngine;
using ZLinq;

namespace Manimal.Icebreaker
{
    // The server makes the one per-raid Combine selection when it builds the
    // BossLocationSpawn table.  The client must observe that table rather than roll
    // again: in Fika only the host has BossSpawnScenario and clients receive bot
    // replicas without a local wave table.
    internal static class IcebreakerCombineEncounter
    {
        internal const int CombineSoldier = 1700420001;
        internal const int CombineShotgunner = 1700420002;
        internal const int CombineElite = 1700420003;
        private const string CombinePluginGuid = "com.manimal.combinesoldiers";

        private const float ReadyTimeoutSeconds = 30f;
        private static bool _ready;
        private static bool _active;
        private static bool _selectionAuthoritative;
        private static bool _reset;
        private static int _raidToken;

        // Root/visuals use the reset edge to discard render-only state before the
        // authoritative wave selection arrives.  SelectionReady is intentionally a
        // separate edge: false means the authoritative result was "Black Division";
        // it does not mean that a new raid has started.
        internal static event Action RaidReset;
        internal static event Action<bool> SelectionReady;

        // Keep this a property: the optional Combine hostility bridge discovers it by
        // reflection so the Icebreaker client never acquires a hard Combine reference.
        internal static bool Active => _active && IceGate.On && CombinePluginInstalled;
        internal static bool Ready => _ready;
        internal static bool RaidInitialized => _reset;
        internal static bool SelectionAuthoritative => _selectionAuthoritative;
        internal static int RaidToken => _raidToken;

        private static bool CombinePluginInstalled
        {
            get
            {
                try { return Chainloader.PluginInfos.ContainsKey(CombinePluginGuid); }
                catch { return false; }
            }
        }

        internal static bool IsCrewRole(int role)
            => role == IcebreakerCrew.BdIb
                || (Active && (role == CombineSoldier || role == CombineShotgunner));

        // Before the host's selection is authoritative, a Combine soldier or
        // shotgunner may still become a crew bot on this raid. Callers must defer
        // negative caching for those roles until the selection edge resolves.
        internal static bool IsPotentialCrewRole(int role)
            => role == CombineSoldier || role == CombineShotgunner;

        internal static bool IsWedgeRole(int role)
            => role == IcebreakerCrew.BdWedge
                || (Active && role == CombineElite);

        internal static void ResetForRaid()
        {
            _ready = false;
            _active = false;
            _selectionAuthoritative = false;
            _reset = true;
            _raidToken = NextRaidToken();
            try { RaidReset?.Invoke(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[Combine] raid reset hook failed: {e.Message}"); }
        }

        // The first converted T4 can be dispatched before the Crew coroutine gets
        // its first frame. Inspect the host's already-initialized wave table at that
        // point so the spawn prefix and hostility bridge see the same authoritative
        // result without waiting on a Unity coroutine.
        internal static bool TryEnsureHostSelection()
        {
            if (!FikaBridge.BotsAuthority || !IceGate.On || !CombinePluginInstalled)
                return false;
            // Crew.Start is the per-raid reset edge. Do not create an early
            // selection that a just-created Crew component would immediately clear.
            if (!_reset)
                return false;
            if (_selectionAuthoritative)
                return Active;

            try
            {
                var waves = Singleton<IBotGame>.Instance?.BossSpawnScenario?.BossSpawnWaves;
                if (waves == null) return false;
                Publish(ContainsCombineWave(waves), "authoritative BossSpawnScenario (spawn prefix)", authoritative: true);
                return Active;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Combine] authoritative wave inspection from spawn prefix failed: {e.Message}");
                return false;
            }
        }

        // Called from the crew component at raid start.  The coroutine is bounded so
        // a Fika client waiting for a host reply cannot hold cutscene/gameplay setup
        // forever.  A later packet may still replace the timed-out false result.
        internal static IEnumerator WaitUntilReady(float timeoutSeconds = ReadyTimeoutSeconds)
        {
            if (!_reset) ResetForRaid();
            if (_ready) yield break;

            float deadline = Time.time + Mathf.Max(1f, timeoutSeconds);
            while (!_ready && Time.time < deadline)
            {
                if (!IceGate.On)
                {
                    yield return null;
                    continue;
                }

                if (!CombinePluginInstalled)
                {
                    Publish(false, "CombineSoldiers plugin is not loaded", authoritative: FikaBridge.BotsAuthority);
                    yield break;
                }

                // Fika clients deliberately have no BossSpawnScenario. Their only
                // source of truth is IceSyncHandler's host reply.
                if (FikaBridge.BotsAuthority)
                {
                    var botGame = Singleton<IBotGame>.Instance;
                    var scenario = botGame?.BossSpawnScenario;
                    var waves = scenario?.BossSpawnWaves;
                    if (waves != null)
                    {
                        Publish(ContainsCombineWave(waves), "authoritative BossSpawnScenario", authoritative: true);
                        yield break;
                    }
                }

                yield return new WaitForSeconds(0.25f);
            }

            if (!_ready)
            {
                Plugin.Log.LogWarning(FikaBridge.BotsAuthority
                    ? "[Combine] BossSpawnScenario was not ready before timeout; continuing with Combine selection inactive"
                    : "[Combine] host selection reply was not received before timeout; continuing gameplay and waiting for late state");
                // A solo/host timeout is still the authority's deterministic
                // no-selection answer, so a Fika client may answer late-join requests
                // with it. A client timeout remains a local fall-through only; it must
                // keep requesting the host state and must not advertise a guess.
                Publish(false, "selection timeout", authoritative: FikaBridge.BotsAuthority);
            }
        }

        // Fika invokes this only for a packet received from the host.  Rejecting the
        // method on an authority instance prevents a client-originated packet from
        // changing the host's selection (and keeps solo/local guesses out entirely).
        internal static void ApplyRemoteSelection(bool active, int token)
        {
            if (FikaBridge.BotsAuthority)
            {
                Plugin.Log.LogWarning("[Combine] ignored remote selection on the bot authority");
                return;
            }
            if (!IceGate.On) return;
            if (!_reset) ResetForRaid();
            if (token != 0 && _raidToken != 0 && token != _raidToken)
            {
                Plugin.Log.LogWarning($"[Combine] ignored stale Fika selection token {token} (current {_raidToken})");
                return;
            }
            Publish(active && CombinePluginInstalled, "Fika host selection", authoritative: true);
        }

        private static void Publish(bool active, string source, bool authoritative)
        {
            if (_ready && _active == active)
            {
                _selectionAuthoritative |= authoritative;
                return;
            }
            _active = active;
            _ready = true;
            _selectionAuthoritative = authoritative;
            Plugin.Log.LogInfo($"[Combine] selection ready: active={active} source={source} token={_raidToken}");
            try { SelectionReady?.Invoke(active); }
            catch (Exception e) { Plugin.Log.LogWarning($"[Combine] selection hook failed: {e.Message}"); }
        }

        private static bool ContainsCombineWave(BossLocationSpawn[] waves)
        {
            if (waves == null) return false;
            // The encounter is one atomic server rewrite. A random Combine row
            // supplied by another mod must not turn on Icebreaker cutscenes or AI
            // roles, so require both signatures: a Combine Elite as the boss on one
            // of our wedges* triggers and the converted T4 soldier row with its
            // exact 2+2 Supports contract.
            bool hasEliteWedge = waves.AsValueEnumerable().Any(IsConvertedEliteWedge);
            bool hasCombineT4 = waves.AsValueEnumerable().Any(IsConvertedT4);
            return hasEliteWedge && hasCombineT4;
        }

        private static bool IsConvertedEliteWedge(BossLocationSpawn wave)
            => wave != null
                && wave.TriggerId != null
                && wave.TriggerId.StartsWith("wedges", StringComparison.OrdinalIgnoreCase)
                && (int)wave.BossType == CombineElite;

        private static bool IsConvertedT4(BossLocationSpawn wave)
        {
            if (wave == null || !string.Equals(wave.TriggerId, "T4", StringComparison.OrdinalIgnoreCase)
                || (int)wave.BossType != CombineSoldier
                || !string.Equals(wave.BossEscortAmount, "0", StringComparison.OrdinalIgnoreCase)
                || wave.Supports == null)
                return false;

            var supports = wave.Supports.AsValueEnumerable();
            return supports.Count(s => s != null) == 2
                && supports.Count(s => s != null
                    && (int)s.BossEscortType == CombineSoldier && s.BossEscortAmount == 2) == 1
                && supports.Count(s => s != null
                    && (int)s.BossEscortType == CombineShotgunner && s.BossEscortAmount == 2) == 1;
        }

        private static int NextRaidToken()
        {
            // Fika exposes a shared per-raid seed through FikaBridge. When that
            // contract is unavailable, zero is a deliberate degraded-mode sentinel:
            // peers accept the host packet without comparing unrelated local tokens.
            try
            {
                var rng = FikaBridge.SharedRaidRng("icebreaker-combine-selection");
                if (rng != null) return rng.Next(1, int.MaxValue);
            }
            catch { }
            return 0;
        }
    }
}
