# resurrection #3: the weather/sky stack. recovers the retail icebreaker Weather +
# Sky Dome component tree from level698 (same raw-bytes technique as spatial audio —
# see extract_spatial_audio.py). all 24 components are small; the drift check is
# check_read=True first, flagged fallback parse second (hand-verify flagged classes
# against hexdumps like the portal/room fixes).
#
# output: icebreaker_weather.json — per-component fields + GO identity (subtree path +
# local TRS) for runtime rehydration onto the surviving bundled GO tree, plus a census
# of external asset PPtrs that need follow-up extraction (fog remap tables etc).

import json
from collections import Counter
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

TTH.read_typetree_boost = None

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
MANAGED = Path(r"D:\SPTDev\EscapeFromTarkov_Data\Managed")
OUT = Path(__file__).parent / "icebreaker_weather.json"

LEVEL = "level698"  # Icebreaker_Scripts
ROOT_NAMES = {"Weather", "Sky Dome"}


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
    if isinstance(v, bytes):
        return v.decode("utf-8", "replace")
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

    roots = {}
    for pid, go in gos.items():
        if go.m_Name in ROOT_NAMES:
            roots[go_tid(go)] = go.m_Name

    def subtree_path(tid):
        """path from the weather root down to this transform, or None if outside."""
        chain = []
        hops = 0
        t = tid
        while t and hops < 64:
            hops += 1
            go = gos.get(transforms[t].m_GameObject.path_id) if t in transforms else None
            chain.append(go.m_Name if go else "?")
            if t in roots:
                return "/".join(reversed(chain))
            t = parent.get(t, 0)
        return None

    def trs_of(go):
        tid = go_tid(go)
        if tid not in transforms:
            return None
        t = transforms[tid]
        p, r, s = t.m_LocalPosition, t.m_LocalRotation, t.m_LocalScale
        return {"pos": [p.x, p.y, p.z], "rot": [r.x, r.y, r.z, r.w], "scale": [s.x, s.y, s.z]}

    node_cache = {}
    results = {}
    ext_refs = Counter()
    parse_flags = []

    def count_ext(v, cls):
        if isinstance(v, dict):
            if "file" in v and "ref" in v:
                ext_refs[f"{cls}: file#{v['file']}"] += 1
            else:
                for x in v.values():
                    count_ext(x, cls)
        elif isinstance(v, list):
            for x in v:
                count_ext(x, cls)

    for o in sf.objects.values():
        if o.type.name != "MonoBehaviour":
            continue
        try:
            mb = o.read(check_read=False)
            go = gos.get(mb.m_GameObject.path_id)
            if go is None:
                continue
            spath = subtree_path(go_tid(go))
            if spath is None:
                continue
            scr = mb.m_Script.read()
        except Exception:
            continue
        cls = scr.m_ClassName
        full = (scr.m_Namespace + "." if scr.m_Namespace else "") + cls
        asm = scr.m_AssemblyName
        row = {"path_id": o.path_id, "go": spath, "trs": trs_of(go), "assembly": asm, "class": full}
        try:
            key = (asm, full)
            if key not in node_cache:
                node_cache[key] = gen.get_nodes_up(asm, full)
            try:
                data = o.read_typetree(node_cache[key], check_read=True)
            except Exception:
                # layout drift — keep the loose parse but FLAG it for hand verification
                data = o.read_typetree(node_cache[key], check_read=False)
                row["parse_flagged"] = True
                parse_flags.append(f"{cls} ({len(o.get_raw_data())}B)")
            row["fields"] = sanitize({k: v for k, v in data.items()
                                      if k not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name")})
            count_ext(row["fields"], cls)
        except Exception as e:
            row["parse_error"] = str(e)[:200]
            parse_flags.append(f"{cls} FAILED: {e}")
        results.setdefault(cls, []).append(row)

    # scene-object table: every engine object in the subtree, path_id -> {path, type}.
    # runtime resolves component fields that point at Transforms/GOs/renderers with this
    # (find child by path under the root, GetComponent by type).
    scene_objects = {}
    for pid, go in gos.items():
        spath = subtree_path(go_tid(go))
        if spath is None:
            continue
        scene_objects[str(pid)] = {"path": spath, "type": "GameObject"}
        for comp in go.m_Component:
            c = comp.component if hasattr(comp, "component") else comp[1]
            try:
                tname = sf.objects[c.path_id].type.name
            except Exception:
                continue
            if tname != "MonoBehaviour":
                scene_objects[str(c.path_id)] = {"path": spath, "type": tname}

    out = {
        "source": "retail 1.0 level698 Weather + Sky Dome subtree, typetrees from SPT 0.16.9 Mono",
        "components": results,
        "scene_objects": scene_objects,
    }
    OUT.write_text(json.dumps(out, indent=1))

    print(f"\nwrote {OUT} ({OUT.stat().st_size // 1024} KB)")
    for cls in sorted(results):
        n = len(results[cls])
        flagged = sum(1 for r in results[cls] if r.get("parse_flagged") or r.get("parse_error"))
        print(f"  {cls}: {n}" + (f"  <-- {flagged} FLAGGED (layout drift)" if flagged else ""))
    if ext_refs:
        print("\nexternal asset refs (need follow-up extraction):")
        for k, v in ext_refs.most_common(20):
            print(f"  {v:3d}x {k}")


if __name__ == "__main__":
    main()
