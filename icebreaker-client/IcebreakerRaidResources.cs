using System;
using Manimal.Icebreaker.Keypad;

namespace Manimal.Icebreaker
{
    // The patch lifecycle calls this only after all map load producers have
    // stopped and the last Icebreaker scene has unloaded. Keep each release
    // independent so a failed optional integration cannot retain the rest.
    internal static class IcebreakerRaidResources
    {
        internal static void Release()
        {
            Release("render driver", RenderEnvProbe.DestroyGlobal);
            Release("keypad sessions", KeypadSession.ReleaseRaid);
            Release("AI events", IcebreakerAIPlaces.ReleaseRaid);
            Release("fog", IcebreakerVolFog.ReleaseRaid);
            Release("flares", IcebreakerFlares.ReleaseRaid);
            Release("snow gusts", IcebreakerSnowGusts.ReleaseRaid);
            Release("ambient audio", IcebreakerAmbientAudio.ReleaseRaid);
            Release("acoustics", IcebreakerAcoustics.ReleaseRaid);
            Release("weather", IcebreakerWeather.ReleaseRaid);
            Release("LOD cache", IcebreakerLodCullFloor.ReleaseRaid);
            Release("AI bake", IcebreakerAIBake.ReleaseRaid);
            Release("loot graph", LootScanner.ReleaseRaid);
            Release("Fika roster", FikaBridge.ReleaseRaid);
            Release("crew jobs", IceCrewJobs.Reset);
            Release("muted speakers", IceHoldLogic.MutedSpeakers.Clear);
            // Last: no subsystem may start loading from the host after this point.
            Release("bundles", IcebreakerBundleHost.ReleaseRaid);
        }

        private static void Release(string name, Action cleanup)
        {
            try { cleanup(); }
            catch (Exception e) { Plugin.Log?.LogWarning($"[Cleanup] {name}: {e.Message}"); }
        }
    }
}
