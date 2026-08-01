using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // HYBRID BAKE — retail cover data everywhere, our generated data in named zones only.
    //
    // WHY THIS NEEDED INVENTING RATHER THAN CONFIGURING. the obvious question was whether the
    // bake is already split per zone so we could just pick a source per zone. it is not:
    //
    //   AICoversData.Points   ONE flat List<GroupPoint> for the entire map
    //   GroupPoint            carries NO BotZone reference (grepped the decompile — zero)
    //   AICorePointsHolder    global, likewise
    //   AIVoxelesData         a world-space grid; cover lookup is spatial, never by zone
    //   BotZone.PatrolWays    the ONE genuinely per-zone thing in the whole bake
    //
    // so cover selection has no zone dimension to switch on. the partition has to be one we
    // define geometrically, which is what ZoneRegion below is.
    //
    // the shape of the problem this exists for: on the RETAIL bake the helipad squad takes
    // its proper cover spots but bots park in the freezer; on the FULLY generated bake the
    // freezer clears and the helipad squad stands in the open instead. neither set is good
    // everywhere, and the two failures are in different places, so per-zone sourcing is a
    // real experiment rather than a guess.
    //
    // SEAM CAVEAT, stated up front because it is a genuine limitation and not a bug to chase
    // later: generated ways only survive when BOTH endpoints are inside the region, so there
    // are no cover-to-cover links crossing a region border. a bot can walk across the seam
    // normally (that is navmesh, untouched) but it will not chain a cover hop from a synth
    // point directly to a retail one. keeping the regions bigger than the firefights inside
    // them is what keeps that from mattering.
    internal static class IcebreakerHybridBake
    {
        // REGION SHAPE — nearest-zone (Voronoi), not a fixed radius.
        //
        // the first cut gave each zone a disc of spawn-spread + 22m. measured against the real
        // roster that was hopeless: these compartments sit 10-13m apart, so a disc sized to
        // cover the galley also swallowed RoomsThird, Room_Eng, Room_Eng2 and Mash_t1. five
        // requested zones carved 764 of 1885 retail points — ten zones' worth of ship.
        //
        // no single radius fixes that, because the right radius differs per zone. so drop the
        // radius idea: a point belongs to whichever zone centre is NEAREST, and we rebuild it
        // only if that zone was named. the partition is then bounded by the neighbours
        // themselves — a synth zone's territory stops halfway to the next zone, whatever the
        // spacing happens to be, and no zone can annex another.
        //
        // vertical distance is weighted because decks here are only ~2.7m apart while
        // compartments are 10m+ wide: unweighted, a point on the deck ABOVE the galley is
        // "closer" to the galley than to its own zone across the room.
        private const float VerticalWeight = 3f;
        // points further than this from every zone centre belong to nobody (open weather deck,
        // holds) and keep their retail data rather than being annexed by the nearest zone.
        // this only ever bites on the OUTWARD frontier — boundaries between two zones are
        // decided by the nearest rule, which puts them ~5-6m out at this map's zone spacing.
        // kept near 2x that spacing: at 32m the galley annexed the whole bow to z~78 simply
        // because nothing else was competing for it.
        private const float MaxClaim = 18f;

        private sealed class ZoneRegion
        {
            internal BotZone Zone;
            internal Vector3 Centre;
            internal bool Synth;
        }

        private static float ClaimDist(Vector3 p, Vector3 centre)
        {
            float dx = p.x - centre.x, dz = p.z - centre.z, dy = (p.y - centre.y) * VerticalWeight;
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // called from the AICoversData.RestoreData finalizer on the SUCCESS path, i.e. after
        // the retail bake has fully restored: points resolved, voxels bucketed, cache built.
        // we then carve and refill, which is why every one of those has to be redone below.
        internal static void Apply(AICoversData covers)
        {
            // roster first, before any early return: SynthZones takes literal BotZone names
            // and only a handful of this map's zones have ever surfaced in a log line, so
            // without this the config is unfillable without decompiling the scene
            LogZoneRoster();

            var wanted = (Plugin.SynthZones?.Value ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (wanted.Count == 0) return;                       // pure retail — the default
            if (covers?.Points == null || covers.Points.Count == 0) return;
            if (covers.AICorePointsHolder?.CorePoints == null || covers.AICorePointsHolder.CorePoints.Count == 0)
            {
                Plugin.Log.LogWarning("[Hybrid] retail cores missing — cannot anchor generated points, skipping");
                return;
            }

            // EVERY zone goes in, not just the named ones — the unnamed ones are what bound
            // the named ones' territory. that is the whole point of the nearest-zone rule.
            var all = BuildRegions(wanted);
            var synth = all.Where(r => r.Synth).ToList();
            if (synth.Count == 0)
            {
                Plugin.Log.LogDebug($"[Hybrid] none of [{string.Join(", ", wanted)}] matched a BotZone in the scene — "
                                      + "check the names against the zone list this map actually has");
                return;
            }

            Func<Vector3, bool> inRegion = p =>
            {
                ZoneRegion best = null; float bestD = float.MaxValue;
                foreach (var r in all)
                {
                    float d = ClaimDist(p, r.Centre);
                    if (d < bestD) { bestD = d; best = r; }
                }
                return best != null && best.Synth && bestD <= MaxClaim;
            };

            // ids must clear everything retail owns, INCLUDING the manual points holder —
            // _lastId is the game's own high-water mark, so start above it
            int idOffset = Math.Max(covers.Points.Max(p => p.Id),
                                    covers.Ways?.Count > 0 ? covers.Ways.Max(w => w.Id) : 0) + 1000;

            List<GroupPoint> genPoints;
            List<GroupPointWay> genWays;
            try
            {
                var built = CoverScanner.BuildForRegion(inRegion, covers.AICorePointsHolder.CorePoints, idOffset);
                genPoints = built.Item1; genWays = built.Item2;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Hybrid] region scan failed — retail bake left intact: {e}");
                return;
            }
            if (genPoints == null || genPoints.Count == 0)
            {
                Plugin.Log.LogDebug("[Hybrid] scanner found no cover inside the named zones — retail bake left intact");
                return;
            }

            // carve: drop retail points that fall inside a region, and any retail way whose
            // target went with them (a dangling Target is a null deref mid cover-walk)
            int before = covers.Points.Count;
            var doomed = new HashSet<int>();
            foreach (var p in covers.Points) if (inRegion(p.Position)) doomed.Add(p.Id);
            covers.Points = covers.Points.Where(p => !doomed.Contains(p.Id)).ToList();
            if (covers.Ways != null)
                covers.Ways = covers.Ways.Where(w => w.Target == null || !doomed.Contains(w.Target.Id)).ToList();
            // and scrub the dropped way ids out of the surviving retail points' neighbour lists
            var liveWays = new HashSet<int>(covers.Ways?.Select(w => w.Id) ?? Enumerable.Empty<int>());
            foreach (var p in covers.Points)
                if (p.NeighbourhoodsWaysIds != null)
                    p.NeighbourhoodsWaysIds = p.NeighbourhoodsWaysIds.Where(liveWays.Contains).ToList();

            covers.Points.AddRange(genPoints);
            if (covers.Ways == null) covers.Ways = new List<GroupPointWay>();
            covers.Ways.AddRange(genWays);

            // the generated points never went through RestoreData, so CorePointInGame is
            // still unresolved on them — that is the field bots deref in GetLastConnectionId
            foreach (var p in genPoints)
                try { p.InitForGame(covers.AICorePointsHolder); } catch { }

            RebucketVoxels(covers);
            AccessTools.Field(typeof(AICoversData), "_cache").SetValue(covers, new GClass411(covers));
            AccessTools.Field(typeof(AICoversData), "_lastId")?.SetValue(covers, covers.Points.Max(p => p.Id) + 1);

            RegeneratePatrols(synth, covers);

            Plugin.Log.LogDebug($"[Hybrid] {synth.Count} synth zone(s) of {all.Count} [{string.Join(", ", synth.Select(r => r.Zone.NameZone))}]: "
                + $"dropped {doomed.Count} retail point(s), added {genPoints.Count} generated; "
                + $"map total {before} -> {covers.Points.Count}");
        }

        private static void LogZoneRoster()
        {
            try
            {
                var zones = UnityEngine.Object.FindObjectsOfType<BotZone>()
                    .Where(z => z != null).OrderBy(z => z.NameZone).ToList();
                Plugin.Log.LogDebug($"[Hybrid] {zones.Count} BotZone(s) on this map — SynthZones takes these names:");
                foreach (var z in zones)
                {
                    var c = Vector3.zero; int n = 0;
                    if (z.SpawnPointMarkers != null)
                        foreach (var m in z.SpawnPointMarkers)
                            if (m != null) { c += m.transform.position; n++; }
                    if (n > 0) c /= n;
                    Plugin.Log.LogDebug($"    {z.NameZone,-28} markers={n,-3} ways={z.PatrolWays?.Length ?? 0,-3} "
                                          + (n > 0 ? $"centre=({c.x:F0},{c.y:F0},{c.z:F0})" : "centre=n/a"));
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Hybrid] zone roster dump failed: {e.Message}"); }
        }

        // every zone with markers becomes a region. the ones NOT named still matter: they
        // are the walls that stop a named zone's territory from spilling into the next
        // compartment. dropping them would collapse the partition back to unbounded radii.
        private static List<ZoneRegion> BuildRegions(List<string> wanted)
        {
            var regions = new List<ZoneRegion>();
            foreach (var zone in UnityEngine.Object.FindObjectsOfType<BotZone>())
            {
                if (zone == null || zone.SpawnPointMarkers == null || zone.SpawnPointMarkers.Count == 0) continue;

                var centre = Vector3.zero; int n = 0;
                foreach (var m in zone.SpawnPointMarkers)
                    if (m != null) { centre += m.transform.position; n++; }
                if (n == 0) continue;
                centre /= n;

                bool synth = wanted.Any(w => string.Equals(w, zone.NameZone, StringComparison.OrdinalIgnoreCase));
                regions.Add(new ZoneRegion { Zone = zone, Centre = centre, Synth = synth });
                if (synth)
                    Plugin.Log.LogDebug($"[Hybrid] synth region '{zone.NameZone}' centre "
                        + $"({centre.x:F1},{centre.y:F1},{centre.z:F1}) from {n} spawn marker(s)");
            }
            return regions;
        }

        // full rewrite of every cell's point list, same as the full-synth path: a stale
        // _closetsPointId pointing at a carved-out id is a silent statue bot
        private static void RebucketVoxels(AICoversData covers)
        {
            var vox = covers.Voxels;
            if (vox?.VoxelsList == null) return;

            var byCell = new Dictionary<(int, int, int), List<GroupPoint>>();
            foreach (var p in covers.Points)
            {
                var key = CoverScanner.CellIndex(vox, p.Position);
                if (!byCell.TryGetValue(key, out var list)) byCell[key] = list = new List<GroupPoint>();
                list.Add(p);
            }

            foreach (var cell in vox.VoxelsList)
            {
                var idx = ((int)cell.IndexX, (int)cell.IndexY, (int)cell.IndexZ);
                cell.PointsIds = new List<int>();
                cell.Points = new List<GroupPoint>();
                cell._closetsPointId = 0;
                cell.PointStartSearch = null;
                if (!byCell.TryGetValue(idx, out var inside)) continue;
                var centre = cell.Position + new Vector3(5f, 2.5f, 5f);
                cell.PointsIds = inside.Select(p => p.Id).ToList();
                cell.Points = inside.ToList();
                var closest = inside.OrderBy(p => (p.Position - centre).sqrMagnitude).First();
                cell._closetsPointId = closest.Id;
                cell.PointStartSearch = closest;
                // loot / door / exfil id lists are deliberately untouched: those are retail's
                // and stay valid, since we only ever moved COVER points around
            }

            int filled = 0;
            foreach (var cell in vox.VoxelsList)
            {
                if (cell.PointStartSearch != null || covers.Points.Count == 0) continue;
                var centre = cell.Position + new Vector3(5f, 2.5f, 5f);
                GroupPoint nearest = null; float best = float.MaxValue;
                foreach (var p in covers.Points)
                {
                    float d = (p.Position - centre).sqrMagnitude;
                    if (d < best) { best = d; nearest = p; }
                }
                cell.PointStartSearch = nearest;
                cell._closetsPointId = nearest.Id;
                filled++;
            }
            Plugin.Log.LogInfo($"[Hybrid] voxel grid re-bucketed over {covers.Points.Count} points; {filled} empty cells seeded");
        }

        // patrols ARE per-zone, so this half needs no geometry trickery — swap the ways on
        // the named zones only and leave every other zone on retail's.
        //
        // note BotZone.IsValid() is `SpawnPointMarkers.Count > 0 && PatrolWays.Length != 0`,
        // so leaving a zone with zero ways marks the whole zone invalid. if the generator
        // cannot build a route here we KEEP retail's ways rather than clearing them.
        private static void RegeneratePatrols(List<ZoneRegion> regions, AICoversData covers)
        {
            foreach (var r in regions)
            {
                var retail = r.Zone.PatrolWays;
                try
                {
                    int built = PatrolScanner.GenerateForZone(r.Zone, covers);
                    if (built == 0)
                    {
                        r.Zone.PatrolWays = retail;
                        Plugin.Log.LogDebug($"[Hybrid] zone '{r.Zone.NameZone}': no route generated — keeping retail's "
                                              + $"{retail?.Length ?? 0} way(s) so the zone stays valid");
                    }
                }
                catch (Exception e)
                {
                    r.Zone.PatrolWays = retail;
                    Plugin.Log.LogWarning($"[Hybrid] zone '{r.Zone.NameZone}': patrol gen failed ({e.Message}) — retail ways kept");
                }
            }
        }
    }
}
