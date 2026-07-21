using System;
using System.Collections;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InputSystem;
using UnityEngine;

namespace Manimal.Icebreaker.Blowtorch
{
    // usable blowtorch hands controller. the bundle animator is a clean two-param
    // graph (Active, Firing) with custom state names (Off/draw/idle/torch_*/holster)
    // and NO WeapIn/WeapOut events in the clips — the vanilla usable ops would wait
    // on those forever. our ops subclass the vanilla four: the bases already drive
    // the Active bool (SetActiveParam) which the graph transitions on, and we add
    // normalizedTime polling that calls FastForward() when draw/holster actually
    // land (with a timeout ceiling so a missed transition can never hang the swap).
    public class BlowtorchController : Player.UsableItemController
    {
        internal const int HandsLayer = 1; // Base(0) / Hands(1) / LActions(2)
        private static readonly int FiringHash = Animator.StringToHash("Firing");
        internal Animator TorchAnimator;
        private bool _firing;
        internal bool IsFiring => _firing;

        // burner audio (clips ship in the bundle, unreferenced — resolved by name):
        // start tail on ignition, loop while firing, fade on release
        private AudioSource _audio;
        private AudioClip _sndStart, _sndLoop, _sndFade;

        public override Dictionary<Type, Player.ItemHandsController.OperationFactoryDelegate> GetOperationFactoryDelegates()
        {
            return new Dictionary<Type, Player.ItemHandsController.OperationFactoryDelegate>
            {
                { typeof(TorchDrawOp), new Player.ItemHandsController.OperationFactoryDelegate(method_20) },
                { typeof(TorchIdleOp), new Player.ItemHandsController.OperationFactoryDelegate(method_21) },
                { typeof(TorchHideOp), new Player.ItemHandsController.OperationFactoryDelegate(method_22) },
                { typeof(TorchInteractOp), new Player.ItemHandsController.OperationFactoryDelegate(method_23) },
            };
        }

        private Player.BaseAnimationOperationClass method_20() => new TorchDrawOp(this);
        private Player.BaseAnimationOperationClass method_21() => new TorchIdleOp(this);
        private Player.BaseAnimationOperationClass method_22() => new TorchHideOp(this);
        private Player.BaseAnimationOperationClass method_23() => new TorchInteractOp(this);

        public override void vmethod_1(Action callback)
        {
            InitiateOperation<TorchDrawOp>().Start(callback);
        }

        public override void vmethod_0(Player player, WeaponPrefab weaponPrefab)
        {
            base.vmethod_0(player, weaponPrefab);
            player.ProceduralWeaponAnimation.ManualSetVariables(2f, 0f, 0f, 0f);
            method_6();
            gclass2086_0.AfterGetFromPoolInit(player.ProceduralWeaponAnimation, null, player.IsYourPlayer);
            BaseSoundPlayer soundPlayer = _controllerObject.GetComponent<BaseSoundPlayer>();
            if (soundPlayer != null)
                soundPlayer.Init(this, player.PlayerBones.WeaponRoot, player);

            TorchAnimator = weaponPrefab.GetComponentInChildren<Animator>();
            if (TorchAnimator != null)
            {
                // pooled GO — the animator wakes wherever it stopped last session.
                // normalize to Off before the draw op flips Active.
                _firing = false;
                TorchAnimator.SetBool(FiringHash, false);
                TorchAnimator.Play("Off", HandsLayer, 0f);
                TorchAnimator.Update(0f);
            }
            else
            {
                Plugin.Log.LogWarning("[Blowtorch] no Animator found in torch prefab");
            }

            // vanilla left-hand gate: SetInteractInHands (doors/loot/pickup all route
            // through it) hard-requires FirearmsAnimator.IsIdling() — hash checks
            // against 'IDLE'/'IDLE WEAPON'/'IDLE PLANT' or FullIdleStateName. our
            // graph's layer-1 idle state is lowercase 'idle' so every check failed and
            // the left hand never fired ONCE. FullIdleStateName is the sanctioned
            // per-weapon override — point it at our state.
            var fa = FirearmsAnimator;
            if (fa != null)
            {
                fa.FullIdleStateName = "idle";
                Plugin.Log.LogWarning($"[Blowtorch] FirearmsAnimator bound — LActions layer={fa.LACTIONS_LAYER_INDEX}, FullIdleStateName=idle (left-hand gate open)");
            }
            else
                Plugin.Log.LogWarning("[Blowtorch] FirearmsAnimator NULL — vanilla left-hand path cannot run");

            BindAudio(weaponPrefab);

            // THE third-person pose fix: vanilla usable graphs fire a ThirdAction(1)
            // animation event that lands in the BODY animator's FirstAction int — the
            // only exit condition out of its 'idle_to_out' holster state. our renamed
            // states orphaned the event table, FirstAction kept the previous weapon's
            // value, and the body looped the lowered mid-swap pose forever.
            TranslateAnimatorParameter(1);

            StartCoroutine(PoseDiag(player));
        }

        // third-person pose diagnostic: the body animator's WeaponType drives the TP
        // arm-hold layer (EmptyHands = lowered arms, the reported mismatch). read back
        // what actually landed, one second after spawn.
        private IEnumerator PoseDiag(Player player)
        {
            yield return new WaitForSeconds(1.5f);
            try
            {
                var mc = player.MovementContext;
                // decompiler names dont exist at runtime — locate the PlayerAnimator
                // member by TYPE
                object pa = null;
                foreach (var p in mc.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    if (p.PropertyType == typeof(PlayerAnimator)) { pa = p.GetValue(mc); break; }
                if (pa == null)
                    foreach (var fld in mc.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        if (fld.FieldType == typeof(PlayerAnimator)) { pa = fld.GetValue(mc); break; }
                var f = pa?.GetType().GetMethod("GetWeaponTypeFloat")?.Invoke(pa, null);
                Plugin.Log.LogWarning($"[Blowtorch] pose diag: body WeaponTypeFloat={f ?? "?"} (0=Rifle 1=Pistol 4=EmptyHands), animType={player.GetWeaponAnimationType(this)}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Blowtorch] pose diag failed: {e.Message}");
            }
        }

        private void BindAudio(WeaponPrefab weaponPrefab)
        {
            // clips live in the bundle but nothing references them — the bundle being
            // loaded is what makes FindObjectsOfTypeAll see them (hackermod pattern)
            foreach (var clip in Resources.FindObjectsOfTypeAll<AudioClip>())
            {
                switch (clip.name)
                {
                    case "blow_torch_start_tail": _sndStart = clip; break;
                    case "blow_torch_loop": _sndLoop = clip; break;
                    case "blow_torch_loop_fade": _sndFade = clip; break;
                }
            }
            if (_sndLoop == null)
            {
                Plugin.Log.LogWarning("[Blowtorch] burner clips not found in bundle — torch runs silent");
                return;
            }

            // emit from the burner tip; pooled GO may still carry last session's source
            Transform mount = TransformHelperClass.FindTransformRecursive(weaponPrefab.transform, "fireport", false)
                              ?? weaponPrefab.transform;
            _audio = mount.GetComponent<AudioSource>() ?? mount.gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.loop = false;
            _audio.spatialBlend = 1f;
            _audio.minDistance = 1.5f;
            _audio.maxDistance = 30f;
            _audio.rolloffMode = AudioRolloffMode.Linear;
            _audio.Stop();
        }

        private void SetBurnerAudio(bool on)
        {
            if (_audio == null || _sndLoop == null) return;
            if (on)
            {
                if (_sndStart != null) _audio.PlayOneShot(_sndStart);
                _audio.clip = _sndLoop;
                _audio.loop = true;
                _audio.Play();
            }
            else
            {
                _audio.loop = false;
                _audio.Stop();
                if (_sndFade != null) _audio.PlayOneShot(_sndFade);
            }
        }

        public override void vmethod_2(EPlayerState previousstate, EPlayerState nextstate)
        {
        }

        // vanilla CanInteract requires FirearmsAnimator.IsIdling() — a hash check
        // against the stock 'IDLE' state name. our state is 'idle', so the check is
        // always false and looting/doors die while the torch is out. gate on our states.
        public override bool CanInteract()
        {
            if (TorchAnimator == null) return false;
            return CurrentHandsOperation is TorchIdleOp
                   && TorchAnimator.GetCurrentAnimatorStateInfo(HandsLayer).IsName("idle");
        }

        // no sights on a torch — swallow ADS before the base sets IsAiming (which
        // drives the pwa zoom + aim slowdown)
        public override void ToggleAim() { }
        public override void SetAim(bool value) { }

        // loot/doors/keys left-hand animation is fully vanilla-driven now: the graph
        // carries the UseLeftHand/LActionIndex params + donor LActions transitions,
        // and the LayerWeightStateController behaviours on the states fade the layer
        // weight themselves (authored 0 at rest) — base Loot/Pickup just work.

        // the user's actual Shoot binding (rebind-aware), not hardcoded mouse1. each
        // variant is a key combo — all keys of a variant held = pressed.
        private static List<List<KeyCode>> _fireCombos;

        private static void ResolveFireKeys()
        {
            _fireCombos = new List<List<KeyCode>>();
            try
            {
                foreach (var g in Singleton<SharedGameSettingsClass>.Instance.Control.Settings.UserKeyBindings.Value)
                {
                    if (g == null || g.keyName != EGameKey.Shoot || g.variants == null) continue;
                    foreach (var v in g.variants)
                        if (v != null && v.keyCode != null && v.keyCode.Count > 0)
                            _fireCombos.Add(new List<KeyCode>(v.keyCode));
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Blowtorch] Shoot binding lookup failed ({e.Message}) — falling back to Mouse0"); }
            if (_fireCombos.Count == 0) _fireCombos.Add(new List<KeyCode> { KeyCode.Mouse0 });
        }

        private static bool FirePressed()
        {
            if (_fireCombos == null) ResolveFireKeys();
            foreach (var combo in _fireCombos)
            {
                bool all = true;
                foreach (var k in combo)
                    if (!Input.GetKey(k)) { all = false; break; }
                if (all) return true;
            }
            return false;
        }

        // hold Shoot = blow the torch. graph handles idle -> torch_start -> fireloop
        // -> torch_end -> idle off the Firing bool; we only poll the binding.
        public override void ManualUpdate(float deltaTime)
        {
            base.ManualUpdate(deltaTime);
            if (TorchAnimator == null || _player == null || !_player.IsYourPlayer)
                return;
            // cursor-lock gate: EVERY menu overlay (esc, inventory, trader, our F9
            // tuner) unlocks the cursor — raw mouse polling fired the torch inside
            // all of them
            bool want = FirePressed()
                        && Cursor.lockState == CursorLockMode.Locked
                        && !IsInventoryOpen()
                        && CurrentHandsOperation is TorchIdleOp;
            if (want != _firing)
            {
                _firing = want;
                TorchAnimator.SetBool(FiringHash, want);
                // dont wait for the graph's transition windows (exit-time on the idle
                // loop reads as input lag) — the flame follows the trigger NOW
                TorchAnimator.CrossFade(want ? "torch_start" : "torch_end", 0.05f, HandsLayer);
                SetBurnerAudio(want);
            }
        }

        internal void ForceFiringOff()
        {
            if (_firing) SetBurnerAudio(false);
            _firing = false;
            if (TorchAnimator != null) TorchAnimator.SetBool(FiringHash, false);
        }

        // completion-by-polling: the ops call these instead of waiting for WeapIn/
        // WeapOut animation events the clips dont have. FastForward() is idempotent
        // (no-op once the op is Finished), so if BSG's event system ever DOES fire,
        // nothing double-completes.
        internal void PollDrawDone(Action done) => StartCoroutine(PollRoutine(
            info => info.IsName("idle") || info.IsName("torch_start") || info.IsName("torch_fireloop"),
            2.5f, done));

        internal void PollHolsterDone(Action done) => StartCoroutine(PollRoutine(
            info => info.IsName("Off") || (info.IsName("holster") && info.normalizedTime >= 0.95f),
            2.0f, done));

        private IEnumerator PollRoutine(Func<AnimatorStateInfo, bool> reached, float timeout, Action done)
        {
            float stop = Time.unscaledTime + timeout;
            while (Time.unscaledTime < stop)
            {
                if (TorchAnimator == null) break;
                if (reached(TorchAnimator.GetCurrentAnimatorStateInfo(HandsLayer))) break;
                yield return null;
            }
            done();
        }

        // ---- operations: vanilla bases drive Active; we supply completion ----

        public class TorchDrawOp : Player.UsableItemController.Class1305
        {
            public TorchDrawOp(BlowtorchController controller) : base(controller) { }

            public new void Start(Action callback)
            {
                base.Start(callback); // SetActiveParam(true) -> Off -> draw; RELOAD float -> 1
                var c = (BlowtorchController)UsableItemController_0;
                // dont sit in Off waiting for the transition window (exit-time reads
                // as equip lag) — the draw starts the moment the prefab is up
                if (c.TorchAnimator != null)
                    c.TorchAnimator.CrossFade("draw", 0.02f, HandsLayer, 0f);
                c.PollDrawDone(() =>
                {
                    try { FastForward(); }
                    catch (Exception e) { Plugin.Log.LogError($"[Blowtorch] draw completion threw: {e}"); }
                    // the body's mid-swap overlay (RELOAD float=1, arms lowered) is
                    // normally reset inside WeaponAppeared AFTER SetupProp — if that
                    // chain throws, third person stays in the lowered swap pose
                    // forever (the reported raise-then-snap-down). force the reset.
                    try { c._player.BodyAnimatorCommon.SetFloat(PlayerAnimator.RELOAD_FLOAT_PARAM_HASH, 0f); } catch { }
                });
            }

            public override void vmethod_0()
            {
                TorchIdleOp idle = UsableItemController_0.InitiateOperation<TorchIdleOp>();
                idle.Start();
                Action_1();
                if (Action_0 != null)
                    idle.HideWeapon(Action_0, Bool_0);
            }
        }

        public class TorchIdleOp : Player.UsableItemController.Class1299
        {
            public TorchIdleOp(BlowtorchController controller) : base(controller) { }

            public override void HideWeapon(Action onHidden, bool fastDrop)
            {
                State = Player.EOperationState.Finished;
                UsableItemController_0.InitiateOperation<TorchHideOp>().Start(onHidden, fastDrop);
            }

            public override void vmethod_0(GInterface443 oneItemOperation, Callback callback)
            {
                UsableItemController_0.InitiateOperation<TorchInteractOp>().Start(oneItemOperation.Item1, callback);
            }
        }

        public class TorchHideOp : Player.UsableItemController.Class1302
        {
            public TorchHideOp(BlowtorchController controller) : base(controller) { }

            public override void Start(Action onHidden, bool fastDrop)
            {
                var c = (BlowtorchController)UsableItemController_0;
                c.ForceFiringOff(); // never holster with the burner lit
                base.Start(onHidden, fastDrop); // SetActiveParam(false) -> idle -> holster
                // the graph only reaches holster FROM idle — holstering mid-burn
                // strands the animator in a torch state until the poll timeout
                // force-swaps with no visible put-away. jump straight to holster.
                if (c.TorchAnimator != null)
                    c.TorchAnimator.CrossFade("holster", 0.05f, HandsLayer);
                // body-side mirror of the draw fix: FirstAction==2 is the ONLY way the
                // body enters its idle_to_out lowering anim — vanilla graphs fire it as
                // a ThirdAction(2) event from the put-away clip; ours has no events
                c.TranslateAnimatorParameter(2);
                c.PollHolsterDone(FastForward);
            }
        }

        public class TorchInteractOp : Player.UsableItemController.Class1293
        {
            public TorchInteractOp(BlowtorchController controller) : base(controller) { }

            public override void vmethod_0()
            {
                UsableItemController_0.InitiateOperation<TorchIdleOp>().Start();
            }
        }
    }
}
