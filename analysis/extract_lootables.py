# retail LootableContainer extraction: every lootable container from the staged retail
# levels with Id + Template (container item tpl) + world TRS + hierarchy path. feeds
# TWO consumers: an SDK editor rebake (components back into the bundle, doors-style)
# and a staticContainers.json generator for the server's loot pipeline. same raw-bytes
# pipeline as extract_doors.py — including the shared 1.0 base-class drift surgery.

import json
import re
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

TTH.read_typetree_boost = None

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
MANAGED = Path(r"D:\SPTDev\EscapeFromTarkov_Data\Managed")
OUT = Path(__file__).parent / "icebreaker_lootables.json"

LEVELS = [f"level{n}" for n in range(698, 710)]

TARGETS = {"LootableContainer", "LootableContainersGroup"}

TPL_RE = re.compile(r"^[0-9a-f]{24}$")

# same base-class drift as Door (they share the WorldInteractiveObject chain):
# 0.16.9's _mboitRenderers + DoorKeyOpenInteraction/Operatable u8s replaced in 1.0
# by 4 plain values. whether 1.0 ALSO appends a tail before the LootableContainer
# subclass fields is unknown until parse — the sanity checks below will scream.
def surgery(fl, drop_field, insert_after):
    fl = drop_field(fl, "_mboitRenderers")
    fl = drop_field(fl, "DoorKeyOpenInteraction")
    fl = drop_field(fl, "Operatable")
    fl = insert_after(fl, "NoInteractionsAllowed", [
        [1, "float", "retailExtra_f0", 0],
        [1, "int", "retailExtra_i0", 0],
        [1, "int", "retailDoorKeyOpen", 0],
        [1, "int", "retailOperatable", 0],
    ])
    return fl


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


def insert_after(fl, name, newrows, level=1):
    out, i, n = [], 0, len(fl)
    while i < n:
        out.append(fl[i])
        if fl[i][0] == level and fl[i][2] == name:
            j = i + 1
            while j < n and fl[j][0] > level:
                out.append(fl[j])
                j += 1
            out.extend(newrows)
            i = j
            continue
        i += 1
    return out


def sanitize(v, path_of_ref):
    if isinstance(v, dict):
        if "m_PathID" in v and "m_FileID" in v:
            pid = v["m_PathID"]
            if pid == 0:
                return None
            if v["m_FileID"] != 0:
                return {"externalRef": [v["m_FileID"], pid]}
            info = path_of_ref(pid)
            return {"refPath": info[0], "refType": info[1]} if info else {"unresolvedRef": pid}
        return {k: sanitize(x, path_of_ref) for k, x in v.items()}
    if isinstance(v, (list, tuple)):
        return [sanitize(x, path_of_ref) for x in v]
    if isinstance(v, bool) or isinstance(v, (int, str)) or v is None:
        return v
    if isinstance(v, float):
        return v if v == v and abs(v) != float("inf") else 0.0
    if hasattr(v, "__dict__"):
        return {k: sanitize(x, path_of_ref) for k, x in v.__dict__.items() if not k.startswith("_UnityPy")}
    return str(v)


def carve_lc_tail(raw):
    import struct
    m = None
    for cand in TPL_RE_B.finditer(raw):
        # the real anchor has its 4-byte length prefix (24) right before it
        if cand.start() >= 4 and struct.unpack_from("<i", raw, cand.start() - 4)[0] == 24:
            m = cand
            break
    if m is None:
        return None
    def align4(x):
        return (x + 3) & ~3
    off = align4(m.end())
    out = {"Template": m.group().decode()}
    def f3():
        nonlocal off
        v = struct.unpack_from("<3f", raw, off)
        off += 12
        return {"x": round(v[0], 4), "y": round(v[1], 4), "z": round(v[2], 4)}
    def i4():
        nonlocal off
        v = struct.unpack_from("<i", raw, off)[0]
        off += 4
        return v
    def s4():
        nonlocal off
        n = i4()
        v = raw[off:off + n].decode(errors="replace")
        off = align4(off + n)
        return v
    try:
        out["ClosedPosition"] = f3()
        out["OpenPosition"] = f3()
        out["LootableContainersGroupId"] = s4()
        out["IsAlwaysLootable"] = i4()
        out["IsAlwaysSpawn"] = i4()
        out["_isConverted"] = i4()
        out["SpawnType"] = i4()
        out["SpawnChance"] = i4()
        ndestroy = i4()
        out["GameObjectsToDestroyCount"] = ndestroy
        off += ndestroy * 12  # PPtr = int fileID + int64 pathID
        out["_openInteraction"] = i4()
        out["_closeInteraction"] = i4()
        out["ChanceModifier"] = round(struct.unpack_from("<f", raw, off)[0], 4); off += 4
        nfilter = i4()
        out["FilterExtendedCount"] = nfilter
        out["carve"] = "ok" if off <= len(raw) else "OVERRUN"
    except Exception as e:
        out["carve"] = f"failed: {e}"
    return out


TPL_RE_B = re.compile(rb"[0-9a-f]{24}")


def main():
    print("loading SPT Managed dlls for typetrees...")
    gen = TypeTreeGenerator("2022.3.43f2")
    gen.load_local_dll_folder(str(MANAGED))

    results = {}
    flagged = []
    insane = []

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

        def path_of(go):
            chain, t, hops = [], go_tid(go), 0
            while t in transforms and hops < 64:
                hops += 1
                chain.append(sib_key(t))
                t = parent.get(t, 0)
            return "/".join(reversed(chain))

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

        _world_cache = {}

        def world_trs(tid):
            if tid in _world_cache:
                return _world_cache[tid]
            tr = transforms[tid]
            lp2 = (tr.m_LocalPosition.x, tr.m_LocalPosition.y, tr.m_LocalPosition.z)
            lr = (tr.m_LocalRotation.x, tr.m_LocalRotation.y, tr.m_LocalRotation.z, tr.m_LocalRotation.w)
            ls = (tr.m_LocalScale.x, tr.m_LocalScale.y, tr.m_LocalScale.z)
            par = parent.get(tid, 0)
            if par not in transforms:
                res = (lp2, lr, ls)
            else:
                pp, pr, ps = world_trs(par)
                scaled = (lp2[0]*ps[0], lp2[1]*ps[1], lp2[2]*ps[2])
                rot = qrot(pr, scaled)
                res = ((pp[0]+rot[0], pp[1]+rot[1], pp[2]+rot[2]),
                       qmul(pr, lr),
                       (ps[0]*ls[0], ps[1]*ls[1], ps[2]*ls[2]))
            _world_cache[tid] = res
            return res

        def world_of(go):
            t = go_tid(go)
            if t not in transforms:
                return None, None
            p, r, _ = world_trs(t)
            return ([round(p[0], 3), round(p[1], 3), round(p[2], 3)],
                    [round(r[0], 6), round(r[1], 6), round(r[2], 6), round(r[3], 6)])

        ref_index = {}
        for pid, tr in transforms.items():
            g = gos.get(tr.m_GameObject.path_id)
            if g is not None:
                ref_index[pid] = (path_of(g), "Transform")
        for pid, g in gos.items():
            ref_index[pid] = (path_of(g), "GameObject")

        def path_of_ref(pid):
            return ref_index.get(pid)

        node_cache = {}
        n_level = 0
        for o in sf.objects.values():
            if o.type.name != "MonoBehaviour":
                continue
            try:
                mb = o.read(check_read=False)
                scr = mb.m_Script.read()
            except Exception:
                continue
            cls = scr.m_ClassName
            if cls not in TARGETS:
                continue
            go = gos.get(mb.m_GameObject.path_id)
            full = (scr.m_Namespace + "." if scr.m_Namespace else "") + cls
            wpos, wrot = world_of(go) if go else (None, None)
            row = {
                "level": level,
                "go": path_of(go) if go else "?",
                "world": wpos,
                "worldRot": wrot,
                "class": full,
            }
            try:
                key = (scr.m_AssemblyName, full)
                if key not in node_cache:
                    fl = flat(gen.get_nodes_up(scr.m_AssemblyName, full))
                    fl = surgery(fl, drop_field, insert_after)
                    node_cache[key] = to_tree(fl)
                data = o.read_typetree(node_cache[key], check_read=False)
                row["fields"] = sanitize({k: v for k, v in data.items()
                                          if k not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name")},
                                         path_of_ref)
                # the 1.0 sound arrays are structs (not bare PPtrs) — everything from
                # ShutSound to the rolloffs is misparsed, but the stream RE-ALIGNS at
                # the Template string. carve the LC-specific block by anchoring on the
                # 24-hex tpl (hand-verified layout on level704 PC blocks, raw offsets
                # 372..480: len+tpl, ClosedPos, OpenPos, GroupId str, 5 ints
                # (IsAlwaysLootable, IsAlwaysSpawn, _isConverted, SpawnType,
                # SpawnChance), GameObjectsToDestroy PPtr array, _openInteraction,
                # _closeInteraction as ints, ChanceModifier, FilterExtended count).
                if cls == "LootableContainer":
                    carved = carve_lc_tail(bytes(o.get_raw_data()))
                    if carved is None:
                        insane.append(f"{level} {row['go']}: no tpl anchor in raw bytes")
                    else:
                        row["fields"].update(carved)
                    cid = row["fields"].get("Id") or ""
                    if not cid:
                        insane.append(f"{level} {row['go']}: empty Id — drift")
                n_level += 1
            except Exception as e:
                row["parse_error"] = str(e)[:200]
                flagged.append(f"{level} {cls} FAILED: {str(e)[:90]}")
            results.setdefault(cls, []).append(row)
        if n_level:
            print(f"  {level}: {n_level} lootable components")

    OUT.write_text(json.dumps({"source": "retail 1.0 icebreaker levels 698-709", "components": results}, indent=1))
    print(f"wrote {OUT} ({OUT.stat().st_size // 1024} KB)")
    for cls, rows in results.items():
        fails = sum(1 for r in rows if "parse_error" in r)
        print(f"  {cls}: {len(rows)} total, {fails} failed")
    if insane:
        print(f"\nSANITY FAILURES ({len(insane)}) — 1.0 drift in the LC segment:")
        for s in insane[:12]:
            print("  " + s)
    if flagged:
        print("\nPARSE FAILURES (first 10):")
        for f in flagged[:10]:
            print("  " + f)


if __name__ == "__main__":
    main()
