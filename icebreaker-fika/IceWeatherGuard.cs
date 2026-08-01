using System;
using Comfort.Common;
using EFT;
using EFT.Weather;
using Fika.Core.Main.GameMode;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.Packets.World;
using HarmonyLib;

namespace Manimal.Icebreaker.Fika
{
    // the 80% "Generating weather" deadlock (07-29 headless test): fika clients loop
    // forever waiting for the host's weather answer, and the host's WeatherRequest
    // handler SILENTLY drops the request when GameController.WeatherClasses is null —
    // which happens whenever HostGameController.GenerateWeathers found
    // WeatherController.Instance null at its moment (on this backported map the
    // controller only exists because our weather stack builds it). two patches:
    //  1. a diagnostic on the host's GenerateWeathers naming the controller state
    //  2. a guard on the request handler: no weather? synthesize defaults and answer
    //     anyway — a raid must never hang on missing weather data
    [HarmonyPatch(typeof(HostGameController), nameof(HostGameController.GenerateWeathers))]
    internal static class Patch_HostWeatherDiag
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            // 07-29 headless run proved the root cause: the headless flow generates
            // weather BEFORE its player exists, so the Player.Init weather-stack anchor
            // hasn't fired yet and GenerateWeathers silently skips on the null
            // controller. build the stack HERE, before fika reads it — then the host
            // serves real backend weather instead of the synthesized fallback.
            try { Patch_EnsureEnvironmentManager.EnsureEnvAndWeather(); }
            catch (Exception e) { FikaAddonPlugin.Log.LogWarning($"[IceWeather] ensure failed: {e.Message}"); }
            FikaAddonPlugin.Log.LogDebug($"[IceWeather] host GenerateWeathers — WeatherController.Instance={(WeatherController.Instance != null ? "OK" : "NULL (WeatherClasses will stay empty!)")}");
        }
    }

    // NOTE: in the shipped 2.3.9 dll the packet classes are NESTED inside a
    // RequestSubPackets class (the master source has them namespace-level)
    [HarmonyPatch(typeof(RequestSubPackets.WeatherRequest), nameof(RequestSubPackets.WeatherRequest.HandleRequest))]
    internal static class Patch_WeatherRequestGuard
    {
        [HarmonyPrefix]
        private static void Prefix(NetPeer peer, FikaServer server)
        {
            try
            {
                var game = Singleton<IFikaGame>.Instance;
                if (game?.GameController == null) return;
                if (game.GameController.WeatherClasses != null && game.GameController.WeatherClasses.Length > 0) return;

                // mirror fika's own SetupCustomWeather default shape: two entries
                // spanning today so the client's weather curve has ends to blend
                var day = EFTDateTimeClass.StartOfDay();
                var w1 = WeatherClass.CreateDefault();
                var w2 = WeatherClass.CreateDefault();
                w1.Time = day.Ticks;
                w2.Time = day.AddDays(1).Ticks;
                game.GameController.WeatherClasses = new[] { w1, w2 };
                FikaAddonPlugin.Log.LogWarning("[IceWeather] host had NO WeatherClasses when a client asked — synthesized defaults so the raid can't deadlock");
            }
            catch (Exception e) { FikaAddonPlugin.Log.LogWarning($"[IceWeather] guard failed: {e.Message}"); }
        }
    }
}
