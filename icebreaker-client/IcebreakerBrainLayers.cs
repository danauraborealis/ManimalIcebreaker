using System;
using System.Collections.Generic;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace Manimal.Icebreaker
{
    // BIGBRAIN CREW LAYER — replaces the PatrollingData hold/push machinery.
    //
    // why a layer: SAIN removes the vanilla patrol layers wholesale, so every
    // PatrollingData poke (the old engine-room hold, FindNextPoint pushes) targeted a
    // layer that no longer exists — with SAIN installed the black division just sat in
    // their spawn rooms, both the initial patrol groups and the trigger squads. a
    // bigbrain layer competes where SAIN competes: priority 19 beats the vanilla patrol
    // layers, loses to SAIN combat (solo 20 / squad 22 / avoid-threat 80), and IsActive
    // self-gates to enemy-less idle time — so ANY combat system, vanilla or SAIN, takes
    // over the moment there's a fight, and we only ever drive the "what do I do when
    // nothing is happening" slot that SAIN leaves empty.
    //
    // brains: rogues run ExUsec; the faction mods (BlackDiv/UNTAR/RUAF) run the PMC
    // brains (PmcBear/PmcUsec — confirmed in ORBIT's source, which special-cases them
    // for exactly that reason). registered once at plugin load; IceGate keeps it inert
    // off-map.
    internal static class IceCrewJobs
    {
        internal enum Job { None, Guard, Hunt, Hold }

        internal sealed class Rec
        {
            public Job Job;
            public Bounds Zone;     // Guard only: roam box
            public float RushUntil; // while Time.time < this, the RUSH layer owns the bot
        }

        private static readonly Dictionary<string, Rec> ByProfile = new Dictionary<string, Rec>();

        internal static void Register()
        {
            var brains = new List<string> { "ExUsec", "PmcBear", "PmcUsec" };
            BrainManager.AddCustomLayer(typeof(IceCrewLayer), brains, 19);
            // the RUSH tier (user call 08-03): on the engine trigger the held squad must
            // DEPLOY, not win a priority fight — SAIN's combat layers (solo 20/squad 22/
            // extract 24) go active the moment it hears the player and its decision is
            // to hold, which read as "they sat in spawn until I pushed them". 30 outranks
            // all of those (only SAIN's grenade-dodge at 80 and debug at 99 sit higher,
            // both fine to lose to); the layer self-limits to the RushUntil window so
            // normal combat AI gets the bot back the moment the deployment is done.
            BrainManager.AddCustomLayer(typeof(IceRushLayer), brains, 30);
            Plugin.Log.LogInfo("[CrewLayer] bigbrain layers registered (ExUsec + PMC brains, crew 19 / rush 30)");
        }

        internal static void Assign(BotOwner bot, Job job, Bounds zone = default, float rushSeconds = 0f)
        {
            var id = bot?.ProfileId;
            if (id == null) return;
            ByProfile[id] = new Rec { Job = job, Zone = zone, RushUntil = rushSeconds > 0f ? Time.time + rushSeconds : 0f };
            try
            {
                Plugin.Log.LogDebug($"[CrewLayer] {bot.name}: job={job} brain='{bot.Brain?.BaseBrain?.ShortName()}'"
                    + (rushSeconds > 0f ? $" RUSH {rushSeconds:F0}s" : "")
                    + (job == Job.Guard ? $" zone c={zone.center} s={zone.size}" : ""));
            }
            catch { }
        }

        // black division bots that nobody assigned explicitly default to guarding the
        // area they spawned in — that's the "initial group patrols the room" behavior.
        // lazy, on the layer's first query, so it needs no activation hook and covers
        // every spawn path (waves, pen deliveries, force spawns) automatically.
        internal static Rec For(BotOwner bot)
        {
            var id = bot?.ProfileId;
            if (id == null) return null;
            if (ByProfile.TryGetValue(id, out var rec)) return rec;
            if (bot.Profile?.Info?.Settings?.Role == (WildSpawnType)IcebreakerCrew.BdIb)
            {
                var zone = new Bounds(bot.Position, new Vector3(24f, 8f, 24f));
                Assign(bot, Job.Guard, zone);
                return ByProfile[id];
            }
            ByProfile[id] = null; // cached negative — not a crew-managed bot
            return null;
        }

        internal static void Reset() => ByProfile.Clear(); // per raid
    }

    internal class IceCrewLayer : CustomLayer
    {
        public IceCrewLayer(BotOwner botOwner, int priority) : base(botOwner, priority) { }

        public override string GetName() => "IceCrew";

        public override bool IsActive()
        {
            if (!IceGate.On) return false;
            var rec = IceCrewJobs.For(BotOwner);
            if (rec == null || rec.Job == IceCrewJobs.Job.None) return false;
            var p = BotOwner.GetPlayer;
            if (p == null || p.HealthController == null || !p.HealthController.IsAlive) return false;
            // any live threat = stand down instantly; combat layers own the bot. cheap
            // checks only — this runs every brain tick for every covered bot.
            try
            {
                if (BotOwner.Memory != null && (BotOwner.Memory.GoalEnemy != null || BotOwner.Memory.IsUnderFire))
                    return false;
            }
            catch { return false; }
            return true;
        }

        public override Action GetNextAction()
        {
            switch (IceCrewJobs.For(BotOwner)?.Job)
            {
                case IceCrewJobs.Job.Hunt: return new Action(typeof(IceHuntLogic), "hunt the players");
                case IceCrewJobs.Job.Hold: return new Action(typeof(IceHoldLogic), "hold the ambush");
                default: return new Action(typeof(IceGuardLogic), "guard the zone");
            }
        }

        public override bool IsCurrentActionEnding()
        {
            Type want;
            switch (IceCrewJobs.For(BotOwner)?.Job)
            {
                case IceCrewJobs.Job.Hunt: want = typeof(IceHuntLogic); break;
                case IceCrewJobs.Job.Hold: want = typeof(IceHoldLogic); break;
                default: want = typeof(IceGuardLogic); break;
            }
            return CurrentAction == null || CurrentAction.Type != want;
        }
    }

    // RUSH — the deployment override. same jobs, same logics, but priority 30 and NO
    // enemy/underfire gate: while the rush window is open the bot moves, full stop.
    // this exists because a deploy order that yields to combat state never executes —
    // SAIN flips to combat off the trigger noise and holds them in the spawn room.
    internal class IceRushLayer : CustomLayer
    {
        public IceRushLayer(BotOwner botOwner, int priority) : base(botOwner, priority) { }

        public override string GetName() => "IceCrewRush";

        public override bool IsActive()
        {
            if (!IceGate.On) return false;
            var rec = IceCrewJobs.For(BotOwner);
            if (rec == null || rec.Job == IceCrewJobs.Job.None || Time.time >= rec.RushUntil) return false;
            var p = BotOwner.GetPlayer;
            return p != null && p.HealthController != null && p.HealthController.IsAlive;
        }

        public override Action GetNextAction()
        {
            var rec = IceCrewJobs.For(BotOwner);
            return rec != null && rec.Job == IceCrewJobs.Job.Guard
                ? new Action(typeof(IceGuardLogic), "rush to the patrol box")
                : new Action(typeof(IceHuntLogic), "rush the players");
        }

        public override bool IsCurrentActionEnding()
        {
            var rec = IceCrewJobs.For(BotOwner);
            var want = rec != null && rec.Job == IceCrewJobs.Job.Guard ? typeof(IceGuardLogic) : typeof(IceHuntLogic);
            return CurrentAction == null || CurrentAction.Type != want;
        }
    }

    // AMBUSH hold — the engine-hide squad. the layer being active is what keeps them
    // hidden: vanilla patrol cant wander them off and SAIN cant reposition them, but
    // unlike the old PatrollingData.Pause this survives SAIN removing the patrol
    // layers entirely. crouched at their hide marker, creeping back if a fight (which
    // deactivates the layer and hands them to combat) displaced them.
    internal class IceHoldLogic : CustomLogic
    {
        private Vector3 _post;
        private bool _anchored;
        private float _next;

        public IceHoldLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            base.Start();
            // the hide marker = wherever the spawner put them; first activation anchors it
            if (!_anchored) { _post = BotOwner.Position; _anchored = true; }
        }

        public override void Update(CustomLayer.ActionData data)
        {
            if (Time.time < _next) return;
            _next = Time.time + 2f;
            if ((BotOwner.Position - _post).sqrMagnitude > 2f * 2f)
            {
                // displaced (post-fight drift) — slip back to the post, low and quiet
                BotOwner.Mover?.SetTargetMoveSpeed(0.4f);
                BotOwner.Mover?.SetPose(0.4f);
                BotOwner.Mover?.GoToPoint(_post, true, 0.5f);
            }
            else
            {
                BotOwner.Mover?.SetPose(0.25f); // crouched behind the machinery — the ambush read
            }
        }

        public override void Stop()
        {
            base.Stop();
            BotOwner.Mover?.SetPose(1f); // stand up for whatever comes next (combat/hunt)
            _next = 0f;
        }
    }

    // roam the assigned box at a watchful walk: random reachable point every 6-12s.
    // the movement itself gives SAIN/vanilla vision something to work with — a moving
    // bot acquires targets; a parked one waits to be shot first.
    internal class IceGuardLogic : CustomLogic
    {
        private float _next;

        public IceGuardLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Update(CustomLayer.ActionData data)
        {
            if (Time.time < _next) return;
            _next = Time.time + UnityEngine.Random.Range(6f, 12f);
            var rec = IceCrewJobs.For(BotOwner);
            if (rec == null) return;
            var z = rec.Zone;
            var want = new Vector3(
                UnityEngine.Random.Range(z.min.x, z.max.x),
                z.center.y,
                UnityEngine.Random.Range(z.min.z, z.max.z));
            if (!NavMesh.SamplePosition(want, out var hit, 4f, NavMesh.AllAreas)) return;
            BotOwner.Mover?.SetTargetMoveSpeed(0.5f);
            BotOwner.Mover?.SetPose(1f);
            BotOwner.Mover?.GoToPoint(hit.position, true, 0.6f);
        }

        public override void Stop()
        {
            base.Stop();
            _next = 0f; // re-goal immediately on the next activation
        }
    }

    // push toward the nearest living human: repath every few seconds, sprint when far,
    // slow to a hunting walk close-in so vision/hearing can acquire before contact.
    // reach distance stops them 4m short — the goal is contact, not a hug.
    internal class IceHuntLogic : CustomLogic
    {
        private float _next;
        private readonly List<Player> _humans = new List<Player>();

        public IceHuntLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Update(CustomLayer.ActionData data)
        {
            if (Time.time < _next) return;
            _next = Time.time + 3f;

            _humans.Clear();
            FikaBridge.CollectHumans(_humans);
            Player target = null;
            float best = float.MaxValue;
            foreach (var h in _humans)
            {
                if (h == null || h.HealthController == null || !h.HealthController.IsAlive) continue;
                float d = (h.Position - BotOwner.Position).sqrMagnitude;
                if (d < best) { best = d; target = h; }
            }
            if (target == null) return;

            bool far = best > 20f * 20f;
            BotOwner.Mover?.SetPose(1f);
            BotOwner.Mover?.SetTargetMoveSpeed(far ? 1f : 0.7f);
            try { BotOwner.Sprint(far, true); } catch { }
            BotOwner.Mover?.GoToPoint(target.Position, true, 4f);
        }

        public override void Stop()
        {
            base.Stop();
            try { BotOwner.Sprint(false, true); } catch { }
            _next = 0f;
        }
    }
}
