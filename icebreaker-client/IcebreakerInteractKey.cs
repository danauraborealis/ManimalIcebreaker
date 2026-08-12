using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InputSystem;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // THE PLAYER'S REAL INTERACT KEY (field report 08-12: "if a player rebinds their
    // interaction key from F, unsealing doors doesnt work"). both hold sessions — the
    // sealed-door seal/unseal and the chain-door charge plant — polled Input.GetKey(F)
    // directly, so anyone who rebinds Interact could start a hold (the prompt is the
    // game's own) but the very next frame saw F unheld and cancelled it. from the
    // player's side: the prompt appears, nothing ever happens, no notifier, ten attempts.
    //
    // resolved from ControlSettingsClass.UserKeyBindings, which is the same table the
    // settings screen writes: find the KeyGroup for EGameKey.Interact and take the key
    // codes off its variants. re-read whenever the binding changes so a mid-session
    // rebind is picked up, and fall back to F if the lookup ever drifts.
    internal static class IcebreakerInteractKey
    {
        private static readonly List<KeyCode> _keys = new List<KeyCode>();
        private static float _nextRefresh;
        private static bool _loggedOnce;

        // held-down test across every key bound to Interact (a binding can carry more
        // than one variant, e.g. a keyboard key and a mouse button)
        internal static bool Held()
        {
            Refresh();
            for (int i = 0; i < _keys.Count; i++)
                if (Input.GetKey(_keys[i])) return true;
            return false;
        }

        internal static string Name()
        {
            Refresh();
            return _keys.Count > 0 ? _keys[0].ToString() : "F";
        }

        private static void Refresh()
        {
            if (Time.unscaledTime < _nextRefresh && _keys.Count > 0) return;
            _nextRefresh = Time.unscaledTime + 5f; // cheap, and catches a mid-raid rebind
            try
            {
                var bindings = Singleton<SharedGameSettingsClass>.Instance?.Control?.Settings?.UserKeyBindings?.Value;
                if (bindings != null)
                {
                    foreach (var group in bindings)
                    {
                        if (group == null || group.keyName != EGameKey.Interact || group.variants == null) continue;
                        _keys.Clear();
                        foreach (var v in group.variants)
                        {
                            if (v?.keyCode == null) continue;
                            foreach (var kc in v.keyCode)
                                if (kc != KeyCode.None && !_keys.Contains(kc)) _keys.Add(kc);
                        }
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                if (!_loggedOnce) { _loggedOnce = true; Plugin.Log.LogWarning($"[InteractKey] lookup failed, falling back to F: {e.Message}"); }
            }
            // never leave the sessions unable to read ANY key — an empty list would make
            // every hold cancel instantly, which is the bug we're fixing
            if (_keys.Count == 0) _keys.Add(KeyCode.F);
        }
    }

    // MOVEMENT LOCK FOR HOLD SESSIONS (user call 08-12): sealing and unsealing are
    // deliberate, animated actions — the player should be planted while they happen, not
    // strafing around the door mid-hold. Player.Move is the single funnel every WASD
    // input reaches, so zeroing the direction there stops movement without touching the
    // animator state machine (returning false instead left the run animation playing).
    // the flag is owned by the sessions and released in their OnDestroy, which already
    // guarantees teardown for BlockFirearms on the same path.
    internal static class IcebreakerHoldLock
    {
        private static int _holds;

        internal static bool Active => _holds > 0;

        internal static void Acquire() => _holds++;

        internal static void Release()
        {
            _holds--;
            if (_holds < 0) _holds = 0; // never latch movement off because of a double release
        }

        internal static void ResetForRaid() => _holds = 0;

        [HarmonyPatch(typeof(Player), nameof(Player.Move))]
        internal static class Patch_FreezeDuringHold
        {
            [HarmonyPrefix]
            private static void Prefix(Player __instance, ref Vector2 direction)
            {
                if (_holds > 0 && __instance != null && __instance.IsYourPlayer)
                    direction = Vector2.zero;
            }
        }
    }
}
