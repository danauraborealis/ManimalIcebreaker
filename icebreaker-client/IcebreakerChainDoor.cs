using System;
using System.Collections;
using System.Collections.Generic;
using Comfort.Common;
using HarmonyLib;
using UnityEngine;
using EFT;
using EFT.InventoryLogic;

namespace Manimal.Icebreaker
{
    // the engine-section chain door: an Animator-driven prop (Icebreaker_chain_door
    // controller, bools IsTryOpen/IsExplosion/IsOpen) with a 'Try_open' Switch as the
    // interaction surface. the switch is a prop-trigger, not a real switch — vanilla
    // interaction would flip its DoorState and kill the prompt, so we own the action:
    // "Open" pulses IsTryOpen (the door rattles but holds — it opens for real only
    // after the explosive charge, a later step).
    internal static class IcebreakerChainDoor
    {
        internal const string TryOpenId = "Try_open_00000";
        internal const string ExplosionSwitchId = "Explosion_switch";
        internal const string ChargeTpl = "69a0174087a75d2cbd0842e8"; // the demo charge item
        internal const float PlantSeconds = 5f;

        internal static bool Planted;  // one-way per raid — the charge is in the door
        internal static bool Exploded; // fuse ran out — Try_open now really opens
        internal static bool Opened;   // door dropped — no more interactions

        private static Animator _anim;

        internal static Animator FindAnimator(Component from)
        {
            if (_anim != null) return _anim;
            // the switch usually lives under the door prop — parent search first,
            // scene-name fallback second (hierarchy layout isnt guaranteed)
            _anim = from != null ? from.GetComponentInParent<Animator>() : null;
            if (_anim == null)
            {
                var go = GameObject.Find("Icebreaker_chain_door");
                if (go != null) _anim = go.GetComponentInChildren<Animator>(true);
            }
            if (_anim == null) Plugin.Log.LogWarning("[ChainDoor] no Animator found (parent search + 'Icebreaker_chain_door' lookup) — try-open does nothing");
            return _anim;
        }

        // bundled foley (shipped via the 1R carrier alongside the seal clips): the rattle,
        // the post-explosion open, and the blast itself
        private static AudioClip _sndTryOpen, _sndOpenAfter, _sndExplosion, _sndExplosionReaction, _sndPlant, _sndTimer;
        private static bool _sndSearched;

        internal static AudioClip PlantClip { get { EnsureClips(); return _sndPlant; } }

        private static AudioClip FindClip(string name)
        {
            foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>())
                if (c != null && c.name == name) return c;
            return null;
        }

        private static void EnsureClips()
        {
            if (_sndSearched) return;
            _sndSearched = true;
            _sndTryOpen = FindClip("chain_door_try_open");
            _sndOpenAfter = FindClip("chain_door_try_open_after_explosion");
            _sndExplosion = FindClip("IB_chain_door_explosion");
            _sndExplosionReaction = FindClip("IB_chain_door_explosion_reaction");
            _sndPlant = FindClip("amb_terminal_interactive_c6_plant_activate");
            _sndTimer = FindClip("amb_terminal_interactive_c6_timer");
            if (_sndTryOpen == null || _sndOpenAfter == null || _sndExplosion == null || _sndPlant == null || _sndTimer == null)
                Plugin.Log.LogWarning($"[ChainDoor] foley incomplete (tryOpen={_sndTryOpen != null} openAfter={_sndOpenAfter != null} explosion={_sndExplosion != null} reaction={_sndExplosionReaction != null} plant={_sndPlant != null} timer={_sndTimer != null}) — clips missing from bundle? rerun 1R + rebuild");
        }

        // positional one-shot on a throwaway host — PlayClipAtPoint gives no rolloff
        // control and these need door-scale (or blast-scale) audibility ranges
        private static void PlayAt(AudioClip clip, Vector3 pos, float maxDist, float delay = 0f)
        {
            if (clip == null) return;
            var go = new GameObject("Icebreaker_ChainDoorSnd");
            go.transform.position = pos;
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.spatialBlend = 1f;
            src.maxDistance = maxDist;
            src.rolloffMode = AudioRolloffMode.Linear;
            if (delay > 0f) src.PlayDelayed(delay); else src.Play();
            UnityEngine.Object.Destroy(go, clip.length + delay + 0.5f);
        }

        internal static void ResetForRaid()
        {
            _anim = null; _chargeProp = null; _handleIntact = null; _handleDropped = null;
            _sndSearched = false; _sndTryOpen = _sndOpenAfter = _sndExplosion = _sndExplosionReaction = null;
            Planted = false; Exploded = false; Opened = false;
            // the planted-charge prop must start hidden regardless of how the scene ships;
            // finding it now (scene just went live) also caches the transform so the
            // show/hide later doesn't depend on GameObject.Find, which can't see inactive GOs
            HideChargeProp();
        }

        // door-rig props toggled by the sequence. all found near the animator (and cached
        // while active — GameObject.Find can't see inactive GOs later):
        //   item_spec_sz_1: the planted charge — hidden until planted, gone after the blast
        //   handle_02: the intact handle — visible until the door drops
        //   handle_01: the dropped-door handle — hidden until the drop finishes
        private static Transform _chargeProp, _handleIntact, _handleDropped;

        internal static Transform FindChargeProp() => FindDoorProp(ref _chargeProp, "item_spec_sz_1");
        internal static Transform FindHandleIntact() => FindDoorProp(ref _handleIntact, "Icebreaker_exterior_door_handle_02");
        internal static Transform FindHandleDropped() => FindDoorProp(ref _handleDropped, "Icebreaker_exterior_door_handle_01");

        private static Transform FindDoorProp(ref Transform cache, string name)
        {
            if (cache != null) return cache;
            var anim = FindAnimator(null);
            if (anim != null)
            {
                cache = FindChildNamed(anim.transform, name);
                if (cache == null && anim.transform.parent != null)
                    cache = FindChildNamed(anim.transform.parent, name);
            }
            if (cache == null)
            {
                var go = GameObject.Find(name);
                if (go != null) cache = go.transform;
            }
            if (cache == null) Plugin.Log.LogWarning($"[ChainDoor] '{name}' not found near the door");
            return cache;
        }

        private static void SetProp(Transform t, bool on)
        {
            if (t != null && t.gameObject.activeSelf != on) t.gameObject.SetActive(on);
        }

        private static void HideChargeProp()
        {
            SetProp(FindChargeProp(), false);
            SetProp(FindHandleDropped(), false); // dropped-door handle starts hidden too
        }

        internal static Item FindCharge(Player player)
        {
            if (player?.Profile?.Inventory == null) return null;
            foreach (var it in player.Profile.Inventory.AllRealPlayerItems)
                if (it != null && it.TemplateId == ChargeTpl) return it;
            return null;
        }

        // consume the charge the way the game destroys the cultist amulet:
        // RemoveWithoutRestrictions executes ONCE and that's it. the first version did
        // Remove(simulate:false) — which already executes — and THEN ran the op again
        // through TryRunNetworkTransaction: double-execution, the flashing-item bug.
        internal static bool ConsumeCharge(Player player)
        {
            var item = FindCharge(player);
            if (item == null) return false;
            var op = InteractionsHandlerClass.RemoveWithoutRestrictions(item, player.InventoryController);
            if (op.Failed)
            {
                Plugin.Log.LogWarning($"[Plant] charge remove op failed: {op.Error}");
                return false;
            }
            Plugin.Log.LogInfo($"[Plant] consumed charge '{item.Name.Localized()}' ({item.Id})");
            return true;
        }

        // planting done: charge consumed -> 10s fuse -> IsExplosion pulse + the VFX burst
        // (Flash/Smoke/Sparks/DistortionWave particle rig authored next to the door)
        internal const float FuseSeconds = 10f;

        internal static void OnPlanted()
        {
            Planted = true;
            var prop = FindChargeProp();
            if (prop != null) prop.gameObject.SetActive(true); // the charge appears on the door
            // the 10.5s timer bed runs the length of the fuse — the boom lands over its tail
            EnsureClips();
            var at = prop != null ? prop.position : (FindAnimator(null)?.transform.position ?? Vector3.zero);
            PlayAt(_sndTimer, at, 30f);
            new GameObject("Icebreaker_ChainDoorFuse").AddComponent<FuseRunner>();
            Plugin.Log.LogInfo($"[Plant] charge set — detonation in {FuseSeconds:0}s");
        }

        // the explosion particle rig: a 'VFX' GO with a root ParticleSystem + children.
        // generic name, so search NEAR the door first (animator's own tree, then its
        // parent's) before falling back to a global find.
        internal static Transform FindVfx()
        {
            var anim = FindAnimator(null);
            if (anim != null)
            {
                var t = FindChildNamed(anim.transform, "VFX");
                if (t == null && anim.transform.parent != null)
                    t = FindChildNamed(anim.transform.parent, "VFX");
                if (t != null) return t;
            }
            var go = GameObject.Find("VFX");
            if (go != null) return go.transform;
            Plugin.Log.LogWarning("[ChainDoor] no 'VFX' object found near the door — explosion plays without particles");
            return null;
        }

        private static Transform FindChildNamed(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var hit = FindChildNamed(root.GetChild(i), name);
                if (hit != null) return hit;
            }
            return null;
        }

        // watch the door-drop clip play out, then swap the handles: the intact one
        // (handle_02, part of the closed door) vanishes, the dropped-door one (handle_01,
        // hidden since raid start) appears. clip-end detection over a fixed sleep so an
        // authoring-time change to the clip length can't desync the swap; 20s backstop
        // in case the state machine never enters the drop state (mis-wired transition).
        private class HandleSwapRunner : MonoBehaviour
        {
            public Animator Target;
            private void Start() => StartCoroutine(Run());

            private IEnumerator Run()
            {
                const string DropClip = "Icebreaker_chain_door_drop";
                float deadline = Time.time + 20f;
                bool seen = false;
                while (Time.time < deadline && Target != null)
                {
                    bool inDrop = false;
                    foreach (var ci in Target.GetCurrentAnimatorClipInfo(0))
                        if (ci.clip != null && ci.clip.name == DropClip) { inDrop = true; break; }
                    if (inDrop)
                    {
                        seen = true;
                        if (Target.GetCurrentAnimatorStateInfo(0).normalizedTime >= 1f) break;
                    }
                    else if (seen) break; // transitioned onward = the drop played out
                    yield return null;
                }
                if (!seen) Plugin.Log.LogWarning($"[ChainDoor] never saw '{DropClip}' playing — swapping handles on the backstop");
                SetProp(FindHandleIntact(), false);
                SetProp(FindHandleDropped(), true);
                // the chained door is off — the real Door behind it goes Locked -> Shut
                // and works like any other door from here on
                IcebreakerCrew.UnlockDoorById("door_Icebreaker_Outdoor_00000");
                Plugin.Log.LogInfo("[ChainDoor] handles swapped (intact off, dropped on)");
                Destroy(gameObject);
            }
        }

        private class FuseRunner : MonoBehaviour
        {
            private void Start() => StartCoroutine(Fuse());

            private IEnumerator Fuse()
            {
                yield return new WaitForSeconds(FuseSeconds);

                Exploded = true;
                var anim = FindAnimator(null);
                if (anim != null)
                {
                    EnsureClips();
                    PlayAt(_sndExplosion, anim.transform.position, 120f); // blast-scale audibility
                    PlayAt(_sndExplosionReaction, anim.transform.position, 60f, 1f); // settling debris after the boom
                    var go = new GameObject("Icebreaker_ChainDoorExplosion");
                    var pr = go.AddComponent<PulseRunner>();
                    pr.Target = anim;
                    pr.Param = "IsExplosion";
                }

                var vfx = FindVfx();
                if (vfx != null)
                {
                    if (!vfx.gameObject.activeSelf) vfx.gameObject.SetActive(true);
                    var ps = vfx.GetComponent<ParticleSystem>();
                    if (ps != null) ps.Play(true); // root + all children (Flash/Smoke/Sparks/...)
                    else
                        foreach (var child in vfx.GetComponentsInChildren<ParticleSystem>(true))
                            child.Play();
                    Plugin.Log.LogInfo("[ChainDoor] explosion VFX fired");
                }

                // the charge prop is spent — vanish it with the blast
                var prop = FindChargeProp();
                if (prop != null) prop.gameObject.SetActive(false);
                Destroy(gameObject);
            }
        }

        internal static void TryOpen()
        {
            var anim = FindAnimator(null);
            if (anim == null) return;

            // post-explosion the same prompt opens for real: IsOpen latches true (the
            // door-drop state is one-way, no unset) and the prompt retires via Opened
            EnsureClips();
            if (Exploded)
            {
                anim.SetBool("IsOpen", true);
                Opened = true;
                PlayAt(_sndOpenAfter, anim.transform.position, 35f);
                new GameObject("Icebreaker_ChainDoorHandleSwap").AddComponent<HandleSwapRunner>().Target = anim;
                Plugin.Log.LogInfo("[ChainDoor] IsOpen set — door drop");
                return;
            }

            bool has = false;
            foreach (var p in anim.parameters)
                if (p.name == "IsTryOpen") { has = true; break; }
            if (!has)
            {
                Plugin.Log.LogWarning($"[ChainDoor] animator has no 'IsTryOpen' bool — parameters: {string.Join(", ", Array.ConvertAll(anim.parameters, p => p.name))}");
                return;
            }
            PlayAt(_sndTryOpen, anim.transform.position, 35f); // the chains rattle
            var go = new GameObject("Icebreaker_ChainDoorPulse");
            var pr = go.AddComponent<PulseRunner>();
            pr.Target = anim;
            pr.Param = "IsTryOpen";
        }

        // set -> hold a few frames -> unset. a same-frame flip can miss the transition
        // check entirely; ~0.3s guarantees the state machine consumed it while staying
        // well under any reasonable clip length.
        private class PulseRunner : MonoBehaviour
        {
            public Animator Target;
            public string Param;
            private void Start() => StartCoroutine(Pulse());

            private IEnumerator Pulse()
            {
                if (Target != null && !string.IsNullOrEmpty(Param))
                {
                    Target.SetBool(Param, true);
                    Plugin.Log.LogInfo($"[ChainDoor] {Param} pulsed");
                    yield return new WaitForSeconds(0.3f);
                    if (Target != null) Target.SetBool(Param, false);
                }
                Destroy(gameObject);
            }
        }
    }

    // hold-to-plant session on the chain door's Explosion_Logic switch — the seal-session
    // pattern: objectives-panel countdown, weapon lowered, release/Escape/walk-away
    // cancels. success consumes the charge item and fires the explosion transition.
    internal class PlantSession : MonoBehaviour
    {
        public GamePlayerOwner Owner;
        public EFT.Interactive.Switch Switch;

        private float _start;
        private bool _started, _ending, _handsLocked, _panelShown;
        private AudioSource _snd;

        private void Update()
        {
            try
            {
                if (_ending) return;
                var player = Singleton<GameWorld>.Instance?.MainPlayer;
                if (player == null || Switch == null) { End(false); return; }

                if (!_started)
                {
                    _started = true;
                    _start = Time.time;
                    if (Owner != null)
                    {
                        Owner.ShowObjectivesPanel("Planting charge {0:F1}", IcebreakerChainDoor.PlantSeconds);
                        _panelShown = true;
                    }
                    var mc = player.MovementContext;
                    if (mc != null) { mc.BlockFirearms = true; _handsLocked = true; }
                    transform.position = Switch.transform.position;

                    // plant foley for the duration of the hold — cut off with the session
                    var clip = IcebreakerChainDoor.PlantClip;
                    if (clip != null)
                    {
                        _snd = gameObject.AddComponent<AudioSource>();
                        _snd.clip = clip;
                        _snd.spatialBlend = 1f;
                        _snd.maxDistance = 25f;
                        _snd.rolloffMode = AudioRolloffMode.Linear;
                        _snd.Play();
                    }
                }

                if (Input.GetKeyDown(KeyCode.Escape)) { End(false); return; }
                if ((player.Position - Switch.transform.position).sqrMagnitude > 25f) { End(false); return; }
                if (!Input.GetKey(KeyCode.F)) { End(false); return; }
                if (Time.time - _start >= IcebreakerChainDoor.PlantSeconds) { End(true); return; }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Plant] session threw: {e.Message}");
                End(false);
            }
        }

        private void End(bool success)
        {
            if (_ending) return;
            _ending = true;
            if (_snd != null) _snd.Stop(); // the session GO dies with the hold — foley goes with it
            // finally guarantees teardown: last time a throw in the success path skipped
            // Destroy -> OnDestroy never ran -> BlockFirearms stuck (gun unusable)
            try
            {
                if (success)
                {
                    var player = Singleton<GameWorld>.Instance?.MainPlayer;
                    // consume-first: if the charge vanished mid-hold (dropped it), no explosion
                    if (player != null && IcebreakerChainDoor.ConsumeCharge(player))
                        IcebreakerChainDoor.OnPlanted();
                    else
                        Plugin.Log.LogWarning("[Plant] hold finished but no charge to consume — cancelled");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Plant] completion threw: {e}"); }
            finally { Destroy(gameObject); }
        }

        private void OnDestroy()
        {
            if (_handsLocked)
            {
                var mc = Singleton<GameWorld>.Instance?.MainPlayer?.MovementContext;
                if (mc != null) mc.BlockFirearms = false;
            }
            if (_panelShown && Owner != null) Owner.CloseObjectivesPanel();
        }
    }

    // own the chain-door switches' action menus. vanilla smethod_11 names actions from
    // the empty ContextMenuTip (the unselectable "-") and its callback would run the real
    // switch interaction (state flip + prompt death) — replace wholesale:
    //   Try_open        -> "Open" (pulse IsTryOpen; the rattle)
    //   Explosion_Logic -> "Plant" hold session, gated on carrying the charge item
    [HarmonyPatch(typeof(GetActionsClass), "smethod_11")]
    internal static class Patch_ChainDoorSwitches
    {
        private static void Replace(ref ActionsReturnClass result, ActionsTypesClass act)
        {
            if (result == null) result = new ActionsReturnClass { Actions = new List<ActionsTypesClass> { act } };
            else { result.Actions.Clear(); result.Actions.Add(act); }
        }

        private static void Postfix(ref ActionsReturnClass __result, GamePlayerOwner owner, EFT.Interactive.Switch interactiveSwitch)
        {
            try
            {
                if (interactiveSwitch == null) return;
                if (interactiveSwitch.Id == IcebreakerChainDoor.TryOpenId)
                {
                    if (IcebreakerChainDoor.Opened)
                    {
                        // door's on the floor — nothing left to interact with
                        if (__result != null) __result.Actions.Clear();
                        __result = null;
                        return;
                    }
                    Replace(ref __result, new ActionsTypesClass
                    {
                        Name = "Open",
                        Action = IcebreakerChainDoor.TryOpen,
                    });
                }
                else if (interactiveSwitch.Id == IcebreakerChainDoor.ExplosionSwitchId)
                {
                    if (IcebreakerChainDoor.Planted)
                    {
                        // charge is in — no further interaction on the panel
                        if (__result != null) __result.Actions.Clear();
                        __result = null;
                        return;
                    }
                    var player = Singleton<GameWorld>.Instance?.MainPlayer;
                    bool hasCharge = player != null && IcebreakerChainDoor.FindCharge(player) != null;
                    var sw = interactiveSwitch;
                    Replace(ref __result, new ActionsTypesClass
                    {
                        Name = "Plant",
                        Disabled = !hasCharge, // greyed = "theres an interaction here, you lack the item"
                        Action = () =>
                        {
                            var go = new GameObject("Icebreaker_PlantSession");
                            var s = go.AddComponent<PlantSession>();
                            s.Owner = owner;
                            s.Switch = sw;
                        },
                    });
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[ChainDoor] actions patch threw: {e.Message}"); }
        }
    }
}
