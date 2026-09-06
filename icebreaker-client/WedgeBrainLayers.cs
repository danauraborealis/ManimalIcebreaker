using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace Manimal.Icebreaker
{
    // The recovered retail 1.0 assembly is incomplete (docs/BOSSWEDGE-AI-REPORT.md).
    // Our rooms/ambush/escort-hold approximations could own an unseen-enemy fight
    // indefinitely without a valid cover path or search action. Leave movement,
    // perception and combat to Black Division's brain; retain only close taunts.
    internal static class WedgeBrains
    {
        internal const int RoleWedge = IcebreakerCrew.BdWedge;

        internal static void Register()
        {
            Plugin.Log.LogInfo("[WedgeBrain] native combat enabled; unfinished custom combat layers removed; close taunts retained");
        }
    }

    internal enum EWedgeBand { Close, Mid, Far }

    internal static class WedgeBands
    {
        internal static EWedgeBand Current = EWedgeBand.Far;

        // one path object reused forever — CalculatePath clears it, and this runs on a
        // 3s cadence on the main thread only (the stutter hunt made per-frame garbage
        // a firing offense)
        private static NavMeshPath _path;

        internal static void ResetForRaid() => Current = EWedgeBand.Far;

        internal static EWedgeBand Compute(Vector3 from, Vector3 to)
        {
            try
            {
                if (_path == null) _path = new NavMeshPath();
                if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, _path)
                    || _path.status != NavMeshPathStatus.PathComplete)
                    return Current = EWedgeBand.Far;
                var c = _path.corners; // one array alloc per compute, every 3s — fine
                float d = 0f;
                for (int i = 1; i < c.Length; i++) d += Vector3.Distance(c[i - 1], c[i]);
                return Current = d <= 12f ? EWedgeBand.Close : d <= 27f ? EWedgeBand.Mid : EWedgeBand.Far;
            }
            catch { return Current = EWedgeBand.Far; }
        }
    }

    // Voice-only watcher: never changes movement, steering or enemy knowledge.
    internal class WedgeVoice : MonoBehaviour
    {
        private const float TickEvery = 0.5f;
        private const float FindEvery = 3f;
        private const float BandEvery = 3f;          // retail's CheckDistPeriod cadence

        private BotOwner _boss;
        private float _nextTick, _nextFind, _nextTaunt, _nextBand;

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_Attach
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!IceGate.On || !FikaBridge.BotsAuthority) return;
                WedgeBands.ResetForRaid();
                if (UnityEngine.Object.FindObjectOfType<WedgeVoice>() == null)
                    new GameObject("Icebreaker_WedgeVoice").AddComponent<WedgeVoice>();
            }
        }

        private static readonly System.Diagnostics.Stopwatch _updSw = new System.Diagnostics.Stopwatch();

        private void Update()
        {
            if (!IceGate.On || !FikaBridge.BotsAuthority || Time.time < _nextTick) return;
            _nextTick = Time.time + TickEvery;
            _updSw.Restart();
            try
            {
                if (!AliveBoss() && Time.time >= _nextFind)
                {
                    _nextFind = Time.time + FindEvery;
                    FindBoss();
                }
                if (!AliveBoss()) return;

                // taunt his CURRENT enemy — GoalEnemy means he knows about you, so this
                // never leaks information the bot doesn't have
                var ge = _boss.Memory?.GoalEnemy;
                if (ge == null) { WedgeBands.Current = EWedgeBand.Far; return; }

                // retail bands: 12/27m of WALKING distance, re-checked every 3s. an
                // enemy one bulkhead away is Far, so no more taunting through walls
                // (the round-1 euclidean stand-in did)
                if (Time.time >= _nextBand)
                {
                    _nextBand = Time.time + BandEvery;
                    WedgeBands.Compute(_boss.Position, ge.CurrPosition);
                }
                if (WedgeBands.Current != EWedgeBand.Close) return;
                if (Time.time < _nextTaunt) return;

                _boss.BotTalk?.TrySay(EPhraseTrigger.Provocation);
                _nextTaunt = Time.time + UnityEngine.Random.Range(15f, 30f);
                // taunts were inaudible-or-not-firing ambiguous in the 08-15 field log —
                // one line settles which
                Plugin.Log.LogDebug("[WedgeBrain] taunt fired (close band)");
            }
            catch { }
            finally { RenderEnvProbe.AddTick(RenderEnvProbe.TickWedgeVoice, _updSw.Elapsed.TotalMilliseconds); }
        }

        private bool AliveBoss()
        {
            var p = _boss?.GetPlayer;
            return p != null && p.HealthController != null && p.HealthController.IsAlive;
        }

        private void FindBoss()
        {
            _boss = null;
            var world = Singleton<GameWorld>.Instance;
            if (world?.AllAlivePlayersList == null) return;
            foreach (var pl in world.AllAlivePlayersList)
            {
                try
                {
                    if (pl == null || !pl.AIData.IsAI) continue;
                    if ((int)pl.Profile.Info.Settings.Role != WedgeBrains.RoleWedge) continue;
                    _boss = pl.AIData.BotOwner;
                    if (_boss != null)
                    {
                        Plugin.Log.LogDebug("[WedgeBrain] boss located — taunts armed");
                        return;
                    }
                }
                catch { }
            }
        }
    }
}
