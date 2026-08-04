using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;

namespace Manimal.Icebreaker.Server;

// which map the ACTIVE raid runs on, latched by the loot firewall at StartLocalRaid.
// bot generation arrives on later requests with no location in its payload — this is
// how per-map bot tweaks (the knight's SZ-1 charge) know they're on the ship. single
// active raid per server holds for local SPT and fika alike.
internal static class IcebreakerRaidContext
{
    internal static bool OnIcebreaker;
}

// LOOT ISOLATION — on the icebreaker, loot generation runs with every third-party
// harmony patch on LocationLootGenerator SUSPENDED, then restored. mods keep working
// on every other map; on ours, authored loot generates exactly as intended.
//
// why not reverse-patch pristine copies (the first attempt): pristine is one level
// deep. LotsofLoot hooks PRIVATE CreateStaticLootItem, which the pristine copy of
// GenerateStaticContainers still CALLS — the call lands in the detour and their
// per-map dictionary throws from beneath us (fresh-install repro, 2026-08-03; the
// original fatal was the same mod's GenerateDynamicLoot prefix hanging
// /client/match/local/start with no response). suspend/restore has no depth limit:
// every method of the class is swept, so any hook by any mod at any level is out of
// the picture for exactly one call.
//
// mechanics: DI override (registrator walks base types; mod registrations land after
// core = last-wins, the CompoundingPerf pattern) makes this subclass THE generator.
// GenerateLocationLoot is the single entry the raid-start path uses; around its base
// call we Harmony.GetPatchInfo every declared method, Unpatch each foreign owner's
// patches, and re-Patch them afterward under their own owner ids with their own
// priorities/before/after so ownership and ordering survive.
//
// the try/catch stays as the last line: if something still throws (a mod hooking a
// DIFFERENT class entirely), the raid starts lootless with the culprit named — which
// still beats the fatal-with-no-response infinite loading screen.
[Injectable]
public class IcebreakerLootFirewall(
    ISptLogger<LocationLootGenerator> logger,
    RandomUtil randomUtil,
    ItemHelper itemHelper,
    DatabaseService databaseService,
    PresetHelper presetHelper,
    ServerLocalisationService serverLocalisationService,
    SeasonalEventService seasonalEventService,
    ItemFilterService itemFilterService,
    ConfigServer configServer,
    CounterTrackerHelper counterTrackerHelper,
    ICloner cloner)
    : LocationLootGenerator(logger, randomUtil, itemHelper, databaseService, presetHelper,
        serverLocalisationService, seasonalEventService, itemFilterService, configServer,
        counterTrackerHelper, cloner)
{
    private const string OwnPrefix = "com.manimal.icebreaker";
    private readonly ISptLogger<LocationLootGenerator> _log = logger;
    private static readonly object Gate = new();

    private sealed record Suspended(MethodBase Target, HarmonyLib.Patch Patch, HarmonyPatchType Kind);

    private static bool Ours(string id) => string.Equals(id, "suburbs", StringComparison.OrdinalIgnoreCase);

    public override List<SpawnpointTemplate> GenerateLocationLoot(string locationId)
    {
        // raid-context latch: loot generates at StartLocalRaid, bots on later requests —
        // this is the only place the server tells us which map the active raid is on
        IcebreakerRaidContext.OnIcebreaker = Ours(locationId);
        if (!Ours(locationId)) return base.GenerateLocationLoot(locationId);

        lock (Gate) // raid starts are rare; simplest way to keep suspend/restore atomic
        {
            // PBS MASQUERADE (policy exception, user-approved 2026-08-03): Progressive
            // Bot System keys hardcoded per-map switches off its static
            // RaidInformation.RaidLocation — 'Suburbs' throws for EVERY bot the server
            // generates (empty map). its router hook writes that static BEFORE
            // StartLocalRaid, we run INSIDE it, and bot generation reads it after — so
            // overwriting here makes PBS run its LABS profile on the icebreaker:
            // LongRange 10 / ShortRange 90 — tight-interior CQB, and labs tier tables
            // skew high-end gear, which fits a ship crewed by rogues and Black Division.
            // reflection + soft: no reference, no-op when PBS is absent or drifts.
            IcebreakerPbsMasquerade.Apply(_log);

            List<Suspended> suspended = null;
            try
            {
                suspended = SuspendForeign();
                if (suspended.Count > 0)
                    _log.Info($"[Icebreaker] loot isolation: {suspended.Count} third-party patch(es) on "
                        + $"LocationLootGenerator suspended for this raid's generation "
                        + $"({string.Join(", ", OwnersOf(suspended))}) — restored right after");
                return base.GenerateLocationLoot(locationId);
            }
            catch (Exception e)
            {
                _log.Error("[Icebreaker] a mod threw inside loot generation for the icebreaker — "
                    + "starting the raid WITHOUT generated loot rather than hanging the client. "
                    + $"culprit: {Culprit(e)} — report it upstream. inner error: {e.Message}");
                return new List<SpawnpointTemplate>();
            }
            finally
            {
                if (suspended != null) Restore(suspended);
            }
        }
    }

    private static List<Suspended> SuspendForeign()
    {
        var outp = new List<Suspended>();
        var h = new Harmony(OwnPrefix + ".lootisolation");
        foreach (var m in typeof(LocationLootGenerator).GetMethods(
                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            var info = Harmony.GetPatchInfo(m);
            if (info == null) continue;
            Collect(outp, m, info.Prefixes, HarmonyPatchType.Prefix);
            Collect(outp, m, info.Postfixes, HarmonyPatchType.Postfix);
            Collect(outp, m, info.Transpilers, HarmonyPatchType.Transpiler);
            Collect(outp, m, info.Finalizers, HarmonyPatchType.Finalizer);
        }
        foreach (var s in outp)
            h.Unpatch(s.Target, s.Patch.PatchMethod);
        return outp;
    }

    private static void Collect(List<Suspended> outp, MethodBase m, IEnumerable<HarmonyLib.Patch> patches, HarmonyPatchType kind)
    {
        foreach (var p in patches ?? Array.Empty<HarmonyLib.Patch>())
            if (!p.owner.StartsWith(OwnPrefix, StringComparison.OrdinalIgnoreCase))
                outp.Add(new Suspended(m, p, kind));
    }

    private static void Restore(List<Suspended> suspended)
    {
        foreach (var s in suspended)
        {
            try
            {
                // re-applied under the ORIGINAL owner id with the original ordering
                // metadata, so a later unpatch by that mod still finds its own patch
                var h = new Harmony(s.Patch.owner);
                var hm = new HarmonyMethod(s.Patch.PatchMethod)
                {
                    priority = s.Patch.priority,
                    before = s.Patch.before?.Length > 0 ? s.Patch.before : null,
                    after = s.Patch.after?.Length > 0 ? s.Patch.after : null,
                };
                h.Patch(s.Target,
                    prefix: s.Kind == HarmonyPatchType.Prefix ? hm : null,
                    postfix: s.Kind == HarmonyPatchType.Postfix ? hm : null,
                    transpiler: s.Kind == HarmonyPatchType.Transpiler ? hm : null,
                    finalizer: s.Kind == HarmonyPatchType.Finalizer ? hm : null,
                    ilmanipulator: null);
            }
            catch
            {
                // a failed restore only means that mod's loot feature stays off for the
                // rest of THIS server session — still strictly better than the crash
            }
        }
    }

    private static IEnumerable<string> OwnersOf(List<Suspended> s)
    {
        var seen = new HashSet<string>();
        foreach (var x in s) if (seen.Add(x.Patch.owner)) yield return x.Patch.owner;
    }

    private static string Culprit(Exception e)
    {
        foreach (var line in (e.StackTrace ?? "").Split('\n'))
        {
            var m = Regex.Match(line, @"at (?!SPTarkov\.|System\.|Microsoft\.|DMD|SyncProxy)([A-Za-z_][\w]*)[\w.]*\.");
            if (m.Success) return m.Groups[1].Value;
        }
        return "unknown (see stack in this log)";
    }
}
