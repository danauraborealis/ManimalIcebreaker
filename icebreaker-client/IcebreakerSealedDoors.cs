using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // SEALED DOORS: doors authored with DoorState = 64 ("Sealed" — an editor-only enum
    // value the runtime doesn't know) act like locked doors that need no key: hold the
    // interact for 5s to unseal (weapon lowered, vanilla countdown panel), after which
    // they're normal doors. a registry door that's Shut offers "Reseal" (same 5s hold).
    // ONLY doors the mapper flagged are ever seal-capable — the registry is built from
    // the authored value, everything else on the map is untouched. sealed doors also
    // carve the navmesh shut so bots route around them until unsealed.
    internal static class IcebreakerSealedDoors
    {
        private const int SealedStateValue = 64;
        private const float HoldSeconds = 5f;

        // registry: seal-capable doors + their nav link (null if the door has none)
        private static readonly Dictionary<Door, NavMeshDoorLink> Registry = new Dictionary<Door, NavMeshDoorLink>();

        // called after doors + links are live (BotsController.Init postfix)
        internal static void Setup()
        {
            Registry.Clear();
            var links = UnityEngine.Object.FindObjectsOfType<NavMeshDoorLink>();
            int n = 0;
            foreach (var door in UnityEngine.Object.FindObjectsOfType<Door>(true))
            {
                if ((int)door.DoorState != SealedStateValue) continue;
                NavMeshDoorLink myLink = null;
                foreach (var l in links)
                    if (l != null && (l.Door == door || (!string.IsNullOrEmpty(l.DoorId) && l.DoorId == door.Id)))
                    { myLink = l; break; }
                Registry[door] = myLink;
                ApplySealed(door, myLink, true);
                n++;
            }
            if (n > 0) Plugin.Log.LogDebug($"[Sealed] {n} sealed door(s) registered (hold {HoldSeconds:0}s to unseal)");
        }

        private static void ApplySealed(Door door, NavMeshDoorLink link, bool sealedNow)
        {
            // Locked-without-a-key = the vanilla-visible face of "sealed": no open action,
            // no unlock-with-key action (KeyId may be empty), breach still possible if the
            // door allows it. the runtime never sees the authoring value 64.
            door.DoorState = sealedNow ? EDoorState.Locked : EDoorState.Shut;
            if (link != null && link.Carver_Closed != null)
            {
                // bots: sealed door = wall. carve the navmesh shut so they route around.
                link.Carver_Closed.carving = sealedNow;
                link.Carver_Closed.enabled = sealedNow;
            }
        }

        internal static bool IsSealed(Door door) => Registry.TryGetValue(door, out _) && door.DoorState == EDoorState.Locked;
        internal static bool CanReseal(Door door) => Registry.TryGetValue(door, out _) && door.DoorState == EDoorState.Shut;

        internal static void StartSession(GamePlayerOwner owner, Door door, bool sealing)
        {
            var go = new GameObject("Icebreaker_SealSession");
            var s = go.AddComponent<SealSession>();
            s.Owner = owner;
            s.Door = door;
            s.Sealing = sealing;
        }

        // world-event hook for the fika sync addon — same contract as the chain door's:
        // raised on LOCAL commits only, remote applications are suppressed so a received
        // event never echoes back out. fika does NOT replicate these changes itself
        // (they're direct DoorState writes, not the player-interaction path it syncs).
        internal static event Action<string, bool> SealEvent; // doorId, sealing
        private static bool _remoteApply;

        internal static void ApplyRemote(string doorId, bool sealing)
        {
            _remoteApply = true;
            try
            {
                foreach (var kv in Registry)
                    if (kv.Key != null && kv.Key.Id == doorId) { Complete(kv.Key, sealing); return; }
                Plugin.Log.LogWarning($"[Sealed] remote seal event for unknown door '{doorId}'");
            }
            finally { _remoteApply = false; }
        }

        internal static void Complete(Door door, bool sealing)
        {
            if (!Registry.TryGetValue(door, out var link)) return;
            ApplySealed(door, link, sealing);
            Plugin.Log.LogInfo($"[Sealed] '{door.Id}' {(sealing ? "RESEALED" : "unsealed")}");
            if (!_remoteApply)
            {
                try { SealEvent?.Invoke(door.Id, sealing); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Sealed] world-event hook failed: {e.Message}"); }
            }
        }

        // hold-to-complete session — the BoatInteractionSession pattern: vanilla
        // objectives-panel countdown, weapon lowered via BlockFirearms, strict hold
        // (release/Escape/walk-away cancels), idempotent restore in OnDestroy.
        // retail seal foley (shipped via the Author 1R carrier in the bundle): start clip
        // on session begin, loop underneath for the duration, end clip only on success
        private static AudioClip _sndStart, _sndLoop, _sndEnd;
        private static bool _sndSearched;

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
            _sndStart = FindClip("IB_metal_door_seal_start");
            _sndLoop = FindClip("IB_metal_door_seal_loop");
            _sndEnd = FindClip("IB_metal_door_seal_end");
            if (_sndStart == null || _sndLoop == null || _sndEnd == null)
                Plugin.Log.LogWarning($"[Sealed] seal foley incomplete (start={_sndStart != null} loop={_sndLoop != null} end={_sndEnd != null}) — carrier missing from bundle?");
        }

        internal class SealSession : MonoBehaviour
        {
            public GamePlayerOwner Owner;
            public Door Door;
            public bool Sealing;

            private float _start;
            private bool _started, _ending, _handsLocked, _panelShown;
            private AudioSource _oneShot, _loop;

            private void Update()
            {
                try
                {
                    if (_ending) return;
                    var player = Singleton<GameWorld>.Instance?.MainPlayer;
                    if (player == null || Door == null) { End(false); return; }

                    if (!_started)
                    {
                        _started = true;
                        _start = Time.time;
                        if (Owner != null)
                        {
                            Owner.ShowObjectivesPanel((Sealing ? "Sealing Door" : "Unsealing Door") + " {0:F1}", HoldSeconds);
                            _panelShown = true;
                        }
                        var mc = player.MovementContext;
                        if (mc != null) { mc.BlockFirearms = true; _handsLocked = true; }

                        // foley: start clip immediately, loop takes over underneath it
                        EnsureClips();
                        transform.position = Door.transform.position;
                        _oneShot = gameObject.AddComponent<AudioSource>();
                        _oneShot.spatialBlend = 1f;
                        _oneShot.maxDistance = 25f;
                        if (_sndStart != null) _oneShot.PlayOneShot(_sndStart);
                        if (_sndLoop != null)
                        {
                            _loop = gameObject.AddComponent<AudioSource>();
                            _loop.spatialBlend = 1f;
                            _loop.maxDistance = 25f;
                            _loop.clip = _sndLoop;
                            _loop.loop = true;
                            if (_sndStart != null) _loop.PlayDelayed(Mathf.Min(_sndStart.length, 1.5f));
                            else _loop.Play();
                        }
                    }

                    if (Input.GetKeyDown(KeyCode.Escape)) { End(false); return; }
                    if ((player.Position - Door.transform.position).sqrMagnitude > 25f) { End(false); return; }
                    if (!Input.GetKey(KeyCode.F)) { End(false); return; }
                    if (Time.time - _start >= HoldSeconds) { End(true); return; }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Sealed] session threw: {e.Message}");
                    End(false);
                }
            }

            private void End(bool success)
            {
                if (_ending) return;
                _ending = true;
                // finally guarantees teardown — a throw in Complete() would otherwise skip
                // Destroy, OnDestroy never runs, and BlockFirearms stays stuck (same bug
                // class as the plant session's flashing-charge incident)
                try
                {
                    if (_loop != null) _loop.Stop();
                    if (success)
                    {
                        Complete(Door, Sealing);
                        // end clip outlives the session GO — one-shot on a throwaway host
                        if (_sndEnd != null)
                            AudioSource.PlayClipAtPoint(_sndEnd, Door != null ? Door.transform.position : transform.position);
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Sealed] completion threw: {e}"); }
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
    }

    // append "Unseal Door" / "Reseal Door" to the vanilla door action menu. smethod_14
    // is the builder for plain doors — the sealed face (Locked, no key) lands here.
    [HarmonyPatch(typeof(GetActionsClass), "smethod_14")]
    internal static class Patch_SealedDoorActions
    {
        private static void Postfix(ActionsReturnClass __result, GamePlayerOwner owner, Door door)
        {
            if (__result == null || door == null) return;
            try
            {
                if (IcebreakerSealedDoors.IsSealed(door))
                    __result.Actions.Add(new ActionsTypesClass
                    {
                        Name = "Unseal Door",
                        Action = () => IcebreakerSealedDoors.StartSession(owner, door, false),
                    });
                else if (IcebreakerSealedDoors.CanReseal(door))
                    __result.Actions.Add(new ActionsTypesClass
                    {
                        Name = "Reseal Door",
                        Action = () => IcebreakerSealedDoors.StartSession(owner, door, true),
                    });
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sealed] actions patch threw: {e.Message}"); }
        }
    }
}
