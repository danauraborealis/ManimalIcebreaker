using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace Manimal.Icebreaker.Fika
{
    // fika sync addon, the spt-ladders pattern: a SEPARATE plugin with a HARD fika
    // dependency, shipped as its OWN addon zip (user call 07-30) — solo installs never
    // have this dll at all. syncs the custom world events fika can't see (direct
    // animator/prop/DoorState writes, not the player-interaction path it replicates):
    // chain-door plant + open, sealed-door seal/unseal, hatch stages, keypad unlocks.
    //
    // the fika dependency MUST stay HARD. the 08-03 soft-dep experiment (self-gate in
    // Awake, NoInlining around fika-typed code) hard-hung the game before the main
    // menu on solo installs: EFT's own GlobalEventHandlerClass.Initialize sweeps every
    // LOADED assembly with Assembly.GetTypes(), which throws ReflectionTypeLoadException
    // on an assembly whose types reference the absent Fika.Core — no code of ours has
    // to run to break. only bepinex SKIPPING the load (= hard dep) keeps the assembly
    // out of the appdomain. the red "1 PLUGIN FAILED TO LOAD" banner on a fika-less
    // install is correct feedback for installing the fika addon without fika.
    [BepInPlugin(BuildInfo.ModGuid, "Manimal-IcebreakerFika", BuildInfo.Version)]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.manimal.icebreaker", BepInDependency.DependencyFlags.HardDependency)]
    public class FikaAddonPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut> ProbeKey;
        private IceSyncHandler _handler;

        private void Awake()
        {
            Log = Logger;
            // F7: the only free F-key — F5 rebind, F8 fika extract, F9 fog tuner,
            // F10 torch pose probe, F11 sky probe, F12 configuration manager
            ProbeKey = Config.Bind("Diagnostics", "BodyProbeKey",
                new BepInEx.Configuration.KeyboardShortcut(UnityEngine.KeyCode.F7),
                "dump the renderer state of the observed player you're LOOKING AT to the log (invisible-bot hunt)");
            _handler = new IceSyncHandler();
            new Harmony(BuildInfo.ModGuid).PatchAll(); // door-sync diagnostics etc.
            // observed-body probe (temporary, self-gates to icebreaker raids)
            var diagGo = new UnityEngine.GameObject("Icebreaker_FikaDiag");
            UnityEngine.Object.DontDestroyOnLoad(diagGo);
            diagGo.AddComponent<IceBodyDiag>();
            Log.LogInfo($"Manimal-IcebreakerFika {BuildInfo.Version} loaded — chain/seal/keypad world events will sync");
        }

        private void OnDestroy()
        {
            _handler?.Dispose();
            _handler = null;
        }
    }
}
