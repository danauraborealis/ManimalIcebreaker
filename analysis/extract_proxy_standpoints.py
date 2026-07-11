# retail world-space stand points for the 11 keycard swiper proxies: the character's
# swipe position is proxy.TransformPoint(_interactionPosition), so a wrong RIPPED proxy
# rotation mirrors the player to the wrong side of the door (the slide-to-the-left bug).
# ripped GO positions verified to match retail (<0.5m) but rotations are suspect (same
# rip pass mangled the lightplanes) — so compute the stand point from RETAIL transforms
# and let the client re-derive a correct local offset per proxy at runtime.
# no typetrees needed: Transform/GameObject parse natively.

import json
from pathlib import Path

import UnityPy

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
DOORS_JSON = Path(__file__).parent / "icebreaker_doors.json"
OUT = Path(__file__).parent / "icebreaker_proxy_standpoints.json"

LEVELS = [f"level{n}" for n in range(698, 710)]


def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return (aw*bx + ax*bw + ay*bz - az*by,
            aw*by - ax*bz + ay*bw + az*bx,
            aw*bz + ax*by - ay*bx + az*bw,
            aw*bw - ax*bx - ay*by - az*bz)


def qrot(q, v):
    qv = (v[0], v[1], v[2], 0.0)
    qc = (-q[0], -q[1], -q[2], q[3])
    r = qmul(qmul(q, qv), qc)
    return (r[0], r[1], r[2])


def main():
    doors = json.loads(DOORS_JSON.read_text())
    prox_rows = doors["components"]["InteractiveProxy"]
    # swiper name (unique) -> its serialized local offsets
    offsets = {}
    for r in prox_rows:
        swiper = r["go"].split("/")[-2]
        ip = r["fields"].get("_interactionPosition") or {}
        vt = r["fields"].get("_viewTarget") or {}
        offsets[swiper] = (
            (ip.get("x", 0.0), ip.get("y", 0.0), ip.get("z", 0.0)),
            (vt.get("x", 0.0), vt.get("y", 0.0), vt.get("z", 0.0)),
        )

    out = {}
    for level in LEVELS:
        lp = LEVELS_DIR / level
        if not lp.exists():
            continue
        env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"), str(lp))
        sf = next(f for k, f in env.files.items() if str(k).endswith(level))

        gos, transforms = {}, {}
        for o in sf.objects.values():
            try:
                if o.type.name == "GameObject":
                    gos[o.path_id] = o.read()
                elif o.type.name in ("Transform", "RectTransform"):
                    transforms[o.path_id] = o.read()
            except Exception:
                pass
        parent = {pid: tr.m_Father.path_id for pid, tr in transforms.items()}

        cache = {}

        def world_trs(tid):
            if tid in cache:
                return cache[tid]
            tr = transforms[tid]
            lp_ = (tr.m_LocalPosition.x, tr.m_LocalPosition.y, tr.m_LocalPosition.z)
            lr = (tr.m_LocalRotation.x, tr.m_LocalRotation.y, tr.m_LocalRotation.z, tr.m_LocalRotation.w)
            ls = (tr.m_LocalScale.x, tr.m_LocalScale.y, tr.m_LocalScale.z)
            par = parent.get(tid, 0)
            if par not in transforms:
                res = (lp_, lr, ls)
            else:
                pp, pr, ps = world_trs(par)
                scaled = (lp_[0]*ps[0], lp_[1]*ps[1], lp_[2]*ps[2])
                rot = qrot(pr, scaled)
                res = ((pp[0]+rot[0], pp[1]+rot[1], pp[2]+rot[2]),
                       qmul(pr, lr),
                       (ps[0]*ls[0], ps[1]*ls[1], ps[2]*ls[2]))
            cache[tid] = res
            return res

        name_of = {}
        for pid, tr in transforms.items():
            g = gos.get(tr.m_GameObject.path_id)
            if g is not None:
                name_of[pid] = g.m_Name

        for pid, tr in transforms.items():
            if name_of.get(pid) != "proxy":
                continue
            par = parent.get(pid, 0)
            pname = name_of.get(par, "")
            if pname not in offsets:
                continue
            wp, wr, ws = world_trs(pid)
            ipo, vto = offsets[pname]

            def tp(off):
                s = (off[0]*ws[0], off[1]*ws[1], off[2]*ws[2])
                r = qrot(wr, s)
                return [round(wp[0]+r[0], 3), round(wp[1]+r[1], 3), round(wp[2]+r[2], 3)]

            out[pname] = {
                "level": level,
                "proxyWorld": [round(v, 3) for v in wp],
                "standWorld": tp(ipo),
                "viewWorld": tp(vto),
            }
            print(f"{pname:38s} proxy={out[pname]['proxyWorld']} stand={out[pname]['standWorld']}")

    OUT.write_text(json.dumps(out, indent=1))
    print(f"\n{len(out)} proxies -> {OUT}")


if __name__ == "__main__":
    main()
