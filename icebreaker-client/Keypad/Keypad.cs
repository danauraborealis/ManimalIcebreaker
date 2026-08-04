using System;
using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;

namespace Manimal.Icebreaker.Keypad
{
    // marker MonoBehaviour on each live terminal panel. inherits
    // InteractableObject so EFT's player-aim raycast resolves the panel as an
    // interactive target — that's what lets the action patch pick it up in
    // GetActionsClass.GetAvailableActions. holds the per-instance state the
    // action handler + UI session read: the bound door, the unlock code, the
    // grafted audio sources, and the active-session/unlocked flags.
    internal sealed class Keypad : InteractableObject
    {
        // settable so the session can refresh the reference at unlock time if
        // the Configure-time value goes stale
        public Door BoundDoor { get; set; }
        public string UnlockCode { get; private set; }

        public AudioSource AudioKeypress { get; private set; }
        public AudioSource AudioDenied   { get; private set; }
        public AudioSource AudioGranted  { get; private set; }

        // non-null while the keypad UI is open — prevents stacking sessions
        // when the player spams the action
        public KeypadSession ActiveSession { get; set; }

        // set true once the code has been entered this raid. the action patch
        // uses this (NOT door.DoorState) to grey out the action — BSG can have
        // a door in non-Locked state but still gate Open behind other flags,
        // so inferring from DoorState gives false positives.
        public bool Unlocked { get; set; }

        // same-code-group peers (driver fills this). on success, peers within
        // LinkedDisableRange also unlock — the twin panel on the other side of
        // the same door is spent, but a same-code panel across the room isnt.
        public readonly List<Keypad> LinkedKeypads = new List<Keypad>();

        // ---- fika sync (codes match via the shared raid seed; the UNLOCK is a
        // direct DoorState write, which fika never replicates — same class of
        // write as the sealed doors, same event/ApplyRemote pattern) ----

        // raised on LOCAL commits only, with the bound door's id — the fika
        // addon ships it as an IceWorldPacket. remote applies are suppressed so
        // the packet never echoes.
        internal static event Action<string> UnlockCommitted;
        private static bool _applyingRemote;

        // the one unlock path both local success and remote apply run: flip the
        // door Locked->Shut, spend in-range twins, then announce (local only).
        // door-less terminals cant sync (no stable id) — their Unlocked flag
        // stays per-peer, which only affects the greyed-out action label.
        internal static void CommitUnlock(Keypad keypad)
        {
            keypad.Unlocked = true;
            var door = keypad.BoundDoor;
            if (door != null && door.DoorState == EDoorState.Locked)
                door.DoorState = EDoorState.Shut;

            float rangeSq = KeypadConstants.LinkedDisableRange * KeypadConstants.LinkedDisableRange;
            foreach (var twin in keypad.LinkedKeypads)
            {
                if (twin == null || twin.Unlocked) continue;
                if ((twin.transform.position - keypad.transform.position).sqrMagnitude > rangeSq) continue;
                twin.Unlocked = true;
                if (twin.BoundDoor != null && twin.BoundDoor.DoorState == EDoorState.Locked)
                    twin.BoundDoor.DoorState = EDoorState.Shut;
                Plugin.Log?.LogInfo($"[Keypad] linked terminal '{twin.name}' disabled (within {KeypadConstants.LinkedDisableRange:F0}m).");
            }

            if (!_applyingRemote && door != null)
                UnlockCommitted?.Invoke(door.Id);
        }

        internal static void ApplyRemoteUnlock(string doorId)
        {
            if (string.IsNullOrEmpty(doorId)) return;
            _applyingRemote = true;
            try
            {
                foreach (var k in UnityEngine.Object.FindObjectsOfType<Keypad>())
                {
                    if (k == null || k.Unlocked || k.BoundDoor == null || k.BoundDoor.Id != doorId) continue;
                    CommitUnlock(k);
                    Plugin.Log?.LogInfo($"[Keypad] remote unlock applied for door '{doorId}'.");
                }
            }
            finally { _applyingRemote = false; }
        }

        public void Configure(Door boundDoor, string unlockCode)
        {
            BoundDoor  = boundDoor;
            UnlockCode = unlockCode;

            // resolve audio children once and cache — the driver grafts them
            // from the keypad prefab onto this panel before Configure runs
            AudioKeypress = FindChildAudio(KeypadConstants.AudioKeypressName);
            AudioDenied   = FindChildAudio(KeypadConstants.AudioDeniedName);
            AudioGranted  = FindChildAudio(KeypadConstants.AudioGrantedName);

            if (AudioKeypress == null || AudioDenied == null || AudioGranted == null)
            {
                Plugin.Log?.LogWarning(
                    "[Keypad] one or more audio source children missing — " +
                    $"keypress={AudioKeypress != null} denied={AudioDenied != null} granted={AudioGranted != null}. " +
                    "UI still works but cues are silent.");
            }
        }

        private AudioSource FindChildAudio(string childName)
        {
            var child = transform.Find(childName);
            if (child == null) return null;
            return child.GetComponent<AudioSource>();
        }
    }
}
