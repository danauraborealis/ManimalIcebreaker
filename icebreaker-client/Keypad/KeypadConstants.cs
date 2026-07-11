using UnityEngine;

namespace Manimal.Icebreaker.Keypad
{
    // all hardcoded knobs for the icebreaker keypad in one spot — bundle keys,
    // UI child paths, colors, timing. ported from the ShorelineExpansion keypad
    // but standalone: no shoreline door coords, codes come from the passcode
    // sidecar (authored override or per-raid roll), doors bind by Id.
    internal static class KeypadConstants
    {
        // --- bundle keys -------------------------------------------------
        // distinct keys from the shoreline ones — the user runs both mods, and
        // SPT's bundle registry is global, so a shared key would collide.
        // world prop bundle — the map ships its own terminal panels so the
        // model never spawns, but its keypress/denied/granted AudioSource
        // children are the ONLY copy of the keypad sounds; they get grafted
        // onto each terminal panel.
        public const string KeypadBundleKey = "manimal/icebreaker_keypad.bundle";

        public static readonly string[] KeypadAssetNameCandidates =
        {
            "keypad",
            "Keypad",
            "keypad_world",
        };

        // UI canvas prefab (KeypadCanvas with Panel_1/Panel/...).
        public const string KeypadUIBundleKey = "manimal/icebreaker_keypad_ui.bundle";

        public static readonly string[] KeypadUIAssetNameCandidates =
        {
            "KeypadCanvas",
            "keypad_ui",
            "KeypadUI",
        };

        // --- terminal panel audio child names --------------------------------
        // AudioSource children Keypad.Configure resolves by name — grafted from
        // the keypad prefab onto each terminal panel by the driver.
        public const string AudioKeypressName = "keypress";
        public const string AudioDeniedName   = "denied";
        public const string AudioGrantedName  = "granted";

        // --- UI prefab child paths (under KeypadCanvas root) ---------------
        // strict naming — the runtime looks children up by these literal
        // paths, so prefab reorganisation means updating here.
        public const string UIPathDisplayParent     = "Panel_1/Panel/Display";
        public const string UIPathDisplayCellsRoot  = "Panel_1/Panel/Display/Cells";
        public const string CellNameFormat          = "Cell_{0}";   // Cell_0..Cell_{DisplayLength-1}
        public const string CellDigitChildName      = "Digit";       // TMP_Text under each cell
        public const string UIPathGlowBorder        = "Panel_1/Panel/GlowBorder";
        public const string UIPathButtonsRoot       = "Panel_1/Panel/Buttons";

        // display substrate sprites — runtime swap of the Display Image's
        // .sprite between gray/green/red. idle = whatever the prefab authored.
        public const string UIPathDisplaySpriteHolders = "Panel_1/Panel/Display/SpriteHolders";
        public const string SubstrateSuccessChildName  = "Green";
        public const string SubstrateFailChildName     = "Red";

        // full-screen flash images — siblings of Panel_1, authored at alpha 0.
        // three-phase fade on success/fail; keep total <= the display seconds
        // below or teardown cuts the flash mid-fade.
        public const string UIPathFlashGreen      = "Flash/GreenFlash";
        public const string UIPathFlashRed        = "Flash/RedFlash";
        public const float  FlashFadeInSeconds    = 1.0f;
        public const float  FlashHoldSeconds      = 1.0f;
        public const float  FlashFadeOutSeconds   = 1.0f;

        // --- display -------------------------------------------------------
        // digit slots the UI shows — decoupled from code length (codes here
        // are 6, display stays full-width at 7 like the authored prefab).
        public const int DisplayLength = 7;

        public const string ColorEnteredHex     = "FFFFFF";  // entered digits — white
        public const string ColorPlaceholderHex = "404040";  // unentered slots — dim gray
        public const string ColorSuccessHex     = "40FF60";  // bright LCD green
        public const string ColorFailHex        = "FF4040";  // red

        // --- timing ----------------------------------------------------------
        // how long SUCCESS/FAIL stays up before close (success) / reset (fail);
        // 3s = full flash fade-in + hold + fade-out.
        public const float SuccessDisplaySeconds = 3f;
        public const float FailDisplaySeconds    = 3f;

        // --- linked terminals ---------------------------------------------------
        // when a code is accepted, shared-code peers closer than this also
        // disable — covers the twin panel on the far side of the same door
        // (~1-3m apart through the wall) without eating a same-code terminal
        // across the room.
        public const float LinkedDisableRange = 5f;

        // --- interaction trigger ----------------------------------------------
        // fallback box collider size when a terminal panel has no collider of
        // its own for EFT's aim raycast.
        public static readonly Vector3 InteractionBoxSize = new Vector3(0.3f, 0.3f, 0.15f);
    }
}
