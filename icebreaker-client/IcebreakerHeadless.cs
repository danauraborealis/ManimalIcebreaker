using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // RenderEnvProbe normally starts these gameplay services after shader repair.
    // A dedicated host must start them without creating a camera or render probe.
    [IcebreakerPatchTarget(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
    internal sealed class IcebreakerHeadless : IcebreakerModulePatch<IcebreakerHeadless>
    {
        [PatchPostfix]
        private static void Postfix(GameWorld __instance)
        {
            if (FikaBridge.CanRender || !IceGate.On) return;
            RenderEnvProbe.HealDoorRegistry();
            IcebreakerVolFog.StripMarkerColliders();
            if (__instance.GetComponent<IcebreakerCrew>() == null)
                __instance.gameObject.AddComponent<IcebreakerCrew>();
            if (__instance.GetComponent<IcebreakerHeliExfil>() == null)
                __instance.gameObject.AddComponent<IcebreakerHeliExfil>();
            Plugin.Log.LogInfo("[Headless] gameplay services started without camera, weather graphics or render probe");
        }
    }
}
