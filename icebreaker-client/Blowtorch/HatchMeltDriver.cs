using System.Collections;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker.Blowtorch
{
    // interactive ice-melt on the frozen exterior hatchway. retail drives this prop
    // through a Handler* trigger graph (extracted from level704, postfix _722179887)
    // that was stripped on import — we reproduce it faithfully, with ONE design
    // change: the melt step is the player burning the ice with the usable blowtorch
    // (anim runs only under the flame) instead of a key-use on the MeltLogic switch.
    //
    // retail graph:
    //   Rezak_01:  IsRezak -> Melt anim; activate VFX/FIre
    //   +5.0s:     unlock Handler_01; hide FakeTrunk_01
    //   Open_01:   IsOpen_01 -> Break; hide Snow_pile_19_melt_LOD0
    //   +1.62s:    hide Snow_pile_19_LOD0; show Snow_pile_19_fragment + Snow + StateSnow
    //   +4.0s:     unlock rotate switch; hide FakeTrunk_02
    //   Open_02/3: IsOpen_02 -> Open anim; activate Open_hatch_VFX
    public class HatchMeltDriver : MonoBehaviour
    {
        private const string HatchRootName = "INTERACTIVE_Icebreaker_exterior_hatchway_door_frozen";
        private const float BurnRange = 2.4f;
        private const float BurnRayTolerance = 0.45f;
        // melt-anim playback rate while burning — the clip is ~5s at speed 1, so 0.5
        // makes the full melt ~10s of sustained flame (user call: more than a couple
        // seconds of actual torch work)
        private const float MeltSpeed = 0.5f;

        internal static HatchMeltDriver Instance;

        private Animator _anim;
        private Transform _root;
        private Collider _meltCollider;
        internal EFT.Interactive.Switch MeltSwitch;
        internal EFT.Interactive.Switch HandleSwitch;
        internal EFT.Interactive.Switch RotateSwitch;
        private bool _started;
        internal bool MeltDone;
        internal bool HandleTurned;
        internal bool RotateReady;
        internal bool HatchOpened;
        private readonly List<Material> _meltMats = new List<Material>();
        private readonly List<Transform> _meltTransforms = new List<Transform>();
        private Vector3 _meltBaseScale = Vector3.one;

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        private static class Patch_SpawnDriver
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                new GameObject("Icebreaker_HatchMelt").AddComponent<HatchMeltDriver>();
            }
        }

        private IEnumerator Start()
        {
            Instance = this;
            for (int i = 0; i < 20 && _anim == null; i++)
            {
                var root = GameObject.Find(HatchRootName);
                if (root != null)
                {
                    _root = root.transform;
                    _anim = root.GetComponentInChildren<Animator>(true);
                    foreach (var sw in root.GetComponentsInChildren<EFT.Interactive.Switch>(true))
                    {
                        if (sw.name == "MeltLogic") { MeltSwitch = sw; _meltCollider = sw.GetComponent<Collider>(); }
                        else if (sw.name == "HandleLogic") HandleSwitch = sw;
                        else if (sw.name == "Icebreaker_exterior_hatchway_rotate") RotateSwitch = sw;
                    }
                }
                if (_anim == null) yield return new WaitForSeconds(1f);
            }
            if (_anim == null || _meltCollider == null || HandleSwitch == null)
            {
                if (_anim != null)
                    Plugin.Log.LogWarning("[HatchMelt] hatch found but MeltLogic/HandleLogic missing — driver off");
                Destroy(gameObject);
                yield break;
            }
            if (RotateSwitch == null)
                Plugin.Log.LogWarning("[HatchMelt] rotate switch not found — stage 2 (open hatch) will be missing");

            // the melt visual is a _Cutoff dissolve gated behind the USE_CUTOFF shader
            // keyword — assetripper demoted it to "invalid" on import, so the built
            // material ships with the dissolve branch off. force the keyword AND drive
            // _Cutoff ourselves from melt progress: the imported clip's material curve
            // proved unreliable, and this way the dissolve tracks the burn exactly.
            // the rebuilt scene bundle embeds the SDK's assetripper-reconstructed
            // shader, whose USE_CUTOFF dissolve variant didnt survive — the animated
            // _Cutoff is visually dead on it. try rebinding to the GAME's real shader
            // instance (loaded from the shaders bundle) before enabling the keyword.
            var realShader = Shader.Find("p0/Reflective/Bumped Specular SMap");
            foreach (var r in _root.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.name.Contains("_melt_")) continue;
                foreach (var m in r.materials)
                    if (m != null)
                    {
                        if (realShader != null && m.shader != realShader)
                        {
                            m.shader = realShader;
                            Plugin.Log.LogWarning($"[HatchMelt] melt material rebound to game shader instance");
                        }
                        m.EnableKeyword("USE_CUTOFF");
                        m.SetFloat("_Cutoff", 0f);
                        _meltMats.Add(m);
                    }
                if (!_meltTransforms.Contains(r.transform))
                {
                    _meltTransforms.Add(r.transform);
                    _meltBaseScale = r.transform.localScale;
                }
            }

            // retail's _resetStateOnAwake handlers normalize the start state each raid —
            // our scene ships whatever the SDK saved, so do the same normalization.
            // CAREFUL: two GOs are both named Snow_pile_19_fragment — the container
            // parent (whose children include LOD0 + the melt ice) and the chunk mesh.
            // only the MESH may be toggled; deactivating the parent hides the whole pile.
            SetActiveAll(false, "FIre", "Snow", "StateSnow", "Open_hatch_VFX");
            SetActiveMeshesOnly(false, "Snow_pile_19_fragment");
            SetActiveAll(true, "Snow_pile_19_melt_LOD0", "Snow_pile_19_LOD0", "FakeTrunk_01", "FakeTrunk_02");

            Plugin.Log.LogWarning("[HatchMelt] armed — melt the hatchway ice with the blowtorch");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (_anim == null || MeltDone) return;

            bool burning = IsBurningIce();

            if (!_started)
            {
                if (!burning) return;
                _started = true;
                _anim.SetBool("IsRezak", true);          // Rezak_01: Still -> Melt
                SetActiveAll(true, "FIre");              // the burn vfx on the ice
                Plugin.Log.LogWarning("[HatchMelt] melting started");
            }

            var info = _anim.GetCurrentAnimatorStateInfo(0);
            if (info.IsName("Melt"))
            {
                // the mechanic: melt only advances under the flame; the handle-ice
                // dissolve tracks the clip directly. the shader-cutoff dissolve is
                // dead (imported _MainTex lost the alpha ramp the clip tests against),
                // so the VISIBLE melt is a shrink — receding ice — while _Cutoff keeps
                // being driven in case the texture ever gets fixed.
                _anim.speed = burning ? MeltSpeed : 0f;
                float cut = Mathf.Clamp01(info.normalizedTime);
                foreach (var m in _meltMats) m.SetFloat("_Cutoff", cut);
                // quadratic ease-in: barely recedes early, melts away toward the end
                foreach (var t in _meltTransforms)
                    t.localScale = _meltBaseScale * Mathf.Lerp(1f, 0.12f, cut * cut);
                if (info.normalizedTime < 0.98f) return;
            }
            else if (info.IsName("Still"))
            {
                return; // transition into Melt still pending
            }

            // melt clip done — retail's Unlock_01 beat. hide the (fully dissolved)
            // handle ice outright too, in case the shader variant ignores _Cutoff
            _anim.speed = 1f;
            MeltDone = true;
            foreach (var m in _meltMats) m.SetFloat("_Cutoff", 1f);
            SetActiveAll(false, "FIre", "FakeTrunk_01", "Snow_pile_19_melt_LOD0");
            // the 'Melt the ice' hint is a cached prompt — if the player is aiming at the
            // switch as the melt lands, it lingers until the aim moves. clear it now.
            try
            {
                var pl = Comfort.Common.Singleton<EFT.GameWorld>.Instance?.MainPlayer;
                var own = pl != null ? pl.GetComponent<EFT.GamePlayerOwner>() : null;
                own?.ClearInteractionState();
            }
            catch { }
            Plugin.Log.LogWarning("[HatchMelt] ice melted — the handle can be turned now");
        }

        internal void TurnHandle()
        {
            if (!MeltDone || HandleTurned) return;
            HandleTurned = true;
            _anim.SetBool("IsOpen_01", true);                    // Open_01: Melt -> Break
            SetActiveAll(false, "Snow_pile_19_melt_LOD0");
            PlayFoley("frozen_door_open_1");
            StartCoroutine(BreakBeats());
            Plugin.Log.LogWarning("[HatchMelt] handle turned — Break");
        }

        private IEnumerator BreakBeats()
        {
            // retail Start_open_01 at +1.62s: the solid pile shatters into fragments
            // with the snow burst; StateSnow loops from here on
            yield return new WaitForSeconds(1.62f);
            SetActiveAll(false, "Snow_pile_19_LOD0");
            SetActiveMeshesOnly(true, "Snow_pile_19_fragment");
            SetActiveAll(true, "Snow", "StateSnow");
            PlayParticles("Snow");
            PlayParticles("StateSnow");
            // retail Unlock_02 at +4.0s: the hatch itself becomes usable
            yield return new WaitForSeconds(4.0f - 1.62f);
            SetActiveAll(false, "FakeTrunk_02");
            RotateReady = true;
            Plugin.Log.LogWarning("[HatchMelt] hatch can be opened now");
        }

        internal void OpenHatch()
        {
            if (!RotateReady || HatchOpened) return;
            HatchOpened = true;
            SetActiveAll(false, "Snow_pile_19_melt_LOD0");       // retail Open_02 ensure
            _anim.SetBool("IsOpen_02", true);                    // Open_03: Break -> Open
            SetActiveAll(true, "Open_hatch_VFX");
            PlayParticles("Open_hatch_VFX");
            PlayFoley("frozen_door_open_2");
            Plugin.Log.LogWarning("[HatchMelt] hatch opening");
            Destroy(gameObject, 10f); // sequence complete
        }

        // toggle EVERY matching descendant — retail lists Snow_pile_19_fragment twice
        // (parent shell + mesh child share the name)
        private void SetActiveAll(bool state, params string[] names)
        {
            var wanted = new HashSet<string>(names);
            int hit = 0;
            foreach (var tr in _root.GetComponentsInChildren<Transform>(true))
                if (wanted.Contains(tr.name))
                {
                    tr.gameObject.SetActive(state);
                    hit++;
                }
            if (hit == 0)
                Plugin.Log.LogWarning($"[HatchMelt] no GOs matched [{string.Join(", ", names)}]");
        }

        // toggle only same-named GOs that carry a renderer — leaves same-named
        // container parents alone
        private void SetActiveMeshesOnly(bool state, string name)
        {
            int hit = 0;
            foreach (var tr in _root.GetComponentsInChildren<Transform>(true))
                if (tr.name == name && tr.GetComponent<Renderer>() != null)
                {
                    tr.gameObject.SetActive(state);
                    if (state) // showing a mesh needs its (possibly inactive) parents too
                        for (var p = tr.parent; p != null && p != _root; p = p.parent)
                            if (!p.gameObject.activeSelf) p.gameObject.SetActive(true);
                    hit++;
                }
            if (hit == 0)
                Plugin.Log.LogWarning($"[HatchMelt] no mesh GOs matched '{name}'");
        }

        private void PlayParticles(string name)
        {
            foreach (var tr in _root.GetComponentsInChildren<Transform>(true))
                if (tr.name == name)
                    foreach (var ps in tr.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        ps.gameObject.SetActive(true);
                        ps.Play(true);
                    }
        }

        private void PlayFoley(string clipName)
        {
            AudioClip clip = null;
            foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>())
                if (c != null && c.name == clipName) { clip = c; break; }
            if (clip == null) return;
            var go = new GameObject("Icebreaker_HatchSnd");
            go.transform.position = _meltCollider.bounds.center;
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.spatialBlend = 1f;
            src.maxDistance = 40f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.Play();
            Destroy(go, clip.length + 0.5f);
        }

        private bool IsBurningIce()
        {
            var player = Singleton<GameWorld>.Instance?.MainPlayer;
            var torch = player?.HandsController as BlowtorchController;
            if (torch == null || !torch.IsFiring) return false;

            var cam = Camera.main;
            if (cam == null) return false;
            Vector3 origin = cam.transform.position;
            Vector3 dir = cam.transform.forward;

            Vector3 target = _meltCollider.bounds.center;
            if ((target - origin).sqrMagnitude > BurnRange * BurnRange) return false;
            Vector3 toT = target - origin;
            float along = Vector3.Dot(toT, dir);
            if (along < 0f) return false;
            Vector3 closestOnRay = origin + dir * along;
            return (_meltCollider.ClosestPoint(closestOnRay) - closestOnRay).sqrMagnitude
                   < BurnRayTolerance * BurnRayTolerance;
        }
    }

    // own the hatch switches' action menus (chain-door pattern): the Handler* stack
    // that translated switch use into trigger events is gone, so vanilla interaction
    // would flip switch state and do nothing. unavailable stages are HIDDEN entirely
    // (user call 07-17), not greyed — only the melt hint shows before its time.
    [HarmonyPatch(typeof(GetActionsClass), "smethod_11")]
    internal static class Patch_HatchSwitchActions
    {
        private static void Replace(ref ActionsReturnClass result, ActionsTypesClass act)
        {
            if (result == null) result = new ActionsReturnClass { Actions = new List<ActionsTypesClass> { act } };
            else { result.Actions.Clear(); result.Actions.Add(act); }
        }

        private static void Clear(ref ActionsReturnClass result)
        {
            if (result != null) result.Actions.Clear();
            result = null;
        }

        [HarmonyPostfix]
        private static void Postfix(ref ActionsReturnClass __result, GamePlayerOwner owner, EFT.Interactive.Switch interactiveSwitch)
        {
            var d = HatchMeltDriver.Instance;
            if (d == null || interactiveSwitch == null) return;
            try
            {
                if (interactiveSwitch == d.MeltSwitch)
                {
                    if (d.MeltDone) { Clear(ref __result); return; }
                    Replace(ref __result, new ActionsTypesClass
                    {
                        Name = "Melt the ice (blowtorch)",
                        Disabled = true, // informational — the melt happens by burning it
                    });
                }
                else if (interactiveSwitch == d.HandleSwitch)
                {
                    if (!d.MeltDone || d.HandleTurned) { Clear(ref __result); return; }
                    Replace(ref __result, new ActionsTypesClass
                    {
                        Name = "Turn handle",
                        // retire the cached prompt the moment the action runs — it lingered
                        // until the aim left the collider otherwise
                        Action = () => { d.TurnHandle(); try { owner?.ClearInteractionState(); } catch { } },
                    });
                }
                else if (interactiveSwitch == d.RotateSwitch)
                {
                    if (!d.RotateReady || d.HatchOpened) { Clear(ref __result); return; }
                    Replace(ref __result, new ActionsTypesClass
                    {
                        Name = "Open hatch",
                        Action = () => { d.OpenHatch(); try { owner?.ClearInteractionState(); } catch { } },
                    });
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[HatchMelt] actions patch threw: {e.Message}"); }
        }
    }
}
