namespace Manimal.Icebreaker.Keypad
{
    // post-it note prefab knobs. no spawn pool here — spot positions come from
    // the Author 9 passcode sidecar (authored per-terminal in the SDK).
    internal static class PostItConstants
    {
        // distinct key from the shoreline postit.bundle — SPT's bundle
        // registry is global and the user runs both mods
        public const string BundleKey = "manimal/icebreaker_postit.bundle";

        // asset name candidates inside the bundle; first hit wins, loader
        // falls back to a raw-bundle scan so a typo surfaces in the log
        public static readonly string[] AssetNameCandidates =
        {
            "PostItNotes",
            "postitnotes",
            "PostIt",
            "postit",
        };

        // the TMP_Text child on each SM_note variant whose .text gets the
        // digit half at spawn time — same name across all variants
        public const string TmpChildName = "Digits";
    }
}
