#!/usr/bin/env python3
# spatial diff between a real (BSG) dump and a generated dump on the same map.
# answers two questions the aggregate stats cant:
#   1. coverage — do we find the spots BSG found? (real points with a gen point nearby)
#   2. excess — where are our extra points? interleaved along BSG walls (tuning:
#      raise cluster distances) vs in BSG-empty areas (placement filter needed)
# usage: python compare_dumps.py <real.json> <generated.json>
import json
import math
import sys
from collections import Counter, defaultdict


def v(d):
    return (d["x"], d["y"], d["z"])


def build_hash(points, cell):
    grid = defaultdict(list)
    for p in points:
        grid[(int(p[0] // cell), int(p[1] // cell), int(p[2] // cell))].append(p)
    return grid


def nn_dist(p, grid, cell, max_r=3):
    best = None
    kx, ky, kz = int(p[0] // cell), int(p[1] // cell), int(p[2] // cell)
    for r in range(max_r + 1):
        for dx in range(-r, r + 1):
            for dy in range(-r, r + 1):
                for dz in range(-r, r + 1):
                    if max(abs(dx), abs(dy), abs(dz)) != r:
                        continue
                    for q in grid.get((kx + dx, ky + dy, kz + dz), ()):
                        d = math.dist(p, q)
                        if best is None or d < best:
                            best = d
        if best is not None and best <= r * cell:
            break
    return best if best is not None else float("inf")


def quantiles(vals):
    if not vals:
        return "n/a"
    s = sorted(vals)
    def q(f):
        return s[min(len(s) - 1, int(f * len(s)))]
    return f"p25={q(.25):.2f} p50={q(.5):.2f} p75={q(.75):.2f} p95={q(.95):.2f}"


def main(real_file, gen_file):
    real = json.load(open(real_file))
    gen = json.load(open(gen_file))
    rp = [v(p["position"]) for p in real["groupPoints"]]
    gp = [v(p["position"]) for p in gen["groupPoints"]]
    cell = 4.0
    rhash = build_hash(rp, cell)
    ghash = build_hash(gp, cell)

    print(f"real: {len(rp)} points   generated: {len(gp)} points   ratio {len(gp)/len(rp):.2f}x")

    # 1. coverage of BSG's points by ours
    dists = [nn_dist(p, ghash, cell) for p in rp]
    for r in (0.5, 1.0, 2.0, 4.0):
        n = sum(1 for d in dists if d <= r)
        print(f"  real points with a generated point within {r}m: {n} ({100*n/len(rp):.1f}%)")
    print(f"  real->nearest-generated: {quantiles(dists)}")
    far_real = [p for p, d in zip(rp, dists) if d > 4.0]
    if far_real:
        ys = Counter(round(p[1]) for p in far_real)
        print(f"  uncovered real points by height y: {dict(sorted(ys.items()))}")

    # 2. our excess relative to BSG
    gdists = [nn_dist(p, rhash, cell) for p in gp]
    for r in (1.0, 2.0, 4.0):
        n = sum(1 for d in gdists if d > r)
        print(f"  generated points with NO real point within {r}m: {n} ({100*n/len(gp):.1f}%)")
    print(f"  generated->nearest-real: {quantiles(gdists)}")
    far_gen = [p for p, d in zip(gp, gdists) if d > 4.0]
    if far_gen:
        ys = Counter(round(p[1]) for p in far_gen)
        print(f"  excess generated points (no real within 4m) by height y: {dict(sorted(ys.items()))}")
        # coarse xz clusters of the excess so they can be found in-game
        cl = Counter((int(p[0] // 20) * 20, int(p[2] // 20) * 20) for p in far_gen)
        print(f"  top excess 20m-cells (x,z): {cl.most_common(8)}")

    # local density comparison inside mutually covered space
    both = [d for d in gdists if d <= 2.0]
    print(f"  generated points in BSG-covered space (<=2m): {len(both)} ({100*len(both)/len(gp):.1f}%)")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
