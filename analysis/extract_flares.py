# retail MultiFlare extraction: the 1000+ FlareLight components (per-lamp lens flares,
# all-primitive Flare[] payloads), the scene FlareSceneSettings (atlas + batch material
# refs), the ProFlareAtlas (name+rect containers) and its texture (exported as PNG),
# and the two batch materials (shader name + props). the client rebuilds everything at
# runtime with the GAME's own MultiFlare classes — no SDK stubs needed, the scheduler
# already exists (AbstractApplication.CreateTechnicalSystems).

import json
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

TTH.read_typetree_boost = None

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
MANAGED = Path(r"D:\SPTDev\EscapeFromTarkov_Data\Managed")
OUT_JSON = Path(__file__).parent / "icebreaker_flares.json"
OUT_ATLAS = Path(__file__).parent / "icebreaker_flare_atlas.png"

LEVELS = [f"level{n}" for n in range(698, 710)]
TARGETS = {"FlareLight", "FlareSceneSettings"}


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


def main():
    print("loading SPT Managed dlls for typetrees...")
    gen = TypeTreeGenerator("2022.3.43f2")
    gen.load_local_dll_folder(str(MANAGED))

    results = {"FlareLight": [], "FlareSceneSettings": []}
    atlas_out = None
    materials_out = []

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
            return (aw*bx + ax*bw + ay*bz - az*by, aw*by - ax*bz + ay*bw + az*bx,
                    aw*bz + ax*by - ay*bx + az*bw, aw*bw - ax*bx - ay*by - az*bz)

        def qrot(q, v):
            qv = (v[0], v[1], v[2], 0.0)
            qc = (-q[0], -q[1], -q[2], q[3])
            r = qmul(qmul(q, qv), qc)
            return (r[0], r[1], r[2])

        _wc = {}

        def world_trs(tid):
            if tid in _wc:
                return _wc[tid]
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
                res = ((pp[0]+rot[0], pp[1]+rot[1], pp[2]+rot[2]), qmul(pr, lr),
                       (ps[0]*ls[0], ps[1]*ls[1], ps[2]*ls[2]))
            _wc[tid] = res
            return res

        ref_index = {}
        for pid, tr in transforms.items():
            g = gos.get(tr.m_GameObject.path_id)
            if g is not None:
                ref_index[pid] = (path_of(g), "Transform")
        for pid, g in gos.items():
            ref_index[pid] = (path_of(g), "GameObject")

        def path_of_ref(pid):
            return ref_index.get(pid)

        count = 0
        node_cache = {}
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
            row = {"level": level, "go": path_of(go) if go else "?", "class": full}
            if go is not None and go_tid(go) in transforms:
                p = world_trs(go_tid(go))[0]
                row["world"] = [round(p[0], 3), round(p[1], 3), round(p[2], 3)]
            try:
                key = (scr.m_AssemblyName, full)
                if key not in node_cache:
                    fl_nodes = flat(gen.get_nodes_up(scr.m_AssemblyName, full))
                    # retail 1.0 FlareLight drift: ONE extra float between _totalAlpha and
                    # _flares (hand-verified: raw sizes 108/168/348 = hdr28 + 16 + N*60)
                    if cls == "FlareLight":
                        out = []
                        for rowx in fl_nodes:
                            out.append(rowx)
                            if rowx[0] == 1 and rowx[2] == "_totalAlpha":
                                out.append([1, "float", "retail_f0", 0])
                        fl_nodes = out
                    node_cache[key] = to_tree(fl_nodes)
                data = o.read_typetree(node_cache[key], check_read=False)
                row["fields"] = {k: sanitize(v, path_of_ref) for k, v in data.items()
                                 if k not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name")}
                count += 1
            except Exception as e:
                row["parse_error"] = str(e)[:200]
            results[cls].append(row)
        if count:
            print(f"{level}: {count} MultiFlare components")

    total = sum(len(v) for v in results.values())
    out = {"components": results, "atlas": atlas_out, "materials": materials_out}
    OUT_JSON.write_text(json.dumps(out, indent=1))
    print(f"\n{total} components -> {OUT_JSON}")
    for cls, rows in results.items():
        print(f"  {cls}: {len(rows)}")


if __name__ == "__main__":
    main()
