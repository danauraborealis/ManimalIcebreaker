using UnityEngine;

namespace Manimal.Icebreaker
{
    // cutscene router. the mp4 fallback player that used to live here is GONE
    // (07-30): the in-engine timeline cutscene is proven (incl. fika, per-player),
    // and the 70MB video was dead weight in every package. TimelineCutscene=false
    // now simply means no cutscene — the story beat (BD phase, spawns, progress
    // door) fires from the watcher regardless.
    internal static class IcebreakerCutscene
    {
        private static bool _played;

        public static void ResetForRaid() => _played = false;

        public static void TryPlay()
        {
            if (_played) return;
            if (!Plugin.TimelineCutscene.Value) { _played = true; return; }
            if (!IcebreakerTimelineCutscene.Available)
            {
                Plugin.Log.LogWarning("[Cutscene] timeline cutscene unavailable (scene missing from bundle?) — skipping");
                _played = true;
                return;
            }
            _played = true;
            IcebreakerTimelineCutscene.Play();
        }
    }
}
