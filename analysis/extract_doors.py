# retail Door rebake, editor-side: extract every EFT.Interactive.Door (and friends)
# from ALL staged retail levels with full typetree fields + refs resolved to hierarchy
# paths. consumed by ApplyRetailDoors.cs (unity editor script) which strips the
# hand-placed Door components and rebakes them from this data. same raw-bytes pipeline
# as the aibake/weather/audio extractions.

import json
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

TTH.read_typetree_boost = None

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
MANAGED = Path(r"D:\SPTDev\EscapeFromTarkov_Data\Managed")
OUT = Path(__file__).parent / "icebreaker_doors.json"

# scene levels on disk (698=Scripts .. 709; .resS are data siblings)
LEVELS = [f"level{n}" for n in range(698, 710)]

# the rebake set. Door is the workhorse; KeycardDoor derives from it (same drift).
# anything ELSE deriving from WorldInteractiveObject gets counted + reported.
# swiper anatomy (security_pass_card_*): InteractiveProxy on 'proxy' (Link -> the
# KeycardDoor), DoorHandle on 'proxy/Lock', GripPose on 'proxy/Lock/KeyGrip' (swipe
# hand pose), GameObjectStateSync (lab only). all plain MonoBehaviours in 0.16.9.
TARGETS = {"Door", "SlidingDoor", "DoorSwitch", "KeycardDoor", "Switch",
           "InteractiveProxy", "DoorHandle", "GripPose", "GameObjectStateSync"}

# retail 1.0 Door drift (hand-walked on level700): 0.16.9's _mboitRenderers array +
# DoorKeyOpenInteraction/Operatable u8s are GONE; in their place 4 plain values
# (float -1, int 0, int DoorKeyOpen-ish, int Operatable-ish); then ~308 bytes of
# 1.0-only TAIL after _occlusionPortal which we don't need -> loose by design.
def door_surgery(fl, drop_field, insert_after):
    fl = drop_field(fl, "_mboitRenderers")
    fl = drop_field(fl, "DoorKeyOpenInteraction")
    fl = drop_field(fl, "Operatable")
    fl = insert_after(fl, "NoInteractionsAllowed", [
        [1, "float", "retailExtra_f0", 0],
        [1, "int", "retailExtra_i0", 0],
        [1, "int", "retailDoorKeyOpen", 0],
        [1, "int", "retailOperatable", 0],
    ])
    # KeycardDoor subclass fields sit AFTER Door's variable-size 1.0 tail — misaligned
    # garbage in a loose parse. byte-level check across all 11: every one holds class
    # DEFAULTS (empty keys, both flags false), so dropping them is lossless.
    for f in ("_additionalKeys", "_openOnUnlock", "_lockOnShut",
              "DeniedBeep", "GrantedBeep", "UnlockSound"):
        fl = drop_field(fl, f)
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


def sanitize(v, refs, path_of_ref):
    if isinstance(v, dict):
        if "m_PathID" in v and "m_FileID" in v:
            pid = v["m_PathID"]
            if pid == 0:
                return None
            if v["m_FileID"] != 0:
                # external bundle asset (audio clips, fx) — pathID is only meaningful in
                # THAT file; never resolve against level objects. editor skips these.
                return {"externalRef": [v["m_FileID"], pid]}
            info = path_of_ref(pid)
            return {"refPath": info[0], "refType": info[1]} if info else {"unresolvedRef": pid}
        return {k: sanitize(x, refs, path_of_ref) for k, x in v.items()}
    if isinstance(v, (list, tuple)):
        return [sanitize(x, refs, path_of_ref) for x in v]
    if isinstance(v, bool) or isinstance(v, (int, str)) or v is None:
        return v
    if isinstance(v, float):
        return v if v == v and abs(v) != float("inf") else 0.0
    if hasattr(v, "__dict__"):
        return {k: sanitize(x, refs, path_of_ref) for k, x in v.__dict__.items() if not k.startswith("_UnityPy")}
    return str(v)


def main():
    print("loading SPT Managed dlls for typetrees...")
    gen = TypeTreeGenerator("2022.3.43f2")
    gen.load_local_dll_folder(str(MANAGED))

    results = {}
    counted = {}
    flagged = []

    for level in LEVELS:
        lp = LEVELS_DIR / level
        if not lp.exists():
            continue
        env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"), str(lp))
        sf = next(f for k, f in env.files.items() if str(k).endswith(level))

        gos, transforms = {}, {}
        comp_owner = {}  # component path_id -> (go path_id, class name)
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

        # sibling occurrence index among SAME-NAMED siblings — retail scenes contain
        # exact-duplicate names (five 'Exterior_door_02_door' under one wrapper), so a
        # bare name path is ambiguous. child ORDER survives the rip, so 'name~k' is
        # deterministic on both sides. k omitted for the first occurrence (back-compat).
        def sib_key(tid):
            g = gos.get(transforms[tid].m_GameObject.path_id)
            name = g.m_Name if g else "?"
            par = parent.get(tid, 0)
            if par not in transforms:
                return name  # scene roots: duplicates rare, keep simple
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

        # TRUE world position: full TRS composition up the chain (door wrappers are
        # rotated all over the ship — naive local-position sums are wrong for them).
        # cross-reference matcher in the editor uses this to verify/fallback.
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
            lp = (tr.m_LocalPosition.x, tr.m_LocalPosition.y, tr.m_LocalPosition.z)
            lr = (tr.m_LocalRotation.x, tr.m_LocalRotation.y, tr.m_LocalRotation.z, tr.m_LocalRotation.w)
            ls = (tr.m_LocalScale.x, tr.m_LocalScale.y, tr.m_LocalScale.z)
            par = parent.get(tid, 0)
            if par not in transforms:
                res = (lp, lr, ls)
            else:
                pp, pr, ps = world_trs(par)
                scaled = (lp[0]*ps[0], lp[1]*ps[1], lp[2]*ps[2])
                rot = qrot(pr, scaled)
                res = ((pp[0]+rot[0], pp[1]+rot[1], pp[2]+rot[2]),
                       qmul(pr, lr),
                       (ps[0]*ls[0], ps[1]*ls[1], ps[2]*ls[2]))
            _world_cache[tid] = res
            return res

        def world_of(go):
            t = go_tid(go)
            if t not in transforms:
                return None
            p = world_trs(t)[0]
            return [round(p[0], 3), round(p[1], 3), round(p[2], 3)]

        # index every component + transform + GO by path_id so PPtr refs resolve to a
        # hierarchy path + component type the editor can re-find
        ref_index = {}
        for pid, tr in transforms.items():
            g = gos.get(tr.m_GameObject.path_id)
            if g is not None:
                ref_index[pid] = (path_of(g), "Transform")
        for pid, g in gos.items():
            ref_index[pid] = (path_of(g), "GameObject")
        for o in sf.objects.values():
            if o.path_id in ref_index or o.type.name in ("GameObject", "Transform", "RectTransform"):
                continue
            try:
                if o.type.name == "MonoBehaviour":
                    # DoorHandle etc — refType = script class so the editor can GetComponent it
                    mb2 = o.read(check_read=False)
                    scr2 = mb2.m_Script.read()
                    g2 = gos.get(mb2.m_GameObject.path_id)
                    if g2 is not None:
                        ref_index[o.path_id] = (path_of(g2), scr2.m_ClassName)
                    continue
                obj = o.read()
                g = gos.get(obj.m_GameObject.path_id) if hasattr(obj, "m_GameObject") else None
                if g is not None:
                    ref_index[o.path_id] = (path_of(g), o.type.name)
            except Exception:
                pass

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
            counted[cls] = counted.get(cls, 0) + 1 if cls in ("Door", "SlidingDoor", "DoorSwitch", "KeycardDoor", "Switch", "WorldInteractiveObject") else counted.get(cls, 0)
            if cls not in TARGETS:
                continue
            go = gos.get(mb.m_GameObject.path_id)
            full = (scr.m_Namespace + "." if scr.m_Namespace else "") + cls
            row = {
                "level": level,
                "go": path_of(go) if go else "?",
                "world": world_of(go) if go else None,
                "class": full,
            }
            try:
                key = (scr.m_AssemblyName, full)
                if key not in node_cache:
                    fl = flat(gen.get_nodes_up(scr.m_AssemblyName, full))
                    fl = door_surgery(fl, drop_field, insert_after)
                    node_cache[key] = to_tree(fl)
                # 1.0 appends a ~308B tail after _occlusionPortal we don't carry — parse
                # loose ALWAYS; correctness of the aligned region was hand-verified.
                data = o.read_typetree(node_cache[key], check_read=False)
                row["parse"] = "loose-by-design (1.0 tail dropped)"
                row["fields"] = sanitize({k: v for k, v in data.items()
                                          if k not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name")},
                                         None, path_of_ref)
                n_level += 1
            except Exception as e:
                row["parse_error"] = str(e)[:200]
                flagged.append(f"{level} {cls} FAILED: {str(e)[:90]}")
            results.setdefault(cls, []).append(row)
        if n_level:
            print(f"  {level}: {n_level} door components")

    OUT.write_text(json.dumps({"source": "retail 1.0 icebreaker levels 698-709", "components": results}, indent=1))
    print(f"wrote {OUT} ({OUT.stat().st_size // 1024} KB)")
    for cls, rows in results.items():
        loose = sum(1 for r in rows if r.get("parse") != "byte-exact")
        print(f"  {cls}: {len(rows)} total, {loose} loose/failed")
    print("  WIO-family sightings:", {k: v for k, v in counted.items() if v})
    if flagged:
        print("\nDRIFT DETAIL (first 10):")
        for f in flagged[:10]:
            print("  " + f)


if __name__ == "__main__":
    main()
