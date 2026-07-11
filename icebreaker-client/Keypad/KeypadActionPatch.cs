using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker.Keypad
{
    // prefix on GetActionsClass.GetAvailableActions(GamePlayerOwner, object) —
    // when the player aims at a Keypad, replace the vanilla (empty) result
    // with a single "Enter code" action. all other interactive types fall
    // through via return true, so the chain-door smethod_11 postfix is
    // unaffected. target resolved reflectively: GetAvailableActions has 2
    // overloads and only the (GamePlayerOwner, object) dispatcher is ours.
    [HarmonyPatch]
    internal static class Patch_KeypadActions
    {
        private static MethodBase TargetMethod()
        {
            return typeof(GetActionsClass)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "GetAvailableActions" &&
                    m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType == typeof(GamePlayerOwner));
        }

        [HarmonyPrefix]
        private static bool Prefix(
            GamePlayerOwner owner,
            object interactive,
            ref ActionsReturnClass __result)
        {
            var keypad = interactive as Keypad;
            if (keypad == null) return true;

            try
            {
                __result = BuildActions(owner, keypad);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[Keypad] action build failed: {ex.GetType().Name}: {ex.Message}");
                __result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            }
            return false;
        }

        private static ActionsReturnClass BuildActions(GamePlayerOwner owner, Keypad keypad)
        {
            var result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            if (owner?.Player == null || keypad == null) return result;

            // our own per-keypad Unlocked flag, NOT door.DoorState — BSG can
            // have the door in non-Locked state but still gate Open behind
            // other internal flags, so DoorState gives false positives
            var alreadyUnlocked = keypad.Unlocked;

            // prevent stacking — one UI session per keypad
            var sessionActive = keypad.ActiveSession != null;

            var disabled = alreadyUnlocked || sessionActive;
            var name     = alreadyUnlocked ? "Door unlocked" : "Enter code";

            result.Actions.Add(new ActionsTypesClass
            {
                Name     = name,
                Disabled = disabled,
                Action   = disabled ? (Action)null : (() => OnEnterCodeClicked(keypad)),
            });
            return result;
        }

        private static void OnEnterCodeClicked(Keypad keypad)
        {
            try
            {
                if (keypad == null) return;
                if (keypad.ActiveSession != null) return; // re-entrancy guard

                // session lives on a fresh GameObject until it self-destroys
                // (success + delay, or player abort), and owns the UI canvas
                var sessionHost = new GameObject("KeypadSession");
                UnityEngine.Object.DontDestroyOnLoad(sessionHost);
                var session = sessionHost.AddComponent<KeypadSession>();
                session.Begin(keypad);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[Keypad] EnterCode click handler threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
