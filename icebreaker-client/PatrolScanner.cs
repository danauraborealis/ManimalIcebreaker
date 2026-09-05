using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace Manimal.Icebreaker
{
    // PATROL GENERATION — the missing half of the synthesized AI graph.
    //
    // CoverScanner/DoorScanner/LootScanner rebuild covers, cores, ways, door links, loot and
    // exfils from the navmesh. Nothing generated PATROL data, so RetailAIBake=false produced
    // a map with zero PatrolPoints and zero PatrolWays — bots with nowhere to walk, which
    // made the retail-vs-generated comparison meaningless.
    //
    // shape copied from retail's own numbers (icebreaker_aibake.json): ~20 ways, 2-6 points
    // each (median 4), PatrolType.patrolling, _maxPersons -1, CoefSubPoints 1. sub-points are
    // NOT generated — PatrolPoint.CreateSubPoints() builds those at runtime from the way,
    // which is where retail's 580 of 647 came from.
    //
    // SEEDING (v2). the first pass sampled a single 14m ring around each zone's spawn-marker
    // centroid and got 81 points, 26 rejects, and two zones with no way at all
    // (Outside_t3 got 1 reachable point, Korr_t2 got 2). a ring is a poor guess at where a
    // ship's walkable space actually is. the cover scanner has already produced thousands of
    // navmesh-validated positions by this point, so use THOSE as candidates and let the
    // geometry pick the route.
    //
    // reachability is enforced, not assumed. a patrol point that sits on the navmesh but
    // cannot be reached is the exact trap that cost a day: PatrollingData sets Status=go and
    // THROWS AWAY the NavMeshPathStatus GoToPoint returns, so an unreachable point reads as
    // "walking" forever while the bot stands still.
    internal static class PatrolScanner
    {
        private const int MinPointsPerWay = 2;    // retail's own minimum
        private const int MaxPointsPerWay = 6;    // retail's own maximum
        private const float MinSpacing = 7f;      // stop a route doubling back on itself
        private const float MaxSpacing = 45f;     // and stop it teleporting across the ship
        private const float SnapRadius = 3f;
        private const float ZonePad = 18f;        // how far past the spawn spread to look
        private const int MaxCandidates = 70;     // path calls are the cost — cap the pool
        private static readonly float[] RingRadii = { 8f, 16f, 26f };
        private const int RingSamples = 10;

        private static GameObject _root;

        private static GameObject Root()
        {
            if (_root == null) _root = new GameObject("Icebreaker_GeneratedPatrols");
            return _root;
        }

        // the cover graph is our position library: every one of these already snapped to the
        // navmesh during cover generation, so they beat any geometric guess
        private static List<Vector3> Library(AICoversData covers)
        {
            var library = new List<Vector3>();
            if (covers?.Points != null)
                foreach (var p in covers.Points)
                    if (p != null) library.Add(p.Position);
            return library;
        }

        private static bool NavReady()
        {
            if (NavMesh.CalculateTriangulation().indices.Length != 0) return true;
            Plugin.Log.LogError("[PatrolGen] navmesh has no triangulation — skipping");
            return false;
        }

        // FULL SYNTH path: every zone in the scene gets a generated way.
        internal static int GenerateForZones(AICoversData covers)
        {
            var zones = UnityEngine.Object.FindObjectsOfType<BotZone>()
                .Where(z => z != null && z.SpawnPointMarkers != null && z.SpawnPointMarkers.Count > 0)
                .ToList();
            if (zones.Count == 0)
            {
                Plugin.Log.LogWarning("[PatrolGen] no bot zones with spawn markers — nothing to generate");
                return 0;
            }
            if (!NavReady()) return 0;

            var library = Library(covers);
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;

            int ways = 0, points = 0, rejected = 0, thin = 0;
            foreach (var zone in zones)
            {
                int made = Build(zone, library, ref rejected, ref points, ref thin);
                ways += made;
            }

            Plugin.Log.LogDebug($"[PatrolGen] generated {ways} way(s), {points} point(s) across {zones.Count} zone(s) "
                                  + $"from {library.Count} candidate position(s)"
                                  + (rejected > 0 ? $"; {rejected} unreachable" : "")
                                  + (thin > 0 ? $"; {thin} zone(s) too thin for a route" : ""));
            return ways;
        }

        // HYBRID path: one named zone only, everything else keeps whatever it already has.
        // returns 1 if a way was built, 0 if the zone was too thin — the caller restores
        // retail's ways on 0, because a zone with no ways fails BotZone.IsValid().
        internal static int GenerateForZone(BotZone zone, AICoversData covers)
        {
            if (zone == null || zone.SpawnPointMarkers == null || zone.SpawnPointMarkers.Count == 0) return 0;
            if (!NavReady()) return 0;
            int rejected = 0, points = 0, thin = 0;
            return Build(zone, Library(covers), ref rejected, ref points, ref thin);
        }

        private static int Build(BotZone zone, List<Vector3> library, ref int rejected, ref int points, ref int thin)
        {
            var picked = BuildRoute(zone, library, ref rejected);
            if (picked.Count < MinPointsPerWay)
            {
                thin++;
                Plugin.Log.LogWarning($"[PatrolGen] zone '{zone.NameZone}': only {picked.Count} reachable point(s) "
                                      + "— no way built, bots here will have nothing to patrol");
                return 0;
            }

            // the way is the PARENT and its points are CHILDREN: EditorInitPoints walks child
            // transforms to build Points and assign ids, so the hierarchy IS the data
            var wayGo = new GameObject($"GenPatrol_{zone.NameZone}");
            wayGo.transform.SetParent(Root().transform, false);
            wayGo.transform.position = picked[0];
            var way = wayGo.AddComponent<PatrolWay>();
            way.PatrolType = PatrolType.patrolling;
            way.CoefSubPoints = 1f;

            foreach (var p in picked)
            {
                var pgo = new GameObject("GenPatrolPoint");
                pgo.transform.SetParent(wayGo.transform, false);
                pgo.transform.position = p;
                var pp = pgo.AddComponent<PatrolPoint>();
                try { pp.CanUseByBoss = true; } catch { }
                points++;
            }

            try
            {
                way.EditorInitPoints();   // fills Points from children, assigns ids
                way.InitPoints();         // back-refs: point.SetWay(this)
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[PatrolGen] zone '{zone.NameZone}': way init failed: {e.Message}");
                UnityEngine.Object.Destroy(wayGo);
                return 0;
            }

            zone.PatrolWays = new[] { way };
            return 1;
        }

        // greedy nearest-REACHABLE chain. the first version demanded each new point be
        // reachable from the previous one and then gave up on the candidate entirely if it
        // was not — with one ring of 12 samples that failed whole zones. walking the pool by
        // nearest-reachable instead means a blocked candidate is skipped, not fatal.
        private static List<Vector3> BuildRoute(BotZone zone, List<Vector3> library, ref int rejected)
        {
            var seeds = new List<Vector3>();
            foreach (var m in zone.SpawnPointMarkers)
                if (m != null) seeds.Add(m.transform.position);
            if (seeds.Count == 0) return new List<Vector3>();

            var centre = Vector3.zero;
            foreach (var s in seeds) centre += s;
            centre /= seeds.Count;
            // reach = how far the spawn markers themselves spread, plus padding. a big zone
            // gets a big search, a cupboard-sized one does not drag in the next room.
            float spread = 0f;
            foreach (var s in seeds) spread = Mathf.Max(spread, Vector3.Distance(s, centre));
            float reach = spread + ZonePad;

            var pool = new List<Vector3>(seeds);
            foreach (var p in library)
                if ((p - centre).sqrMagnitude <= reach * reach) pool.Add(p);
            // rings are now only a FALLBACK for zones the cover graph barely touched
            if (pool.Count < 8)
                foreach (var r in RingRadii)
                    for (int i = 0; i < RingSamples; i++)
                    {
                        float a = i * Mathf.PI * 2f / RingSamples;
                        pool.Add(centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r);
                    }

            // snap + dedupe, nearest to the zone centre first, then cap: path queries are the
            // expensive part and an unbounded pool on a 2135-point library would crawl
            var snapped = new List<Vector3>();
            foreach (var c in pool.OrderBy(p => (p - centre).sqrMagnitude))
            {
                if (snapped.Count >= MaxCandidates) break;
                NavMeshHit hit;
                if (!NavMesh.SamplePosition(c, out hit, SnapRadius, NavMesh.AllAreas)) continue;
                bool dupe = false;
                foreach (var s in snapped)
                    if ((s - hit.position).sqrMagnitude < 1f) { dupe = true; break; }
                if (!dupe) snapped.Add(hit.position);
            }
            if (snapped.Count == 0) return new List<Vector3>();

            // returns (route, rejectedCount) instead of touching the enclosing method's `ref
            // rejected` directly: a local function can't reference a ref/out/in parameter of
            // its enclosing method once it also captures anything else (CS1628) - `snapped`
            // being captured here is what triggers it.
            (List<Vector3> route, int rejectedCount) TryRoute(float minSpacing)
            {
                var route = new List<Vector3> { snapped[0] };
                var used = new HashSet<int> { 0 };
                var path = new NavMeshPath();
                int rejectedHere = 0;

                while (route.Count < MaxPointsPerWay)
                {
                    var from = route[route.Count - 1];
                    int best = -1; float bestD = float.MaxValue;
                    for (int i = 0; i < snapped.Count; i++)
                    {
                        if (used.Contains(i)) continue;
                        float d = Vector3.Distance(from, snapped[i]);
                        if (d < minSpacing || d > MaxSpacing || d >= bestD) continue;
                        // only pay for the path query on a candidate that could actually win
                        if (!NavMesh.CalculatePath(from, snapped[i], NavMesh.AllAreas, path)
                            || path.status != NavMeshPathStatus.PathComplete)
                        {
                            rejectedHere++;
                            // burn it permanently, not just this round: a failed PathComplete means
                            // a different navmesh component, and every later route point is reachable
                            // from this one, so it can never reach the candidate either
                            used.Add(i);
                            continue;
                        }
                        best = i; bestD = d;
                    }
                    if (best < 0) break;
                    used.Add(best);
                    route.Add(snapped[best]);
                }
                return (route, rejectedHere);
            }

            var (route, rejectedCount) = TryRoute(MinSpacing);
            rejected += rejectedCount;
            // CRAMPED-ROOM FALLBACK (08-29 field report: Wedge's own room, BotZoneRoomsThird,
            // logged "only 1 reachable point(s) — no way built" and he stood frozen the whole
            // fight). A room small enough that every candidate sits closer than MinSpacing (7m)
            // to the seed leaves the loop stuck at 1 point forever, even with plenty of
            // reachable candidates available. A short in-room shuffle beats a statue: retry
            // once with a much tighter floor before giving up on the zone entirely.
            if (route.Count < MinPointsPerWay && snapped.Count >= MinPointsPerWay)
            {
                var (route2, rejectedCount2) = TryRoute(MinSpacing * 0.25f);
                route = route2;
                rejected += rejectedCount2;
            }
            return route;
        }
    }
}
