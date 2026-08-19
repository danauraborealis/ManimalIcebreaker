using System;
using System.Collections.Generic;
using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace Manimal.Icebreaker
{
    // WEDGE BOSS AI, round 1 (user call 08-13, from docs/BOSSWEDGE-AI-REPORT.md — the
    // retail 1.0 wedge brain decompile). retail wedge is a patient cover-fighting
    // ambush boss; this file ports the two lowest-risk, highest-flavor pieces:
    //
    //   1. CLOSE-RANGE TAUNTS — retail WedgeCloseDist fires EPhraseTrigger.Provocation
    //      on a periodic while fighting inside its close band. verified present in the
    //      4.0 assembly and wired through BotTalk.TrySay, which self-gates on CanSay.
    //   2. ONE PUSHER AT A TIME — retail followers ask the boss before pushing
    //      (ShallHoldBecauseRecentGroupGoToEnemy): while one squadmate advances on a
    //      known-but-unseen enemy, the rest hold cover. ported as a push token: the
    //      sanctioned pusher runs vanilla combat, everyone else holds until the token
    //      rotates. our machinery, retail's behavior.
    //
    // the standing rule from the terminal ports applies double to a boss squad: NEVER
    // replace visible-enemy gunfighting. the hold yields the instant a bot's enemy is
    // visible or it comes under fire, and with no known enemy at all it stands down
    // entirely (vanilla patrol / crew guard keeps the bot). round 1 deliberately does
    // NOT touch the boss's own combat — no bands, no blood-ambush yet.
    //
    // NO MUTE anywhere in this file (user call: wedge is not the engine-room hold
    // squad and must keep his voice — the taunts are the whole point).
    internal static class WedgeBrains
    {
        // the wedge waves ship bossWedge + blackDivIb escorts (base.json wedges1-4 —
        // there is no separate wedgeguard type on this map), so the type filter can only
        // narrow to "some BD bot"; the real membership test is sharing a BotsGroup with
        // a live bossWedge. ids from the BlackDiv prepatch, same source as IcebreakerCrew.
        internal const int RoleWedge = IcebreakerCrew.BdWedge; // 848424
        internal const int RoleIb = IcebreakerCrew.BdIb;       // 848426

        internal static void Register()
        {
            // same brain list as the crew tiers: BD bots run the LITERAL "PMC" brain
            // (proven by the post-release diagnostic), the rest are parity padding.
            var brains = new List<string> { "ExUsec", "PmcBear", "PmcUsec", "PMC" };
            // 79 is deliberate, and different from IceHold's 95: PMC-brain combat
            // approach layers run 70-78 and AvoidDanger runs 80. at 79 the hold beats
            // the approach (that IS the throttle) but still LOSES to AvoidDanger — a
            // grenade at a holder's feet scatters him like it should. IceHold's 95
            // outranks AvoidDanger on purpose (ambushers stay put through danger);
            // copying that here would get throttled escorts killed by every grenade.
            // the 4-arg overload's type filter keeps this off non-BD bots entirely —
            // same overload BlackDiv itself uses for its wedge hunt layer (prio 10,
            // far below us, no interaction).
            BrainManager.AddCustomLayer(typeof(WedgeEscortHoldLayer), brains, 79,
                new List<WildSpawnType> { (WildSpawnType)RoleIb });
            // BLOOD AMBUSH (round 2), boss only. 85 is above PMC combat (70-78) AND
            // AvoidDanger (80) on purpose: getting hit is the trigger, and AvoidDanger's
            // own shot-reaction shuffle would otherwise own him the moment it fires.
            // retail runs its ambush at the same relative height (87, above its combat
            // bands). the layer still refuses to run while his enemy is VISIBLE, so an
            // actual gunfight is never interrupted — see the doctrine note on the layer.
            BrainManager.AddCustomLayer(typeof(WedgeAmbushLayer), brains, 85,
                new List<WildSpawnType> { (WildSpawnType)RoleWedge });
            // ROOMS MODE (round 3) — wedge's actual identity on this map: every zone he
            // spawns in is Rooms-named, so this is effectively his permanent combat
            // posture (the retail gate — SpawnBotZone.NameZone.Contains("Rooms") — is
            // kept anyway, so the behavior follows the data if he's ever re-zoned).
            // 79 on purpose, same reasoning as the escort hold: beats PMC combat
            // approach 70-78 so dead-air POSITIONING is ours, loses to AvoidDanger 80
            // so a grenade still breaks the cycle — retail agrees with that ordering
            // (their AvoidDanger 130 outranks their Rooms 82).
            BrainManager.AddCustomLayer(typeof(WedgeRoomsLayer), brains, 79,
                new List<WildSpawnType> { (WildSpawnType)RoleWedge });
            Plugin.Log.LogInfo("[WedgeBrain] escort push-throttle (79) + boss rooms mode (79) + blood-ambush (85) registered");
        }
    }

    // squad registry: who the boss is, and who currently holds the push token
    internal static class WedgeSquad
    {
        private const float TokenSeconds = 10f;   // one push window, then the next bot
        private const float BossRecheck = 3f;

        private static BotOwner _pusher;
        private static float _pusherUntil;

        // per-group boss cache — the lookup walks the group, so don't do it per-poll.
        // keyed on the BotsGroup instance; entries die with the raid (ResetForRaid).
        private sealed class BossRef { public BotOwner Boss; public float Next; }
        private static readonly Dictionary<object, BossRef> _byGroup = new Dictionary<object, BossRef>();

        internal static void ResetForRaid()
        {
            _byGroup.Clear();
            _pusher = null;
            _pusherUntil = 0f;
        }

        private static bool Alive(BotOwner b)
        {
            var p = b?.GetPlayer;
            return p != null && p.HealthController != null && p.HealthController.IsAlive;
        }

        // the live bossWedge sharing this bot's group, or null
        internal static BotOwner BossOf(BotOwner member)
        {
            var group = member?.BotsGroup;
            if (group == null) return null;
            if (!_byGroup.TryGetValue(group, out var re))
                _byGroup[group] = re = new BossRef();
            if (Time.time < re.Next && Alive(re.Boss)) return re.Boss;
            re.Next = Time.time + BossRecheck;
            re.Boss = null;
            try
            {
                for (int i = 0; i < group.MembersCount; i++)
                {
                    var m = group.Member(i);
                    if (m == null || !Alive(m)) continue;
                    if ((int)m.Profile.Info.Settings.Role == WedgeBrains.RoleWedge) { re.Boss = m; break; }
                }
            }
            catch { }
            return re.Boss;
        }

        // the push token. first asker in a window claims it; the claim survives until
        // it expires or the pusher dies, then the next asker takes it. deliberately no
        // fairness queue — retail's version is exactly this shape ("recent group
        // go-to-enemy" = hold), and a dead pusher freeing the token instantly is what
        // keeps the squad from stalling when you kill the point man.
        internal static bool MayPush(BotOwner b)
        {
            if (b == null) return true;
            if (ReferenceEquals(b, _pusher) && Time.time < _pusherUntil && Alive(b)) return true;
            if (Time.time < _pusherUntil && Alive(_pusher)) return false;
            _pusher = b;
            _pusherUntil = Time.time + TokenSeconds;
            return true;
        }
    }

    // the throttle. active = this escort HOLDS (the sanctioned pusher and every bot
    // with a visible enemy fall through to vanilla combat).
    internal class WedgeEscortHoldLayer : CustomLayer
    {
        public WedgeEscortHoldLayer(BotOwner botOwner, int priority) : base(botOwner, priority) { }

        public override string GetName() => "WedgeEscortHold";

        public override bool IsActive()
        {
            if (!IceGate.On) return false;
            var p = BotOwner.GetPlayer;
            if (p == null || p.HealthController == null || !p.HealthController.IsAlive) return false;

            // wedge squad only — every other BD bot (T1-T4 crews, hides, stern) shares
            // the blackDivIb type this layer is registered for, and stands down here
            var boss = WedgeSquad.BossOf(BotOwner);
            if (boss == null || ReferenceEquals(boss, BotOwner)) return false;

            // the throttle only exists in dead air with a KNOWN enemy. nothing known =
            // vanilla idle/crew-guard; visible or shot at = vanilla combat, right now.
            try
            {
                var ge = BotOwner.Memory?.GoalEnemy;
                if (ge == null) return false;
                if (ge.IsVisible) return false;
                if (BotOwner.Memory.IsUnderFire) return false;
            }
            catch { return false; }

            // the one sanctioned pusher advances (vanilla layers take him)
            if (WedgeSquad.MayPush(BotOwner)) return false;

            return true;
        }

        public override Action GetNextAction()
            => new Action(typeof(WedgeHoldLogic), "hold — a squadmate is pushing");

        public override bool IsCurrentActionEnding()
            => CurrentAction == null || CurrentAction.Type != typeof(WedgeHoldLogic);
    }

    // minimal stand-and-cover hold. no crouch theater, no mute — the bot just stops
    // advancing; vanilla steering keeps him scanning, and any visible enemy or incoming
    // fire ends the layer from IsActive before this logic ever matters.
    internal class WedgeHoldLogic : CustomLogic
    {
        private float _next;

        public WedgeHoldLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            try { BotOwner.Mover?.Stop(); } catch { }
        }

        public override void Update(CustomLayer.ActionData data)
        {
            // re-assert occasionally — cheap insurance against anything re-pathing him
            // while held (nothing should, the layer owns him, but Stop() is idempotent)
            if (Time.time < _next) return;
            _next = Time.time + 2f;
            try { BotOwner.Mover?.Stop(); } catch { }
        }

        public override void Stop()
        {
            base.Stop();
            _next = 0f;
            // PRIME THE VOXEL — same ghosting-through-doors fix as IceHoldLogic.Stop
            // (08-12): CurVoxel only refreshes inside BotMover's movement code, so a bot
            // released from any stand-still logic can charge a shut door with a null
            // voxel, get no door links, and ghost straight through it.
            try { BotOwner.AIData?.SetPosToVoxel(BotOwner.Position); } catch { }
        }
    }

    // RANGE BANDS (round 2) — retail computes them by NAVMESH PATH LENGTH, not
    // euclidean distance (BossWedge.CalcDistByPath: CLOSE_DIST 12, MID_DIST 27, on a
    // 3s periodic). the difference is the whole point on a ship: an enemy 8m away
    // through a bulkhead is FAR, because wedge would have to walk the corridor to
    // reach them. an incomplete path reads as Far for the same reason.
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

    // BLOOD-TRIGGERED AMBUSH state (round 2). retail: BossWedgeAmbush subscribes the
    // boss's own BeingHitAction — take a hit and he breaks contact, relocates to a
    // shoot-cover point and waits (the "hit him and he disappears, then kills you from
    // cover" signature). windows instead of flags so a missed activation can't wedge
    // the state machine: a hit opens a 6s entry window, an actual relocate holds the
    // layer for up to 25s and starts a 45s cooldown.
    internal static class WedgeAmbush
    {
        private static float _pendingUntil, _holdUntil, _cooldownUntil;

        internal static void ResetForRaid() { _pendingUntil = _holdUntil = _cooldownUntil = 0f; }

        internal static void NoteHit()
        {
            if (Time.time >= _cooldownUntil && Time.time >= _holdUntil)
                _pendingUntil = Time.time + 6f;
        }

        internal static bool WantsAmbush => Time.time < _pendingUntil || Time.time < _holdUntil;

        internal static void MarkStarted()
        {
            _pendingUntil = 0f;
            _holdUntil = Time.time + 25f;
            _cooldownUntil = Time.time + 45f;
        }

        internal static void Abort() { _pendingUntil = 0f; _holdUntil = 0f; }
    }

    // DOCTRINE NOTE, stated because it is a deliberate deviation: retail's ambush CAN
    // yank wedge out of a visible firefight (its layers keep shooting while he moves;
    // ours would give the player a free running target). so this ports the SAFE SUBSET:
    // the relocate only fires when he takes a hit while his enemy is NOT visible —
    // tagged from an angle he can't see, or sight just broke. the payoff is intact:
    // the layer drops the instant the enemy becomes visible again, which lands wedge
    // in vanilla combat AT the shoot-cover point he just claimed. it also deliberately
    // does NOT yield on IsUnderFire — being shot IS the trigger.
    internal class WedgeAmbushLayer : CustomLayer
    {
        public WedgeAmbushLayer(BotOwner botOwner, int priority) : base(botOwner, priority) { }

        public override string GetName() => "WedgeBloodAmbush";

        public override bool IsActive()
        {
            if (!IceGate.On) return false;
            var p = BotOwner.GetPlayer;
            if (p == null || p.HealthController == null || !p.HealthController.IsAlive) return false;
            try
            {
                var ge = BotOwner.Memory?.GoalEnemy;
                if (ge == null) { WedgeAmbush.Abort(); return false; } // enemy forgotten — nothing to ambush
                if (ge.IsVisible) return false;                        // visible fight is vanilla's, always
            }
            catch { return false; }
            return WedgeAmbush.WantsAmbush;
        }

        public override Action GetNextAction()
            => new Action(typeof(WedgeAmbushLogic), "hit taken — break contact to shoot-cover");

        public override bool IsCurrentActionEnding()
            => CurrentAction == null || CurrentAction.Type != typeof(WedgeAmbushLogic);
    }

    internal class WedgeAmbushLogic : CustomLogic
    {
        private Vector3 _coverPos, _watchPos;
        private bool _placed;
        private float _next;

        public WedgeAmbushLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            _placed = false;
            try
            {
                var ge = BotOwner.Memory?.GoalEnemy;
                _watchPos = ge != null ? ge.EnemyLastPosition : BotOwner.Position;

                // shoot-cover, retail's filter shape: a point with real defence that
                // keeps some distance from where the enemy was last known. fall back to
                // any hide point; no cover at all aborts the whole window (standing in
                // the open "ambushing" is worse than fighting on)
                // AT LEAST 10m from where he is NOW (08-15 field log: the first live
                // ambush relocated 9m — one couch over, read as "meh". FindClosestPoint
                // walks nearest-first, so without a self-distance floor the "relocate"
                // is always a shuffle. the disappear feel IS the distance.)
                var self = BotOwner.Position;
                CustomNavigationPoint point = null;
                try
                {
                    point = BotOwner.Covers?.FindClosestPoint(self, 10f, _watchPos,
                        false, g => g != null && g.DefenceLevel >= 2f
                                 && (g.Position - self).sqrMagnitude >= 100f);
                }
                catch { }
                if (point == null)
                {
                    try { point = BotOwner.Covers?.FindHidePoint(BotOwner.Position, 1f); } catch { }
                }
                if (point == null)
                {
                    Plugin.Log.LogDebug("[WedgeBrain] ambush aborted — no shoot-cover reachable");
                    WedgeAmbush.Abort();
                    return;
                }

                _coverPos = point.BasePosition;
                _placed = true;
                WedgeAmbush.MarkStarted();
                BotOwner.Mover?.GoToPoint(_coverPos, true, 0.6f);
                Plugin.Log.LogDebug($"[WedgeBrain] blood ambush — relocating {Vector3.Distance(BotOwner.Position, _coverPos):0}m to shoot-cover");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[WedgeBrain] ambush start failed: {e.Message}");
                WedgeAmbush.Abort();
            }
        }

        public override void Update(CustomLayer.ActionData data)
        {
            if (!_placed || Time.time < _next) return;
            _next = Time.time + 0.4f;
            try
            {
                if ((BotOwner.Position - _coverPos).sqrMagnitude < 2.25f)
                {
                    // in position: slight crouch, eyes on where they were last seen —
                    // the moment they show themselves, IsActive drops this layer and
                    // vanilla combat opens up from here
                    BotOwner.Mover?.SetPose(0.6f);
                    BotOwner.Steering?.LookToPoint(_watchPos + Vector3.up * 1.4f);
                }
                else
                {
                    BotOwner.Mover?.GoToPoint(_coverPos, true, 0.6f); // re-assert, mover can drop orders
                }
            }
            catch { }
        }

        public override void Stop()
        {
            base.Stop();
            _next = 0f;
            try { BotOwner.Mover?.SetPose(1f); } catch { }
            // the ghosting-through-doors fix — mandatory in every stand-still logic's Stop
            try { BotOwner.AIData?.SetPosToVoxel(BotOwner.Position); } catch { }
        }
    }

    // ROOMS MODE (round 3, retail WedgeRooms prio 82, 350 lines — ported to its shape,
    // not its line count). the CQB personality: with a known-but-unseen enemy he does
    // not stand still and does not leave his rooms — he CYCLES between shoot-cover
    // points inside his area (retail STEP_AWAY_DIST 5m between picks, a go-round timer
    // between moves), eyes on the last known position, and if you deny him personal
    // sight long enough he STIMS and keeps pressure (retail: 15s no sight -> stim,
    // 30s cooldown — Medecine.Stimulators.CanUseNow/TryApply verified in 4.0).
    // visible enemy or incoming fire ends the layer instantly: the fighting itself
    // stays vanilla, this owns only the dead air between exchanges.
    internal static class WedgeRoomsState
    {
        // the room anchor: where he was when rooms mode first engaged. cheap stand-in
        // for retail's AIPlaceInfo place-scoping, which our rip has no volumes for —
        // covers are only considered inside RoomRadius of this point, so he works his
        // room set instead of drifting across the ship.
        internal static Vector3 Anchor;
        internal static bool Anchored;
        internal static void ResetForRaid() { Anchored = false; Anchor = Vector3.zero; }
    }

    internal class WedgeRoomsLayer : CustomLayer
    {
        private int _zoneGate = -1; // -1 unknown, 0 no, 1 yes — zone never changes, check once

        public WedgeRoomsLayer(BotOwner botOwner, int priority) : base(botOwner, priority) { }

        public override string GetName() => "WedgeRooms";

        public override bool IsActive()
        {
            if (!IceGate.On) return false;
            var p = BotOwner.GetPlayer;
            if (p == null || p.HealthController == null || !p.HealthController.IsAlive) return false;

            if (_zoneGate < 0)
            {
                try { _zoneGate = (BotOwner.SpawnBotZone?.NameZone ?? "").IndexOf("Rooms", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0; }
                catch { _zoneGate = 0; }
                if (_zoneGate == 1) Plugin.Log.LogDebug($"[WedgeBrain] rooms mode armed (zone '{BotOwner.SpawnBotZone?.NameZone}')");
            }
            if (_zoneGate == 0) return false;

            if (WedgeAmbush.WantsAmbush) return false; // the 85 layer owns him then
            try
            {
                var ge = BotOwner.Memory?.GoalEnemy;
                if (ge == null) return false;          // no known enemy — vanilla idle/patrol
                if (ge.IsVisible) return false;        // the fight itself is vanilla's
                if (BotOwner.Memory.IsUnderFire) return false;
            }
            catch { return false; }
            return true;
        }

        public override Action GetNextAction()
            => new Action(typeof(WedgeRoomsLogic), "rooms — cycle shoot-cover, deny nothing");

        public override bool IsCurrentActionEnding()
            => CurrentAction == null || CurrentAction.Type != typeof(WedgeRoomsLogic);
    }

    internal class WedgeRoomsLogic : CustomLogic
    {
        private const float RoomRadius = 25f;       // the "his rooms" leash around the anchor
        private const float StepAway = 5f;          // retail STEP_AWAY_DIST
        private const float StimNoSightSec = 15f;   // retail STIM_NEED_NO_PERSONAL_SIGHT_SEC
        private const float StimCooldown = 30f;     // retail STIM_COOLDOWN_SEC

        private Vector3 _coverPos, _watch;
        private bool _hasCover;
        private float _nextTick, _nextRound, _lastSight, _nextStim, _nextGoReassert;

        public WedgeRoomsLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            if (!WedgeRoomsState.Anchored)
            {
                WedgeRoomsState.Anchor = BotOwner.Position;
                WedgeRoomsState.Anchored = true;
            }
            _lastSight = Time.time; // grace period — don't stim the moment the layer engages
            _hasCover = false;
            _nextRound = 0f;
        }

        public override void Update(CustomLayer.ActionData data)
        {
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + 0.4f;
            try
            {
                var ge = BotOwner.Memory?.GoalEnemy;
                if (ge != null)
                {
                    _watch = ge.EnemyLastPosition;
                    if (ge.IsVisible) _lastSight = Time.time; // layer yields next poll anyway
                }

                // the stim rule: sight denied long enough -> chem up and keep working.
                // TryApply's own checks still apply (has one, animation slot free)
                if (Time.time - _lastSight > StimNoSightSec && Time.time >= _nextStim)
                {
                    _nextStim = Time.time + StimCooldown;
                    var st = BotOwner.Medecine?.Stimulators;
                    if (st != null && st.HaveSmt && st.CanUseNow())
                    {
                        st.TryApply();
                        Plugin.Log.LogDebug("[WedgeBrain] rooms — sight denied 15s, stimming");
                    }
                }

                if (!_hasCover || Time.time >= _nextRound) PickNextCover();

                if (_hasCover)
                {
                    if ((BotOwner.Position - _coverPos).sqrMagnitude < 2.25f)
                    {
                        BotOwner.Mover?.SetPose(0.75f);
                        BotOwner.Steering?.LookToPoint(_watch + Vector3.up * 1.4f);
                    }
                    else if (Time.time >= _nextGoReassert)
                    {
                        _nextGoReassert = Time.time + 2f;
                        BotOwner.Mover?.GoToPoint(_coverPos, true, 0.6f);
                    }
                }
            }
            catch { }
        }

        // the cycle: a DIFFERENT shoot-cover inside the room leash, never the one he's
        // on (step-away), re-picked on the go-round timer so he keeps changing the
        // angle you have to solve
        private void PickNextCover()
        {
            _nextRound = Time.time + UnityEngine.Random.Range(10f, 16f);
            try
            {
                var self = BotOwner.Position;
                var anchor = WedgeRoomsState.Anchor;
                CustomNavigationPoint point = null;
                try
                {
                    point = BotOwner.Covers?.FindClosestPoint(self, 6f, _watch,
                        false, g => g != null && g.DefenceLevel >= 2f
                                 && (g.Position - anchor).sqrMagnitude <= RoomRadius * RoomRadius
                                 && (g.Position - self).sqrMagnitude >= StepAway * StepAway);
                }
                catch { }
                if (point == null) return; // no fresh cover in the rooms — hold the current one

                _coverPos = point.BasePosition;
                _hasCover = true;
                _nextGoReassert = 0f;
                BotOwner.Mover?.GoToPoint(_coverPos, true, 0.6f);
            }
            catch { }
        }

        public override void Stop()
        {
            base.Stop();
            _hasCover = false;
            try { BotOwner.Mover?.SetPose(1f); } catch { }
            // the ghosting-through-doors fix — mandatory in every stand-still logic's Stop
            try { BotOwner.AIData?.SetPosToVoxel(BotOwner.Position); } catch { }
        }
    }

    // CLOSE-RANGE TAUNTS. retail fires Provocation from inside its close-band layer;
    // we deliberately DON'T own the boss with a layer round 1, so a watcher drives the
    // voice line from outside — TrySay is safe to call on a bot owned by any layer,
    // and BotTalk's own CanSay/group-delay gating still applies.
    internal class WedgeVoice : MonoBehaviour
    {
        private const float TickEvery = 0.5f;
        private const float FindEvery = 3f;
        private const float BandEvery = 3f;          // retail's CheckDistPeriod cadence
        private const float CheatVisionDist = 30f;   // retail CHEAT_VISION_DIST

        private BotOwner _boss;
        private Player _hitSub;                       // who BeingHitAction is wired to
        private float _nextTick, _nextFind, _nextTaunt, _nextBand;
        private readonly HashSet<int> _aliveSquad = new HashSet<int>();
        private bool _squadSeeded;

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_Attach
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!IceGate.On) return;
                WedgeSquad.ResetForRaid();
                WedgeAmbush.ResetForRaid();
                WedgeBands.ResetForRaid();
                WedgeRoomsState.ResetForRaid();
                if (UnityEngine.Object.FindObjectOfType<WedgeVoice>() == null)
                    new GameObject("Icebreaker_WedgeVoice").AddComponent<WedgeVoice>();
            }
        }

        private static readonly System.Diagnostics.Stopwatch _updSw = new System.Diagnostics.Stopwatch();

        private void Update()
        {
            if (!IceGate.On || Time.time < _nextTick) return;
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

                // the blood-ambush trigger rides the boss's own hit event — keep the
                // subscription pinned to whoever the live boss player currently is
                EnsureHitSub();
                // squad death watch -> retail's party-death cheat vision
                WatchSquadDeaths();

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

        private void EnsureHitSub()
        {
            var p = _boss?.GetPlayer;
            if (ReferenceEquals(p, _hitSub)) return;
            Unsub();
            if (p != null)
            {
                p.BeingHitAction += OnBossHit;
                _hitSub = p;
            }
        }

        private void Unsub()
        {
            if (_hitSub == null) return;
            try { _hitSub.BeingHitAction -= OnBossHit; } catch { }
            _hitSub = null;
        }

        private void OnBossHit(DamageInfoStruct damage, EBodyPart part, float hp) => WedgeAmbush.NoteHit();

        private void OnDestroy() => Unsub();

        // PARTY-DEATH CHEAT VISION (retail BossWedge: DEAD_NEED_TO_CHEAT_VISION + a 30m
        // sweep). when one of his own dies, the group is fed the position of every enemy
        // within 30m WITHOUT line of sight — "you can't quietly whittle the squad".
        // EEnemyPartVisibleType.Sence is the sense-only flag: the bots get a location to
        // work with, not a free aimbot lock.
        private void WatchSquadDeaths()
        {
            try
            {
                var group = _boss.BotsGroup;
                if (group == null) return;
                int deaths = 0;
                var seen = new HashSet<int>();
                for (int i = 0; i < group.MembersCount; i++)
                {
                    var m = group.Member(i);
                    var mp = m?.GetPlayer;
                    if (mp != null && mp.HealthController != null && mp.HealthController.IsAlive)
                        seen.Add(m.Id);
                }
                if (_squadSeeded)
                    foreach (var id in _aliveSquad)
                        if (!seen.Contains(id)) deaths++;
                _aliveSquad.Clear();
                foreach (var id in seen) _aliveSquad.Add(id);
                _squadSeeded = true;
                if (deaths == 0) return;

                int fed = 0;
                var world = Singleton<GameWorld>.Instance;
                if (world?.AllAlivePlayersList == null) return;
                foreach (var pl in world.AllAlivePlayersList)
                {
                    if (pl == null) continue;
                    bool enemy = false;
                    try { enemy = _boss.EnemiesController != null && _boss.EnemiesController.IsEnemy(pl); }
                    catch { }
                    if (!enemy) continue;
                    if ((pl.Position - _boss.Position).sqrMagnitude > CheatVisionDist * CheatVisionDist) continue;
                    var weaponRoot = pl.WeaponRoot != null ? pl.WeaponRoot.position : pl.Position;
                    try { _boss.BotsGroup?.SetEnemyPos(pl, pl.Position, weaponRoot, EEnemyPartVisibleType.Sence); fed++; }
                    catch { }
                }
                if (fed > 0)
                    Plugin.Log.LogDebug($"[WedgeBrain] squadmate down — cheat vision fed {fed} enemy position(s) within {CheatVisionDist:0}m");
            }
            catch { }
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
