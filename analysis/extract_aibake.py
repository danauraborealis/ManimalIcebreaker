# resurrection #5: BSG's baked AI data from retail level706 — AICoversData (the 390KB
# cover bake: GroupPoints/Ways/Pathes), AIVoxelesData (voxel grid), AIPatrolsData
# (loot/exfil points), AICorePointHolder + the 20 AICorePoints. replaces the runtime
# generation (CoverScanner/BuildVoxelGrid) with retail-authored data on this map.
# same raw-bytes pipeline as spatial audio / weather / aiplaces.

import json
from collections import Counter
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

TTH.read_typetree_boost = None

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
MANAGED = Path(r"D:\SPTDev\EscapeFromTarkov_Data\Managed")
OUT = Path(__file__).parent / "icebreaker_aibake.json"
LEVEL = "level706"  # Icebreaker_AI

TARGETS = {
    "AICoversData", "AIVoxelesData", "AIPatrolsData", "AICorePointHolder", "AICorePoint",
    "AIManualPointsHolder", "AIMinesPositionsHolder", "AIDangerPlacesHolder",
    "BotZoneEntranceInfo", "AIMinePoint", "NavMeshDoorLink",
}
# ref-name tables so runtime can resolve PPtrs to our authored scene components
NAME_TABLE = {"BotZone", "PatrolWay", "PatrolPoint", "Door"}


# retail drift surgeries (hand-verified):
#   AICoversData: element data (GroupPoint 152B incl ways/ids) aligns byte-perfect —
#     the drift is the EDITOR DEBUG TAIL (Draw*/NetSteps/... strings) which retail
#     dropped. we don't need it: truncate the tree after _lastId and parse loose.
#   AIVoxelesData: generator emits the [,,] VoxelesArray field which unity never
#     serializes — drop it.
#   AIMinePoint: retail added ONE trailing 4-byte field.
def flat(n, lvl=0, out=None):
    if out is None:
        out = []
    out.append([lvl, n.m_Type, n.m_Name, n.m_MetaFlag or 0])
    for c in (n.m_Children or []):
        flat(c, lvl + 1, out)
    return out


def to_tree(fl):
    from UnityPy.helpers.TypeTreeNode import TypeTreeNode
    return TypeTreeNode.from_list([TypeTreeNode(l, t, n, 0, 0, m_MetaFlag=m) for l, t, n, m in fl])


def drop_field(fl, name, level=1):
    out, skip = [], False
    for row in fl:
        if skip:
            if row[0] > level:
                continue
            skip = False
        if row[0] == level and row[2] == name:
            skip = True
            continue
        out.append(row)
    return out


def truncate_after(fl, name, level=1):
    out, i, n = [], 0, len(fl)
    while i < n:
        out.append(fl[i])
        if fl[i][0] == level and fl[i][2] == name:
            i += 1
            while i < n and fl[i][0] > level:
                out.append(fl[i])
                i += 1
            return out
        i += 1
    return out


LOOSE_OK = set()  # classes where a loose parse is accepted by design


def patched_nodes(gen, asm, full, cls):
    nodes = gen.get_nodes_up(asm, full)
    fl = flat(nodes)
    if cls == "AICoversData":
        fl = truncate_after(fl, "_lastId")
        LOOSE_OK.add(cls)  # tail dropped on purpose — byte counts won't match
    elif cls == "AIVoxelesData":
        fl = drop_field(fl, "VoxelesArray")
    elif cls == "AIMinePoint":
        fl = fl + [[1, "int", "retailExtra_0", 0]]
    return to_tree(fl)


def sanitize(v):
    if isinstance(v, dict):
        if "m_PathID" in v and "m_FileID" in v:
            return {"ref": v["m_PathID"], "file": v["m_FileID"]} if v["m_FileID"] else {"ref": v["m_PathID"]}
        return {k: sanitize(x) for k, x in v.items()}
    if isinstance(v, (list, tuple)):
        return [sanitize(x) for x in v]
    if isinstance(v, bool) or isinstance(v, (int, str)) or v is None:
        return v
    if isinstance(v, float):
        return v if v == v and abs(v) != float("inf") else 0.0
    if hasattr(v, "__dict__"):
        return {k: sanitize(x) for k, x in v.__dict__.items() if not k.startswith("_UnityPy")}
    return str(v)


def main():
    print("loading SPT Managed dlls for typetrees...")
    gen = TypeTreeGenerator("2022.3.43f2")
    gen.load_local_dll_folder(str(MANAGED))

    env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"), str(LEVELS_DIR / LEVEL))
    sf = next(f for k, f in env.files.items() if str(k).endswith(LEVEL))

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

    def path_of(go):
        chain, t, hops = [], go_tid(go), 0
        while t in transforms and hops < 64:
            hops += 1
            g = gos.get(transforms[t].m_GameObject.path_id)
            chain.append(g.m_Name if g else "?")
            t = parent.get(t, 0)
        return "/".join(reversed(chain))

    def world_of(go):
        x = y = z = 0.0
        t, hops = go_tid(go), 0
        while t in transforms and hops < 64:
            hops += 1
            p = transforms[t].m_LocalPosition
            x += p.x; y += p.y; z += p.z
            t = parent.get(t, 0)
        return [round(x, 3), round(y, 3), round(z, 3)]

    node_cache, results, flagged = {}, {}, []
    ref_names = {}  # path_id -> {class, go, world} for PatrolWay/BotZone/etc

    for o in sf.objects.values():
        if o.type.name != "MonoBehaviour":
            continue
        try:
            mb = o.read(check_read=False)
            scr = mb.m_Script.read()
        except Exception:
            continue
        cls = scr.m_ClassName
        go = gos.get(mb.m_GameObject.path_id)
        if cls in NAME_TABLE:
            if go is not None:
                ref_names[str(o.path_id)] = {"class": cls, "go": path_of(go), "world": world_of(go)}
            continue
        if cls not in TARGETS:
            continue
        full = (scr.m_Namespace + "." if scr.m_Namespace else "") + cls
        row = {
            "path_id": o.path_id,
            "go": path_of(go) if go else "?",
            "world": world_of(go) if go else None,
            "class": full,
            "bytes": len(o.get_raw_data()),
        }
        try:
            key = (scr.m_AssemblyName, full)
            if key not in node_cache:
                node_cache[key] = patched_nodes(gen, scr.m_AssemblyName, full, cls)
            try:
                data = o.read_typetree(node_cache[key], check_read=cls not in LOOSE_OK)
                row["parse"] = "byte-exact" if cls not in LOOSE_OK else "loose-by-design (tail dropped)"
            except Exception as e1:
                data = o.read_typetree(node_cache[key], check_read=False)
                row["parse"] = "FLAGGED"
                flagged.append(f"{cls} ({len(o.get_raw_data())}B): {str(e1)[:80]}")
            row["fields"] = sanitize({k: v for k, v in data.items()
                                      if k not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name")})
        except Exception as e:
            row["parse_error"] = str(e)[:200]
            flagged.append(f"{cls} FAILED: {str(e)[:80]}")
        results.setdefault(cls, []).append(row)

    out = {
        "source": "retail 1.0 level706 baked AI data",
        "ref_names": ref_names,
        "components": results,
    }
    OUT.write_text(json.dumps(out, indent=1))
    print(f"wrote {OUT} ({OUT.stat().st_size // 1024} KB)")
    for cls in sorted(results):
        rows = results[cls]
        fl = sum(1 for r in rows if r.get("parse") == "FLAGGED" or r.get("parse_error"))
        tot = sum(r["bytes"] for r in rows)
        print(f"  {cls}: {len(rows)} ({tot//1024}KB)" + (f"  <-- {fl} FLAGGED" if fl else "  byte-exact"))
    print(f"  ref-name table: {len(ref_names)} entries")
    if flagged:
        print("\nDRIFT DETAIL:")
        for f in flagged[:8]:
            print("  " + f)


if __name__ == "__main__":
    main()
