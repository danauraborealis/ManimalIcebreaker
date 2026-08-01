using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // STUCK-BOT WATCHDOG — turn them to face their target, nothing else.
    //
    // what actually happens, after a day of instrumenting it:
    //
    //   decision=runToCover  at=(-11.8,18.4,32.0)  dest=(-11.9,18.4,32.0)  distDest=0.1
    //   goal=THIS:'testdev' @ 1.1m   lookAngle=171deg   VisLvl=0.000   moving=False
    //
    // the bot is not asleep, paused, deaf, pathless or stranded. it knows exactly where you
    // are, decides to take cover, and the cover search hands back the point it is already
    // standing on — so BotMover clears IsMoving because CheckShouldMove() says it arrived,
    // which it did, 10cm away. then it holds that spot facing its cover orientation (a wall)
    // and never re-evaluates, because it cannot SEE you to trigger one.
    //
    // WHY EVERY OTHER FIX WAS DELETED (all tried, all measured, all failed):
    //   forced GoToPoint   38 pushes to valid 14m targets, accepted and PathComplete, all
    //                      ignored — we issue one order per 12s against a decision loop
    //                      running many times a second, so the cover choice re-asserts
    //                      before the bot takes a step.
    //   drop the enemy     bot re-acquired and re-parked on the same tile inside 12s.
    //   teleport           worked, but a bot blinking out a metre from your face is worse
    //                      than a bot standing still.
    //
    // so stop commanding and fix the INPUT. lookAngle ~170deg is the whole problem: it faces
    // a wall, the LookSensor never casts toward us, vision never resolves, the decision
    // never changes. turn it and the brain frees itself — measured, decisions that follow
    // real visual contact MOVE: dogFight 100%, attackMovingFlank 100%.
    //
    // deliberately NOT EnemyInfo.SetVisible(true): that would hand the bot sight straight
    // through the crate it is hiding behind. we only turn it; the normal raycast then
    // resolves honestly, and if the crate really does occlude us the bot stays blind, which
    // is the correct outcome rather than a cheat.
    //
    // nothing we restore is at fault: navmesh, patrol points, cover graph, wall directions,
    // GoToPoint, the AI pump and the 49-step activation all measured clean. this is a
    // selection behaviour on a cramped, prop-dense map.
    internal class IcebreakerStuckBots : MonoBehaviour
    {
        private const float StillEpsilon = 0.35f;   // drift still counted as "parked"
        // split threshold: a bot holding an enemy and standing still is obviously wrong and
        // gets kicked fast, while a peaceful patroller may legitimately pause to look around
        // or work a door, so it gets more rope before we interfere
        private const float EngagedSeconds = 5f;    // has a GoalEnemy
        private const float PeacefulSeconds = 7f;   // no enemy — just patrolling
        private const float CheckEvery = 0.25f;     // re-aim often — a one-shot aim is overwritten
        private const float EyeHeight = 1.2f;       // chest, not feet, or the look lands in the floor

        private sealed class Watch
        {
            internal Vector3 Anchor;
            internal float Since;
            internal bool Announced;
        }

        private readonly Dictionary<int, Watch> _watch = new Dictionary<int, Watch>();
        private float _next;

        private void Update()
        {
            if (!IceGate.On || Time.time < _next) return;
            _next = Time.time + CheckEvery;

            var bots = Singleton<GameWorld>.Instance?.AllAlivePlayersList;
            if (bots == null) return;

            foreach (var p in bots)
            {
                if (p == null || !p.IsAI || !p.HealthController.IsAlive) continue;
                var owner = p.AIData?.BotOwner;
                if (owner == null || owner.BotState != EBotState.Active) continue;
                try { Tick(owner); }
                catch { }
            }
        }

        private void Tick(BotOwner owner)
        {
            var mover = owner.Mover;
            if (mover == null) return;
            int id = owner.Id;

            // only bots that HOLD AN ENEMY and are standing still. a bot with nothing to
            // fight, standing still, is just idling — that is not this bug
            var goal = owner.Memory?.GoalEnemy;
            bool parked = !mover.IsMoving && !mover.Pause;
            // NOTE the gate used to require goal != null, which quietly excluded the MAJORITY
            // of stuck bots: 1195 of 1623 samples were on PatrolAssault with no enemy at all.
            // two usecs in a hallway who have not noticed you yet have no GoalEnemy, so the
            // watchdog never looked at them — which is why this "worked" and still left bots
            // staring at walls. both cases are handled now; only the remedy differs.
            if (!parked)
            {
                // report the WIN here, not in the drift check below: a bot that starts moving
                // trips !parked and exits on this line, so the drift branch never sees it and
                // the release message was unreachable in the one case it existed for
                Watch prev;
                if (_watch.TryGetValue(id, out prev) && prev.Announced)
                    Plugin.Log.LogDebug($"[Stuck] '{owner.name}' RELEASED — moving again");
                _watch.Remove(id);
                return;
            }

            Watch w;
            if (!_watch.TryGetValue(id, out w))
            {
                _watch[id] = new Watch { Anchor = owner.Position, Since = Time.time };
                return;
            }
            // any real movement resets the clock — we only care about a bot that has not
            // meaningfully left the tile it is standing on
            if ((owner.Position - w.Anchor).sqrMagnitude > StillEpsilon * StillEpsilon)
            {
                if (w.Announced)
                    Plugin.Log.LogDebug($"[Stuck] '{owner.name}' moved again — released");
                w.Anchor = owner.Position; w.Since = Time.time; w.Announced = false;
                return;
            }
            float threshold = goal == null ? PeacefulSeconds : EngagedSeconds;
            if (Time.time - w.Since < threshold) return;

            // --- no enemy: it is a patrol bot with nowhere to go (distDest 0 in 154 of 181
            // simplePatrol samples). do NOT shove the mover — that lost every time. use
            // BSG's own patrol chooser to hand it a FRESH point, which is a destination the
            // brain already considers legitimate and will not immediately overrule.
            if (goal == null)
            {
                var pd = owner.PatrollingData;
                if (pd == null) return;
                try
                {
                    pd.Unpause();
                    pd.FindNextPoint(true, false);
                    pd.GoToPoint();
                    if (!w.Announced)
                    {
                        w.Announced = true;
                        Plugin.Log.LogDebug($"[Stuck] '{owner.name}' parked {PeacefulSeconds:F0}s at "
                            + $"({owner.Position.x:F1},{owner.Position.y:F1},{owner.Position.z:F1}) with no enemy — "
                            + "sent to a fresh patrol point");
                    }
                }
                catch (Exception e)
                {
                    if (!w.Announced) Plugin.Log.LogWarning($"[Stuck] '{owner.name}' patrol re-kick failed: {e.Message}");
                    w.Announced = true;
                }
                // re-kick on a slower cadence than the look — a new point needs time to walk
                w.Since = Time.time;
                return;
            }

            // --- has an enemy: FACE IT, and keep re-applying, because the bot's own look
            // logic retargets constantly and a single aim is gone by the next frame
            var target = goal.Person;
            if (target == null) return;
            Vector3 eye;
            try { eye = target.Position + Vector3.up * EyeHeight; }
            catch { return; }

            try { owner.Steering.LookToPoint(eye); }
            catch (Exception e)
            {
                if (!w.Announced) Plugin.Log.LogWarning($"[Stuck] '{owner.name}' look failed: {e.Message}");
                w.Announced = true;
                return;
            }

            if (!w.Announced)
            {
                w.Announced = true;
                float d = Vector3.Distance(owner.Position, target.Position);
                Plugin.Log.LogDebug($"[Stuck] '{owner.name}' parked {EngagedSeconds:F0}s at "
                    + $"({owner.Position.x:F1},{owner.Position.y:F1},{owner.Position.z:F1}) — now facing "
                    + $"'{target.Profile?.Info?.Nickname}' at {d:F1}m until it moves or loses the target");
            }
        }
    }
}
