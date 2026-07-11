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
