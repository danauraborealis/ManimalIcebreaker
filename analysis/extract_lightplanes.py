# light-shaft planes (Lightplane*) exported with corrupted transforms (aiming AT the
# ship from the sky instead of shafting out of it). grab the retail LOCAL TRS for every
# Lightplane GO, keyed by ordinal hierarchy path — consumed by the Fix Lightplanes
# editor pass in IcebreakerRetailDoors.cs.

import json
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH

TTH.read_typetree_boost = None

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
OUT = Path(__file__).parent / "icebreaker_lightplanes.json"

rows = []
for n in range(698, 710):
    lp = LEVELS_DIR / f"level{n}"
    if not lp.exists():
        continue
    env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"), str(lp))
    sf = next(f for k, f in env.files.items() if str(k).endswith(f"level{n}"))
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

    def go_tid(go):
        for comp in go.m_Component:
            c = comp.component if hasattr(comp, "component") else comp[1]
            if c.path_id in transforms:
                return c.path_id
        return 0

    def sib_key(tid):
        g = gos.get(transforms[tid].m_GameObject.path_id)
        name = g.m_Name if g else "?"
        par = parent.get(tid, 0)
        if par not in transforms:
            return name
        k = 0
        for ch in (transforms[par].m_Children or []):
            cpid = ch.path_id
            if cpid == tid:
                break
            cg = gos.get(transforms[cpid].m_GameObject.path_id) if cpid in transforms else None
            if cg is not None and cg.m_Name == name:
                k += 1
        return name if k == 0 else f"{name}~{k}"

    def path_of(tid):
        chain, t, hops = [], tid, 0
        while t in transforms and hops < 64:
            hops += 1
            chain.append(sib_key(t))
            t = parent.get(t, 0)
        return "/".join(reversed(chain))

    for pid, tr in transforms.items():
        g = gos.get(tr.m_GameObject.path_id)
        if g is None or not g.m_Name.startswith("Lightplane"):
            continue
        rows.append({
            "level": f"level{n}",
            "path": path_of(pid),
            "localPosition": {"x": tr.m_LocalPosition.x, "y": tr.m_LocalPosition.y, "z": tr.m_LocalPosition.z},
            "localRotation": {"x": tr.m_LocalRotation.x, "y": tr.m_LocalRotation.y,
                              "z": tr.m_LocalRotation.z, "w": tr.m_LocalRotation.w},
            "localScale": {"x": tr.m_LocalScale.x, "y": tr.m_LocalScale.y, "z": tr.m_LocalScale.z},
        })
    if rows and rows[-1]["level"] == f"level{n}":
        print(f"level{n}: {sum(1 for r in rows if r['level'] == f'level{n}')} lightplanes")

OUT.write_text(json.dumps({"lightplanes": rows}, indent=1))
print(f"wrote {OUT} — {len(rows)} lightplanes total")
