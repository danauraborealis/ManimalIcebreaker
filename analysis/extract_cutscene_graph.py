# pass 2: walk the retail Icebreaker cutscene timeline graph out of resources.assets.
# level709's PlayableDirector points at {fileID:6 -> resources.assets, pathID:45784}
# (the TimelineAsset). tracks/clips/markers are sibling objects there; this BFS's
# every PPtr from the root and dumps full typetree fields for each MonoBehaviour.
# stock Timeline types come from SPT Managed (same Unity 2022.3.43 -> no drift);
# BSG custom tracks (SyncedAudioTrack etc.) resolve from the WIP 1.0 assembly since
# SPT's 0.16.9 Assembly-CSharp predates the cutscene system entirely.

import json
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

TTH.read_typetree_boost = None

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
MANAGED = Path(r"D:\SPTDev\EscapeFromTarkov_Data\Managed")
WIP_ASSEMBLY = Path(r"G:\downloads\Assembly-CSharp.dll")
OUT = Path(__file__).parent / "icebreaker_cutscene_graph.json"

ROOT_PATH_ID = 45784  # TimelineAsset (from level709 PlayableDirector.m_PlayableAsset)


def sanitize(v):
    if isinstance(v, dict):
        return {k: sanitize(x) for k, x in v.items()}
    if isinstance(v, (list, tuple)):
        return [sanitize(x) for x in v]
    if isinstance(v, bool) or isinstance(v, (int, str)) or v is None:
        return v
    if isinstance(v, float):
        return v if v == v and abs(v) != float("inf") else 0.0
    if isinstance(v, bytes):
        return f"<bytes {len(v)}>"
    if hasattr(v, "__dict__"):
        return {k: sanitize(x) for k, x in v.__dict__.items() if not k.startswith("_UnityPy")}
    return str(v)


def collect_pptrs(v, out):
    # any dict with m_FileID+m_PathID is a PPtr
    if isinstance(v, dict):
        if "m_FileID" in v and "m_PathID" in v and len(v) <= 3:
            if v["m_PathID"] != 0:
                out.append((v["m_FileID"], v["m_PathID"]))
        else:
            for x in v.values():
                collect_pptrs(x, out)
    elif isinstance(v, (list, tuple)):
        for x in v:
            collect_pptrs(x, out)
    elif hasattr(v, "__dict__"):
        for k, x in v.__dict__.items():
            if not k.startswith("_UnityPy"):
                collect_pptrs(x, out)


def main():
    print("loading typetrees (SPT Managed + WIP 1.0 Assembly-CSharp)...")
    gen = TypeTreeGenerator("2022.3.43f2")
    gen.load_local_dll_folder(str(MANAGED))
    # WIP 1.0 assembly for BSG cutscene classes. loaded second — if the generator
    # keys by assembly name this REPLACES SPT's Assembly-CSharp, which is what we
    # want here (the graph's custom tracks are 1.0 classes).
    try:
        gen.load_dll(WIP_ASSEMBLY.read_bytes())
        print("  WIP assembly loaded via load_dll(bytes)")
    except Exception as e1:
        try:
            gen.load_local_dll(str(WIP_ASSEMBLY))
            print("  WIP assembly loaded via load_local_dll(path)")
        except Exception as e2:
            print(f"  WARN could not load WIP assembly: {e1} / {e2}")
            print("  available methods:", [m for m in dir(gen) if not m.startswith("_")])

    env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"),
                       str(LEVELS_DIR / "resources.assets"))
    sf = next(f for k, f in env.files.items() if str(k).endswith("resources.assets"))

    objs = sf.objects
    node_cache = {}
    graph = {}
    external_refs = {}
    queue = [ROOT_PATH_ID]
    seen = set()

    while queue:
        pid = queue.pop(0)
        if pid in seen:
            continue
        seen.add(pid)
        o = objs.get(pid)
        if o is None:
            graph[str(pid)] = {"error": "no object at pathID"}
            continue
        tname = o.type.name
        entry = {"unityType": tname}
        refs = []
        if tname == "MonoBehaviour":
            try:
                mb = o.read(check_read=False)
                scr = mb.m_Script.read()
                full = (scr.m_Namespace + "." if scr.m_Namespace else "") + scr.m_ClassName
                entry["class"] = full
                entry["assembly"] = scr.m_AssemblyName
                entry["name"] = getattr(mb, "m_Name", "")
                key = (scr.m_AssemblyName, full)
                if key not in node_cache:
                    node_cache[key] = gen.get_nodes_up(scr.m_AssemblyName, full)
                data = o.read_typetree(node_cache[key], check_read=False)
                data = {k: v for k, v in data.items() if k not in ("m_GameObject", "m_Script")}
                entry["data"] = sanitize(data)
                collect_pptrs(data, refs)
            except Exception as e:
                entry["parse_error"] = str(e)[:300]
        elif tname in ("AnimationClip", "AudioClip", "AvatarMask", "Texture2D", "Material", "Shader"):
            # inventory only — heavy native assets get their own export pass later
            try:
                r = o.read()
                entry["name"] = getattr(r, "m_Name", "")
                entry["byteSize"] = o.byte_size
            except Exception as e:
                entry["parse_error"] = str(e)[:200]
        else:
            try:
                r = o.read()
                entry["name"] = getattr(r, "m_Name", "")
            except Exception:
                pass
        # only follow same-file refs; log externals
        for fid, rpid in refs:
            if fid == 0:
                if rpid not in seen:
                    queue.append(rpid)
            else:
                ext = sf.externals[fid - 1].path if fid - 1 < len(sf.externals) else f"?{fid}"
                external_refs.setdefault(ext, []).append(rpid)
        graph[str(pid)] = entry

    OUT.write_text(json.dumps({
        "root": ROOT_PATH_ID,
        "objects": graph,
        "external_refs": {k: sorted(set(v)) for k, v in external_refs.items()},
    }, indent=1))

    classes = {}
    for v in graph.values():
        c = v.get("class") or v.get("unityType")
        classes[c] = classes.get(c, 0) + 1
    print(f"wrote {OUT} ({OUT.stat().st_size // 1024} KB), {len(graph)} objects:")
    for c, n in sorted(classes.items(), key=lambda kv: -kv[1]):
        print(f"  {n:3d}  {c}")
    if external_refs:
        print("external refs:", {k: len(v) for k, v in external_refs.items()})
    errs = [(k, v) for k, v in graph.items() if "parse_error" in v]
    if errs:
        print(f"{len(errs)} parse errors, first 5:")
        for k, v in errs[:5]:
            print(f"  {k} {v.get('class')}: {v['parse_error'][:120]}")


if __name__ == "__main__":
    main()
