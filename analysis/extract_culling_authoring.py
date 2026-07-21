# retail culling-scene authoring recovery (level708 = Icebreaker_Culling): BSG used
# Perfect Culling natively — this scene holds their bake AUTHORING (23 exclude
# volumes, adaptive grid, portal/door preprocessors). the exclude volumes are
# immediately restorable into the SDK (Author 14) so OUR bakes sample with BSG's
# exclusion set; the EFT-layer components' data is preserved here for the future
# native-runtime restore (needs sharedassets1.assets for the grid data asset).

import json
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

TTH.read_typetree_boost = None

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
MANAGED = Path(r"D:\SPTDev\EscapeFromTarkov_Data\Managed")
OUT = Path(__file__).parent / "icebreaker_culling_authoring.json"


def sanitize(v):
    if isinstance(v, dict):
        if "m_PathID" in v and "m_FileID" in v:
            if v["m_PathID"] == 0:
                return None
            return {"ref": [v["m_FileID"], v["m_PathID"]]}
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

    env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"), str(LEVELS_DIR / "level708"))
    sf = next(f for k, f in env.files.items() if str(k).endswith("level708"))

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
            if ch.path_id == tid:
                break
            cg = gos.get(transforms[ch.path_id].m_GameObject.path_id) if ch.path_id in transforms else None
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

    def local_trs(go):
        t = go_tid(go)
        if t not in transforms:
            return None
        tr = transforms[t]
        return {
            "pos": [tr.m_LocalPosition.x, tr.m_LocalPosition.y, tr.m_LocalPosition.z],
            "rot": [tr.m_LocalRotation.x, tr.m_LocalRotation.y, tr.m_LocalRotation.z, tr.m_LocalRotation.w],
            "scale": [tr.m_LocalScale.x, tr.m_LocalScale.y, tr.m_LocalScale.z],
        }

    rows = []
    node_cache = {}
    for o in sf.objects.values():
        if o.type.name != "MonoBehaviour":
            continue
        try:
            mb = o.read(check_read=False)
            scr = mb.m_Script.read()
        except Exception:
            continue
        go = gos.get(mb.m_GameObject.path_id)
        row = {
            "class": scr.m_ClassName,
            "go": path_of(go) if go else "?",
            "trs": local_trs(go) if go else None,
        }
        try:
            full = (scr.m_Namespace + "." if scr.m_Namespace else "") + scr.m_ClassName
            key = (scr.m_AssemblyName, full)
            if key not in node_cache:
                from UnityPy.helpers.TypeTreeNode import TypeTreeNode
                node_cache[key] = gen.get_nodes_up(scr.m_AssemblyName, full)
            data = o.read_typetree(node_cache[key], check_read=False)
            row["fields"] = sanitize({k: v for k, v in data.items()
                                      if k not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name")})
        except Exception as e:
            row["parse_error"] = str(e)[:200]
        rows.append(row)

    OUT.write_text(json.dumps({"components": rows}, indent=1))
    ok = sum(1 for r in rows if "fields" in r)
    bad = sum(1 for r in rows if "parse_error" in r)
    print(f"wrote {OUT}: {len(rows)} components, {ok} parsed, {bad} drifted (data still has go/trs)")
    for r in rows:
        if r["class"] == "PerfectCullingExcludeVolume":
            vs = (r.get("fields") or {}).get("volumeSize")
            print(f"  ExcludeVolume '{r['go']}' size={vs} {'(DRIFTED)' if 'parse_error' in r else ''}")


if __name__ == "__main__":
    main()
