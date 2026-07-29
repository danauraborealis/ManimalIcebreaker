using System;
using System.Text;
using Comfort.Common;
using EFT;
using HarmonyLib;

namespace Manimal.Icebreaker
{
    // TEMPORARY diagnostic for the fika-transit 25% hang (07-28): Player.Init NREs at
    // [0x005de] inside FikaPlayer.Create on the icebreaker arrival — works solo, works
    // on shoreline coop, so it's something icebreaker x fika. this prefix null-checks
    // everything Init dereferences and prints one [InitDiag] block, so the next raid
    // log names the culprit instead of an offset. remove once the null is found.
    [HarmonyPatch(typeof(Player), nameof(Player.Init))]
    internal static class Patch_PlayerInitDiag
    {
        [HarmonyPrefix]
        private static void Prefix(Player __instance, Profile profile)
        {
            try
            {
                if (!IceGate.On) return;
                var sb = new StringBuilder("[InitDiag] ");
                void P(string k, bool ok) => sb.Append(k).Append(ok ? "=ok " : "=NULL ");

                P("profile", profile != null);
                P("profile.Info", profile?.Info != null);
                P("profile.Health", profile?.Health != null);
                P("profile.Skills", profile?.Skills != null);
                P("profile.Customization", profile?.Customization != null);
                bool hasVoice = false;
                try { hasVoice = profile != null && profile.Customization != null && profile.Customization.ContainsKey(EBodyModelPart.Voice); } catch { }
                P("voiceKey", hasVoice);
                P("custSolver", Singleton<CustomizationSolverClass>.Instantiated);
                try
                {
                    if (hasVoice && Singleton<CustomizationSolverClass>.Instantiated)
                        P("voiceResolved", Singleton<CustomizationSolverClass>.Instance.GetVoice(profile.Customization[EBodyModelPart.Voice]) != null);
                }
                catch (Exception e) { sb.Append($"voiceResolved=THREW({e.GetType().Name}) "); }
                P("envMgr", EnvironmentManagerBase.Instance != null);
                var world = Singleton<GameWorld>.Instance;
                P("gameWorld", world != null);
                P("speakerMgr", world?.SpeakerManager != null);
                P("backendCfg", Singleton<BackendConfigSettingsClass>.Instance != null);
                P("bones", __instance.PlayerBones != null);
                P("bones.Animated", __instance.PlayerBones?.AnimatedTransform != null);
                P("bones.Animated.Orig", __instance.PlayerBones?.AnimatedTransform?.Original != null);
                bool body = false;
                try { body = __instance.PlayerBones != null && __instance.PlayerBones.AnimatedTransform.Original.gameObject.GetComponent<PlayerBody>() != null; } catch { }
                P("playerBodyComp", body);
                P("bones.LootRaycast", __instance.PlayerBones?.LootRaycastOrigin != null);
                P("movementCtx", __instance.MovementContext != null);

                Plugin.Log.LogWarning(sb.ToString());
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[InitDiag] diagnostic itself failed: {e.Message}"); }
        }
    }
}
