using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // soft Fika detection — no compile-time reference, so the mod builds and runs
    // without Fika installed. the one question this answers: does THIS instance own
    // the bots? in a Fika raid only the host runs bot AI (clients get replicas over
    // the network), so all crew/spawn/hostility logic must be host-only or every
    // client grows its own ghost crew.
    //
    // FikaBackendUtils is resolved by simple type name across the Fika.Core assembly
    // rather than a hardcoded namespace — their internal layout has moved between
    // versions and a rename must degrade loudly, not silently.
    internal static class FikaBridge
    {
        private const string FikaGuid = "com.fika.core";

        private static bool _resolved;
        private static bool _present;
        private static PropertyInfo _isServer;
        private static bool _warnedBroken;

        internal static bool Present { get { Resolve(); return _present; } }

        // true when this instance runs the real bots: solo SPT always, Fika host yes,
        // Fika client no. read PER CALL — IsServer is only meaningful once matchmaking
        // has decided a role, so never cache the value at plugin init.
        internal static bool BotsAuthority
        {
            get
            {
                Resolve();
                if (!_present) return true;
                if (_isServer == null)
                {
                    // fika is loaded but the reflection contract broke (their refactor
                    // or ours). FAIL CLOSED: with an unknown role, claiming authority
                    // means EVERY peer spawns its own crew — chaos for the whole group.
                    // no authority anywhere = an empty ship, which is stable, identical
                    // for everyone, and obviously broken enough to get reported.
                    if (!_warnedBroken)
                    {
                        _warnedBroken = true;
                        Plugin.Log.LogError("[Fika] present but FikaBackendUtils.IsServer not found — NO instance takes bot authority (crew will not spawn). Report this with your fika version.");
                    }
                    return false;
                }
                // fika INSTALLED is not fika IN USE (08-05 field report: a player had
                // fika core only as a dependency of a combat mod and raided solo — the
                // raid ran as plain LocalGame, IsServer stayed false, and the host-gate
                // turned the whole crew off with nobody else to spawn it). a real fika
                // raid runs CoopGame; any other game type under an installed fika is
                // solo semantics, full authority. game null (menus/loading) falls
                // through to the fika flags as before.
                try
                {
                    var game = Singleton<AbstractGame>.Instance;
                    if (game != null && !game.GetType().Name.Contains("Coop")) return true;
                    return (bool)_isServer.GetValue(null);
                }
                catch { return false; }
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(FikaGuid)) return;
                _present = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "Fika.Core") continue;
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == "FikaBackendUtils")
                        {
                            _isServer = t.GetProperty("IsServer", BindingFlags.Public | BindingFlags.Static);
                            _raidCode = t.GetProperty("RaidCode", BindingFlags.Public | BindingFlags.Static);
                            _serverGuid = t.GetProperty("ServerGuid", BindingFlags.Public | BindingFlags.Static);
                        }
                        else if (t.Name == "CoopHandler")
                        {
                            _tryGetCoop = t.GetMethod("TryGetCoopHandler", BindingFlags.Public | BindingFlags.Static);
                            _humanList = t.GetProperty("HumanPlayers", BindingFlags.Public | BindingFlags.Instance);
                        }
                    }
                    break;
                }
                Plugin.Log.LogWarning($"[Fika] detected (IsServer {(_isServer != null ? "ok" : "MISSING")}, CoopHandler {(_tryGetCoop != null && _humanList != null ? "ok" : "MISSING")}) — bot systems will be host-gated");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Fika] detection failed (assuming solo): {e.Message}");
            }
        }

        // ---- shared per-raid RNG (deterministic sync without packets) ----
        // the host generates RaidCode + ServerGuid at match creation and every client
        // receives both from the fika backend BEFORE the raid starts (verified in
        // Fika-Plugin source: FikaBackendUtils setters on the host path and in the join
        // result / FikaPingingClient) — so a Random seeded off them produces IDENTICAL
        // draw sequences on every peer with zero networking. per-raid unique (host rolls
        // a fresh code each raid, incl. transits re-creating the match). the salt keeps
        // independent systems from correlating their draws off the same sequence.
        private static PropertyInfo _raidCode;
        private static PropertyInfo _serverGuid;

        internal static System.Random SharedRaidRng(string salt)
        {
            Resolve();
            if (!_present) return null; // solo: caller keeps its local random
            try
            {
                var code = _raidCode?.GetValue(null) as string;
                var guid = _serverGuid?.GetValue(null)?.ToString();
                if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(guid))
                {
                    Plugin.Log.LogWarning("[Fika] RaidCode/ServerGuid unavailable — shared-seed sync degraded to local random (peers may desync). Report with your fika version.");
                    return null;
                }
                // fnv-1a over the combined string — string.GetHashCode is not guaranteed
                // stable across runtimes, and every peer MUST hash to the same seed
                unchecked
                {
                    uint h = 2166136261;
                    foreach (char c in $"{code}|{guid}|{salt}")
                        h = (h ^ c) * 16777619;
                    return new System.Random((int)h);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Fika] shared-seed derivation failed: {e.Message}");
                return null;
            }
        }

        // ---- human roster (any-player triggers) ----
        // solo: the roster is just you. fika: CoopHandler.HumanPlayers — includes the
        // observed replicas of remote teammates, whose Position is synced, which is
        // exactly what map triggers need. handler is a MonoBehaviour that dies at raid
        // end, so the cache re-resolves whenever unity says it's gone.
        private static MethodInfo _tryGetCoop;
        private static PropertyInfo _humanList;
        private static object _handler;
        private static readonly List<Player> Scratch = new List<Player>();

        private static System.Collections.IEnumerable HumanListOrNull()
        {
            Resolve();
            if (!_present || _tryGetCoop == null || _humanList == null) return null;
            if (_handler is UnityEngine.Object u && !u) _handler = null; // destroyed last raid
            if (_handler == null)
            {
                var args = new object[] { null };
                try { _handler = (bool)_tryGetCoop.Invoke(null, args) ? args[0] : null; }
                catch { _handler = null; }
            }
            if (_handler == null) return null;
            try { return _humanList.GetValue(_handler) as System.Collections.IEnumerable; }
            catch { return null; }
        }

        // a headless host's idle player sits in CoopHandler.HumanPlayers like any human
        // — fika's own code tells them apart by this nickname prefix (CoopGame inserts
        // it at headless raid create). counting it would inflate GroupSize brackets and
        // feed a phantom body to the trigger/spawn logic.
        private static bool IsHeadlessBody(Player p)
        {
            try { return p.Profile?.Info?.Nickname != null && p.Profile.Info.Nickname.StartsWith("headless_"); }
            catch { return false; }
        }

        // fills `into` with every alive human; falls back to MainPlayer whenever the
        // fika roster isn't available (solo, pre-raid, reflection drift)
        internal static void CollectHumans(List<Player> into)
        {
            into.Clear();
            var list = HumanListOrNull();
            if (list != null)
                foreach (var o in list)
                    if (o is Player p && p != null && p.HealthController != null && p.HealthController.IsAlive
                        && !IsHeadlessBody(p))
                        into.Add(p);
            if (into.Count == 0)
            {
                var main = Singleton<GameWorld>.Instance?.MainPlayer;
                if (main != null) into.Add(main);
            }
        }

        internal static bool AnyHumanIn(Bounds b)
        {
            CollectHumans(Scratch);
            for (int i = 0; i < Scratch.Count; i++)
                if (b.Contains(Scratch[i].Position)) return true;
            return false;
        }

        internal static float NearestHumanSqr(Vector3 pos)
        {
            CollectHumans(Scratch);
            float best = float.MaxValue;
            for (int i = 0; i < Scratch.Count; i++)
            {
                float d = (Scratch[i].Position - pos).sqrMagnitude;
                if (d < best) best = d;
            }
            return best;
        }

        // 'a human is standing in this trigger' — the bot-exclusion the IsYourPlayer
        // filters were really after (bots trip boxes during their spawn frame), minus
        // the coop blindness (a remote teammate is NOT IsYourPlayer on the host)
        internal static bool IsHumanPlayer(Player p)
        {
            if (p == null) return false;
            if (IsHeadlessBody(p)) return false; // incl. the headless's own IsYourPlayer body
            if (p.IsYourPlayer) return true;
            var list = HumanListOrNull();
            if (list != null)
                foreach (var o in list)
                    if (ReferenceEquals(o, p)) return true;
            return false;
        }
    }
}
