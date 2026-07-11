#!/usr/bin/env python3
# analyzes AIDataDumper json dumps and prints the stats a custom-map point baker
# needs to replicate: point spacing/density, field conventions, graph shape,
# voxel layout invariants. usage: python analyze_dump.py <dump.json> [more.json ...]
import json
import math
import sys
from collections import Counter, defaultdict

CELL_X, CELL_Y, CELL_Z = 10.0, 5.0, 10.0


def v(d):
    return (d["x"], d["y"], d["z"])


def dist(a, b):
    return math.sqrt((a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2 + (a[2] - b[2]) ** 2)


def pct(n, total):
    return f"{n} ({100.0 * n / total:.1f}%)" if total else "0"


def quantiles(vals, qs=(0.05, 0.25, 0.5, 0.75, 0.95)):
    if not vals:
        return "n/a"
    s = sorted(vals)
    out = []
    for q in qs:
        i = min(len(s) - 1, int(q * len(s)))
        out.append(f"p{int(q*100)}={s[i]:.2f}")
    return f"min={s[0]:.2f} " + " ".join(out) + f" max={s[-1]:.2f} mean={sum(s)/len(s):.2f}"


def nearest_neighbor_dists(points):
    # grid-hash so this stays fast on tens of thousands of points
    cell = 4.0
    grid = defaultdict(list)
    for p in points:
        key = (int(p[0] // cell), int(p[1] // cell), int(p[2] // cell))
        grid[key].append(p)
    out = []
    for p in points:
        kx, ky, kz = int(p[0] // cell), int(p[1] // cell), int(p[2] // cell)
        best = None
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    for q in grid.get((kx + dx, ky + dy, kz + dz), ()):
                        if q is p:
                            continue
                        d = dist(p, q)
                        if best is None or d < best:
                            best = d
        if best is not None:
            out.append(best)
    return out


def analyze(path):
    d = json.load(open(path))
    meta = d["meta"]
    gps = d["groupPoints"]
    cps = d["corePoints"]
    ways = d["ways"]
    vg = d["voxelGrid"]
    cells = vg["cells"]
    print("=" * 78)
    print(f"MAP: {meta['location']}   points={len(gps)} core={len(cps)} ways={len(ways)} voxels={len(cells)}")
    print("=" * 78)

    # --- group point field conventions ---
    print("\n## GroupPoint field conventions")
    for field in ("coverLevel", "coverType", "neighborType", "special", "environmentType"):
        print(f"  {field}: {dict(Counter(p[field] for p in gps))}")
    n = len(gps)
    print(f"  alwaysGood: {pct(sum(1 for p in gps if p['alwaysGood']), n)}")
    print(f"  isGoodInsideBuilding: {pct(sum(1 for p in gps if p['isGoodInsideBuilding']), n)}")
    print(f"  altPosition set: {pct(sum(1 for p in gps if p['altPosition']), n)}")

    # wallDirection: normalized? horizontal?
    mags = [math.sqrt(p["wallDirection"]["x"] ** 2 + p["wallDirection"]["y"] ** 2 + p["wallDirection"]["z"] ** 2) for p in gps]
    zeros = sum(1 for m in mags if m < 1e-6)
    ys = [abs(p["wallDirection"]["y"]) / m for p, m in zip(gps, mags) if m > 1e-6]
    print(f"  wallDirection zero: {pct(zeros, n)}; magnitude: {quantiles(mags)}")
    print(f"  wallDirection |y|/mag (0 = horizontal): {quantiles(ys)}")

    # firePosition relative to position
    fp_dy = [p["firePosition"]["y"] - p["position"]["y"] for p in gps]
    fp_dh = [math.sqrt((p["firePosition"]["x"] - p["position"]["x"]) ** 2 + (p["firePosition"]["z"] - p["position"]["z"]) ** 2) for p in gps]
    print(f"  firePos dY: {quantiles(fp_dy)}")
    print(f"  firePos horizontal offset: {quantiles(fp_dh)}")
    dy_counter = Counter(round(x, 3) for x in fp_dy)
    print(f"  firePos dY top values: {dy_counter.most_common(5)}")

    # altPosition offset when present
    alt = [p for p in gps if p["altPosition"]]
    if alt:
        ady = [p["altPosition"]["y"] - p["position"]["y"] for p in alt]
        adh = [math.sqrt((p["altPosition"]["x"] - p["position"]["x"]) ** 2 + (p["altPosition"]["z"] - p["position"]["z"]) ** 2) for p in alt]
        print(f"  altPos dY: {quantiles(ady)}")
        print(f"  altPos horizontal offset: {quantiles(adh)}")

    print(f"  defenceLevel: {quantiles([p['defenceLevel'] for p in gps])}")
    dl_hist = Counter(int(p["defenceLevel"]) // 8 * 8 for p in gps)
    print(f"  defenceLevel histogram (bucket=8): {dict(sorted(dl_hist.items()))}")

    # --- spacing / density ---
    print("\n## Spacing & density")
    pts = [v(p["position"]) for p in gps]
    nn = nearest_neighbor_dists(pts)
    print(f"  nearest-neighbor dist: {quantiles(nn)}")
    nav_cols = {(c["ix"], c["iz"]) for c in cells if c["haveNavMesh"]}
    nav_cells = sum(1 for c in cells if c["haveNavMesh"])
    if nav_cols:
        area = len(nav_cols) * CELL_X * CELL_Z
        print(f"  navmesh voxel columns: {len(nav_cols)} (~{area:.0f} m^2 footprint)")
        print(f"  cover density: {len(gps)/area*100:.2f} points per 100 m^2 of footprint")

    # --- connection groups & core points ---
    print("\n## Connectivity")
    cg = Counter(p["connectionGroup"] for p in gps)
    print(f"  connectionGroups: {len(cg)}; sizes: {dict(cg.most_common(10))}{'...' if len(cg) > 10 else ''}")
    core_cg = {c["id"]: c["connectionGroupId"] for c in cps}
    mismatch = sum(1 for p in gps if core_cg.get(p["corePointId"]) not in (None, p["connectionGroup"]))
    orphan = sum(1 for p in gps if p["corePointId"] not in core_cg)
    print(f"  points whose connectionGroup != their corePoint's group: {mismatch}; corePointId not found: {orphan}")
    per_core = Counter(p["corePointId"] for p in gps)
    print(f"  group points per core point: {quantiles(list(per_core.values()), (0.25, 0.5, 0.75))}")
    core_deg = [len(c["connections"]) for c in cps]
    print(f"  core point degree: {dict(Counter(core_deg))}")
    core_pts = [v(c["position"]) for c in cps]
    if len(core_pts) > 1:
        print(f"  core point NN dist: {quantiles(nearest_neighbor_dists(core_pts))}")

    # --- ways graph ---
    print("\n## Ways graph")
    way_by_id = {w["id"]: w for w in ways}
    degs = [len(p["waysIds"]) for p in gps]
    print(f"  degree (waysIds per point): {quantiles(degs)}; deg=0: {pct(sum(1 for x in degs if x == 0), n)}")
    print(f"  way dist: {quantiles([w['dist'] for w in ways])}")
    # reciprocity + path-vs-euclidean stretch
    owner = {}
    for p in gps:
        for wid in p["waysIds"]:
            owner[wid] = p["id"]
    gp_by_id = {p["id"]: p for p in gps}
    edges = set()
    stretch = []
    for w in ways:
        o = owner.get(w["id"])
        if o is None:
            continue
        edges.add((o, w["idTarget"]))
        a, b = gp_by_id.get(o), gp_by_id.get(w["idTarget"])
        if a and b and w["dist"] > 0:
            e = dist(v(a["position"]), v(b["position"]))
            if e > 0.5:
                stretch.append(w["dist"] / e)
    recip = sum(1 for (a, b) in edges if (b, a) in edges)
    print(f"  edges with reverse edge: {pct(recip, len(edges))}")
    print(f"  path-dist / euclidean stretch: {quantiles(stretch)}")

    # --- voxel invariants ---
    print("\n## Voxel grid invariants")
    mn = v(vg["min"])
    bad_pos = 0
    for c in cells:
        exp = (mn[0] + c["ix"] * CELL_X, mn[1] + c["iy"] * CELL_Y, mn[2] + c["iz"] * CELL_Z)
        if dist(v(c["position"]), exp) > 0.01:
            bad_pos += 1
    print(f"  cells where position != min + idx*cellsize: {bad_pos}/{len(cells)}")
    print(f"  cells with navmesh: {pct(nav_cells, len(cells))}; with points: {pct(sum(1 for c in cells if c['pointsIds']), len(cells))}")
    ppc = [len(c["pointsIds"]) for c in cells if c["pointsIds"]]
    print(f"  points per non-empty cell: {quantiles(ppc)}")
    # membership: does every point sit inside a cell that lists it?
    cell_by_idx = {(c["ix"], c["iy"], c["iz"]): c for c in cells}
    misplaced = missing = 0
    for p in gps:
        pp = v(p["position"])
        idx = (int((pp[0] - mn[0]) // CELL_X), int((pp[1] - mn[1]) // CELL_Y), int((pp[2] - mn[2]) // CELL_Z))
        c = cell_by_idx.get(idx)
        if c is None:
            missing += 1
        elif p["id"] not in c["pointsIds"]:
            misplaced += 1
    print(f"  points not in any listed cell: {missing}; in a cell that doesnt list them: {misplaced}")
    closest_ok = sum(1 for c in cells if c["_closetsPointId" if "_closetsPointId" in c else "closestPointId"] in
                     (c["pointsIds"][0] if c["pointsIds"] else None, *(c["pointsIds"] or [None])))
    print(f"  cells whose closestPointId is one of its own pointsIds: {closest_ok}/{len(cells)}")

    # --- zones / patrol summary ---
    print("\n## Zones & patrols")
    for z in d["botZones"]:
        pw = z["patrolWays"]
        npts = sum(len(w["points"]) for w in pw)
        print(f"  zone '{z['nameZone']}' id={z['id']}: spawns={len(z['spawnPoints'])} patrolWays={len(pw)} patrolPoints={npts} snipe={z['snipeZone']} boss={z['canSpawnBoss']}")
    print()


if __name__ == "__main__":
    for f in sys.argv[1:]:
        analyze(f)
