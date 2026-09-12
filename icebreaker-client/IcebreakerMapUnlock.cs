using EFT;
using EFT.Quests;
using EFT.UI.Matchmaker;
using HarmonyLib;
using JsonType;
using System.Reflection;
using SPT.Reflection.Patching;

namespace Manimal.Icebreaker
{
    internal static class IcebreakerMapUnlock
    {
        internal const string BoreasPart3 = "9d5e3f7d6320a7fd139a2772";

        internal static bool IsUnlocked(Profile profile)
        {
            var quests = profile?.QuestsData;
            if (quests != null)
                for (int i = 0; i < quests.Count; i++)
                    if (quests[i] != null && quests[i].Id == BoreasPart3 && quests[i].Status == EQuestStatus.Success)
                        return true;
            return false;
        }

        internal static void RefreshLocation(LocationSettings.Location location, bool requirementMet)
        {
            if (!requirementMet || location == null ||
                !string.Equals(location.Id, IcebreakerLocation.Key, System.StringComparison.OrdinalIgnoreCase)) return;
            location.Enabled = true;
            location.Locked = false;
        }

        // The menu caches /client/locations at login. Refresh our slot from the
        // current PMC quest state before it builds buttons or restores selection.
        internal sealed class Patch_Show : ModulePatch
        {
            protected override MethodBase GetTargetMethod()
                => AccessTools.Method(typeof(MatchMakerSelectionLocationScreen), nameof(MatchMakerSelectionLocationScreen.Show),
                    new[] { typeof(IEftSession), typeof(RaidSettings), typeof(MatchmakerPlayersController) });

            [PatchPrefix]
            private static void Prefix(IEftSession session)
            {
                var locations = session?.LocationSettings?.locations;
                if (locations == null) return;
                // An enabled server entry may reflect a disabled maplock.json.
                // This refresh only grants a newly earned unlock; it must never
                // revoke access the server already granted at login.
                bool requirementMet = IsUnlocked(session.Profile);
                foreach (LocationSettings.Location location in locations.Values)
                    RefreshLocation(location, requirementMet);
            }
        }
    }
}
