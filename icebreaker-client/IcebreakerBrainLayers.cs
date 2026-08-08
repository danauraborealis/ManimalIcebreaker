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
            // "PMC" — the LITERAL brain name — is what the faction mods (BlackDiv et al)
            // actually run: proven by the post-release diagnostic (brain='PMC',
            // activeLayer='AvoidDanger'), which also proved SAIN does NOT cover that
            // brain (its lists hold PmcBear/PmcUsec only) — so BD runs pure VANILLA
            // layers even with SAIN installed, and vanilla priorities are the ones our
            // tiers must beat: PMC brain = AvoidDanger 80, combat 70-78, idle <=61;
            // ExUsec brain = AvoidDanger 100, combat 65-95, idle <=60 (decompiled).
            var brains = new List<string> { "ExUsec", "PmcBear", "PmcUsec", "PMC" };
            // idle tier: 68 owns the "nothing is happening" slot on both brains (above
            // idle/utility <=65, below combat >=70) — and IsActive stands down on any
            // live enemy anyway, so combat layers of ANY system keep their bots.
            BrainManager.AddCustomLayer(typeof(IceCrewLayer), brains, 68);
            // RUSH tier (user call 08-03: deploys must not lose a priority fight to
            // ANYTHING): 110 clears vanilla ExUsec AvoidDanger at 100, PMC AvoidDanger
            // at 80, and every SAIN layer (<=99). self-limits to the RushUntil window,
            // then the bot drops back to whatever combat AI owns it.
            BrainManager.AddCustomLayer(typeof(IceRushLayer), brains, 110);
            // HOLD tier (08-07: held engine squads kept walking out early): at 68 the
            // hold lost to PMC combat 70-78 AND AvoidDanger 80 — any grenade, gunfire
            // or heard-footstep danger event upstairs pulled the ambush out with
            // nobody visible. 95 clears both for the "PMC" brain the BD squads run;
            // the yield narrows to VISIBLE enemy / actually under fire, same doctrine
            // as the rush tier. deliberate trade: a grenade landing in the hide room
            // no longer scatters them (AvoidDanger is outranked) — ambushers hold.
            BrainManager.AddCustomLayer(typeof(IceHoldLayer), brains, 95);
            Plugin.Log.LogInfo("[CrewLayer] bigbrain layers registered (ExUsec/PmcBear/PmcUsec/PMC, crew 68 / hold 95 / rush 110)");
        }

        internal static void Assign(BotOwner bot, Job job, Bounds zone = default, float rushSeconds = 0f)
        {
            var id = bot?.ProfileId;
            if (id == null) return;
            // Hold anchors HERE, at assignment — the ambush post is where the spawner
            // put the bot, not wherever a logic instance first ticks (a bot that
            // wandered before the hold landed used to crouch mid-room)
            if (job == Job.Hold && zone == default)
                zone = new Bounds(bot.Position, Vector3.zero);
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
        // lazy, on the layer's queries, so it needs no activation hook and covers
        // every spawn path (waves, pen deliveries, force spawns) automatically.
        // 5s GRACE before the default lands: the first query fires on the bot's first
        // brain tick, which used to beat the engine-hold sweep — freshly spawned
        // ambushers wandered off as Guards for a second or two and then got anchored
        // mid-room. every explicit Assign wins inside the grace window.
        private static readonly Dictionary<string, float> FirstSeen = new Dictionary<string, float>();

        internal static Rec For(BotOwner bot)
        {
            var id = bot?.ProfileId;
            if (id == null) return null;
            if (ByProfile.TryGetValue(id, out var rec)) return rec;
            if (bot.Profile?.Info?.Settings?.Role == (WildSpawnType)IcebreakerCrew.BdIb)
            {
                if (!FirstSeen.TryGetValue(id, out var seen)) { FirstSeen[id] = Time.time; return null; }
                if (Time.time - seen < 5f) return null;
                var zone = new Bounds(bot.Position, new Vector3(24f, 8f, 24f));
                Assign(bot, Job.Guard, zone);
                return ByProfile[id];
            }
            ByProfile[id] = null; // cached negative — not a crew-managed bot
            return null;
        }

        internal static void Reset() { ByProfile.Clear(); FirstSeen.Clear(); } // per raid
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
            if (rec.Job == IceCrewJobs.Job.Hold) return false; // hold has its own tier at 95
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
                default: return new Action(typeof(IceGuardLogic), "guard the zone");
            }
        }

        public override bool IsCurrentActionEnding()
        {
            Type want;
            switch (IceCrewJobs.For(BotOwner)?.Job)
            {
                case IceCrewJobs.Job.Hunt: want = typeof(IceHuntLogic); break;
                default: want = typeof(IceGuardLogic); break;
            }
            return CurrentAction == null || CurrentAction.Type != want;
        }
    }

    // HOLD — the ambush tier. its own layer ABOVE vanilla combat/AvoidDanger because
    // the ambush must not be walked out by noise: at crew-tier 68 every danger event
    // upstairs (grenade, gunfire, heard steps -> GoalEnemy memory) activated a higher
    // vanilla layer and the hide room emptied with nobody visible (08-07 reports).
    // yields ONLY on a VISIBLE enemy or actually taking fire — a blown ambush is
    // combat's bot; everything quieter keeps them crouched at their markers.
    internal class IceHoldLayer : CustomLayer
    {
        public IceHoldLayer(BotOwner botOwner, int priority) : base(botOwner, priority) { }

        public override string GetName() => "IceCrewHold";

        public override bool IsActive()
        {
            if (!IceGate.On) return false;
            var rec = IceCrewJobs.For(BotOwner);
            if (rec == null || rec.Job != IceCrewJobs.Job.Hold) return false;
            var p = BotOwner.GetPlayer;
            if (p == null || p.HealthController == null || !p.HealthController.IsAlive) return false;
            try
            {
                var ge = BotOwner.Memory?.GoalEnemy;
                if (ge != null && ge.IsVisible) return false;         // walked in on — fight
                if (BotOwner.Memory != null && BotOwner.Memory.IsUnderFire) return false; // shot at — fight
            }
            catch { return false; }
            return true;
        }

        public override Action GetNextAction() => new Action(typeof(IceHoldLogic), "hold the ambush");

        public override bool IsCurrentActionEnding()
            => CurrentAction == null || CurrentAction.Type != typeof(IceHoldLogic);
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
            if (p == null || p.HealthController == null || !p.HealthController.IsAlive) return false;
            // SIGHT ends the charge (user call 08-03: rushing bots ran past the player
            // without firing) — a VISIBLE enemy hands the bot to combat right now.
            // hearing and under-fire deliberately do NOT yield: noise-holding is the
            // exact failure this tier exists to break. lose sight and the rush resumes
            // for whatever remains of the window.
            try
            {
                var ge = BotOwner.Memory?.GoalEnemy;
                if (ge != null && ge.IsVisible) return false;
            }
            catch { }
            return true;
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
        private float _next;
        private bool _muted;

        public IceHoldLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Update(CustomLayer.ActionData data)
        {
            // AMBUSHERS DON'T CHAT (user call 08-07): held bots kept spouting idle
            // voicelines. BotTalk.CanSay is THE gate — Say/TrySay/the query drain all
            // early-out on it. (SetSilence/IsSilenced looks like the intended API but
            // is VESTIGIAL in this build: the engine's own quiet logics set it and
            // nothing anywhere reads it back.) re-assert every tick — Activate() and
            // other systems may rewrite it. restored in Stop().
            try
            {
                if (BotOwner.BotTalk != null && BotOwner.BotTalk.CanSay)
                {
                    BotOwner.BotTalk.CanSay = false;
                    _muted = true;
                }
            }
            catch { }

            if (Time.time < _next) return;
            _next = Time.time + 2f;
            // the post is captured at ASSIGN time (Rec.Zone.center) — where the spawner
            // put the bot, immune to whatever it did between activation and the hold
            var rec = IceCrewJobs.For(BotOwner);
            if (rec == null) return;
            var _post = rec.Zone.center;
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
            // voice back on for combat/hunt — restore to the bot's OWN configured value,
            // not a blind true (some roles ship CAN_TALK false)
            try
            {
                if (_muted && BotOwner.BotTalk != null)
                    BotOwner.BotTalk.CanSay = BotOwner.Settings?.FileSettings?.Mind?.CAN_TALK ?? true;
            }
            catch { }
            _muted = false;
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
