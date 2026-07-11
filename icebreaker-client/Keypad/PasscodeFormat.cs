namespace Manimal.Icebreaker.Keypad
{
    // code shape shared by the terminal driver (rolls/loads the code) and the
    // note spawner (splits it across two post-its): 6 digits, first 3 on one
    // note, last 3 on the other. notes are unlabeled — the player figures out
    // the order.
    internal static class PasscodeFormat
    {
        public const int Length = 6;
        public const int FirstHalfLength = 3;
    }
}
